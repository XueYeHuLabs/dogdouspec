using System.CommandLine;
using System.Globalization;
using DogdouSpec.Core.Diagnostics;
using DogdouSpec.Core.Formatting;
using DogdouSpec.Core.Knowledge;
using DogdouSpec.Core.Revisions;
using DogdouSpec.Core.Transactions;
using DogdouSpec.Core.Workspace;

namespace DogdouSpec.Cli.Commands;

public static class KnowledgeCommand
{
    public static Command BuildCommand()
    {
        var command = new Command("knowledge", "Manage verified and reusable project knowledge");
        command.Add(BuildAddCommand());
        command.Add(BuildListCommand());
        return command;
    }

    private static Command BuildAddCommand()
    {
        var command = new Command("add", "Create a proposed knowledge entry (mutating unless --dry-run)");
        var id = RequiredString("--id", "Time-first knowledge entry ID");
        var operationId = RequiredString("--operation-id", "Time-first replay operation ID");
        var actor = RequiredString("--actor", "Actor attribution (provenance, not authenticated identity)");
        var occurredAt = RequiredString("--occurred-at", "UTC or offset timestamp for deterministic replay");
        var topic = RequiredString("--topic", "Knowledge topic index term");
        var summary = RequiredString("--summary", "Short indexed summary");
        var statement = RequiredString("--statement", "Reusable fact statement");
        var rationale = RequiredString("--rationale", "Provenance and why it should be reusable");
        var sourceIteration = Repeatable("--source-iteration", "Source iteration ID (repeatable; at least one --source-iteration or --source-task required)");
        var sourceTask = Repeatable("--source-task", "Source task ID (repeatable; at least one --source-iteration or --source-task required)");
        var expectedRevision = RequiredRevision();
        var dryRun = new Option<bool>("--dry-run") { Description = "Validate mutation preconditions and report prospective revision without writing" };
        var workspaceRoot = WorkspaceRoot();
        var format = FormatOption();

        foreach (var option in new Option[]
                 { id, operationId, actor, occurredAt, topic, summary, statement, rationale,
                   sourceIteration, sourceTask, expectedRevision, dryRun, workspaceRoot, format })
        {
            command.Add(option);
        }

        command.SetAction(parseResult =>
        {
            var outputFormat = WorkspaceCommand.ResolveFormat(parseResult.GetValue(format));
            if (!TryParseOccurredAt(parseResult.GetValue(occurredAt), out var timestamp))
            {
                return WriteError("knowledge add", outputFormat,
                    Diagnostic.Error(DiagnosticCodes.InvalidArgument, "--occurred-at must be a valid ISO-8601 timestamp."));
            }
            var (found, root, discoveryError) = WorkspaceDiscovery.FindWorkspaceRoot(
                parseResult.GetValue(workspaceRoot), Environment.CurrentDirectory);
            if (!found || discoveryError != null)
            {
                return WriteError("knowledge add", outputFormat, discoveryError!);
            }
            var (revOk, resolvedRev, revErr) = DocumentRevisionResolver.ResolveExpectedRevision(
                root, "knowledge.xml", parseResult.GetValue(expectedRevision));
            if (!revOk || revErr != null)
            {
                return WriteError("knowledge add", outputFormat, revErr!);
            }
            var isDryRun = parseResult.GetValue(dryRun);
            var input = new KnowledgeCreateInput(
                parseResult.GetValue(id)!,
                parseResult.GetValue(operationId)!,
                parseResult.GetValue(actor)!,
                timestamp,
                parseResult.GetValue(topic)!,
                parseResult.GetValue(summary)!,
                parseResult.GetValue(statement)!,
                parseResult.GetValue(rationale)!,
                parseResult.GetValue(sourceIteration) ?? Array.Empty<string>(),
                parseResult.GetValue(sourceTask) ?? Array.Empty<string>());
            var (success, envelope, diagnostics) = KnowledgeLifecycle.Add(root, resolvedRev, input, dryRun: isDryRun);
            return WriteMutationResult("knowledge add", outputFormat, success, envelope, diagnostics);
        });
        return command;
    }

    private static Command BuildListCommand()
    {
        var command = new Command("list", "List knowledge entries deterministically (read-only)");
        var status = OptionalString("--status", "Filter by lifecycle status");
        status.AcceptOnlyFromAmong("proposed", "verified", "retired", "rejected");
        var topic = OptionalString("--topic", "Filter by topic index term");
        var workspaceRoot = WorkspaceRoot();
        var format = FormatOption();

        command.Add(status);
        command.Add(topic);
        command.Add(workspaceRoot);
        command.Add(format);

        command.SetAction(parseResult =>
        {
            var outputFormat = WorkspaceCommand.ResolveFormat(parseResult.GetValue(format));
            var (found, root, discoveryError) = WorkspaceDiscovery.FindWorkspaceRoot(
                parseResult.GetValue(workspaceRoot), Environment.CurrentDirectory);
            if (!found || discoveryError != null)
            {
                return WriteError("knowledge list", outputFormat, discoveryError!);
            }
            var (success, result, diagnostics) = KnowledgeLifecycle.List(
                root, parseResult.GetValue(status), parseResult.GetValue(topic));
            if (!success || diagnostics.Count > 0 || result == null)
            {
                return WriteDiagnostics("knowledge list", outputFormat, diagnostics);
            }
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
        Description = "Expected positive knowledge.xml revision (optional; defaults to current knowledge.xml revision)"
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