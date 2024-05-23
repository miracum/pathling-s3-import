using System.Reactive.Linq;
using System.Text;
using System.Text.Json;
using DotMake.CommandLine;
using Hl7.Fhir.Model;
using Hl7.Fhir.Serialization;
using Microsoft.Extensions.Logging;
using Minio;
using Minio.DataModel.Args;

namespace PathlingS3Import;

[CliCommand(
    Description = "Merges all FHIR resources in NDJSON files into larger ones up to the given size",
    Parent = typeof(RootCommand)
)]
public partial class MergeCommand : CommandBase
{
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

    public MergeCommand()
    {
        log = LogFactory.CreateLogger<MergeCommand>();
    }

    public async System.Threading.Tasks.Task RunAsync(CliContext context)
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
            await minio.ListObjectsAsync(listArgs).ToList()
            ?? throw new InvalidOperationException("observable for listing buckets is null");

        log.LogInformation(
            "Found a total of {ObjectCount} matching objects. Ordering by timestamp ascending",
            allObjects.Count
        );

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

        var currentMergedResources = new Dictionary<string, string>();

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
                                resourceCountInFile++;
                            }
                        }
                    );
                await minio.GetObjectAsync(getArgs);

                log.LogInformation(
                    "{ObjectUrl} contains {ResourceCount} resources",
                    objectUrl,
                    resourceCountInFile
                );

                if (currentMergedResources.Count >= MaxMergedBundleSize)
                {
                    log.LogInformation(
                        "Created merged bundle of {Count} resources",
                        currentMergedResources.Count
                    );

                    await PutMergedBundleAsync(minio, currentMergedResources);
                    currentMergedResources.Clear();
                }
            }
        }

        if (currentMergedResources.Count > 0)
        {
            log.LogInformation(
                "Resources not reaching threshold: {Count}",
                currentMergedResources.Count
            );

            await PutMergedBundleAsync(minio, currentMergedResources);
            currentMergedResources.Clear();
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
            writer.WriteLine(kvp.Value);
        }

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
