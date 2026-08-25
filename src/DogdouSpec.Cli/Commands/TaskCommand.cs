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

        taskCmd.Add(BuildUpdateCommand());
        taskCmd.Add(BuildAddCommand());
        taskCmd.Add(BuildQuickCommand());
        taskCmd.Add(BuildReviseCommand());
        taskCmd.Add(BuildSplitCommand());
        taskCmd.Add(BuildNextCommand());
        taskCmd.Add(BuildScopeCommand());
        taskCmd.Add(BuildReviewCommand());

        return taskCmd;
    }

    private static Command BuildQuickCommand()
    {
        var cmd = new Command("quick", "Create a compact normal task; --start creates it in-progress atomically (mutating unless --dry-run)");
        var title = new Option<string>("--title") { Required = true, Description = "Task title" };
        var scope = new Option<string[]>("--scope") { Required = true, AllowMultipleArgumentsPerToken = true, Description = "Repeatable repository include path" };
        var done = new Option<string>("--done-when") { Required = true, Description = "Single observable completion condition" };
        var why = new Option<string>("--why") { Required = true, Description = "Reason for this bounded work" };
        var origin = new Option<string[]>("--origin") { AllowMultipleArgumentsPerToken = true, Description = "Repeatable approved requirement ID; omitted means operational supports origin" };
        var depends = new Option<string[]>("--depends-on") { AllowMultipleArgumentsPerToken = true, Description = "Repeatable prerequisite task ID" };
        var term = new Option<string[]>("--term") { AllowMultipleArgumentsPerToken = true, Description = "Repeatable index key=value" };
        var iteration = new Option<string?>("--iteration") { Description = "Iteration ID; omitted auto-discovers exactly one active iteration" };
        var revision = new Option<int?>("--expected-revision") { Description = "Exact tasks.xml revision; omitted resolves the current revision" };
        var start = new Option<bool>("--start") { Description = "Atomically create the final in-progress task with start history" };
        var dryRun = new Option<bool>("--dry-run") { Description = "Validate without writing; XML prints request and human prints summary" };
        var id = new Option<string?>("--id") { Description = "Replayable task ID; must be supplied with --operation-id" };
        var operationId = new Option<string?>("--operation-id") { Description = "Replayable ID with UTC timestamp prefix; must be supplied with --id" };
        var agent = new Option<string?>("--agent") { Description = "Declared implementer attribution; provenance only" };
        var reviewRequired = new Option<bool>("--review-required") { Description = "Require a structured independent approval before completion" };
        var workspace = new Option<string?>("--workspace-root");
        var formatOption = new Option<string?>("--format"); formatOption.AcceptOnlyFromAmong("xml", "human");
        foreach (var option in new Option[] { title, scope, done, why, origin, depends, term, iteration, revision, start, dryRun, id, operationId, agent, reviewRequired, workspace, formatOption }) cmd.Add(option);
        cmd.SetAction(parse =>
        {
            var format = WorkspaceCommand.ResolveFormat(parse.GetValue(formatOption));
            var (found, root, findError) = WorkspaceDiscovery.FindWorkspaceRoot(parse.GetValue(workspace), Environment.CurrentDirectory);
            if (!found || findError != null) { Console.Error.Write(new DiagnosticsEnvelope("task quick", findError!).Format(format)); return 2; }
            var input = new QuickTaskInput(parse.GetValue(title)!, parse.GetValue(scope) ?? Array.Empty<string>(), parse.GetValue(done)!, parse.GetValue(why)!,
                parse.GetValue(origin) ?? Array.Empty<string>(), parse.GetValue(depends) ?? Array.Empty<string>(), parse.GetValue(term) ?? Array.Empty<string>(),
                parse.GetValue(iteration), parse.GetValue(revision), parse.GetValue(start), parse.GetValue(dryRun), parse.GetValue(id), parse.GetValue(operationId),
                parse.GetValue(agent), parse.GetValue(reviewRequired));
            var (success, result, envelope, diagnostics) = TaskQuick.Create(root, input);
            if (!success || diagnostics.Count > 0) { var d = new DiagnosticsEnvelope("task quick", diagnostics); Console.Error.Write(d.Format(format)); return d.GetExitCode(); }
            if (input.DryRun && result != null)
            {
                if (format == OutputFormat.Human)
                    Console.Out.WriteLine($"Quick task preview: iteration={result.IterationId}, expected_revision={result.ExpectedRevision}, task={result.Task.Attribute("id")?.Value}, status={result.Task.Attribute("status")?.Value}. Use --format xml to view the canonical request.");
                else
                    Console.Out.Write(result.RequestXml);
                return 0;
            }
            if (envelope != null) Console.Out.Write(envelope.Format(format));
            return 0;
        });
        return cmd;
    }

    private static Command BuildAddCommand()
    {
        var addCmd = new Command("add", "Add a new pending task to tasks.xml (mutating)");

        var iterationOption = new Option<string?>("--iteration")
        {
            Description = "Iteration identifier following YYYYMMDD-name grammar",
            Required = true
        };

        var expectedRevisionOption = new Option<int?>("--expected-revision")
        {
            Description = "Expected positive integer revision of the target tasks.xml document",
            Required = true
        };

        var stdinOption = new Option<bool>("--stdin")
        {
            Description = "Read task-add XML request from standard input"
        };

        var fileOption = new Option<string?>("--file")
        {
            Description = "Path to file containing task-add XML request"
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

        addCmd.Add(iterationOption);
        addCmd.Add(expectedRevisionOption);
        addCmd.Add(stdinOption);
        addCmd.Add(fileOption);
        addCmd.Add(workspaceRootOption);
        addCmd.Add(formatOption);

        addCmd.SetAction(parseResult =>
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
                var envelope = new DiagnosticsEnvelope("task add", Diagnostic.Error(
                    DiagnosticCodes.InvalidArgument,
                    "Specify either --stdin or --file, not both."));
                Console.Error.Write(envelope.Format(format));
                return 2;
            }

            if (!hasStdin && string.IsNullOrWhiteSpace(filePath))
            {
                var envelope = new DiagnosticsEnvelope("task add", Diagnostic.Error(
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
                    var envelope = new DiagnosticsEnvelope("task add", Diagnostic.Error(
                        DiagnosticCodes.InvalidArgument,
                        $"Task add XML file '{filePath}' does not exist."));
                    Console.Error.Write(envelope.Format(format));
                    return 2;
                }

                try
                {
                    requestXml = File.ReadAllText(filePath!);
                }
                catch (Exception ex)
                {
                    var envelope = new DiagnosticsEnvelope("task add", Diagnostic.Error(
                        DiagnosticCodes.InvalidArgument,
                        $"Failed to read file '{filePath}': {ex.Message}"));
                    Console.Error.Write(envelope.Format(format));
                    return 2;
                }
            }

            if (string.IsNullOrWhiteSpace(iterationId))
            {
                var envelope = new DiagnosticsEnvelope("task add", Diagnostic.Error(
                    DiagnosticCodes.InvalidArgument,
                    "--iteration option is required."));
                Console.Error.Write(envelope.Format(format));
                return 2;
            }

            var (isIterValid, _, iterErr) = PathSecurity.ValidateIterationId(iterationId);
            if (!isIterValid || iterErr != null)
            {
                var envelope = new DiagnosticsEnvelope("task add", iterErr ?? Diagnostic.Error(
                    DiagnosticCodes.InvalidArgument,
                    $"Invalid iteration identifier '{iterationId}'."));
                Console.Error.Write(envelope.Format(format));
                return 2;
            }

            if (!expectedRevision.HasValue || expectedRevision.Value <= 0)
            {
                var envelope = new DiagnosticsEnvelope("task add", Diagnostic.Error(
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
                var envelope = new DiagnosticsEnvelope("task add", discoverError!);
                Console.Error.Write(envelope.Format(format));
                return 2;
            }

            var (success, mutationEnvelope, diagnostics) = TaskAdder.Add(
                discoveredRoot,
                iterationId,
                expectedRevision.Value,
                requestXml);

            if (!success || diagnostics.Count > 0)
            {
                var diagEnvelope = new DiagnosticsEnvelope("task add", diagnostics);
                Console.Error.Write(diagEnvelope.Format(format));
                return diagEnvelope.GetExitCode();
            }

            if (mutationEnvelope != null)
            {
                Console.Out.Write(mutationEnvelope.Format(format));
            }

            return 0;
        });

        return addCmd;
    }

    private static Command BuildReviseCommand()
    {
        var reviseCmd = new Command("revise", "Elaborate task scope, constraints, dependencies, or acceptance criteria (mutating)");

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
            Description = "Read task-revise XML request from standard input"
        };

        var fileOption = new Option<string?>("--file")
        {
            Description = "Path to file containing task-revise XML request"
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

        reviseCmd.Add(iterationOption);
        reviseCmd.Add(taskOption);
        reviseCmd.Add(expectedRevisionOption);
        reviseCmd.Add(stdinOption);
        reviseCmd.Add(fileOption);
        reviseCmd.Add(workspaceRootOption);
        reviseCmd.Add(formatOption);

        reviseCmd.SetAction(parseResult =>
        {
            var iterationId = parseResult.GetValue(iterationOption);
            var taskId = parseResult.GetValue(taskOption);
            var expectedRevision = parseResult.GetValue(expectedRevisionOption);
            var hasStdin = parseResult.GetValue(stdinOption);
            var filePath = parseResult.GetValue(fileOption);
            var workspaceRoot = parseResult.GetValue(workspaceRootOption);
            var formatArg = parseResult.GetValue(formatOption);
            var format = WorkspaceCommand.ResolveFormat(formatArg);

            if (hasStdin && !string.IsNullOrWhiteSpace(filePath))
            {
                var envelope = new DiagnosticsEnvelope("task revise", Diagnostic.Error(
                    DiagnosticCodes.InvalidArgument,
                    "Specify either --stdin or --file, not both."));
                Console.Error.Write(envelope.Format(format));
                return 2;
            }

            if (!hasStdin && string.IsNullOrWhiteSpace(filePath))
            {
                var envelope = new DiagnosticsEnvelope("task revise", Diagnostic.Error(
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
                    var envelope = new DiagnosticsEnvelope("task revise", Diagnostic.Error(
                        DiagnosticCodes.InvalidArgument,
                        $"Task revise XML file '{filePath}' does not exist."));
                    Console.Error.Write(envelope.Format(format));
                    return 2;
                }

                try
                {
                    requestXml = File.ReadAllText(filePath!);
                }
                catch (Exception ex)
                {
                    var envelope = new DiagnosticsEnvelope("task revise", Diagnostic.Error(
                        DiagnosticCodes.InvalidArgument,
                        $"Failed to read file '{filePath}': {ex.Message}"));
                    Console.Error.Write(envelope.Format(format));
                    return 2;
                }
            }

            if (string.IsNullOrWhiteSpace(iterationId))
            {
                var envelope = new DiagnosticsEnvelope("task revise", Diagnostic.Error(
                    DiagnosticCodes.InvalidArgument,
                    "--iteration option is required."));
                Console.Error.Write(envelope.Format(format));
                return 2;
            }

            var (isIterValid, _, iterErr) = PathSecurity.ValidateIterationId(iterationId);
            if (!isIterValid || iterErr != null)
            {
                var envelope = new DiagnosticsEnvelope("task revise", iterErr ?? Diagnostic.Error(
                    DiagnosticCodes.InvalidArgument,
                    $"Invalid iteration identifier '{iterationId}'."));
                Console.Error.Write(envelope.Format(format));
                return 2;
            }

            if (string.IsNullOrWhiteSpace(taskId))
            {
                var envelope = new DiagnosticsEnvelope("task revise", Diagnostic.Error(
                    DiagnosticCodes.InvalidArgument,
                    "--task option is required."));
                Console.Error.Write(envelope.Format(format));
                return 2;
            }

            if (!ProjectSemanticIndex.IsValidTimeFirstId(taskId))
            {
                var envelope = new DiagnosticsEnvelope("task revise", Diagnostic.Error(
                    DiagnosticCodes.InvalidIdGrammar,
                    $"Task identifier '{taskId}' does not conform to the time-first ID grammar."));
                Console.Error.Write(envelope.Format(format));
                return 2;
            }

            if (!expectedRevision.HasValue || expectedRevision.Value <= 0)
            {
                var envelope = new DiagnosticsEnvelope("task revise", Diagnostic.Error(
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
                var envelope = new DiagnosticsEnvelope("task revise", discoverError!);
                Console.Error.Write(envelope.Format(format));
                return 2;
            }

            var (success, mutationEnvelope, diagnostics) = TaskReviser.Revise(
                discoveredRoot,
                iterationId,
                taskId,
                expectedRevision.Value,
                requestXml);

            if (!success || diagnostics.Count > 0)
            {
                var diagEnvelope = new DiagnosticsEnvelope("task revise", diagnostics);
                Console.Error.Write(diagEnvelope.Format(format));
                return diagEnvelope.GetExitCode();
            }

            if (mutationEnvelope != null)
            {
                Console.Out.Write(mutationEnvelope.Format(format));
            }

            return 0;
        });

        return reviseCmd;
    }

    private static Command BuildSplitCommand()
    {
        var splitCmd = new Command("split", "Split a task into two or more replacement subtasks and set terminal disposition on parent (mutating)");

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
            Description = "Read task-split XML request from standard input"
        };

        var fileOption = new Option<string?>("--file")
        {
            Description = "Path to file containing task-split XML request"
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

        splitCmd.Add(iterationOption);
        splitCmd.Add(taskOption);
        splitCmd.Add(expectedRevisionOption);
        splitCmd.Add(stdinOption);
        splitCmd.Add(fileOption);
        splitCmd.Add(workspaceRootOption);
        splitCmd.Add(formatOption);

        splitCmd.SetAction(parseResult =>
        {
            var iterationId = parseResult.GetValue(iterationOption);
            var taskId = parseResult.GetValue(taskOption);
            var expectedRevision = parseResult.GetValue(expectedRevisionOption);
            var hasStdin = parseResult.GetValue(stdinOption);
            var filePath = parseResult.GetValue(fileOption);
            var workspaceRoot = parseResult.GetValue(workspaceRootOption);
            var formatArg = parseResult.GetValue(formatOption);
            var format = WorkspaceCommand.ResolveFormat(formatArg);

            if (hasStdin && !string.IsNullOrWhiteSpace(filePath))
            {
                var envelope = new DiagnosticsEnvelope("task split", Diagnostic.Error(
                    DiagnosticCodes.InvalidArgument,
                    "Specify either --stdin or --file, not both."));
                Console.Error.Write(envelope.Format(format));
                return 2;
            }

            if (!hasStdin && string.IsNullOrWhiteSpace(filePath))
            {
                var envelope = new DiagnosticsEnvelope("task split", Diagnostic.Error(
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
                    var envelope = new DiagnosticsEnvelope("task split", Diagnostic.Error(
                        DiagnosticCodes.InvalidArgument,
                        $"Task split XML file '{filePath}' does not exist."));
                    Console.Error.Write(envelope.Format(format));
                    return 2;
                }

                try
                {
                    requestXml = File.ReadAllText(filePath!);
                }
                catch (Exception ex)
                {
                    var envelope = new DiagnosticsEnvelope("task split", Diagnostic.Error(
                        DiagnosticCodes.InvalidArgument,
                        $"Failed to read file '{filePath}': {ex.Message}"));
                    Console.Error.Write(envelope.Format(format));
                    return 2;
                }
            }

            if (string.IsNullOrWhiteSpace(iterationId))
            {
                var envelope = new DiagnosticsEnvelope("task split", Diagnostic.Error(
                    DiagnosticCodes.InvalidArgument,
                    "--iteration option is required."));
                Console.Error.Write(envelope.Format(format));
                return 2;
            }

            var (isIterValid, _, iterErr) = PathSecurity.ValidateIterationId(iterationId);
            if (!isIterValid || iterErr != null)
            {
                var envelope = new DiagnosticsEnvelope("task split", iterErr ?? Diagnostic.Error(
                    DiagnosticCodes.InvalidArgument,
                    $"Invalid iteration identifier '{iterationId}'."));
                Console.Error.Write(envelope.Format(format));
                return 2;
            }

            if (string.IsNullOrWhiteSpace(taskId))
            {
                var envelope = new DiagnosticsEnvelope("task split", Diagnostic.Error(
                    DiagnosticCodes.InvalidArgument,
                    "--task option is required."));
                Console.Error.Write(envelope.Format(format));
                return 2;
            }

            if (!ProjectSemanticIndex.IsValidTimeFirstId(taskId))
            {
                var envelope = new DiagnosticsEnvelope("task split", Diagnostic.Error(
                    DiagnosticCodes.InvalidIdGrammar,
                    $"Task identifier '{taskId}' does not conform to the time-first ID grammar."));
                Console.Error.Write(envelope.Format(format));
                return 2;
            }

            if (!expectedRevision.HasValue || expectedRevision.Value <= 0)
            {
                var envelope = new DiagnosticsEnvelope("task split", Diagnostic.Error(
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
                var envelope = new DiagnosticsEnvelope("task split", discoverError!);
                Console.Error.Write(envelope.Format(format));
                return 2;
            }

            var (success, mutationEnvelope, diagnostics) = TaskSplitter.Split(
                discoveredRoot,
                iterationId,
                taskId,
                expectedRevision.Value,
                requestXml);

            if (!success || diagnostics.Count > 0)
            {
                var diagEnvelope = new DiagnosticsEnvelope("task split", diagnostics);
                Console.Error.Write(diagEnvelope.Format(format));
                return diagEnvelope.GetExitCode();
            }

            if (mutationEnvelope != null)
            {
                Console.Out.Write(mutationEnvelope.Format(format));
            }

            return 0;
        });

        return splitCmd;
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

    private static Command BuildNextCommand()
    {
        var nextCmd = new Command("next", "Derive the next actionable task in an iteration accounting for cross-iteration dependencies (read-only)");

        var iterationOption = new Option<string?>("--iteration")
        {
            Description = "Iteration identifier; omitted auto-discovers exactly one active iteration"
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

        nextCmd.Add(iterationOption);
        nextCmd.Add(workspaceRootOption);
        nextCmd.Add(formatOption);

        nextCmd.SetAction(parseResult =>
        {
            var iterationId = parseResult.GetValue(iterationOption);
            var workspaceRoot = parseResult.GetValue(workspaceRootOption);
            var formatArg = parseResult.GetValue(formatOption);
            var format = WorkspaceCommand.ResolveFormat(formatArg);

            var (discoverSuccess, discoveredRoot, discoverError) = WorkspaceDiscovery.FindWorkspaceRoot(
                workspaceRoot,
                Environment.CurrentDirectory);

            if (!discoverSuccess || discoverError != null)
            {
                var envelope = new DiagnosticsEnvelope("task next", discoverError!);
                Console.Error.Write(envelope.Format(format));
                return 2;
            }

            var (success, result, diagnostics) = TaskNext.SelectNext(
                discoveredRoot,
                iterationId);

            if (!success || diagnostics.Count > 0)
            {
                var envelope = new DiagnosticsEnvelope("task next", diagnostics);
                Console.Error.Write(envelope.Format(format));
                return envelope.GetExitCode();
            }

            if (result != null)
            {
                Console.Out.Write(result.Format(format));
            }

            return 0;
        });

        return nextCmd;
    }

    private static Command BuildScopeCommand()
    {
        var scopeCmd = new Command("scope", "Verify changed repository paths against task declared repository scope (read-only)");

        var taskOption = new Option<string>("--task")
        {
            Description = "Task identifier following time-first grammar",
            Required = true
        };

        var iterationOption = new Option<string?>("--iteration")
        {
            Description = "Iteration identifier; omitted auto-discovers active iteration or resolves across workspace"
        };

        var pathOption = new Option<string[]>("--path")
        {
            Description = "Explicit changed repository-relative path to verify (repeatable)",
            AllowMultipleArgumentsPerToken = true
        };

        var gitRefOption = new Option<string?>("--git-ref")
        {
            Description = "Git reference to diff against working tree (e.g. HEAD, main)"
        };

        var gitRangeOption = new Option<string?>("--git-range")
        {
            Description = "Explicit two-reference Git diff range (e.g. main..HEAD, base...head)"
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

        scopeCmd.Add(taskOption);
        scopeCmd.Add(iterationOption);
        scopeCmd.Add(pathOption);
        scopeCmd.Add(gitRefOption);
        scopeCmd.Add(gitRangeOption);
        scopeCmd.Add(workspaceRootOption);
        scopeCmd.Add(formatOption);

        scopeCmd.SetAction(parseResult =>
        {
            var taskId = parseResult.GetValue(taskOption);
            var iterationId = parseResult.GetValue(iterationOption);
            var parsedPaths = parseResult.GetValue(pathOption);
            IReadOnlyList<string>? explicitPaths = parsedPaths is { Length: > 0 }
                ? parsedPaths
                : null;
            var gitRef = parseResult.GetValue(gitRefOption);
            var gitRange = parseResult.GetValue(gitRangeOption);
            var workspaceRoot = parseResult.GetValue(workspaceRootOption);
            var formatArg = parseResult.GetValue(formatOption);
            var format = WorkspaceCommand.ResolveFormat(formatArg);

            if (string.IsNullOrWhiteSpace(taskId))
            {
                var envelope = new DiagnosticsEnvelope("task scope", Diagnostic.Error(
                    DiagnosticCodes.InvalidArgument,
                    "--task option is required."));
                Console.Error.Write(envelope.Format(format));
                return 2;
            }

            var (discoverSuccess, discoveredRoot, discoverError) = WorkspaceDiscovery.FindWorkspaceRoot(
                workspaceRoot,
                Environment.CurrentDirectory);

            if (!discoverSuccess || discoverError != null)
            {
                var envelope = new DiagnosticsEnvelope("task scope", discoverError!);
                Console.Error.Write(envelope.Format(format));
                return 2;
            }

            var (success, result, diagnostics) = TaskScopeVerifier.VerifyScope(
                discoveredRoot,
                taskId,
                iterationId,
                explicitPaths,
                gitRef,
                gitRange);

            if (!success || diagnostics.Count > 0)
            {
                var envelope = new DiagnosticsEnvelope("task scope", diagnostics);
                Console.Error.Write(envelope.Format(format));
                return envelope.GetExitCode();
            }

            if (result != null)
            {
                Console.Out.Write(result.Format(format));
                return result.IsValid ? 0 : 1;
            }

            return 0;
        });

        return scopeCmd;
    }

    private static Command BuildReviewCommand()
    {
        var command = new Command("review", "Submit a structured task review (mutating; actor separation is provenance, not authenticated identity)");
        var iteration = new Option<string>("--iteration") { Required = true, Description = "Iteration ID" };
        var task = new Option<string>("--task") { Required = true, Description = "Task ID" };
        var expectedRevision = new Option<int?>("--expected-revision") { Required = true, Description = "Exact tasks.xml revision" };
        var stdin = new Option<bool>("--stdin") { Description = "Read task-review XML from standard input" };
        var file = new Option<string?>("--file") { Description = "Path to task-review XML" };
        var workspace = new Option<string?>("--workspace-root") { Description = "Workspace root or project directory" };
        var formatOption = new Option<string?>("--format") { Description = "Output format (xml or human)" };
        formatOption.AcceptOnlyFromAmong("xml", "human");
        foreach (var option in new Option[] { iteration, task, expectedRevision, stdin, file, workspace, formatOption })
        {
            command.Add(option);
        }
        command.SetAction(parse =>
        {
            var format = WorkspaceCommand.ResolveFormat(parse.GetValue(formatOption));
            var useStdin = parse.GetValue(stdin);
            var filePath = parse.GetValue(file);
            if (useStdin == !string.IsNullOrWhiteSpace(filePath))
            {
                var diagnostic = Diagnostic.Error(DiagnosticCodes.InvalidArgument, "Specify exactly one of --stdin or --file.");
                Console.Error.Write(new DiagnosticsEnvelope("task review", diagnostic).Format(format));
                return 2;
            }
            string xml;
            if (useStdin)
            {
                xml = Console.In.ReadToEnd();
            }
            else if (File.Exists(filePath))
            {
                xml = File.ReadAllText(filePath!);
            }
            else
            {
                var diagnostic = Diagnostic.Error(DiagnosticCodes.InvalidArgument, $"Review request file '{filePath}' does not exist.");
                Console.Error.Write(new DiagnosticsEnvelope("task review", diagnostic).Format(format));
                return 2;
            }
            var revision = parse.GetValue(expectedRevision);
            if (!revision.HasValue || revision.Value <= 0)
            {
                var diagnostic = Diagnostic.Error(DiagnosticCodes.InvalidArgument, "--expected-revision must be positive.");
                Console.Error.Write(new DiagnosticsEnvelope("task review", diagnostic).Format(format));
                return 2;
            }
            var (found, root, error) = WorkspaceDiscovery.FindWorkspaceRoot(parse.GetValue(workspace), Environment.CurrentDirectory);
            if (!found || error != null)
            {
                Console.Error.Write(new DiagnosticsEnvelope("task review", error!).Format(format));
                return 2;
            }
            var (success, envelope, diagnostics) = TaskReviewer.Submit(
                root, parse.GetValue(iteration)!, parse.GetValue(task)!, revision.Value, xml);
            if (!success || diagnostics.Count > 0 || envelope == null)
            {
                var diagnosticEnvelope = new DiagnosticsEnvelope("task review", diagnostics);
                Console.Error.Write(diagnosticEnvelope.Format(format));
                return diagnosticEnvelope.GetExitCode();
            }
            Console.Out.Write(envelope.Format(format));
            return 0;
        });
        return command;
    }
}
