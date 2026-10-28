using System.CommandLine;
using System.Globalization;
using System.Xml.Linq;
using DogdouSpec.Core.Diagnostics;
using DogdouSpec.Core.Formatting;
using DogdouSpec.Core.Serialization;
using DogdouSpec.Core.Tasks;
using DogdouSpec.Core.Workspace;

namespace DogdouSpec.Cli.Commands;

public static class TaskReviewExperienceCommand
{
    public static void AddTo(Command review)
    {
        review.Add(BuildRead("status"));
        review.Add(BuildRead("records"));
        review.Add(BuildRecord());
    }

    private static Command BuildRead(string action)
    {
        var command = new Command(action, action == "status"
            ? "Show review configuration, recorded results, formal submissions and useful commands (read-only)"
            : "Read recent reviews in append order with record/submission sources (read-only)");
        var task = new Option<string>("--task") { Required = true, Description = "Task ID" };
        var iteration = new Option<string?>("--iteration") { Description = "Iteration ID; omitted resolves the active iteration" };
        var last = new Option<int>("--last") { DefaultValueFactory = _ => 5, Description = "Recent reviews to read (default 5, maximum 10000)" };
        var bytes = new Option<int>("--max-bytes") { DefaultValueFactory = _ => 32768, Description = "Maximum UTF-8 output bytes (default 32768)" };
        var workspace = new Option<string?>("--workspace-root");
        var format = Format();
        foreach (var option in new Option[] { task, iteration, bytes, workspace, format }) command.Add(option);
        if (action == "records") command.Add(last);
        command.SetAction(parse =>
        {
            var output = WorkspaceCommand.ResolveFormat(parse.GetValue(format));
            var (found, root, error) = WorkspaceDiscovery.FindWorkspaceRoot(parse.GetValue(workspace), Environment.CurrentDirectory);
            if (!found || error != null) return Fail("task review " + action, output, [error!]);
            var (success, result, diagnostics) = action == "status"
                ? TaskReviewHistory.Status(root, parse.GetValue(task)!, parse.GetValue(iteration), parse.GetValue(bytes))
                : TaskReviewHistory.Records(root, parse.GetValue(task)!, parse.GetValue(iteration), parse.GetValue(last), parse.GetValue(bytes));
            if (!success || result == null) return Fail("task review " + action, output, diagnostics);
            if (output == OutputFormat.Xml) Console.Out.Write(ManagedDocumentSerializer.Serialize(result));
            else WriteHuman(result);
            return 0;
        });
        return command;
    }

