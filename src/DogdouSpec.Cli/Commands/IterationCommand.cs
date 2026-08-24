using System.CommandLine;
using DogdouSpec.Core.Diagnostics;
using DogdouSpec.Core.Formatting;
using DogdouSpec.Core.Iterations;
using DogdouSpec.Core.Security;
using DogdouSpec.Core.Workspace;

namespace DogdouSpec.Cli.Commands;

public static class IterationCommand
{
    public static Command BuildCommand()
    {
        var iterationCmd = new Command("iteration", "Manage and inspect DogdouSpec iterations");

        var listCmd = BuildListCommand();
        var createCmd = BuildCreateCommand();
        var readinessCmd = BuildReadinessCommand();
        var confirmCmd = BuildConfirmCommand();

        iterationCmd.Add(listCmd);
        iterationCmd.Add(createCmd);
        iterationCmd.Add(readinessCmd);
        iterationCmd.Add(confirmCmd);

        return iterationCmd;
    }

    private static Command BuildListCommand()
    {
        var listCmd = new Command("list", "List date-prefixed iterations in workspace");

        var workspaceRootOption = new Option<string?>("--workspace-root")
        {
            Description = "Explicit path to workspace root or project directory containing .dogdouspec"
        };

        var formatOption = new Option<string?>("--format")
        {
            Description = "Output format (xml or human)"
        };
        formatOption.AcceptOnlyFromAmong("xml", "human");

        listCmd.Add(workspaceRootOption);
        listCmd.Add(formatOption);

        listCmd.SetAction(parseResult =>
        {
            var workspaceRoot = parseResult.GetValue(workspaceRootOption);
            var formatArg = parseResult.GetValue(formatOption);
            var format = WorkspaceCommand.ResolveFormat(formatArg);

            var (discoverSuccess, discoveredRoot, discoverError) = WorkspaceDiscovery.FindWorkspaceRoot(
                workspaceRoot,
                Environment.CurrentDirectory);

            if (!discoverSuccess || discoverError != null)
            {
                var envelope = new DiagnosticsEnvelope("iteration list", discoverError!);
                Console.Error.Write(envelope.Format(format));
                return 2;
            }

            var (success, result, diagnostics) = IterationLister.List(discoveredRoot);

            if (!success || diagnostics.Count > 0)
            {
                var envelope = new DiagnosticsEnvelope("iteration list", diagnostics);
                Console.Error.Write(envelope.Format(format));
                return envelope.GetExitCode();
            }

            if (result != null)
            {
                Console.Out.Write(result.Format(format));
            }

            return 0;
        });

        return listCmd;
    }

