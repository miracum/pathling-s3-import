using DotNet.Testcontainers.Builders;
using FluentAssertions;
using Hl7.Fhir.Model;
using Hl7.Fhir.Rest;
using Xunit.Abstractions;
using Task = System.Threading.Tasks.Task;

namespace PathlingS3Import.Tests.E2E;

public class Tests(ITestOutputHelper output)
{
    private readonly ITestOutputHelper output = output;

    [Fact]
    public async Task StartImportTool_WithRunningPathlingServerAndMinio_ShouldCreateExpectedNumberOfResources()
    {
        // this test requires the dev fixtures to be running on their default ports as well as
        // a PathlingS3Import image to exist.

        using var stdoutStream = new MemoryStream();
        using var stderrStream = new MemoryStream();
        using var consumer = Consume.RedirectStdoutAndStderrToStream(stdoutStream, stderrStream);

        var pathlingServerBaseUrl = "http://host.docker.internal:8082/fhir";
        var resourceType = ResourceType.Patient;

        string[] args =
        [
            "import",
            "--s3-endpoint=http://host.docker.internal:9000",
            $"--pathling-server-base-url={pathlingServerBaseUrl}",
            "--s3-access-key=admin",
            "--s3-secret-key=miniopass",
            "--s3-bucket-name=fhir",
            "--s3-object-name-prefix=staging/",
            $"--import-resource-type={resourceType}",
            "--dry-run=false"
        ];

        var testImageTag =
            Environment.GetEnvironmentVariable("PATHLING_S3_IMPORT_IMAGE_TAG") ?? "test";

        var testContainer = new ContainerBuilder()
            .WithImage($"ghcr.io/miracum/pathling-s3-import:{testImageTag}")
            .WithCommand(args)
            .WithOutputConsumer(consumer)
            .WithExtraHost("host.docker.internal", "host-gateway")
            .Build();

        await testContainer.StartAsync();

        var exitCode = await testContainer.GetExitCodeAsync();

        output.WriteLine("Test container exited");

        consumer.Stdout.Seek(0, SeekOrigin.Begin);
        using var stdoutReader = new StreamReader(consumer.Stdout);
        var stdout = stdoutReader.ReadToEnd();
        output.WriteLine(stdout);

        consumer.Stderr.Seek(0, SeekOrigin.Begin);
        using var stderrReader = new StreamReader(consumer.Stderr);
        var stderr = stderrReader.ReadToEnd();
        output.WriteLine(stderr);

        exitCode.Should().Be(0);

        // use a different base URL since this test isn't run inside
        // a container. Slightly ugly.
        using var fhirClient = new FhirClient(
            "http://localhost:8082/fhir",
            settings: new()
            {
                PreferredFormat = ResourceFormat.Json,
                Timeout = (int)TimeSpan.FromSeconds(60).TotalMilliseconds
            }
        );

        var response = await fhirClient.SearchAsync(
            resourceType.ToString(),
            summary: SummaryType.Count
        );

        response.Should().NotBeNull();
        response!.Total.Should().Be(5007);
    }
}