    private static Command BuildRecord()
    {
        var command = new Command("record", "Record an actual review as an informational note; callers choose subsequent actions (mutating unless --dry-run)");
        var task = new Option<string>("--task") { Required = true, Description = "Task ID" };
        var summary = new Option<string>("--summary") { Required = true, Description = "Review explanation; never interpreted as formal approval" };
        var result = new Option<string?>("--result") { Description = "Optional caller-declared review result" };
        result.AcceptOnlyFromAmong("approved", "changes-requested");
        var actor = new Option<string?>("--actor") { Description = "Reported reviewer attribution (default agent; not authenticated identity)" };
        var iteration = new Option<string?>("--iteration") { Description = "Iteration ID; omitted resolves the active iteration" };
        var revision = new Option<int?>("--expected-revision") { Description = "Expected tasks.xml revision; omitted resolves the current revision" };
        var operation = new Option<string?>("--operation-id") { Description = "Optional replay operation ID; reuse returned identity, payload and revision for retries" };
        var occurredAt = new Option<string?>("--occurred-at") { Description = "Timestamp (default explicit operation UTC prefix, or automatic task time)" };
        var dryRun = new Option<bool>("--dry-run") { Description = "Preview the append without writing" };
        var workspace = new Option<string?>("--workspace-root");
        var format = Format();
        foreach (var option in new Option[] { task, summary, result, actor, iteration, revision, operation, occurredAt, dryRun, workspace, format }) command.Add(option);
        command.SetAction(parse =>
        {
            var output = WorkspaceCommand.ResolveFormat(parse.GetValue(format));
            var (found, root, error) = WorkspaceDiscovery.FindWorkspaceRoot(parse.GetValue(workspace), Environment.CurrentDirectory);
            if (!found || error != null) return Fail("task review record", output, [error!]);
            var (success, receipt, diagnostics) = TaskReviewNote.Record(root,
                new TaskReviewNoteInput(parse.GetValue(task)!, parse.GetValue(summary)!, parse.GetValue(iteration),
                    parse.GetValue(result), parse.GetValue(actor), parse.GetValue(revision), parse.GetValue(operation), parse.GetValue(occurredAt)),
                parse.GetValue(dryRun));
            if (!success || receipt == null) return Fail("task review record", output, diagnostics);
            if (output == OutputFormat.Xml)
            {
                var envelope = XDocument.Parse(receipt.Envelope.ToXmlString());
                envelope.Root!.Add(receipt.Identity());
                Console.Out.Write(ManagedDocumentSerializer.Serialize(envelope));
            }
            else
            {
                Console.Out.Write(receipt.Envelope.Format(output));
                Console.Out.WriteLine(string.Create(CultureInfo.InvariantCulture,
                    $"Review note: {receipt.RecordId}; operation: {receipt.OperationId}; actor: {receipt.Actor}; occurred_at: {receipt.OccurredAt}"));
                Console.Out.WriteLine(receipt.Envelope.DryRun
                    ? "Preview only; no informational review record was written."
                    : "Saved as an informational review record; task status and formal submissions are unchanged.");
            }
            return 0;
        });
        return command;
    }

    private static Option<string?> Format()
    {
        var format = new Option<string?>("--format");
        format.AcceptOnlyFromAmong("xml", "human");
        return format;
    }

    private static int Fail(string command, OutputFormat format, IReadOnlyList<Diagnostic> diagnostics)
    {
        var error = new DiagnosticsEnvelope(command, diagnostics);
        Console.Error.Write(error.Format(format));
        return error.GetExitCode();
    }

    private static void WriteHuman(XElement result)
    {
        Console.Out.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"Task reviews: {(string?)result.Attribute("task")} (revision {(string?)result.Attribute("tasks_revision")})"));
        if (result.Name == "task-review-records")
        {
            Console.Out.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"Reviews: {(string?)result.Attribute("count")} of {(string?)result.Attribute("total_records")}"));
            foreach (var entry in result.Elements("review"))
            {
                var record = entry.Element("record")!;
                Console.Out.WriteLine(string.Create(CultureInfo.InvariantCulture,
                    $"- [{(string?)entry.Attribute("source")}] {(string?)entry.Attribute("result") ?? "unspecified"}: {(string?)record.Attribute("actor")} ({(string?)record.Attribute("id")})"));
                Console.Out.WriteLine(record.Element("summary")?.Value);
            }
        }
        else
        {
            Console.Out.WriteLine(string.Create(CultureInfo.InvariantCulture, $"Task status: {(string?)result.Attribute("task_status")}; review required: {(string?)result.Element("configuration")?.Attribute("required")}"));
            Console.Out.WriteLine(string.Create(CultureInfo.InvariantCulture, $"Recorded reviews: {(string?)result.Element("recorded-reviews")?.Attribute("count")}; formal submissions: {(string?)result.Element("submission")?.Attribute("count")}"));
            foreach (var latest in result.Elements().Where(e => e.Name.LocalName is "latest-record" or "latest-submission"))
                Console.Out.WriteLine(string.Create(CultureInfo.InvariantCulture,
                    $"{latest.Name}: {(string?)latest.Attribute("result") ?? (string?)latest.Attribute("disposition") ?? "unspecified"}; {(string?)latest.Attribute("actor")}; {latest.Element("summary")?.Value}"));
            foreach (var suggestion in result.Element("commands")?.Elements("command") ?? []) Console.Out.WriteLine(suggestion.Value);
        }
    }
}