    private static Command BuildCreateCommand()
    {
        var createCmd = new Command("create", "Atomically create a new feature or research iteration (mutating)");

        var idOption = new Option<string>("--id")
        {
            Description = "Iteration identifier following YYYYMMDD-name grammar",
            Required = true
        };

        var kindOption = new Option<string>("--kind")
        {
            Description = "Iteration kind (feature or research)",
            Required = true
        };
        kindOption.AcceptOnlyFromAmong("feature", "research");

        var workspaceRootOption = new Option<string?>("--workspace-root")
        {
            Description = "Explicit path to workspace root or project directory containing .dogdouspec"
        };

        var formatOption = new Option<string?>("--format")
        {
            Description = "Output format (xml or human)"
        };
        formatOption.AcceptOnlyFromAmong("xml", "human");

        createCmd.Add(idOption);
        createCmd.Add(kindOption);
        createCmd.Add(workspaceRootOption);
        createCmd.Add(formatOption);

        createCmd.SetAction(parseResult =>
        {
            var id = parseResult.GetValue(idOption);
            var kind = parseResult.GetValue(kindOption);
            var workspaceRoot = parseResult.GetValue(workspaceRootOption);
            var formatArg = parseResult.GetValue(formatOption);
            var format = WorkspaceCommand.ResolveFormat(formatArg);

            if (string.IsNullOrWhiteSpace(id))
            {
                var envelope = new DiagnosticsEnvelope("iteration create", Diagnostic.Error(
                    DiagnosticCodes.InvalidArgument,
                    "--id option is required."));
                Console.Error.Write(envelope.Format(format));
                return 2;
            }

            if (string.IsNullOrWhiteSpace(kind))
            {
                var envelope = new DiagnosticsEnvelope("iteration create", Diagnostic.Error(
                    DiagnosticCodes.InvalidArgument,
                    "--kind option is required."));
                Console.Error.Write(envelope.Format(format));
                return 2;
            }

            var (isIdValid, _, idGrammarError) = PathSecurity.ValidateIterationId(id);
            if (!isIdValid || idGrammarError != null)
            {
                var envelope = new DiagnosticsEnvelope("iteration create", idGrammarError!);
                Console.Error.Write(envelope.Format(format));
                return 2;
            }

            var (discoverSuccess, discoveredRoot, discoverError) = WorkspaceDiscovery.FindWorkspaceRoot(
                workspaceRoot,
                Environment.CurrentDirectory);

            if (!discoverSuccess || discoverError != null)
            {
                var envelope = new DiagnosticsEnvelope("iteration create", discoverError!);
                Console.Error.Write(envelope.Format(format));
                return 2;
            }

            var (success, envelopeResult, diagnostics) = IterationCreator.Create(
                discoveredRoot,
                id,
                kind);

            if (!success || diagnostics.Count > 0)
            {
                var diagEnvelope = new DiagnosticsEnvelope("iteration create", diagnostics);
                Console.Error.Write(diagEnvelope.Format(format));
                return diagEnvelope.GetExitCode();
            }

            if (envelopeResult != null)
            {
                Console.Out.Write(envelopeResult.Format(format));
            }

            return 0;
        });

        return createCmd;
    }

    private static Command BuildReadinessCommand()
    {
        var readinessCmd = new Command("readiness", "Assess and report iteration technical readiness for activation or completion phase");

        var iterationOption = new Option<string>("--iteration")
        {
            Description = "Iteration identifier following YYYYMMDD-name grammar",
            Required = true
        };

        var phaseOption = new Option<string>("--phase")
        {
            Description = "Readiness phase to evaluate (activation or completion)",
            Required = true
        };
        phaseOption.AcceptOnlyFromAmong("activation", "completion");

        var workspaceRootOption = new Option<string?>("--workspace-root")
        {
            Description = "Explicit path to workspace root or project directory containing .dogdouspec"
        };

        var formatOption = new Option<string?>("--format")
        {
            Description = "Output format (xml or human)"
        };
        formatOption.AcceptOnlyFromAmong("xml", "human");

        readinessCmd.Add(iterationOption);
        readinessCmd.Add(phaseOption);
        readinessCmd.Add(workspaceRootOption);
        readinessCmd.Add(formatOption);

        readinessCmd.SetAction(parseResult =>
        {
            var iterationId = parseResult.GetValue(iterationOption);
            var phase = parseResult.GetValue(phaseOption);
            var workspaceRoot = parseResult.GetValue(workspaceRootOption);
            var formatArg = parseResult.GetValue(formatOption);
            var format = WorkspaceCommand.ResolveFormat(formatArg);

            if (string.IsNullOrWhiteSpace(iterationId))
            {
                var envelope = new DiagnosticsEnvelope("iteration readiness", Diagnostic.Error(
                    DiagnosticCodes.InvalidArgument,
                    "--iteration option is required."));
                Console.Error.Write(envelope.Format(format));
                return 2;
            }

            if (string.IsNullOrWhiteSpace(phase))
            {
                var envelope = new DiagnosticsEnvelope("iteration readiness", Diagnostic.Error(
                    DiagnosticCodes.InvalidArgument,
                    "--phase option is required."));
                Console.Error.Write(envelope.Format(format));
                return 2;
            }

            var (discoverSuccess, discoveredRoot, discoverError) = WorkspaceDiscovery.FindWorkspaceRoot(
                workspaceRoot,
                Environment.CurrentDirectory);

            if (!discoverSuccess || discoverError != null)
            {
                var envelope = new DiagnosticsEnvelope("iteration readiness", discoverError!);
                Console.Error.Write(envelope.Format(format));
                return 2;
            }

            var (success, result, diagnostics) = IterationReadiness.Assess(
                discoveredRoot,
                iterationId,
                phase);

            if (!success || diagnostics.Count > 0)
            {
                var envelope = new DiagnosticsEnvelope("iteration readiness", diagnostics);
                Console.Error.Write(envelope.Format(format));
                return envelope.GetExitCode();
            }

            if (result != null)
            {
                Console.Out.Write(result.Format(format));
            }

            return 0;
        });

        return readinessCmd;
    }

