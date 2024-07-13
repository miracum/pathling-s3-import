using System.Collections.Concurrent;
using System.Reactive.Linq;
using System.Text;
using System.Text.Json;
using DotMake.CommandLine;
using Hl7.Fhir.Model;
using Hl7.Fhir.Serialization;
using Microsoft.Extensions.Logging;
using Minio;
using Minio.ApiEndpoints;
using Minio.DataModel.Args;
using Prometheus.Client;
using Prometheus.Client.Collectors;

namespace PathlingS3Import;

[CliCommand(
    Description = "Merges all FHIR resources in NDJSON files into larger ones up to the given size",
    Parent = typeof(RootCommand)
)]
public partial class MergeCommand : CommandBase
{
    private readonly IMetricFamily<ICounter, ValueTuple<string>> bundlesMergedCounter;

    private readonly ILogger<MergeCommand> log;

    private JsonSerializerOptions FhirJsonOptions { get; } =
        new JsonSerializerOptions().ForFhir(ModelInfo.ModelInspector);

    [CliOption(
        Description = "The maximum number of resources a merged bundle may contain. Bundles that are already larger than this value might be merged to files exceeding this value.",
        Required = true
    )]
    public int MaxMergedBundleSize { get; set; } = 1;

    [CliOption(
        Description = "The type of FHIR resources to merge. Sets the correct prefix for the resources folder."
    )]
    public ResourceType ResourceType { get; set; } = ResourceType.Patient;

    [CliOption(Description = "The maximum size of the merged bundle in bytes. Default: 1 GiB")]
    public int MaxMergedBundleSizeInBytes { get; set; } = 1 * 1024 * 1024 * 1024;

    public MergeCommand()
    {
        log = LogFactory.CreateLogger<MergeCommand>();

        var collectorRegistry = new CollectorRegistry();
        var metricFactory = new MetricFactory(collectorRegistry);

        bundlesMergedCounter = metricFactory.CreateCounter(
            "pathlings3import_bundles_merged_total",
            "Total number of bundles merged to larger ones by resource type.",
            "resourceType"
        );
    }

    public async System.Threading.Tasks.Task RunAsync()
    {
        log.LogInformation("Minio endpoint set to {S3Endpoint}", S3Endpoint);
        var minio = new MinioClient()
            .WithEndpoint(S3Endpoint)
            .WithCredentials(S3AccessKey, S3SecretKey)
            .Build();

        var bucketExistsArgs = new BucketExistsArgs().WithBucket(S3BucketName);
        bool found = await minio.BucketExistsAsync(bucketExistsArgs);
        if (!found)
        {
            throw new ArgumentException($"Bucket {S3BucketName} doesn't exist.");
        }

        var prefix = $"{S3ObjectNamePrefix}{ResourceType}/";

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
            "Found a total of {ObjectCount} matching objects. Ordering by timestamp ascending",
            await allObjects.CountAsync()
        );

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

        var currentMergedResources = new ConcurrentDictionary<string, string>();
        var estimatedSizeInBytes = 0;
        var processedCount = 0;
        foreach (var item in objectsToProcess)
        {
            var objectUrl = $"s3://{S3BucketName}/{item.Key}";

            using (log.BeginScope("[Merging ndjson file {NdjsonObjectUrl}]", objectUrl))
            {
                var resourceCountInFile = 0;
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
                                resourceCountInFile++;
                            }
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

                    await PutMergedBundleAsync(minio, currentMergedResources);
                    currentMergedResources.Clear();
                    estimatedSizeInBytes = 0;
                }

                processedCount++;
                bundlesMergedCounter.WithLabels(ResourceType.ToString()).Inc();
                log.LogInformation(
                    "Merged {ProcessedCount} of {ObjectsToProcess} ",
                    processedCount,
                    objectsToProcess.Count
                );
            }
        }

        if (!currentMergedResources.IsEmpty)
        {
            log.LogInformation(
                "Resources remaining: {Count}. Uploading as smaller bundle.",
                currentMergedResources.Count
            );

            await PutMergedBundleAsync(minio, currentMergedResources);
            currentMergedResources.Clear();
            estimatedSizeInBytes = 0;
        }
    }

    private async System.Threading.Tasks.Task PutMergedBundleAsync(
        IMinioClient minio,
        IDictionary<string, string> mergedBundle
    )
    {
        var objectName =
            $"{S3ObjectNamePrefix}merged/{ResourceType}/bundle-{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}.ndjson";

        // a fairly naive in-memory implementation
        using var memoryStream = new MemoryStream();
        using var writer = new StreamWriter(memoryStream, Encoding.UTF8);

        foreach (var kvp in mergedBundle)
        {
            await writer.WriteLineAsync(kvp.Value);
        }

        await writer.FlushAsync();

        log.LogInformation(
            "Uploading merged bundle with size {SizeInBytes} as {ObjectName} to {S3BucketName}",
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
    }
}
