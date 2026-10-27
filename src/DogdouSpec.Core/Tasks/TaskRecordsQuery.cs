using System.Globalization;
using System.Text;
using System.Xml.Linq;
using DogdouSpec.Core.Diagnostics;
using DogdouSpec.Core.Progression;
using DogdouSpec.Core.Validation;
using DogdouSpec.Core.Workspace;
using DogdouSpec.Core.XPath;

namespace DogdouSpec.Core.Tasks;

/// <summary>Read-only history projection; never substitutes for essential task context.</summary>
public static class TaskRecordsQuery
{
    private static readonly string[] DefaultFields = { "id", "kind", "status", "created_at", "summary" };
    private static readonly HashSet<string> AttributeFields = new(StringComparer.Ordinal)
        { "id", "kind", "status", "created_at", "actor", "operation_id" };
    private static readonly HashSet<string> ElementFields = new(StringComparer.Ordinal)
        { "index", "summary", "context", "options", "impact", "outcome", "checks", "covers", "sources" };

    public static (bool Success, XElement? Result, IReadOnlyList<Diagnostic> Diagnostics) Query(
        string workspaceRoot, string taskId, string? iterationId = null, int last = 20,
        IReadOnlyList<string>? kinds = null, IReadOnlyList<string>? fields = null, int? maxSummary = null, int maxBytes = 32768)
    {
        if (string.IsNullOrWhiteSpace(taskId) || last is <= 0 or > XPathQueryLimits.MaxResultNodes || maxSummary is <= 0 || maxBytes is <= 0 or > XPathQueryLimits.MaxOutputBytes)
            return Failure(DiagnosticCodes.InvalidArgument, "Supply a task ID, last between 1 and 10000, a positive max-summary, and max-bytes between 1 and 4194304.");
        var selectedFields = fields ?? DefaultFields;
        if (selectedFields.Count == 0 || selectedFields.Any(f => !AttributeFields.Contains(f) && !ElementFields.Contains(f)))
            return Failure(DiagnosticCodes.InvalidArgument, "Unknown or empty record fields. Use record attributes or index, summary, context, options, impact, outcome, checks, covers, sources.");
        var (found, root, discoveryError) = WorkspaceDiscovery.FindWorkspaceRoot(workspaceRoot, Environment.CurrentDirectory);
        if (!found || discoveryError != null) return (false, null, new[] { discoveryError! });
        var (resolved, iteration, iterationError) = ProgressionEngine.ResolveTargetIteration(root, iterationId);
        if (!resolved || iterationError != null) return (false, null, new[] { iterationError! });
        var (loaded, index, diagnostics) = ProjectSemanticIndex.LoadConsistent(root);
        if (!loaded || index == null) return (false, null, diagnostics);
        var document = index.TasksDocuments.FirstOrDefault(d => d.Document.IterationId == iteration);
        var task = document?.Element.Elements("task").SingleOrDefault(t => (string?)t.Attribute("id") == taskId);
        if (task == null) return Failure(DiagnosticCodes.ResourceNotFound, $"Task '{taskId}' was not found in iteration '{iteration}'.");
        var all = task.Element("records")?.Elements("record").ToArray() ?? Array.Empty<XElement>();
        var matching = all.Where(r => kinds == null || kinds.Count == 0 || kinds.Contains((string?)r.Attribute("kind") ?? string.Empty, StringComparer.Ordinal)).ToArray();
        var selected = matching.TakeLast(last).ToArray();
        var result = new XElement("task-records", new XAttribute("iteration", iteration!), new XAttribute("task", taskId),
            new XAttribute("tasks_revision", document!.Element.Attribute("revision")!.Value),
            new XAttribute("total_records", all.Length), new XAttribute("matching_records", matching.Length),
            new XAttribute("count", selected.Length), new XAttribute("omitted_records", matching.Length - selected.Length),
            new XAttribute("truncated", matching.Length > selected.Length ? "true" : "false"));
        foreach (var record in selected)
        {
            var projected = new XElement("record");
            foreach (var field in selectedFields.Distinct(StringComparer.Ordinal))
            {
                if (AttributeFields.Contains(field))
                {
                    if (record.Attribute(field) is { } attribute) projected.Add(new XAttribute(attribute));
                    continue;
                }
                if (record.Element(field) is not { } element) continue;
                var copy = new XElement(element);
                if (field == "summary" && maxSummary.HasValue && copy.Value.Length > maxSummary.Value)
                {
                    var length = maxSummary.Value;
                    if (char.IsHighSurrogate(copy.Value[length - 1])) length--;
                    projected.SetAttributeValue("summary_truncated", "true");
                    projected.SetAttributeValue("summary_original_characters", copy.Value.Length);
                    copy.Value = copy.Value[..length];
                    result.SetAttributeValue("truncated", "true");
                }
                projected.Add(copy);
            }
            result.Add(projected);
        }
        if (Encoding.UTF8.GetByteCount(result.ToString()) + 64 > maxBytes)
            return Failure(DiagnosticCodes.LimitExceeded, "Selected history exceeds max-bytes. Reduce --last, project fewer --fields, use --max-summary, or increase --max-bytes. Essential context remains available through task context.");
        return (true, result, Array.Empty<Diagnostic>());
    }

    private static (bool, XElement?, IReadOnlyList<Diagnostic>) Failure(string code, string message) =>
        (false, null, new[] { Diagnostic.Error(code, message) });
}
