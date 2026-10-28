using System.Text;
using System.Xml.Linq;
using DogdouSpec.Core.Diagnostics;
using DogdouSpec.Core.Progression;
using DogdouSpec.Core.Serialization;
using DogdouSpec.Core.Validation;
using DogdouSpec.Core.Workspace;
using DogdouSpec.Core.XPath;

namespace DogdouSpec.Core.Tasks;

/// <summary>Factual review projections; informational notes never satisfy a formal submission.</summary>
public static class TaskReviewHistory
{
    private static readonly string[] Actions = ["status", "record", "records"];
    public static string Command(string action, string taskId, string? iterationId = null) =>
        $"dogdouspec task review {action} --task {taskId}" +
        (string.IsNullOrEmpty(iterationId) ? string.Empty : $" --iteration {iterationId}");

    public static (bool Success, XElement? Result, IReadOnlyList<Diagnostic> Diagnostics) Status(
        string workspaceRoot, string taskId, string? iterationId = null, int maxBytes = 32768)
    {
        var (loaded, snapshot, diagnostics) = Load(workspaceRoot, taskId, iterationId);
        if (!loaded || snapshot == null) return (false, null, diagnostics);
        var result = Summary(snapshot.Task, snapshot.IterationId, snapshot.Revision);
        result.Name = "task-review-status";
        return Bound(result, maxBytes);
    }

    public static (bool Success, XElement? Result, IReadOnlyList<Diagnostic> Diagnostics) Records(
        string workspaceRoot, string taskId, string? iterationId = null, int last = 5, int maxBytes = 32768)
    {
        if (last is <= 0 or > XPathQueryLimits.MaxResultNodes)
            return Failure("--last must be between 1 and 10000.");
        var (loaded, snapshot, diagnostics) = Load(workspaceRoot, taskId, iterationId);
        if (!loaded || snapshot == null) return (false, null, diagnostics);
        var entries = Entries(snapshot.Task);
        var selected = entries.TakeLast(last).ToArray();
        var result = new XElement("task-review-records", new XAttribute("iteration", snapshot.IterationId),
            new XAttribute("task", taskId), new XAttribute("tasks_revision", snapshot.Revision),
            new XAttribute("total_records", entries.Count), new XAttribute("count", selected.Length),
            new XAttribute("omitted_records", entries.Count - selected.Length),
            new XAttribute("truncated", entries.Count > selected.Length), selected);
        return Bound(result, maxBytes);
    }

    public static XElement Summary(XElement task, string iterationId, int revision)
    {
        var entries = Entries(task);
        var notes = entries.Where(e => (string?)e.Attribute("source") == "record").ToArray();
        var submissions = task.Element("review")?.Elements("submission").ToArray() ?? [];
        var result = new XElement("review-summary", new XAttribute("iteration", iterationId),
            new XAttribute("task", (string?)task.Attribute("id") ?? string.Empty),
            new XAttribute("task_status", (string?)task.Attribute("status") ?? string.Empty),
            new XAttribute("tasks_revision", revision),
            new XElement("configuration", new XAttribute("required", (bool?)task.Element("review")?.Attribute("required") ?? false)),
            new XElement("recorded-reviews", new XAttribute("count", notes.Length)),
            new XElement("submission", new XAttribute("present", submissions.Length > 0), new XAttribute("count", submissions.Length)));
        if (notes.LastOrDefault() is { } note) result.Add(Compact(note, "latest-record"));
        if (submissions.LastOrDefault() is { } submission)
        {
            var latest = new XElement("latest-submission", submission.Attributes().Select(a => new XAttribute(a)));
            var linked = entries.LastOrDefault(e => (string?)e.Attribute("submission") == (string?)submission.Attribute("id"));
            if (linked != null) latest.Add(ShortSummary(linked.Element("record")?.Element("summary")?.Value ?? string.Empty));
            result.Add(latest);
        }
        var taskId = (string?)task.Attribute("id") ?? string.Empty;
        result.Add(new XElement("commands", Actions.Select(action =>
            new XElement("command", new XAttribute("action", action), Command(action, taskId, iterationId) +
                (action == "record" ? " --summary \"...\"" : string.Empty)))));
        return result;
    }

