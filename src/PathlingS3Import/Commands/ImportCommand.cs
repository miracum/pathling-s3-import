using System;
using System.Diagnostics;
using System.Reactive.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using DotMake.CommandLine;
using Hl7.Fhir.Model;
using Hl7.Fhir.Rest;
using Hl7.Fhir.Serialization;
using Microsoft.Extensions.Logging;
using Minio;
using Minio.DataModel.Args;
using Polly;
using Prometheus.Client;
using Prometheus.Client.Collectors;
using Prometheus.Client.MetricPusher;
using Task = System.Threading.Tasks.Task;

namespace PathlingS3Import;

// Create a simple class like this to define your root command:
[CliCommand(Description = "The import command", Parent = typeof(RootCommand))]
public partial class ImportCommand : CommandBase
{
    private readonly ILogger<ImportCommand> log;

    private readonly CollectorRegistry collectorRegistry;
    private readonly MetricFactory metricFactory;

    private readonly IMetricFamily<ICounter, ValueTuple<string>> bundlesImportedCounter;
    private readonly IMetricFamily<IHistogram, ValueTuple<string>> importDurationHistogram;
    private readonly IMetricFamily<ICounter, ValueTuple<string>> resourcesImportedCounter;

    private class ImportCheckpoint
    {
        public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
        public string? LastImportedObjectUrl { get; set; }
    }

    public ImportCommand()
    {
        log = LogFactory.CreateLogger<ImportCommand>();

        collectorRegistry = new CollectorRegistry();
        metricFactory = new MetricFactory(collectorRegistry);

        bundlesImportedCounter = metricFactory.CreateCounter(
            "pathlings3import_bundles_imported_total",
            "Total number of imported bundles by resource type.",
            "resourceType"
        );

        importDurationHistogram = metricFactory.CreateHistogram(
            "pathlings3import_import_duration_seconds",
            "Time it took for the $import operation to complete.",
            "resourceType",
            buckets: [0.5d, 1d, 5d, 10d, 30d, 60d, 150d, 300d, 600d]
        );

        resourcesImportedCounter = metricFactory.CreateCounter(
            "pathlings3import_resources_imported_total",
            "Total number of imported resources across all bundles processed.",
            "resourceType"
        );
    }

    [CliOption(Description = "The FHIR base URL of the Pathling server")]
    public Uri? PathlingServerBaseUrl { get; set; }

    [CliOption(Description = "The type of FHIR resource to import")]
    public ResourceType ImportResourceType { get; set; } = ResourceType.Patient;

    [CliOption(
        Description = "If enabled, continue processing resources from the last saved checkpoint file.",
        Name = "--continue-from-last-checkpoint"
    )]
    public bool IsContinueFromLastCheckpointEnabled { get; set; } = false;

    [CliOption(Description = "Name of the checkpoint file", Name = "--checkpoint-file-name")]
    public string CheckpointFileName { get; set; } = "_last-import-checkpoint.json";

    [CliOption(Description = "Delay to wait after importing a bundle")]
    public TimeSpan SleepAfterImport { get; set; } = TimeSpan.FromSeconds(10);

    public async Task RunAsync()
    {
        log.LogInformation(
            "Pathling FHIR  base URL set to {PathlingBaseUrl}",
            PathlingServerBaseUrl
        );

        using var fhirClient = new FhirClient(
            PathlingServerBaseUrl,
            settings: new FhirClientSettings
            {
                PreferredFormat = ResourceFormat.Json,
                // TODO: change once we implement it client-side
                UseAsync = false,
                Timeout = (int)TimeSpan.FromMinutes(30).TotalMilliseconds,
            }
        );

        log.LogInformation("Minio endpoint set to {S3Endpoint}", S3Endpoint);
        var minio = new MinioClient()
            .WithEndpoint(S3Endpoint)
            .WithCredentials(S3AccessKey, S3SecretKey)
            .Build();

        if (IsMetricsEnabled)
        {
            ArgumentNullException.ThrowIfNull(PushGatewayEndpoint);

            var options = new MetricPusherOptions
            {
                Endpoint = PushGatewayEndpoint.AbsoluteUri,
                Job = PushGatewayJobName,
                Instance = PushGatewayJobInstance,
                CollectorRegistry = collectorRegistry,
            };

            if (PushGatewayAuthHeader is not null)
            {
                options.AdditionalHeaders = new Dictionary<string, string>
                {
                    { "Authorization", PushGatewayAuthHeader }
                };
            }

            using var metricPusher = new MetricPusher(options);
            var pushServer = new MetricPushServer(metricPusher, TimeSpan.FromSeconds(10));

            pushServer.Start();
            await DoAsync(minio, fhirClient, RetryPipeline);
            metricPusher.PushAsync().Wait();
            pushServer.Stop();
        }
        else
        {
            await DoAsync(minio, fhirClient, RetryPipeline);
        }
    }

