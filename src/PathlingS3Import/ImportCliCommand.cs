using System.Diagnostics;
using System.Reactive.Linq;
using System.Text;
using DotMake.CommandLine;
using Hl7.Fhir.Model;
using Hl7.Fhir.Rest;
using Hl7.Fhir.Serialization;
using Microsoft.Extensions.Logging;
using Minio;
using Minio.DataModel.Args;
using Polly;
using Polly.Retry;
using Task = System.Threading.Tasks.Task;

namespace PathlingS3Import;

// Create a simple class like this to define your root command:
[CliCommand(Description = "The import command")]
public class ImportCliCommand
{
    private readonly ILogger<ImportCliCommand> log;

    public ImportCliCommand()
    {
        using var loggerFactory = LoggerFactory.Create(builder =>
            builder.AddSimpleConsole(options =>
            {
                options.IncludeScopes = true;
                options.SingleLine = true;
                options.TimestampFormat = "yyyy-MM-dd HH:mm:ss ";
            })
        );

        log = loggerFactory.CreateLogger<ImportCliCommand>();
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

        DoAsync(minio, fhirClient, retryPipeline).Wait();
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

        var observable =
            minio.ListObjectsAsync(listArgs)
            ?? throw new InvalidOperationException("observable for listing buckets is null");

        var allObjects = new List<Minio.DataModel.Item>();

        using (log.BeginScope("[Listing objects in {ObjectsPath}]", $"{S3BucketName}/{prefix}"))
        {
            using var subscription = observable.Subscribe(
                item =>
                {
                    log.LogInformation(
                        "Got object: {ItemKey} (IsDir: {IsDir})",
                        item.Key,
                        item.IsDir
                    );
                    if (!item.IsDir && item.Key.EndsWith(".ndjson"))
                    {
                        allObjects.Add(item);
                    }
                },
                ex => log.LogError(ex, "Listing objects failed"),
                () => log.LogInformation("Finished listing.")
            );

            observable.Wait();
        }

        log.LogInformation("Found a total of {ObjectCount} matching objects.", allObjects.Count);

        var allObjectsSorted = allObjects.OrderBy(o => o.Key);

        var currentProgressObjectName = $"{prefix}pathling-s3-importer-last-imported.txt";

        log.LogInformation(
            "Name of the current progress tracker object set to {CurrentProgressObjectName}.",
            currentProgressObjectName
        );

        var stopwatch = new Stopwatch();
        var importedCount = 0;

        foreach (var item in allObjectsSorted)
        {
            var objectUrl = $"s3://{S3BucketName}/{item.Key}";

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

                    log.LogInformation("{ImportResponse}", response.ToJson());
                    log.LogInformation("Import took {ImportDuration}", stopwatch.Elapsed);

                    var bytes = Encoding.UTF8.GetBytes(objectUrl);
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

                importedCount++;
                log.LogInformation(
                    "Imported {ImportedCount} / {AllObjectsCount}",
                    importedCount,
                    allObjects.Count
                );
            }
        }
    }
}
