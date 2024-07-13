using DotMake.CommandLine;

namespace PathlingS3Import;

// Create a simple class like this to define your root command:
[CliCommand(
    Description = "Utility for importing resources from object storage to a Pathling server"
)]
public partial class RootCommand : CommandBase
{
    public RootCommand() { }

    public void Run(CliContext context) => context.ShowValues();
}
