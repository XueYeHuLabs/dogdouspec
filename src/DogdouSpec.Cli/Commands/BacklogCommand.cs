using System.CommandLine;
using System.Globalization;
using DogdouSpec.Core.Backlog;
using DogdouSpec.Core.Diagnostics;
using DogdouSpec.Core.Formatting;
using DogdouSpec.Core.Revisions;
using DogdouSpec.Core.Transactions;
using DogdouSpec.Core.Workspace;

namespace DogdouSpec.Cli.Commands;

public static class BacklogCommand
{
    public static Command BuildCommand()
    {
        var command = new Command("backlog", "Manage deferred project obligations");
        command.Add(BuildAddCommand());
        command.Add(BuildListCommand());
        command.Add(BuildTransitionCommand("schedule", "Schedule an open backlog item", BacklogLifecycle.Schedule));
        command.Add(BuildTransitionCommand("complete", "Complete an open or scheduled backlog item", BacklogLifecycle.Complete));
        command.Add(BuildTransitionCommand("cancel", "Cancel an open or scheduled backlog item", BacklogLifecycle.Cancel));
        return command;
    }

    private static Command BuildAddCommand()
    {
        var command = new Command("add", "Create an open backlog item (mutating unless --dry-run)");
        var id = RequiredString("--id", "Time-first backlog item ID");
        var operationId = RequiredString("--operation-id", "Time-first replay operation ID");
        var actor = RequiredString("--actor", "Actor attribution (provenance, not authenticated identity)");
        var occurredAt = RequiredString("--occurred-at", "UTC or offset timestamp for deterministic replay");
        var kind = RequiredString("--kind", "Backlog kind (use defect for defects; kind 'defect' requires --severity)");
        var severity = OptionalString("--severity", "Defect severity: p0, p1, p2, or p3 (required when --kind is defect)");
        severity.AcceptOnlyFromAmong("p0", "p1", "p2", "p3");
        var summary = RequiredString("--summary", "Short indexed summary");
        var statement = RequiredString("--statement", "Deferred obligation statement");
        var rationale = RequiredString("--rationale", "Why the obligation is not current acceptance");
        var impact = RequiredString("--impact", "Risk and product acceptance impact");
        var sourceIteration = Repeatable("--source-iteration", "Source iteration ID (repeatable; at least one --source-iteration or --source-task required)");
        var sourceTask = Repeatable("--source-task", "Source task ID (repeatable; at least one --source-iteration or --source-task required)");
        var targetIteration = OptionalString("--target-iteration", "Target iteration ID (mutually exclusive with --review-condition; exactly one required)");
        var reviewCondition = OptionalString("--review-condition", "Condition for later review or scheduling (mutually exclusive with --target-iteration; exactly one required)");
        var expectedRevision = RequiredRevision();
        var dryRun = new Option<bool>("--dry-run") { Description = "Validate mutation preconditions and report prospective revision without writing" };
        var workspaceRoot = WorkspaceRoot();
        var format = FormatOption();
        foreach (var option in new Option[]
                 { id, operationId, actor, occurredAt, kind, severity, summary, statement, rationale, impact,
                   sourceIteration, sourceTask, targetIteration, reviewCondition, expectedRevision, dryRun, workspaceRoot, format })
        {
            command.Add(option);
        }

        command.SetAction(parseResult =>
        {
            var outputFormat = WorkspaceCommand.ResolveFormat(parseResult.GetValue(format));
            if (!TryParseOccurredAt(parseResult.GetValue(occurredAt), out var timestamp))
            {
                return WriteError("backlog add", outputFormat,
                    Diagnostic.Error(DiagnosticCodes.InvalidArgument, "--occurred-at must be a valid ISO-8601 timestamp."));
            }
            var (found, root, discoveryError) = WorkspaceDiscovery.FindWorkspaceRoot(
                parseResult.GetValue(workspaceRoot), Environment.CurrentDirectory);
            if (!found || discoveryError != null)
            {
                return WriteError("backlog add", outputFormat, discoveryError!);
            }
            var (revOk, resolvedRev, revErr) = DocumentRevisionResolver.ResolveExpectedRevision(
                root, "backlog.xml", parseResult.GetValue(expectedRevision));
            if (!revOk || revErr != null)
            {
                return WriteError("backlog add", outputFormat, revErr!);
            }
            var isDryRun = parseResult.GetValue(dryRun);
            var input = new BacklogCreateInput(
                parseResult.GetValue(id)!, parseResult.GetValue(operationId)!, parseResult.GetValue(actor)!, timestamp,
                parseResult.GetValue(kind)!, parseResult.GetValue(severity), parseResult.GetValue(summary)!,
                parseResult.GetValue(statement)!, parseResult.GetValue(rationale)!, parseResult.GetValue(impact)!,
                parseResult.GetValue(sourceIteration) ?? Array.Empty<string>(),
                parseResult.GetValue(sourceTask) ?? Array.Empty<string>(),
                parseResult.GetValue(targetIteration), parseResult.GetValue(reviewCondition));
            var (success, envelope, diagnostics) = BacklogLifecycle.Add(root, resolvedRev, input, dryRun: isDryRun);
            return WriteMutationResult("backlog add", outputFormat, success, envelope, diagnostics);
        });
        return command;
    }

