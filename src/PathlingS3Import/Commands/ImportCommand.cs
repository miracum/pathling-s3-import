using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reactive.Linq;
using System.Text;
using System.Text.Json;
using DotMake.CommandLine;
using Hl7.Fhir.Model;
using Hl7.Fhir.Rest;
using Hl7.Fhir.Serialization;
using Microsoft.Extensions.Logging;
using Minio;
using Minio.DataModel;
using Minio.DataModel.Args;
using Minio.Exceptions;
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

    [CliOption(Description = "Delay to wait after importing an NDJSON file")]
    public TimeSpan SleepAfterImport { get; set; } = TimeSpan.FromSeconds(10);

    [CliOption(Description = "Name of the checkpoint file", Name = "--checkpoint-file-name")]
    public string CheckpointFileName { get; set; } = "_last-import-checkpoint.json";

    [CliOption(
        Description = "The maximum number of resources a merged NDJSON may contain."
            + " Files that are already larger than this value might be merged into files exceeding this value."
    )]
    public int MaxMergedBundleSize { get; set; } = 100;

    [CliOption(Description = "The maximum size of the merged NDJSON file in bytes. Default: 1 GiB")]
    public int MaxMergedBundleSizeInBytes { get; set; } = 1 * 1024 * 1024 * 1024;

    [CliOption(
        Description = "Enable merging of multiple NDJSON files before importing.",
        Name = "--enable-merging"
    )]
    public bool IsMergingEnabled { get; set; } = false;

    [CliOption(Description = "Delete source files after merging.", Name = "--delete-after-merging")]
    public bool IsDeleteAfterMergingEnabled { get; set; } = false;

    private JsonSerializerOptions FhirJsonOptions { get; } =
        new JsonSerializerOptions().ForFhir(ModelInfo.ModelInspector);

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
                    { "Authorization", PushGatewayAuthHeader },
                };
            }

            using var metricPusher = new MetricPusher(options);
            var pushServer = new MetricPushServer(metricPusher, TimeSpan.FromSeconds(10));

            pushServer.Start();
            await DoAsync(minio, fhirClient);
            metricPusher.PushAsync().Wait();
            pushServer.Stop();
        }
        else
        {
            await DoAsync(minio, fhirClient);
        }
    }

    private async Task DoAsync(IMinioClient minio, FhirClient fhirClient)
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

        // sort the objects by their name in ascending order
        var objectsToProcess = await allObjects
            // skip over the checkpoint file (or anything that isn't ndjson)
            .Where(o => o.Key.EndsWith(".ndjson"))
            .OrderBy(o => o.Key)
            .ToListAsync();

        var checkpointObjectName = $"{prefix}{CheckpointFileName}";

        log.LogInformation(
            "Name of the current progress checkpoint object set to {CheckpointObjectName}.",
            checkpointObjectName
        );

        if (IsContinueFromLastCheckpointEnabled)
        {
            log.LogInformation("Checkpointing enabled. Continuing from last checkpoint.");

            objectsToProcess = await GetItemsToProcessAfterCheckpointAsync(
                minio,
                objectsToProcess,
                checkpointObjectName
            );
        }

        var objectsToProcessCount = objectsToProcess.Count;
        log.LogInformation("Actually processing {ObjectsToProcessCount}", objectsToProcessCount);

        var stopwatch = new Stopwatch();
        var importedCount = 0;

        var currentMergedResources = new ConcurrentDictionary<string, string>();
        var currentMergedObjectKeys = new ConcurrentBag<string>();
        var estimatedSizeInBytes = 0;
        var mergedItemsCount = 0;
        var lastMergedObjectUrl = string.Empty;
        var lastProcessedObjectUrl = string.Empty;

        foreach (var item in objectsToProcess)
        {
            var objectUrl = $"s3://{S3BucketName}/{item.Key}";

            lastProcessedObjectUrl = objectUrl;

            using var _ = log.BeginScope("[Processing ndjson file {ObjectUrl}]", objectUrl);

            int resourceCountInFile = await CountResourcesInNDJsonAsync(minio, item.Key);

            log.LogInformation(
                "{ObjectUrl} contains {ResourceCount} resources.",
                objectUrl,
                resourceCountInFile
            );

            if (IsMergingEnabled)
            {
                // TODO: this could be consolidated in CountResourcesInNDJsonAsync
                var getArgs = new GetObjectArgs()
                    .WithBucket(S3BucketName)
                    .WithObject(item.Key)
                    .WithCallbackStream(
                        async (stream, ct) =>
                        {
                            using var reader = new StreamReader(stream, Encoding.UTF8);
                            while (await reader.ReadLineAsync(ct) is { } line)
                            {
                                var resource = JsonSerializer.Deserialize<Resource>(
                                    line,
                                    FhirJsonOptions
                                );

                                if (resource is null)
                                {
                                    log.LogWarning("Read a resource that is null");
                                    continue;
                                }
                                // adds or updates the resource by its id in the dictionary.
                                // store the plaintext resource to avoid serializing again later
                                currentMergedResources[resource.Id] = line;
                                estimatedSizeInBytes += Encoding.UTF8.GetByteCount(line);
                            }

                            currentMergedObjectKeys.Add(item.Key);
                        }
                    );
                await minio.GetObjectAsync(getArgs);

                log.LogInformation(
                    "{ObjectUrl} contains {ResourceCount} resources. "
                        + "Current bundle total so far: {CurrentMergedResourcesCount} of {MaxMergedBundleSize}",
                    objectUrl,
                    resourceCountInFile,
                    currentMergedResources.Count,
                    MaxMergedBundleSize
                );

                mergedItemsCount++;

                if (
                    currentMergedResources.Count >= MaxMergedBundleSize
                    || estimatedSizeInBytes >= MaxMergedBundleSizeInBytes
                )
                {
                    log.LogInformation(
                        "Created merged bundle of {Count} resources. "
                            + "Estimated size: {EstimatedSizeInBytes} B ({EstimatedSizeInMebiBytes} MiB). "
                            + "Limit: {MaxMergedBundleSizeInBytes} B ({MaxMergedBundleSizeInMebiBytes} MiB)",
                        currentMergedResources.Count,
                        estimatedSizeInBytes,
                        estimatedSizeInBytes / 1024 / 1024,
                        MaxMergedBundleSizeInBytes,
                        MaxMergedBundleSizeInBytes / 1024 / 1024
                    );

                    var mergedObjectName = await PutMergedBundleAsync(
                        minio,
                        currentMergedResources
                    );

                    lastMergedObjectUrl = $"s3://{S3BucketName}/{mergedObjectName}";

                    // overriding this feels a bit hacky...
                    resourceCountInFile = currentMergedResources.Count;

                    currentMergedResources.Clear();
                    estimatedSizeInBytes = 0;
                }
                else
                {
                    // continue in the foreach loop.
                    // exiting this block will execute the import
                    continue;
                }
            }

            stopwatch.Restart();
            if (!IsDryRun)
            {
                var objectUrlToImport = IsMergingEnabled ? lastMergedObjectUrl : objectUrl;
                await ImportNdjsonFileAsync(fhirClient, objectUrlToImport, resourceCountInFile);

                var lastProcessedFile = string.Empty;
                try
                {
                    // get's the object name of the last processed file in the previous import
                    lastProcessedFile = await GetLastProcessedFileFromCheckpointAsync(
                        minio,
                        checkpointObjectName
                    );
                }
                catch (ObjectNotFoundException e)
                {
                    log.LogWarning(
                        e,
                        "Checkpoint object not found. Can be ignored if this is the first run or checkpointing is disabled."
                    );
                }

                // always checkpoint progress against the object URL, i.e. not the merged one
                await CheckpointProgressAsync(minio, checkpointObjectName, objectUrl);

                // delete the source file after merging if enabled, as well as the previous checkpoint file
                // the second part is necessary, because an import with an existing checkpoint starts processing
                // files after the last processed file in the ckeckpoint.
                if (IsDeleteAfterMergingEnabled)
                {
                    if (!string.IsNullOrEmpty(lastProcessedFile))
                    {
                        log.LogInformation(
                            "Adding last processed file from previous checkpoint: {S3BucketName}/{LastProcessedFile} to objects to be deleted.",
                            S3BucketName,
                            lastProcessedFile
                        );
                        currentMergedObjectKeys.Add(lastProcessedFile);
                    }

                    // don't delete the last file the current checkpoint points to
                    // bit of an ugly solution since objectUrl is an url whereas k is an object key in the bucket.
                    var objectsToDelete = currentMergedObjectKeys
                        .Where(k => !objectUrl.EndsWith(k))
                        .ToList();

                    log.LogInformation(
                        "Removing merged source files ({Count}) from {S3BucketName} after merging.",
                        currentMergedObjectKeys.Count,
                        S3BucketName
                    );

                    var args = new RemoveObjectsArgs()
                        .WithBucket(S3BucketName)
                        .WithObjects(objectsToDelete);

                    await minio.RemoveObjectsAsync(args);

                    currentMergedObjectKeys.Clear();
                }
            }
            else
            {
                log.LogInformation("In dry-run mode. Sleeping for 5s.");
                await Task.Delay(TimeSpan.FromSeconds(5));
            }

            stopwatch.Stop();

            importedCount = IsMergingEnabled ? mergedItemsCount : importedCount + 1;
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

        // will only ever be not-empty if merging is actually enabled
        if (!currentMergedResources.IsEmpty)
        {
            log.LogInformation(
                "Resources remaining: {Count}. Uploading as smaller bundle.",
                currentMergedResources.Count
            );

            var mergedObjectName = await PutMergedBundleAsync(minio, currentMergedResources);
            var mergedObjectUrl = $"s3://{S3BucketName}/{mergedObjectName}";

            if (!IsDryRun)
            {
                await ImportNdjsonFileAsync(
                    fhirClient,
                    mergedObjectUrl,
                    currentMergedResources.Count
                );

                // checkpoint against the last processed object, not the merged object url
                await CheckpointProgressAsync(minio, checkpointObjectName, lastProcessedObjectUrl);
            }
            else
            {
                log.LogInformation("In dry-run mode. Sleeping for 5s.");
                await Task.Delay(TimeSpan.FromSeconds(5));
            }

            currentMergedResources.Clear();
            estimatedSizeInBytes = 0;
        }

        log.LogInformation("Done importing.");
    }

    private async Task ImportNdjsonFileAsync(
        FhirClient fhirClient,
        string objectUrl,
        int resourceCountInFile
    )
    {
        // if merging is enabled, the objectUrl points to the merged NDJSON,
        // otherwise it points to the current item in objectsToProcess.
        var importParameters = CreateImportParameters(objectUrl);

        log.LogInformation("{ImportParameters}", await importParameters.ToJsonAsync());

        log.LogInformation(
            "Starting {PathlingServerBaseUrl}/$import for {ObjectUrl}",
            PathlingServerBaseUrl,
            objectUrl
        );

        var stopwatch = new Stopwatch();

        stopwatch.Restart();
        var response = await RetryPipeline.ExecuteAsync(async token =>
        {
            return await fhirClient.WholeSystemOperationAsync("import", importParameters);
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

        bundlesImportedCounter.WithLabels(ImportResourceType.ToString()).Inc();
        resourcesImportedCounter.WithLabels(ImportResourceType.ToString()).Inc(resourceCountInFile);
    }

    private async Task CheckpointProgressAsync(
        IMinioClient minio,
        string checkpointObjectName,
        string objectUrl
    )
    {
        log.LogInformation(
            "Checkpointing progress '{ObjectUrl}' as '{S3BucketName}/{CheckpointObjectName}'",
            objectUrl,
            S3BucketName,
            checkpointObjectName
        );

        var checkpoint = new ProgressCheckpoint() { LastProcessedObjectUrl = objectUrl };

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

        await RetryPipeline.ExecuteAsync(async token =>
        {
            await minio.PutObjectAsync(putArgs, token);
        });
    }

    private async Task<int> CountResourcesInNDJsonAsync(IMinioClient minio, string objectName)
    {
        var resourceCountInFile = 0;
        var getArgs = new GetObjectArgs()
            .WithBucket(S3BucketName)
            .WithObject(objectName)
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
        await minio.GetObjectAsync(getArgs);
        return resourceCountInFile;
    }

    private Parameters CreateImportParameters(string objectUrl)
    {
        var parameter = new Parameters.ParameterComponent()
        {
            Name = "source",
            Part =
            [
                new Parameters.ParameterComponent()
                {
                    Name = "resourceType",
                    Value = new Code(ImportResourceType.ToString()),
                },
                new Parameters.ParameterComponent() { Name = "mode", Value = new Code("merge") },
                new Parameters.ParameterComponent()
                {
                    Name = "url",
                    Value = new FhirUrl(objectUrl),
                },
            ],
        };

        var importParameters = new Parameters();
        // for now, create one Import request per file. In the future,
        // we might want to add multiple ndjson files at once in batches.
        importParameters.Parameter.Add(parameter);
        return importParameters;
    }

    private async Task<string> GetLastProcessedFileFromCheckpointAsync(
        IMinioClient minio,
        string checkpointObjectName
    )
    {
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
                    var checkpoint = JsonSerializer.Deserialize<ProgressCheckpoint>(checkpointJson);

                    log.LogInformation("Last checkpoint: {CheckpointJson}", checkpointJson);

                    if (checkpoint is not null)
                    {
                        lastProcessedFile = checkpoint.LastProcessedObjectUrl;
                    }
                    else
                    {
                        log.LogError("Failed to read checkpoint file: deserialized object is null");
                    }

                    await stream.DisposeAsync();
                }
            );

        await minio.GetObjectAsync(getArgs);
        return lastProcessedFile;
    }

    private async Task<List<Item>> GetItemsToProcessAfterCheckpointAsync(
        IMinioClient minio,
        List<Item> allItems,
        string checkpointObjectName
    )
    {
        log.LogInformation(
            "Reading last checkpoint file from {CheckpointObjectName}",
            checkpointObjectName
        );

        var lastProcessedFile = string.Empty;

        try
        {
            lastProcessedFile = await GetLastProcessedFileFromCheckpointAsync(
                minio,
                checkpointObjectName
            );
        }
        catch (ObjectNotFoundException e)
        {
            log.LogWarning(e, "Checkpoint object not found. Returning all objects to process.");
            return allItems;
        }

        if (string.IsNullOrEmpty(lastProcessedFile))
        {
            throw new InvalidDataException(
                "Failed to read last processed file. Contents are null or empty."
            );
        }

        log.LogInformation("Continuing after {LastProcessedFile}", lastProcessedFile);

        var itemsAfterCheckpoint = allItems
            .SkipWhile(item => $"s3://{S3BucketName}/{item.Key}" != lastProcessedFile)
            // SkipWhile stops if we reach the lastProcessedFile, but includes the entry itself in the
            // result, so we need to skip that as well.
            // Ideally, we'd say `SkipWhile(item.key-timestamp <= lastProcessedFile-timestamp)`.
            .Skip(1)
            .ToList();

        return itemsAfterCheckpoint;
    }

    private async Task<string> PutMergedBundleAsync(
        IMinioClient minio,
        IDictionary<string, string> mergedBundle
    )
    {
        var objectName =
            $"{S3ObjectNamePrefix}{ImportResourceType}/_merged/bundle-{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}.ndjson";

        // a fairly naive in-memory implementation
        using var memoryStream = new MemoryStream();
        using var writer = new StreamWriter(memoryStream, Encoding.UTF8);

        foreach (var kvp in mergedBundle)
        {
            await writer.WriteLineAsync(kvp.Value);
        }

        await writer.FlushAsync();

        log.LogInformation(
            "Uploading merged bundle with size {SizeInBytes} B as {ObjectName} to {S3BucketName}",
            memoryStream.Length,
            objectName,
            S3BucketName
        );

        memoryStream.Position = 0;

        var putArgs = new PutObjectArgs()
            .WithBucket(S3BucketName)
            .WithObject(objectName)
            .WithContentType("application/x-ndjson")
            .WithStreamData(memoryStream)
            .WithObjectSize(memoryStream.Length);

        if (!IsDryRun)
        {
            await RetryPipeline.ExecuteAsync(async token =>
            {
                await minio.PutObjectAsync(putArgs, token);
            });
        }
        else
        {
            log.LogInformation(
                "Running in dry-run mode. Not putting the merged bundle back in storage."
            );
        }

        return objectName;
    }
}
