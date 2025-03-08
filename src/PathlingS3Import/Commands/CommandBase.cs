using System.Reflection;
using DotMake.CommandLine;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Console;
using Polly;
using Polly.Retry;

namespace PathlingS3Import;

public abstract partial class CommandBase
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

    [CliOption(
        Description = "If enabled, list and read all objects but don't invoke the import operation or store the progress.",
        Name = "--dry-run"
    )]
    public bool IsDryRun { get; set; } = false;

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

    [CliOption(
        Description = "If enabled, continue processing resources from the last saved checkpoint file.",
        Name = "--continue-from-last-checkpoint"
    )]
    public bool IsContinueFromLastCheckpointEnabled { get; set; } = false;

    public ILoggerFactory LogFactory { get; set; }

    public ResiliencePipeline RetryPipeline { get; set; }

    protected CommandBase()
    {
        LogFactory = LoggerFactory.Create(builder =>
            builder.AddSimpleConsole(options =>
            {
                options.IncludeScopes = true;
                options.SingleLine = true;
                options.ColorBehavior = LoggerColorBehavior.Disabled;
                options.TimestampFormat = "yyyy-MM-dd HH:mm:ss ";
            })
        );

        var log = LogFactory.CreateLogger<CommandBase>();

        var retryOptions = new RetryStrategyOptions
        {
            ShouldHandle = new PredicateBuilder().Handle<Exception>(),
            BackoffType = DelayBackoffType.Exponential,
            UseJitter = true, // Adds a random factor to the delay
            MaxRetryAttempts = 10,
            Delay = TimeSpan.FromSeconds(10),
            OnRetry = args =>
            {
                log.LogWarning(
                    args.Outcome.Exception,
                    "Retrying. Attempt: {AttemptNumber}. Duration: {Duration}.",
                    args.AttemptNumber,
                    args.Duration
                );
                // Event handlers can be asynchronous; here, we return an empty ValueTask.
                return default;
            },
        };

        RetryPipeline = new ResiliencePipelineBuilder()
            .AddRetry(retryOptions) // Add retry using the default options
            .Build(); // Builds the resilience pipeline
    }
}