    private async Task DoAsync(
        IMinioClient minio,
        FhirClient fhirClient,
        ResiliencePipeline retryPipeline
    )
    {
        log.LogInformation(
            "Checking if bucket {S3BucketName} exists in {S3BaseUrl}.",
            S3BucketName,
            minio.Config.BaseUrl
        );

        var bucketExistsArgs = new BucketExistsArgs().WithBucket(S3BucketName);
        bool found = await minio.BucketExistsAsync(bucketExistsArgs);
        if (!found)
        {
            throw new ArgumentException($"Bucket {S3BucketName} doesn't exist.");
        }

        var prefix = $"{S3ObjectNamePrefix}{ImportResourceType}/";

        log.LogInformation(
            "Listing objects in {S3BaseUrl}/{S3BucketName}/{Prefix}.",
            minio.Config.BaseUrl,
            S3BucketName,
            prefix
        );

        var listArgs = new ListObjectsArgs()
            .WithBucket(S3BucketName)
            .WithPrefix(prefix)
            .WithRecursive(false);

        var allObjects =
            minio.ListObjectsEnumAsync(listArgs)
            ?? throw new InvalidOperationException("observable for listing buckets is null");

        log.LogInformation(
            "Found a total of {ObjectCount} matching objects.",
            await allObjects.CountAsync()
        );

        // sort the objects by the value of the timestamp in the object name
        // in ascending order.
        var objectsToProcess = await allObjects
            // skip over the checkpoint file (or anything that isn't ndjson)
            .Where(o => o.Key.EndsWith(".ndjson"))
            .OrderBy(o =>
            {
                var match = BundleObjectNameRegex().Match(o.Key);
                if (match.Success)
                {
                    return Convert.ToDouble(match.Groups["timestamp"].Value);
                }

                throw new InvalidOperationException(
                    $"allObjects contains an item whose key doesn't match the regex: {o.Key}"
                );
            })
            .ToListAsync();

        var index = 0;
        foreach (var o in objectsToProcess)
        {
            log.LogInformation("{Index}. {Key}", index, o.Key);
            index++;
        }

        var checkpointObjectName = $"{prefix}{CheckpointFileName}";

        log.LogInformation(
            "Name of the current progress checkpoint object set to {CheckpointObjectName}.",
            checkpointObjectName
        );

        if (IsContinueFromLastCheckpointEnabled)
        {
            log.LogInformation(
                "Reading last checkpoint file from {CheckpointObjectName}",
                checkpointObjectName
            );

            var lastProcessedFile = string.Empty;
            // read the contents of the last checkpoint file
            var getArgs = new GetObjectArgs()
                .WithBucket(S3BucketName)
                .WithObject(checkpointObjectName)
                .WithCallbackStream(
                    async (stream, ct) =>
                    {
                        using var reader = new StreamReader(stream, Encoding.UTF8);

                        var checkpointJson = await reader.ReadToEndAsync(ct);
                        var checkpoint = JsonSerializer.Deserialize<ImportCheckpoint>(
                            checkpointJson
                        );

                        log.LogInformation("Last checkpoint: {CheckpointJson}", checkpointJson);

                        if (checkpoint is not null)
                        {
                            lastProcessedFile = checkpoint.LastImportedObjectUrl;
                        }
                        else
                        {
                            log.LogError(
                                "Failed to read checkpoint file: deserialized object is null"
                            );
                        }

                        await stream.DisposeAsync();
                    }
                );
            await minio.GetObjectAsync(getArgs);

            if (string.IsNullOrEmpty(lastProcessedFile))
            {
                throw new InvalidDataException(
                    "Failed to read last processed file. Contents are null or empty."
                );
            }

            log.LogInformation("Continuing after {LastProcessedFile}", lastProcessedFile);

            // order again just so we have an IOrderedEnumerable in the end.
            // not really necessary.
            objectsToProcess = objectsToProcess
                .SkipWhile(item => $"s3://{S3BucketName}/{item.Key}" != lastProcessedFile)
                // SkipWhile stops if we reach the lastProcessedFile, but includes the entry itself in the
                // result, so we need to skip that as well.
                // Ideally, we'd say `SkipWhile(item.key-timestamp <= lastProcessedFile-timestamp)`.
                .Skip(1)
                .ToList();

            log.LogInformation("Listing actual objects to process");

            index = 0;
            foreach (var o in objectsToProcess)
            {
                log.LogInformation("{Index}. {Key}", index, o.Key);
                index++;
            }
        }

        var objectsToProcessCount = objectsToProcess.Count;
        log.LogInformation("Actually processing {ObjectsToProcessCount}", objectsToProcessCount);

        var stopwatch = new Stopwatch();
        var importedCount = 0;

        foreach (var item in objectsToProcess)
        {
            var objectUrl = $"s3://{S3BucketName}/{item.Key}";

            using (log.BeginScope("[Importing ndjson file {NdjsonObjectUrl}]", objectUrl))
            {
                var resourceCountInFile = 0;
                var getArgs = new GetObjectArgs()
                    .WithBucket(S3BucketName)
                    .WithObject(item.Key)
                    .WithCallbackStream(
                        async (stream, ct) =>
                        {
                            using var reader = new StreamReader(stream, Encoding.UTF8);
                            while (await reader.ReadLineAsync(ct) is not null)
                            {
                                resourceCountInFile++;
                            }
                        }
                    );
                stopwatch.Restart();
                await minio.GetObjectAsync(getArgs);
                stopwatch.Stop();

                log.LogInformation(
                    "{ObjectUrl} contains {ResourceCount} resources. Counting took {ResourceCountDuration}",
                    objectUrl,
                    resourceCountInFile,
                    stopwatch
                );

                var parameter = new Parameters.ParameterComponent()
                {
                    Name = "source",
                    Part =
                    [
                        new Parameters.ParameterComponent()
                        {
                            Name = "resourceType",
                            Value = new Code(ImportResourceType.ToString())
                        },
                        new Parameters.ParameterComponent()
                        {
                            Name = "mode",
                            Value = new Code("merge")
                        },
                        new Parameters.ParameterComponent()
                        {
                            Name = "url",
                            Value = new FhirUrl(objectUrl)
                        }
                    ]
                };

                var importParameters = new Parameters();
                // for now, create one Import request per file. In the future,
                // we might want to add multiple ndjson files at once in batches.
                importParameters.Parameter.Add(parameter);

                log.LogInformation("{ImportParameters}", importParameters.ToJson());

                log.LogInformation(
                    "Starting {PathlingServerBaseUrl}/$import for {ObjectUrl}",
                    PathlingServerBaseUrl,
                    objectUrl
                );

                if (!IsDryRun)
                {
                    stopwatch.Restart();
                    var response = await retryPipeline.ExecuteAsync(async token =>
                    {
                        return await fhirClient.WholeSystemOperationAsync(
                            "import",
                            importParameters
                        );
                    });
                    stopwatch.Stop();

                    importDurationHistogram
                        .WithLabels(ImportResourceType.ToString())
                        .Observe(stopwatch.Elapsed.TotalSeconds);

                    // resource import throughput
                    var resourcesPerSecond = resourceCountInFile / stopwatch.Elapsed.TotalSeconds;

                    log.LogInformation("{ImportResponse}", response.ToJson());
                    log.LogInformation(
                        "Import took {ImportDuration} for a bundle of {ResourceCountInFile}. {ResourcesPerSecond} resources/s",
                        stopwatch.Elapsed,
                        resourceCountInFile,
                        resourcesPerSecond
                    );
                    log.LogInformation(
                        "Checkpointing progress '{ObjectUrl}' as '{S3BucketName}/{CheckpointObjectName}'",
                        objectUrl,
                        S3BucketName,
                        checkpointObjectName
                    );

                    var checkpoint = new ImportCheckpoint() { LastImportedObjectUrl = objectUrl, };
                    var jsonString = JsonSerializer.Serialize(checkpoint);
                    var bytes = Encoding.UTF8.GetBytes(jsonString);
                    using var memoryStream = new MemoryStream(bytes);

                    // persist progress
                    var putArgs = new PutObjectArgs()
                        .WithBucket(S3BucketName)
                        .WithObject(checkpointObjectName)
                        .WithContentType("application/json")
                        .WithStreamData(memoryStream)
                        .WithObjectSize(bytes.LongLength);

                    stopwatch.Restart();
                    await retryPipeline.ExecuteAsync(async token =>
                    {
                        await minio.PutObjectAsync(putArgs, token);
                    });
                    stopwatch.Stop();
                    log.LogInformation(
                        "Persisting progress took {PutProgressDuration}",
                        stopwatch.Elapsed
                    );
                }
                else
                {
                    log.LogInformation("Running import in dry run mode. Waiting a few seconds.");
                    await Task.Delay(TimeSpan.FromSeconds(5));
                }

                bundlesImportedCounter.WithLabels(ImportResourceType.ToString()).Inc();
                resourcesImportedCounter
                    .WithLabels(ImportResourceType.ToString())
                    .Inc(resourceCountInFile);

                importedCount++;
                log.LogInformation(
                    "Imported {ImportedCount} / {ObjectsToProcessCount}",
                    importedCount,
                    objectsToProcessCount
                );

                log.LogInformation(
                    "Sleeping after import for {SleepAfterImportSeconds} s",
                    SleepAfterImport.TotalSeconds
                );

                await Task.Delay(SleepAfterImport);
            }
        }

        log.LogInformation("Done importing.");
    }
}