    private static Command BuildListCommand()
    {
        var command = new Command("list", "List backlog items deterministically (read-only)");
        var status = OptionalString("--status", "Filter by lifecycle status");
        status.AcceptOnlyFromAmong("open", "scheduled", "completed", "cancelled");
        var kind = OptionalString("--kind", "Filter by kind index term");
        var severity = OptionalString("--severity", "Filter by severity index term");
        severity.AcceptOnlyFromAmong("p0", "p1", "p2", "p3");
        var workspaceRoot = WorkspaceRoot();
        var format = FormatOption();
        command.Add(status);
        command.Add(kind);
        command.Add(severity);
        command.Add(workspaceRoot);
        command.Add(format);
        command.SetAction(parseResult =>
        {
            var outputFormat = WorkspaceCommand.ResolveFormat(parseResult.GetValue(format));
            var (found, root, discoveryError) = WorkspaceDiscovery.FindWorkspaceRoot(
                parseResult.GetValue(workspaceRoot), Environment.CurrentDirectory);
            if (!found || discoveryError != null)
            {
                return WriteError("backlog list", outputFormat, discoveryError!);
            }
            var (success, result, diagnostics) = BacklogLifecycle.List(
                root, parseResult.GetValue(status), parseResult.GetValue(kind), parseResult.GetValue(severity));
            if (!success || diagnostics.Count > 0 || result == null)
            {
                return WriteDiagnostics("backlog list", outputFormat, diagnostics);
            }
            Console.Out.Write(result.Format(outputFormat));
            return 0;
        });
        return command;
    }

