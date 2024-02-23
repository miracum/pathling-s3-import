using System.Reactive.Linq;
using DotMake.CommandLine;
using Hl7.Fhir.Model;
using Hl7.Fhir.Rest;
using Hl7.Fhir.Serialization;
using Minio;
using Minio.DataModel.Args;

namespace PathlingS3Import;

// Create a simple class like this to define your root command:
[CliCommand(Description = "The import command")]
public class ImportCliCommand
{
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

    public void Run()
    {
        DoAsync().Wait();
    }

    private async System.Threading.Tasks.Task DoAsync()
    {
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

        using var minio = new MinioClient()
            .WithEndpoint(S3Endpoint)
            .WithCredentials(S3AccessKey, S3SecretKey)
            .Build();

        var bucketExistsArgs = new BucketExistsArgs().WithBucket(S3BucketName);
        bool found = await minio.BucketExistsAsync(bucketExistsArgs);
        if (!found)
        {
            throw new ArgumentException($"Bucket {S3BucketName} doesn't exist.");
        }

        var prefix = $"{S3ObjectNamePrefix}{ImportResourceType}/";

        Console.WriteLine($"Listing objects in {S3BucketName}/{prefix}.");

        var listArgs = new ListObjectsArgs()
            .WithBucket(S3BucketName)
            .WithPrefix(prefix)
            .WithRecursive(false);

        var observable = minio.ListObjectsAsync(listArgs);

        var allObjects = new List<Minio.DataModel.Item>();

        using var subscription = observable.Subscribe(
            item =>
            {
                Console.WriteLine("OnNext: {0}", item.Key);
                allObjects.Add(item);
            },
            ex => Console.WriteLine("OnError: {0}", ex.Message),
            () => Console.WriteLine("Finished listing.")
        );

        observable.Wait();

        foreach (var item in allObjects)
        {
            var objectUrl = $"s3://{S3BucketName}/{item.Key}";

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

            Console.WriteLine(importParameters.ToJson());

            Console.WriteLine($"Running {PathlingServerBaseUrl}/$import for {objectUrl}");

            var response = await fhirClient.WholeSystemOperationAsync("import", importParameters);
            Console.WriteLine(response.ToJson());
        }
    }
}