    private static Command BuildConfirmCommand()
    {
        var confirmCmd = new Command("confirm", "Atomically confirm iteration product decisions and lifecycle (mutating)");

        var stdinOption = new Option<bool>("--stdin")
        {
            Description = "Read iteration-confirmation XML request from standard input"
        };

        var fileOption = new Option<string?>("--file")
        {
            Description = "Path to file containing iteration-confirmation XML request"
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

        confirmCmd.Add(stdinOption);
        confirmCmd.Add(fileOption);
        confirmCmd.Add(workspaceRootOption);
        confirmCmd.Add(formatOption);

        confirmCmd.SetAction(parseResult =>
        {
            var hasStdin = parseResult.GetValue(stdinOption);
            var filePath = parseResult.GetValue(fileOption);
            var workspaceRoot = parseResult.GetValue(workspaceRootOption);
            var formatArg = parseResult.GetValue(formatOption);
            var format = WorkspaceCommand.ResolveFormat(formatArg);

            if (hasStdin && !string.IsNullOrWhiteSpace(filePath))
            {
                var envelope = new DiagnosticsEnvelope("iteration confirm", Diagnostic.Error(
                    DiagnosticCodes.InvalidArgument,
                    "Specify either --stdin or --file, not both."));
                Console.Error.Write(envelope.Format(format));
                return 2;
            }

            if (!hasStdin && string.IsNullOrWhiteSpace(filePath))
            {
                var envelope = new DiagnosticsEnvelope("iteration confirm", Diagnostic.Error(
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
                    var envelope = new DiagnosticsEnvelope("iteration confirm", Diagnostic.Error(
                        DiagnosticCodes.InvalidArgument,
                        $"Iteration confirmation XML file '{filePath}' does not exist."));
                    Console.Error.Write(envelope.Format(format));
                    return 2;
                }

                try
                {
                    requestXml = File.ReadAllText(filePath!);
                }
                catch (Exception ex)
                {
                    var envelope = new DiagnosticsEnvelope("iteration confirm", Diagnostic.Error(
                        DiagnosticCodes.InvalidArgument,
                        $"Failed to read file '{filePath}': {ex.Message}"));
                    Console.Error.Write(envelope.Format(format));
                    return 2;
                }
            }

            var (discoverSuccess, discoveredRoot, discoverError) = WorkspaceDiscovery.FindWorkspaceRoot(
                workspaceRoot,
                Environment.CurrentDirectory);

            if (!discoverSuccess || discoverError != null)
            {
                var envelope = new DiagnosticsEnvelope("iteration confirm", discoverError!);
                Console.Error.Write(envelope.Format(format));
                return 2;
            }

            var (success, envelopeResult, diagnostics) = IterationConfirmer.Confirm(
                discoveredRoot,
                requestXml);

            if (!success || diagnostics.Count > 0)
            {
                var diagEnvelope = new DiagnosticsEnvelope("iteration confirm", diagnostics);
                Console.Error.Write(diagEnvelope.Format(format));
                return diagEnvelope.GetExitCode();
            }

            if (envelopeResult != null)
            {
                Console.Out.Write(envelopeResult.Format(format));
            }

            return 0;
        });

        return confirmCmd;
    }
}
