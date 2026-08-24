using System.CommandLine;
using DogdouSpec.Core.Changes;
using DogdouSpec.Core.Diagnostics;
using DogdouSpec.Core.Formatting;
using DogdouSpec.Core.Security;
using DogdouSpec.Core.Validation;
using DogdouSpec.Core.Workspace;

namespace DogdouSpec.Cli.Commands;

public static class ChangeCommand
{
    public static Command BuildCommand()
    {
        var changeCmd = new Command("change", "Manage requirement and task changes across an active iteration");

        changeCmd.Add(BuildProposeCommand());
        changeCmd.Add(BuildApplyCommand());

        return changeCmd;
    }

    private static Command BuildProposeCommand()
    {
        var proposeCmd = new Command("propose", "Propose an in-flight requirement or task change across spec.xml and tasks.xml (mutating)");

        var iterationOption = new Option<string?>("--iteration")
        {
            Description = "Iteration identifier following YYYYMMDD-name grammar",
            Required = true
        };

        var expectedSpecRevisionOption = new Option<int?>("--expected-spec-revision")
        {
            Description = "Expected positive integer revision of the target spec.xml document",
            Required = true
        };

        var expectedTasksRevisionOption = new Option<int?>("--expected-tasks-revision")
        {
            Description = "Expected positive integer revision of the target tasks.xml document",
            Required = true
        };

        var stdinOption = new Option<bool>("--stdin")
        {
            Description = "Read change-propose XML request from standard input"
        };

        var fileOption = new Option<string?>("--file")
        {
            Description = "Path to file containing change-propose XML request"
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
        proposeCmd.Add(expectedSpecRevisionOption);
        proposeCmd.Add(expectedTasksRevisionOption);
        proposeCmd.Add(stdinOption);
        proposeCmd.Add(fileOption);
        proposeCmd.Add(workspaceRootOption);
        proposeCmd.Add(formatOption);

        proposeCmd.SetAction(parseResult =>
        {
            var iterationId = parseResult.GetValue(iterationOption);
            var expectedSpecRevision = parseResult.GetValue(expectedSpecRevisionOption);
            var expectedTasksRevision = parseResult.GetValue(expectedTasksRevisionOption);
            var hasStdin = parseResult.GetValue(stdinOption);
            var filePath = parseResult.GetValue(fileOption);
            var workspaceRoot = parseResult.GetValue(workspaceRootOption);
            var formatArg = parseResult.GetValue(formatOption);
            var format = WorkspaceCommand.ResolveFormat(formatArg);

            if (hasStdin && !string.IsNullOrWhiteSpace(filePath))
            {
                var envelope = new DiagnosticsEnvelope("change propose", Diagnostic.Error(
                    DiagnosticCodes.InvalidArgument,
                    "Specify either --stdin or --file, not both."));
                Console.Error.Write(envelope.Format(format));
                return 2;
            }

            if (!hasStdin && string.IsNullOrWhiteSpace(filePath))
            {
                var envelope = new DiagnosticsEnvelope("change propose", Diagnostic.Error(
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
                    var envelope = new DiagnosticsEnvelope("change propose", Diagnostic.Error(
                        DiagnosticCodes.InvalidArgument,
                        $"Change propose XML file '{filePath}' does not exist."));
                    Console.Error.Write(envelope.Format(format));
                    return 2;
                }

                try
                {
                    requestXml = File.ReadAllText(filePath!);
                }
                catch (Exception ex)
                {
                    var envelope = new DiagnosticsEnvelope("change propose", Diagnostic.Error(
                        DiagnosticCodes.InvalidArgument,
                        $"Failed to read file '{filePath}': {ex.Message}"));
                    Console.Error.Write(envelope.Format(format));
                    return 2;
                }
            }

            if (string.IsNullOrWhiteSpace(iterationId))
            {
                var envelope = new DiagnosticsEnvelope("change propose", Diagnostic.Error(
                    DiagnosticCodes.InvalidArgument,
                    "--iteration option is required."));
                Console.Error.Write(envelope.Format(format));
                return 2;
            }

            var (isIterValid, _, iterErr) = PathSecurity.ValidateIterationId(iterationId);
            if (!isIterValid || iterErr != null)
            {
                var envelope = new DiagnosticsEnvelope("change propose", iterErr ?? Diagnostic.Error(
                    DiagnosticCodes.InvalidArgument,
                    $"Invalid iteration identifier '{iterationId}'."));
                Console.Error.Write(envelope.Format(format));
                return 2;
            }

            if (!expectedSpecRevision.HasValue || expectedSpecRevision.Value <= 0)
            {
                var envelope = new DiagnosticsEnvelope("change propose", Diagnostic.Error(
                    DiagnosticCodes.InvalidArgument,
                    "--expected-spec-revision must be a positive integer."));
                Console.Error.Write(envelope.Format(format));
                return 2;
            }

            if (!expectedTasksRevision.HasValue || expectedTasksRevision.Value <= 0)
            {
                var envelope = new DiagnosticsEnvelope("change propose", Diagnostic.Error(
                    DiagnosticCodes.InvalidArgument,
                    "--expected-tasks-revision must be a positive integer."));
                Console.Error.Write(envelope.Format(format));
                return 2;
            }

            var (discoverSuccess, discoveredRoot, discoverError) = WorkspaceDiscovery.FindWorkspaceRoot(
                workspaceRoot,
                Environment.CurrentDirectory);

            if (!discoverSuccess || discoverError != null)
            {
                var envelope = new DiagnosticsEnvelope("change propose", discoverError!);
                Console.Error.Write(envelope.Format(format));
                return 2;
            }

            var (success, mutationEnvelope, diagnostics) = ChangeProposer.Propose(
                discoveredRoot,
                iterationId,
                expectedSpecRevision.Value,
                expectedTasksRevision.Value,
                requestXml);

            if (!success || diagnostics.Count > 0)
            {
                var diagEnvelope = new DiagnosticsEnvelope("change propose", diagnostics);
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

    private static Command BuildApplyCommand()
    {
        var applyCmd = new Command("apply", "Apply approved change adjustments and task dispositions during replanning (mutating)");

        var iterationOption = new Option<string?>("--iteration")
        {
            Description = "Iteration identifier following YYYYMMDD-name grammar",
            Required = true
        };

        var expectedSpecRevisionOption = new Option<int?>("--expected-spec-revision")
        {
            Description = "Expected positive integer revision of the target spec.xml document",
            Required = true
        };

        var expectedTasksRevisionOption = new Option<int?>("--expected-tasks-revision")
        {
            Description = "Expected positive integer revision of the target tasks.xml document",
            Required = true
        };

        var stdinOption = new Option<bool>("--stdin")
        {
            Description = "Read change-apply XML request from standard input"
        };

        var fileOption = new Option<string?>("--file")
        {
            Description = "Path to file containing change-apply XML request"
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

        applyCmd.Add(iterationOption);
        applyCmd.Add(expectedSpecRevisionOption);
        applyCmd.Add(expectedTasksRevisionOption);
        applyCmd.Add(stdinOption);
        applyCmd.Add(fileOption);
        applyCmd.Add(workspaceRootOption);
        applyCmd.Add(formatOption);

        applyCmd.SetAction(parseResult =>
        {
            var iterationId = parseResult.GetValue(iterationOption);
            var expectedSpecRevision = parseResult.GetValue(expectedSpecRevisionOption);
            var expectedTasksRevision = parseResult.GetValue(expectedTasksRevisionOption);
            var hasStdin = parseResult.GetValue(stdinOption);
            var filePath = parseResult.GetValue(fileOption);
            var workspaceRoot = parseResult.GetValue(workspaceRootOption);
            var formatArg = parseResult.GetValue(formatOption);
            var format = WorkspaceCommand.ResolveFormat(formatArg);

            if (hasStdin && !string.IsNullOrWhiteSpace(filePath))
            {
                var envelope = new DiagnosticsEnvelope("change apply", Diagnostic.Error(
                    DiagnosticCodes.InvalidArgument,
                    "Specify either --stdin or --file, not both."));
                Console.Error.Write(envelope.Format(format));
                return 2;
            }

            if (!hasStdin && string.IsNullOrWhiteSpace(filePath))
            {
                var envelope = new DiagnosticsEnvelope("change apply", Diagnostic.Error(
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
                    var envelope = new DiagnosticsEnvelope("change apply", Diagnostic.Error(
                        DiagnosticCodes.InvalidArgument,
                        $"Change apply XML file '{filePath}' does not exist."));
                    Console.Error.Write(envelope.Format(format));
                    return 2;
                }

                try
                {
                    requestXml = File.ReadAllText(filePath!);
                }
                catch (Exception ex)
                {
                    var envelope = new DiagnosticsEnvelope("change apply", Diagnostic.Error(
                        DiagnosticCodes.InvalidArgument,
                        $"Failed to read file '{filePath}': {ex.Message}"));
                    Console.Error.Write(envelope.Format(format));
                    return 2;
                }
            }

            if (string.IsNullOrWhiteSpace(iterationId))
            {
                var envelope = new DiagnosticsEnvelope("change apply", Diagnostic.Error(
                    DiagnosticCodes.InvalidArgument,
                    "--iteration option is required."));
                Console.Error.Write(envelope.Format(format));
                return 2;
            }

            var (isIterValid, _, iterErr) = PathSecurity.ValidateIterationId(iterationId);
            if (!isIterValid || iterErr != null)
            {
                var envelope = new DiagnosticsEnvelope("change apply", iterErr ?? Diagnostic.Error(
                    DiagnosticCodes.InvalidArgument,
                    $"Invalid iteration identifier '{iterationId}'."));
                Console.Error.Write(envelope.Format(format));
                return 2;
            }

            if (!expectedSpecRevision.HasValue || expectedSpecRevision.Value <= 0)
            {
                var envelope = new DiagnosticsEnvelope("change apply", Diagnostic.Error(
                    DiagnosticCodes.InvalidArgument,
                    "--expected-spec-revision must be a positive integer."));
                Console.Error.Write(envelope.Format(format));
                return 2;
            }

            if (!expectedTasksRevision.HasValue || expectedTasksRevision.Value <= 0)
            {
                var envelope = new DiagnosticsEnvelope("change apply", Diagnostic.Error(
                    DiagnosticCodes.InvalidArgument,
                    "--expected-tasks-revision must be a positive integer."));
                Console.Error.Write(envelope.Format(format));
                return 2;
            }

            var (discoverSuccess, discoveredRoot, discoverError) = WorkspaceDiscovery.FindWorkspaceRoot(
                workspaceRoot,
                Environment.CurrentDirectory);

            if (!discoverSuccess || discoverError != null)
            {
                var envelope = new DiagnosticsEnvelope("change apply", discoverError!);
                Console.Error.Write(envelope.Format(format));
                return 2;
            }

            var (success, mutationEnvelope, diagnostics) = ChangeApplier.Apply(
                discoveredRoot,
                iterationId,
                expectedSpecRevision.Value,
                expectedTasksRevision.Value,
                requestXml);

            if (!success || diagnostics.Count > 0)
            {
                var diagEnvelope = new DiagnosticsEnvelope("change apply", diagnostics);
                Console.Error.Write(diagEnvelope.Format(format));
                return diagEnvelope.GetExitCode();
            }

            if (mutationEnvelope != null)
            {
                Console.Out.Write(mutationEnvelope.Format(format));
            }

            return 0;
        });

        return applyCmd;
    }
}
