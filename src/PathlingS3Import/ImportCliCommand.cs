using System.Diagnostics;
using System.Reactive.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using DotMake.CommandLine;
using Hl7.Fhir.Model;
using Hl7.Fhir.Rest;
using Hl7.Fhir.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Console;
using Minio;
using Minio.DataModel.Args;
using Polly;
using Polly.Retry;
using Prometheus.Client;
using Prometheus.Client.Collectors;
using Prometheus.Client.MetricPusher;
using Task = System.Threading.Tasks.Task;

namespace PathlingS3Import;

// Create a simple class like this to define your root command:
[CliCommand(Description = "The import command")]
public partial class ImportCliCommand
{
    [GeneratedRegex(".*bundle-(?<timestamp>\\d*)\\.ndjson$")]
    private static partial Regex BundleObjectNameRegex();

    private readonly ILogger<ImportCliCommand> log;

    private readonly CollectorRegistry collectorRegistry;
    private readonly MetricFactory metricFactory;

    private readonly IMetricFamily<ICounter, ValueTuple<string>> bundlesImportedCounter;
    private readonly IMetricFamily<IHistogram, ValueTuple<string>> importDurationHistogram;
    private readonly IMetricFamily<ICounter, ValueTuple<string>> resourcesImportedCounter;

    public ImportCliCommand()
    {
        using var loggerFactory = LoggerFactory.Create(builder =>
            builder.AddSimpleConsole(options =>
            {
                options.IncludeScopes = true;
                options.SingleLine = true;
                options.ColorBehavior = LoggerColorBehavior.Disabled;
                options.TimestampFormat = "yyyy-MM-dd HH:mm:ss ";
            })
        );

        log = loggerFactory.CreateLogger<ImportCliCommand>();

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

    [CliOption(Description = "The S3 endpoint URI", Name = "--s3-endpoint")]
    public Uri? S3Endpoint { get; set; }

    [CliOption(Description = "The S3 access key", Name = "--s3-access-key")]
    public string? S3AccessKey { get; set; }

    [CliOption(Description = "The S3 secret key", Name = "--s3-secret-key")]
    public string? S3SecretKey { get; set; }

    [CliOption(
        Description = "The name of the bucket containing the resources to import",
        Name = "--s3-bucket-name"
    )]
    public string? S3BucketName { get; set; } = "fhir";

    [CliOption(
        Description = "The S3 object name prefix. Corresponds to kafka-fhir-to-server's `S3_OBJECT_NAME_PREFIX`",
        Name = "--s3-object-name-prefix"
    )]
    public string? S3ObjectNamePrefix { get; set; } = "";

    [CliOption(Description = "The FHIR base URL of the Pathling server")]
    public Uri? PathlingServerBaseUrl { get; set; }

    [CliOption(Description = "The type of FHIR resource to import")]
    public ResourceType ImportResourceType { get; set; } = ResourceType.Patient;

    [CliOption(
        Description = "If enabled, list and read all objects but don't invoke the import operation or store the progress.",
        Name = "--dry-run"
    )]
    public bool IsDryRun { get; set; } = false;

    [CliOption(
        Description = "If enabled, continue processing resources from the last saved checkpoint file.",
        Name = "--continue-from-last-checkpoint"
    )]
    public bool IsContinueFromLastCheckpointEnabled { get; set; } = false;

    [CliOption(
        Description = "If enabled, push metrics about the import to the specified Prometheus PushGateway.",
        Name = "--enable-metrics"
    )]
    public bool IsMetricsEnabled { get; set; } = false;

    [CliOption(
        Description = "Endpoint URL for the Prometheus PushGateway.",
        Name = "--pushgateway-endpoint",
        Required = false
    )]
    public Uri? PushGatewayEndpoint { get; set; }

    [CliOption(
        Description = "Prometheus PushGateway job name.",
        Name = "--pushgateway-job-name",
        Required = false
    )]
    public string PushGatewayJobName { get; set; } =
        Assembly.GetExecutingAssembly().GetName().Name!;

    [CliOption(
        Description = "Prometheus PushGateway job instance.",
        Name = "--pushgateway-job-instance",
        Required = false
    )]
    public string? PushGatewayJobInstance { get; set; }

    [CliOption(
        Description = "Value for the `Authorization` header",
        Name = "--pushgateway-auth-header",
        Required = false
    )]
    public string? PushGatewayAuthHeader { get; set; }

    public void Run()
    {
        var retryOptions = new RetryStrategyOptions
        {
            ShouldHandle = new PredicateBuilder().Handle<Exception>(),
            BackoffType = DelayBackoffType.Exponential,
            UseJitter = true, // Adds a random factor to the delay
            MaxRetryAttempts = 10,
            Delay = TimeSpan.FromSeconds(10),
            OnRetry = args =>
            {
                log.LogInformation(
                    "Retrying. Attempt: {AttemptNumber}. Duration: {Duration}. Exception: {Exception}",
                    args.AttemptNumber,
                    args.Duration,
                    args.Outcome.Exception
                );
                // Event handlers can be asynchronous; here, we return an empty ValueTask.
                return default;
            }
        };

        var retryPipeline = new ResiliencePipelineBuilder()
            .AddRetry(retryOptions) // Add retry using the default options
            .Build(); // Builds the resilience pipeline

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
        using var minio = new MinioClient()
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
            DoAsync(minio, fhirClient, retryPipeline).Wait();
            metricPusher.PushAsync().Wait();
            pushServer.Stop();
        }
        else
        {
            DoAsync(minio, fhirClient, retryPipeline).Wait();
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
            await minio.ListObjectsAsync(listArgs).ToList()
            ?? throw new InvalidOperationException("observable for listing buckets is null");

        log.LogInformation("Found a total of {ObjectCount} matching objects.", allObjects.Count);

        // sort the objects by the value of the timestamp in the object name
        // in ascending order.
        var objectsToProcess = allObjects
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
            .ToList();

        var index = 0;
        foreach (var o in objectsToProcess)
        {
            log.LogInformation("{Index}. {Key}", index, o.Key);
            index++;
        }

        var currentProgressObjectName = $"{prefix}pathling-s3-importer-last-imported.txt";

        log.LogInformation(
            "Name of the current progress tracker object set to {CurrentProgressObjectName}.",
            currentProgressObjectName
        );

        if (IsContinueFromLastCheckpointEnabled)
        {
            log.LogInformation(
                "Reading last checkpoint file {CurrentProgressObjectName}",
                currentProgressObjectName
            );

            var lastProcessedFile = string.Empty;
            // read the contents of the last checkpoint file
            var getArgs = new GetObjectArgs()
                .WithBucket(S3BucketName)
                .WithObject(currentProgressObjectName)
                .WithCallbackStream(
                    async (stream, ct) =>
                    {
                        using var reader = new StreamReader(stream, Encoding.UTF8);

                        lastProcessedFile = await reader.ReadToEndAsync(ct);
                        lastProcessedFile = lastProcessedFile.Trim();

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
                .SkipWhile(item => $"{S3BucketName}/{item.Key}" != lastProcessedFile)
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

            var resourceCountInFile = 0;

            using (log.BeginScope("[Counting lines of {NdjsonObjectUrl}]", objectUrl))
            {
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
            }

            using (log.BeginScope("[Importing ndjson file {NdjsonObjectUrl}]", objectUrl))
            {
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
                        "Import took {ImportDuration}. {ResourcesPerSecond} resources/s",
                        stopwatch.Elapsed,
                        resourcesPerSecond
                    );
                    log.LogInformation(
                        "Checkpointing progress '{S3BucketName}/{ItemKey}' as '{S3BucketName}/{CurrentProgressObjectName}'",
                        S3BucketName,
                        item.Key,
                        S3BucketName,
                        currentProgressObjectName
                    );

                    var bytes = Encoding.UTF8.GetBytes($"{S3BucketName}/{item.Key}");
                    using var memoryStream = new MemoryStream(bytes);

                    // persist progress
                    var putArgs = new PutObjectArgs()
                        .WithBucket(S3BucketName)
                        .WithObject(currentProgressObjectName)
                        .WithContentType("text/plain")
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
            }
        }

        log.LogInformation("Done importing.");
    }
}
