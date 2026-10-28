using System.CommandLine;
using System.Globalization;
using System.Xml.Linq;
using DogdouSpec.Core.Serialization;
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
        command.Add(BuildTransitionCommand("record", "Append a progress or follow-up note without changing backlog status", BacklogLifecycle.Record, requiresSummary: true));
        command.Add(BuildRecordsCommand());
        command.Add(BuildTransitionCommand("schedule", "Schedule an open backlog item", BacklogLifecycle.Schedule));
        command.Add(BuildTransitionCommand("complete", "Complete an open or scheduled backlog item", BacklogLifecycle.Complete));
        command.Add(BuildTransitionCommand("cancel", "Cancel an open or scheduled backlog item", BacklogLifecycle.Cancel));
        return command;
    }

    private static Command BuildAddCommand()
    {
        var command = new Command("add", "Create an open backlog item (mutating unless --dry-run)");
        var id = OptionalString("--id", "Backlog item ID (omitted derives from the operation ID)");
        var operationId = OptionalString("--operation-id", "Replay operation ID (omitted generates a UTC timestamp and unique suffix)");
        var actor = OptionalString("--actor", "Actor attribution (default DOGDOUSPEC_ACTOR or agent; not authenticated identity)");
        var occurredAt = OptionalString("--occurred-at", "Timestamp (default operation UTC prefix, or now for a generated operation)");
        var kind = RequiredString("--kind", "Open kind token (e.g. feature, research, xyz); defect requires --severity");
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
            var (identity, identityError) = ResolveIdentity(parseResult.GetValue(id), parseResult.GetValue(operationId), parseResult.GetValue(actor), parseResult.GetValue(occurredAt));
            if (identity == null || identityError != null) return WriteError("backlog add", outputFormat, identityError!);
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
                identity.Id, identity.OperationId, identity.Actor, identity.OccurredAt,
                parseResult.GetValue(kind)!, parseResult.GetValue(severity), parseResult.GetValue(summary)!,
                parseResult.GetValue(statement)!, parseResult.GetValue(rationale)!, parseResult.GetValue(impact)!,
                parseResult.GetValue(sourceIteration) ?? Array.Empty<string>(),
                parseResult.GetValue(sourceTask) ?? Array.Empty<string>(),
                parseResult.GetValue(targetIteration), parseResult.GetValue(reviewCondition));
            var (success, envelope, diagnostics) = BacklogLifecycle.Add(root, resolvedRev, input, dryRun: isDryRun);
            return WriteMutationResult("backlog add", outputFormat, success, envelope, diagnostics, identity);
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
            (bool Success, MutationEnvelope? Envelope, IReadOnlyList<Diagnostic> Diagnostics)> transition,
        bool requiresSummary = false)
    {
        var command = new Command(name, description + " (mutating unless --dry-run)");
        var id = RequiredString("--id", "Backlog item ID");
        var operationId = OptionalString("--operation-id", "Replay operation ID (omitted generates a UTC timestamp and unique suffix)");
        var actor = OptionalString("--actor", "Actor attribution (default DOGDOUSPEC_ACTOR or agent; not authenticated identity)");
        var occurredAt = OptionalString("--occurred-at", "Timestamp (default operation UTC prefix, or now for a generated operation)");
        var resolvingTask = OptionalString("--resolving-task", "Task ID recorded as resolution evidence");
        if (requiresSummary) resolvingTask.Description = "Optional task linked by this note";
        var summary = OptionalString("--summary", requiresSummary
            ? "Progress or follow-up explanation"
            : "Optional scheduling, completion or cancellation explanation");
        summary.Required = requiresSummary;
        var checkCommand = OptionalString("--check-command", "Reported check command; requires --check-result");
        var checkResult = OptionalString("--check-result", "Reported check result (informational; does not decide closure)");
        checkResult.AcceptOnlyFromAmong("passed", "failed", "skipped");
        var checkExit = new Option<int?>("--check-exit-code") { Description = "Reported exit code; requires --check-command" };
        var checkKind = OptionalString("--check-kind", "Check kind (default: command)");
        var checkSummary = OptionalString("--check-summary", "Check explanation (default: record summary or a generic check description)");
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
        foreach (var option in new Option[] { summary, checkCommand, checkResult, checkExit, checkKind, checkSummary }) command.Add(option);
        command.Add(expectedRevision);
        command.Add(dryRun);
        command.Add(workspaceRoot);
        command.Add(format);
        command.SetAction(parseResult =>
        {
            var commandName = "backlog " + name;
            var outputFormat = WorkspaceCommand.ResolveFormat(parseResult.GetValue(format));
            var hasCheck = parseResult.GetValue(checkCommand) != null || parseResult.GetValue(checkResult) != null ||
                parseResult.GetValue(checkExit).HasValue || parseResult.GetValue(checkKind) != null || parseResult.GetValue(checkSummary) != null;
            if (hasCheck && (parseResult.GetValue(checkResult) == null ||
                (parseResult.GetValue(checkExit).HasValue && parseResult.GetValue(checkCommand) == null)))
                return WriteError(commandName, outputFormat, Diagnostic.Error(DiagnosticCodes.InvalidArgument,
                    "A structured check requires --check-result; --check-exit-code also requires --check-command."));
            var (identity, identityError) = ResolveIdentity(parseResult.GetValue(id), parseResult.GetValue(operationId), parseResult.GetValue(actor), parseResult.GetValue(occurredAt));
            if (identity == null || identityError != null) return WriteError(commandName, outputFormat, identityError!);
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
                identity.Id, identity.OperationId, identity.Actor,
                identity.OccurredAt, parseResult.GetValue(resolvingTask), parseResult.GetValue(summary),
                hasCheck ? [new BacklogCheck(parseResult.GetValue(checkKind) ?? "command", parseResult.GetValue(checkResult)!,
                    parseResult.GetValue(checkSummary) ?? parseResult.GetValue(summary) ?? "Reported backlog check.",
                    parseResult.GetValue(checkCommand), parseResult.GetValue(checkExit))] : null);
            var (success, envelope, diagnostics) = transition(root, resolvedRev, input, isDryRun);
            return WriteMutationResult(commandName, outputFormat, success, envelope, diagnostics, identity);
        });
        return command;
    }

    private static Command BuildRecordsCommand()
    {
        var command = new Command("records", "Read recent backlog history, explanations, checks and task links without modifying files");
        var id = RequiredString("--id", "Backlog item ID");
        var last = new Option<int>("--last") { DefaultValueFactory = _ => 5, Description = "Number of recent records in append order (default: 5)" };
        var workspace = WorkspaceRoot();
        var format = FormatOption();
        foreach (var option in new Option[] { id, last, workspace, format }) command.Add(option);
        command.SetAction(parse =>
        {
            var outputFormat = WorkspaceCommand.ResolveFormat(parse.GetValue(format));
            var (found, root, error) = WorkspaceDiscovery.FindWorkspaceRoot(parse.GetValue(workspace), Environment.CurrentDirectory);
            if (!found || error != null) return WriteError("backlog records", outputFormat, error!);
            var (success, result, diagnostics) = BacklogLifecycle.Records(root, parse.GetValue(id)!, parse.GetValue(last));
            if (!success || result == null || diagnostics.Count > 0) return WriteDiagnostics("backlog records", outputFormat, diagnostics);
            Console.Out.Write(result.Format(outputFormat));
            return 0;
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

    private sealed record BacklogIdentity(string Id, string OperationId, string Actor, DateTimeOffset OccurredAt);

    private static (BacklogIdentity? Identity, Diagnostic? Error) ResolveIdentity(string? id, string? operationId, string? actor, string? occurredAt)
    {
        DateTimeOffset timestamp;
        if (occurredAt != null)
        {
            if (!TryParseOccurredAt(occurredAt, out timestamp))
                return (null, Diagnostic.Error(DiagnosticCodes.InvalidArgument, "--occurred-at must be a valid ISO-8601 timestamp."));
        }
        else if (operationId != null)
        {
            if (operationId.Length < 16 || !DateTimeOffset.TryParseExact(operationId[..16], "yyyyMMddTHHmmssZ", CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out timestamp))
                return (null, Diagnostic.Error(DiagnosticCodes.InvalidArgument, "Without --occurred-at, an explicit --operation-id must begin with a valid UTC YYYYMMDDTHHmmssZ timestamp for deterministic replay."));
        }
        else timestamp = DateTimeOffset.UtcNow;
        operationId ??= timestamp.ToString("yyyyMMddTHHmmssZ", CultureInfo.InvariantCulture) + "-backlog-" + Guid.NewGuid().ToString("N");
        id ??= operationId + "-item";
        actor ??= Environment.GetEnvironmentVariable("DOGDOUSPEC_ACTOR") ?? "agent";
        if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(operationId) || string.IsNullOrWhiteSpace(actor))
            return (null, Diagnostic.Error(DiagnosticCodes.InvalidArgument, "Explicit IDs and actor attribution cannot be blank."));
        return (new BacklogIdentity(id, operationId, actor, timestamp), null);
    }

    private static bool TryParseOccurredAt(string? raw, out DateTimeOffset result) =>
        DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture,
            DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out result);

    private static int WriteMutationResult(string command, OutputFormat format, bool success,
        MutationEnvelope? envelope, IReadOnlyList<Diagnostic> diagnostics, BacklogIdentity identity)
    {
        if (!success || diagnostics.Count > 0 || envelope == null)
        {
            return WriteDiagnostics(command, format, diagnostics);
        }
        if (format == OutputFormat.Xml)
        {
            var document = XDocument.Parse(envelope.ToXmlString());
            document.Root!.Add(new XElement("backlog-identity", new XAttribute("id", identity.Id), new XAttribute("operation_id", identity.OperationId),
                new XAttribute("actor", identity.Actor), new XAttribute("occurred_at", identity.OccurredAt.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture))));
            Console.Out.Write(ManagedDocumentSerializer.Serialize(document));
        }
        else
        {
            Console.Out.Write(envelope.Format(format));
            Console.Out.WriteLine(string.Create(CultureInfo.InvariantCulture, $"Backlog item: {identity.Id}; operation: {identity.OperationId}; actor: {identity.Actor}; occurred_at: {identity.OccurredAt:yyyy-MM-ddTHH:mm:ssZ}"));
        }
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
