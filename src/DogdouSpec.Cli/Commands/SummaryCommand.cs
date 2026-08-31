using System.CommandLine;
using DogdouSpec.Core.Diagnostics;
using DogdouSpec.Core.Formatting;
using DogdouSpec.Core.Reporting;
using DogdouSpec.Core.Workspace;

namespace DogdouSpec.Cli.Commands;

public static class SummaryCommand
{
    public static Command BuildCommand()
    {
        var summaryCmd = new Command("summary", "Generate an instant progress summary card and task breakdown for active or specified iteration");

        var iterationOption = new Option<string?>("--iteration")
        {
            Description = "Iteration identifier (omitted auto-resolves active or latest candidate iteration)"
        };

        var workspaceRootOption = new Option<string?>("--workspace-root")
        {
            Description = "Explicit path to workspace root or project directory containing .dogdouspec"
        };

        var formatOption = new Option<string?>("--format")
        {
            Description = "Output format (markdown, json, xml, human)"
        };
        formatOption.AcceptOnlyFromAmong("markdown", "md", "json", "xml", "human");

        summaryCmd.Add(iterationOption);
        summaryCmd.Add(workspaceRootOption);
        summaryCmd.Add(formatOption);

        summaryCmd.SetAction(parseResult =>
        {
            var iterationId = parseResult.GetValue(iterationOption);
            var workspaceRoot = parseResult.GetValue(workspaceRootOption);
            var formatArg = parseResult.GetValue(formatOption);
            var format = ResolveSummaryFormat(formatArg);

            var (discoverSuccess, discoveredRoot, discoverError) = WorkspaceDiscovery.FindWorkspaceRoot(
                workspaceRoot,
                Environment.CurrentDirectory);

            if (!discoverSuccess || discoverError != null)
            {
                var envelope = new DiagnosticsEnvelope("summary", discoverError!);
                Console.Error.Write(envelope.Format(format == OutputFormat.Markdown || format == OutputFormat.Json ? OutputFormat.Human : format));
                return 2;
            }

            var (success, result, diagnostics) = IterationSummaryGenerator.Generate(
                discoveredRoot,
                iterationId);

            if (!success || diagnostics.Count > 0)
            {
                var envelope = new DiagnosticsEnvelope("summary", diagnostics);
                Console.Error.Write(envelope.Format(format == OutputFormat.Markdown || format == OutputFormat.Json ? OutputFormat.Human : format));
                return envelope.GetExitCode();
            }

            if (result != null)
            {
                Console.Out.Write(result.Format(format));
            }

            return 0;
        });

        return summaryCmd;
    }

    public static OutputFormat ResolveSummaryFormat(string? formatArgument)
    {
        if (string.Equals(formatArgument, "markdown", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(formatArgument, "md", StringComparison.OrdinalIgnoreCase)) return OutputFormat.Markdown;
        if (string.Equals(formatArgument, "json", StringComparison.OrdinalIgnoreCase)) return OutputFormat.Json;
        if (string.Equals(formatArgument, "xml", StringComparison.OrdinalIgnoreCase)) return OutputFormat.Xml;
        if (string.Equals(formatArgument, "human", StringComparison.OrdinalIgnoreCase)) return OutputFormat.Human;

        return OutputFormat.Human;
    }
}