    public static XElement ContextSummary(XElement task, string iterationId, int revision)
    {
        var result = Summary(task, iterationId, revision);
        result.RemoveAttributes();
        result.Element("commands")!.ReplaceNodes(new XElement("command", new XAttribute("action", "status"),
            Command("status", (string?)task.Attribute("id") ?? string.Empty, iterationId)));
        return result;
    }

    internal sealed record Snapshot(string Root, string IterationId, XElement Task, int Revision);

    internal static (bool Success, Snapshot? Result, IReadOnlyList<Diagnostic> Diagnostics) Load(
        string workspaceRoot, string taskId, string? iterationId)
    {
        if (!ProjectSemanticIndex.IsValidTimeFirstId(taskId))
            return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.InvalidIdGrammar, "Supply a valid time-first task ID.") });
        var (found, root, error) = WorkspaceDiscovery.FindWorkspaceRoot(workspaceRoot, Environment.CurrentDirectory);
        if (!found || error != null) return (false, null, new[] { error! });
        var (resolved, iteration, iterationError) = ProgressionEngine.ResolveTargetIteration(root, iterationId);
        if (!resolved || iterationError != null) return (false, null, new[] { iterationError! });
        var (loaded, index, diagnostics) = ProjectSemanticIndex.LoadConsistent(root);
        if (!loaded || index == null) return (false, null, diagnostics);
        var document = index.TasksDocuments.FirstOrDefault(d => d.Document.IterationId == iteration);
        var task = document?.Element.Elements("task").SingleOrDefault(t => (string?)t.Attribute("id") == taskId);
        if (task == null) return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.ResourceNotFound,
            $"Task '{taskId}' was not found in iteration '{iteration}'.") });
        return (true, new Snapshot(root, iteration!, new XElement(task), (int)document!.Element.Attribute("revision")!), Array.Empty<Diagnostic>());
    }

    private static List<XElement> Entries(XElement task)
    {
        var submissions = task.Element("review")?.Elements("submission").ToArray() ?? [];
        var entries = new List<XElement>();
        foreach (var record in task.Element("records")?.Elements("record") ?? [])
        {
            var submission = submissions.LastOrDefault(s => (string?)s.Attribute("record") == (string?)record.Attribute("id"));
            var isNote = (string?)record.Attribute("kind") == "discussion" &&
                (string?)record.Attribute("status") == "informational" && Term(record, "activity") == "review";
            if (submission == null && !isNote) continue;
            var result = submission != null ? (string?)submission.Attribute("disposition") : Term(record, "review-result");
            entries.Add(new XElement("review", new XAttribute("source", submission == null ? "record" : "submission"),
                result == null ? null : new XAttribute("result", result),
                submission == null ? null : new XAttribute("submission", (string?)submission.Attribute("id") ?? string.Empty),
                new XElement(record)));
        }
        return entries;
    }

    private static string? Term(XElement record, string key) => record.Element("index")?.Elements("term")
        .FirstOrDefault(t => (string?)t.Attribute("key") == key)?.Attribute("value")?.Value;

    private static XElement Compact(XElement entry, string name)
    {
        var record = entry.Element("record")!;
        return new XElement(name, entry.Attributes().Select(a => new XAttribute(a)),
            record.Attributes().Where(a => a.Name.LocalName is "id" or "actor" or "created_at").Select(a => new XAttribute(a)),
            ShortSummary(record.Element("summary")?.Value ?? string.Empty));
    }

    private static XElement ShortSummary(string summary)
    {
        var length = Math.Min(summary.Length, 240);
        if (length < summary.Length && char.IsHighSurrogate(summary[length - 1])) length--;
        return new XElement("summary", length < summary.Length ? new XAttribute("truncated", true) : null, summary[..length]);
    }

    private static (bool, XElement?, IReadOnlyList<Diagnostic>) Bound(XElement result, int maxBytes)
    {
        if (maxBytes is <= 0 or > XPathQueryLimits.MaxOutputBytes) return Failure("--max-bytes must be between 1 and 4194304.");
        if (Encoding.UTF8.GetByteCount(ManagedDocumentSerializer.Serialize(result)) > maxBytes)
            return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.LimitExceeded,
                "Review output exceeds max-bytes. Reduce --last or increase --max-bytes.") });
        return (true, result, Array.Empty<Diagnostic>());
    }

    private static (bool, XElement?, IReadOnlyList<Diagnostic>) Failure(string message) =>
        (false, null, new[] { Diagnostic.Error(DiagnosticCodes.InvalidArgument, message) });
}
