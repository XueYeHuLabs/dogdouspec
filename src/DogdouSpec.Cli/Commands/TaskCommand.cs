using System.CommandLine;
using DogdouSpec.Core.Diagnostics;
using DogdouSpec.Core.Formatting;
using DogdouSpec.Core.Security;
using DogdouSpec.Core.Tasks;
using DogdouSpec.Core.Validation;
using DogdouSpec.Core.Workspace;

namespace DogdouSpec.Cli.Commands;

public static class TaskCommand
{
    public static Command BuildCommand()
    {
        var taskCmd = new Command("task", "Manage and update tasks in DogdouSpec workspace");

        var updateCmd = BuildUpdateCommand();
        taskCmd.Add(updateCmd);

        return taskCmd;
    }

    private static Command BuildUpdateCommand()
    {
        var updateCmd = new Command("update", "Atomically update a task state, criteria, context, and records (mutating)");

        var iterationOption = new Option<string?>("--iteration")
        {
            Description = "Iteration identifier following YYYYMMDD-name grammar",
            Required = true
        };

        var taskOption = new Option<string?>("--task")
        {
            Description = "Task identifier following time-first grammar",
            Required = true
        };

        var expectedRevisionOption = new Option<int?>("--expected-revision")
        {
            Description = "Expected positive integer revision of the target tasks.xml document",
            Required = true
        };

        var stdinOption = new Option<bool>("--stdin")
        {
            Description = "Read task-update XML request from standard input"
        };

        var fileOption = new Option<string?>("--file")
        {
            Description = "Path to file containing task-update XML request"
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

        updateCmd.Add(iterationOption);
        updateCmd.Add(taskOption);
        updateCmd.Add(expectedRevisionOption);
        updateCmd.Add(stdinOption);
        updateCmd.Add(fileOption);
        updateCmd.Add(workspaceRootOption);
        updateCmd.Add(formatOption);

        updateCmd.SetAction(parseResult =>
        {
            var iterationId = parseResult.GetValue(iterationOption);
            var taskId = parseResult.GetValue(taskOption);
            var expectedRevision = parseResult.GetValue(expectedRevisionOption);
            var hasStdin = parseResult.GetValue(stdinOption);
            var filePath = parseResult.GetValue(fileOption);
            var workspaceRoot = parseResult.GetValue(workspaceRootOption);
            var formatArg = parseResult.GetValue(formatOption);
            var format = WorkspaceCommand.ResolveFormat(formatArg);

            // 1. Validate stdin vs file (mutually exclusive, exactly one required)
            if (hasStdin && !string.IsNullOrWhiteSpace(filePath))
            {
                var envelope = new DiagnosticsEnvelope("task update", Diagnostic.Error(
                    DiagnosticCodes.InvalidArgument,
                    "Specify either --stdin or --file, not both."));
                Console.Error.Write(envelope.Format(format));
                return 2;
            }

            if (!hasStdin && string.IsNullOrWhiteSpace(filePath))
            {
                var envelope = new DiagnosticsEnvelope("task update", Diagnostic.Error(
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
                    var envelope = new DiagnosticsEnvelope("task update", Diagnostic.Error(
                        DiagnosticCodes.InvalidArgument,
                        $"Task update XML file '{filePath}' does not exist."));
                    Console.Error.Write(envelope.Format(format));
                    return 2;
                }

                try
                {
                    requestXml = File.ReadAllText(filePath!);
                }
                catch (Exception ex)
                {
                    var envelope = new DiagnosticsEnvelope("task update", Diagnostic.Error(
                        DiagnosticCodes.InvalidArgument,
                        $"Failed to read file '{filePath}': {ex.Message}"));
                    Console.Error.Write(envelope.Format(format));
                    return 2;
                }
            }

            if (string.IsNullOrWhiteSpace(iterationId))
            {
                var envelope = new DiagnosticsEnvelope("task update", Diagnostic.Error(
                    DiagnosticCodes.InvalidArgument,
                    "--iteration option is required."));
                Console.Error.Write(envelope.Format(format));
                return 2;
            }

            var (isIterValid, _, iterErr) = PathSecurity.ValidateIterationId(iterationId);
            if (!isIterValid || iterErr != null)
            {
                var envelope = new DiagnosticsEnvelope("task update", iterErr ?? Diagnostic.Error(
                    DiagnosticCodes.InvalidArgument,
                    $"Invalid iteration identifier '{iterationId}'."));
                Console.Error.Write(envelope.Format(format));
                return 2;
            }

            if (string.IsNullOrWhiteSpace(taskId))
            {
                var envelope = new DiagnosticsEnvelope("task update", Diagnostic.Error(
                    DiagnosticCodes.InvalidArgument,
                    "--task option is required."));
                Console.Error.Write(envelope.Format(format));
                return 2;
            }

            if (!ProjectSemanticIndex.IsValidTimeFirstId(taskId))
            {
                var envelope = new DiagnosticsEnvelope("task update", Diagnostic.Error(
                    DiagnosticCodes.InvalidIdGrammar,
                    $"Task identifier '{taskId}' does not conform to the time-first ID grammar (YYYYMMDD-name or YYYYMMDDThhmmssZ-name)."));
                Console.Error.Write(envelope.Format(format));
                return 2;
            }

            if (!expectedRevision.HasValue || expectedRevision.Value <= 0)
            {
                var envelope = new DiagnosticsEnvelope("task update", Diagnostic.Error(
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
                var envelope = new DiagnosticsEnvelope("task update", discoverError!);
                Console.Error.Write(envelope.Format(format));
                return 2;
            }

            var (success, mutationEnvelope, diagnostics) = TaskUpdater.Update(
                discoveredRoot,
                iterationId,
                taskId,
                expectedRevision.Value,
                requestXml);

            if (!success || diagnostics.Count > 0)
            {
                var diagEnvelope = new DiagnosticsEnvelope("task update", diagnostics);
                Console.Error.Write(diagEnvelope.Format(format));
                return diagEnvelope.GetExitCode();
            }

            if (mutationEnvelope != null)
            {
                Console.Out.Write(mutationEnvelope.Format(format));
            }

            return 0;
        });

        return updateCmd;
    }
}
