using System.CommandLine;
using DogdouSpec.Core.Diagnostics;
using DogdouSpec.Core.Formatting;
using DogdouSpec.Core.Requirements;
using DogdouSpec.Core.Security;
using DogdouSpec.Core.Validation;
using DogdouSpec.Core.Workspace;

namespace DogdouSpec.Cli.Commands;

public static class RequirementCommand
{
    public static Command BuildCommand()
    {
        var reqCmd = new Command("requirement", "Manage and propose requirements in DogdouSpec workspace");

        reqCmd.Add(BuildProposeCommand());

        return reqCmd;
    }

    private static Command BuildProposeCommand()
    {
        var proposeCmd = new Command("propose", "Propose a new requirement in spec.xml with status='proposed' (mutating)");

        var iterationOption = new Option<string?>("--iteration")
        {
            Description = "Iteration identifier following YYYYMMDD-name grammar",
            Required = true
        };

        var expectedRevisionOption = new Option<int?>("--expected-revision")
        {
            Description = "Expected positive integer revision of the target spec.xml document",
            Required = true
        };

        var stdinOption = new Option<bool>("--stdin")
        {
            Description = "Read requirement-propose XML request from standard input"
        };

        var fileOption = new Option<string?>("--file")
        {
            Description = "Path to file containing requirement-propose XML request"
        };

        var workspaceRootOption = new Option<string?>("--workspace-root")
        {
            Description = "Explicit path to workspace root or project directory containing .dogdouspec"
        };

        var formatOption = new Option<string?>("--format")
        {
            Description = "Output format (xml or human)"
        };
        formatOption.AcceptOnlyFromAmong("xml", "human");

        proposeCmd.Add(iterationOption);
        proposeCmd.Add(expectedRevisionOption);
        proposeCmd.Add(stdinOption);
        proposeCmd.Add(fileOption);
        proposeCmd.Add(workspaceRootOption);
        proposeCmd.Add(formatOption);

        proposeCmd.SetAction(parseResult =>
        {
            var iterationId = parseResult.GetValue(iterationOption);
            var expectedRevision = parseResult.GetValue(expectedRevisionOption);
            var hasStdin = parseResult.GetValue(stdinOption);
            var filePath = parseResult.GetValue(fileOption);
            var workspaceRoot = parseResult.GetValue(workspaceRootOption);
            var formatArg = parseResult.GetValue(formatOption);
            var format = WorkspaceCommand.ResolveFormat(formatArg);

            if (hasStdin && !string.IsNullOrWhiteSpace(filePath))
            {
                var envelope = new DiagnosticsEnvelope("requirement propose", Diagnostic.Error(
                    DiagnosticCodes.InvalidArgument,
                    "Specify either --stdin or --file, not both."));
                Console.Error.Write(envelope.Format(format));
                return 2;
            }

            if (!hasStdin && string.IsNullOrWhiteSpace(filePath))
            {
                var envelope = new DiagnosticsEnvelope("requirement propose", Diagnostic.Error(
                    DiagnosticCodes.InvalidArgument,
                    "Either --stdin or --file must be specified."));
                Console.Error.Write(envelope.Format(format));
                return 2;
            }

            string requestXml;
            if (hasStdin)
            {
                requestXml = Console.In.ReadToEnd();
            }
            else
            {
                if (!File.Exists(filePath))
                {
                    var envelope = new DiagnosticsEnvelope("requirement propose", Diagnostic.Error(
                        DiagnosticCodes.InvalidArgument,
                        $"Requirement propose XML file '{filePath}' does not exist."));
                    Console.Error.Write(envelope.Format(format));
                    return 2;
                }

                try
                {
                    requestXml = File.ReadAllText(filePath!);
                }
                catch (Exception ex)
                {
                    var envelope = new DiagnosticsEnvelope("requirement propose", Diagnostic.Error(
                        DiagnosticCodes.InvalidArgument,
                        $"Failed to read file '{filePath}': {ex.Message}"));
                    Console.Error.Write(envelope.Format(format));
                    return 2;
                }
            }

            if (string.IsNullOrWhiteSpace(iterationId))
            {
                var envelope = new DiagnosticsEnvelope("requirement propose", Diagnostic.Error(
                    DiagnosticCodes.InvalidArgument,
                    "--iteration option is required."));
                Console.Error.Write(envelope.Format(format));
                return 2;
            }

            var (isIterValid, _, iterErr) = PathSecurity.ValidateIterationId(iterationId);
            if (!isIterValid || iterErr != null)
            {
                var envelope = new DiagnosticsEnvelope("requirement propose", iterErr ?? Diagnostic.Error(
                    DiagnosticCodes.InvalidArgument,
                    $"Invalid iteration identifier '{iterationId}'."));
                Console.Error.Write(envelope.Format(format));
                return 2;
            }

            if (!expectedRevision.HasValue || expectedRevision.Value <= 0)
            {
                var envelope = new DiagnosticsEnvelope("requirement propose", Diagnostic.Error(
                    DiagnosticCodes.InvalidArgument,
                    "--expected-revision must be a positive integer."));
                Console.Error.Write(envelope.Format(format));
                return 2;
            }

            var (discoverSuccess, discoveredRoot, discoverError) = WorkspaceDiscovery.FindWorkspaceRoot(
                workspaceRoot,
                Environment.CurrentDirectory);

            if (!discoverSuccess || discoverError != null)
            {
                var envelope = new DiagnosticsEnvelope("requirement propose", discoverError!);
                Console.Error.Write(envelope.Format(format));
                return 2;
            }

            var (success, mutationEnvelope, diagnostics) = RequirementProposer.Propose(
                discoveredRoot,
                iterationId,
                expectedRevision.Value,
                requestXml);

            if (!success || diagnostics.Count > 0)
            {
                var diagEnvelope = new DiagnosticsEnvelope("requirement propose", diagnostics);
                Console.Error.Write(diagEnvelope.Format(format));
                return diagEnvelope.GetExitCode();
            }

            if (mutationEnvelope != null)
            {
                Console.Out.Write(mutationEnvelope.Format(format));
            }

            return 0;
        });

        return proposeCmd;
    }
}