    private static Command BuildTransitionCommand(
        string name,
        string description,
        Func<string, int, BacklogTransitionInput, bool,
            (bool Success, MutationEnvelope? Envelope, IReadOnlyList<Diagnostic> Diagnostics)> transition)
    {
        var command = new Command(name, description + " (mutating unless --dry-run)");
        var id = RequiredString("--id", "Backlog item ID");
        var operationId = RequiredString("--operation-id", "Time-first replay operation ID");
        var actor = RequiredString("--actor", "Actor attribution (provenance, not authenticated identity)");
        var occurredAt = RequiredString("--occurred-at", "UTC or offset timestamp for deterministic replay");
        var resolvingTask = OptionalString("--resolving-task", "Task ID recorded as resolution evidence");
        var expectedRevision = RequiredRevision();
        var dryRun = new Option<bool>("--dry-run")
        {
            Description = "Validate mutation preconditions and report prospective revision without writing"
        };
        var workspaceRoot = WorkspaceRoot();
        var format = FormatOption();
        command.Add(id);
        command.Add(operationId);
        command.Add(actor);
        command.Add(occurredAt);
        command.Add(resolvingTask);
        command.Add(expectedRevision);
        command.Add(dryRun);
        command.Add(workspaceRoot);
        command.Add(format);
        command.SetAction(parseResult =>
        {
            var commandName = "backlog " + name;
            var outputFormat = WorkspaceCommand.ResolveFormat(parseResult.GetValue(format));
            if (!TryParseOccurredAt(parseResult.GetValue(occurredAt), out var timestamp))
            {
                return WriteError(commandName, outputFormat,
                    Diagnostic.Error(DiagnosticCodes.InvalidArgument, "--occurred-at must be a valid ISO-8601 timestamp."));
            }
            var (found, root, discoveryError) = WorkspaceDiscovery.FindWorkspaceRoot(
                parseResult.GetValue(workspaceRoot), Environment.CurrentDirectory);
            if (!found || discoveryError != null)
            {
                return WriteError(commandName, outputFormat, discoveryError!);
            }
            var (revOk, resolvedRev, revErr) = DocumentRevisionResolver.ResolveExpectedRevision(
                root, "backlog.xml", parseResult.GetValue(expectedRevision));
            if (!revOk || revErr != null)
            {
                return WriteError(commandName, outputFormat, revErr!);
            }
            var isDryRun = parseResult.GetValue(dryRun);
            var input = new BacklogTransitionInput(
                parseResult.GetValue(id)!, parseResult.GetValue(operationId)!, parseResult.GetValue(actor)!,
                timestamp, parseResult.GetValue(resolvingTask));
            var (success, envelope, diagnostics) = transition(root, resolvedRev, input, isDryRun);
            return WriteMutationResult(commandName, outputFormat, success, envelope, diagnostics);
        });
        return command;
    }

    private static Option<string> RequiredString(string name, string description) => new(name)
    {
        Description = description,
        Required = true
    };

    private static Option<string?> OptionalString(string name, string description) => new(name)
    {
        Description = description
    };

    private static Option<string[]> Repeatable(string name, string description) => new(name)
    {
        Description = description,
        AllowMultipleArgumentsPerToken = false
    };

    private static Option<int?> RequiredRevision() => new("--expected-revision")
    {
        Description = "Expected positive backlog.xml revision (optional; defaults to current backlog.xml revision)"
    };

    private static Option<string?> WorkspaceRoot() => new("--workspace-root")
    {
        Description = "Explicit path to workspace root or project directory containing .dogdouspec"
    };

    private static Option<string?> FormatOption()
    {
        var option = new Option<string?>("--format") { Description = "Output format (xml or human)" };
        option.AcceptOnlyFromAmong("xml", "human");
        return option;
    }

    private static bool TryParseOccurredAt(string? raw, out DateTimeOffset result) =>
        DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture,
            DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out result);

    private static int WriteMutationResult(string command, OutputFormat format, bool success,
        MutationEnvelope? envelope, IReadOnlyList<Diagnostic> diagnostics)
    {
        if (!success || diagnostics.Count > 0 || envelope == null)
        {
            return WriteDiagnostics(command, format, diagnostics);
        }
        Console.Out.Write(envelope.Format(format));
        return 0;
    }

    private static int WriteError(string command, OutputFormat format, Diagnostic diagnostic) =>
        WriteDiagnostics(command, format, new[] { diagnostic });

    private static int WriteDiagnostics(string command, OutputFormat format, IReadOnlyList<Diagnostic> diagnostics)
    {
        var envelope = new DiagnosticsEnvelope(command, diagnostics.Count == 0
            ? new[] { Diagnostic.Error(DiagnosticCodes.InvalidArgument, "The command did not produce a result.") }
            : diagnostics);
        Console.Error.Write(envelope.Format(format));
        return envelope.GetExitCode();
    }
}
