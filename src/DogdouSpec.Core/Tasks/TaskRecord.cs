using System.Globalization;
using System.Security;
using System.Text;
using System.Xml.Linq;
using DogdouSpec.Core.Diagnostics;
using DogdouSpec.Core.Progression;
using DogdouSpec.Core.Revisions;
using DogdouSpec.Core.Time;
using DogdouSpec.Core.Transactions;
using DogdouSpec.Core.Validation;
using DogdouSpec.Core.Workspace;

namespace DogdouSpec.Core.Tasks;

public static class TaskRecord
{
    private static readonly HashSet<string> ValidKinds = new(StringComparer.OrdinalIgnoreCase)
    {
        "verification", "finding", "resolution", "completion", "discussion",
        "question", "attempt", "decision", "handoff", "start"
    };

    private static readonly HashSet<string> ValidStatuses = new(StringComparer.OrdinalIgnoreCase)
    {
        "informational", "active", "resolved", "superseded"
    };

    public static (bool Success, MutationEnvelope? Envelope, IReadOnlyList<Diagnostic> Diagnostics) Record(
        string workspaceRoot,
        string taskId,
        string kind,
        string summary,
        string? iterationId = null,
        int? expectedRevision = null,
        string? status = null,
        string? actor = null,
        IReadOnlyList<string>? covers = null,
        IReadOnlyList<string>? resolve = null,
        string? context = null,
        string? impact = null,
        string? outcome = null,
        string? occurredAt = null,
        string? operationId = null,
        string? recordId = null,
        IClock? clock = null)
    {
        clock ??= SystemClock.Instance;
        actor = string.IsNullOrWhiteSpace(actor) ? "agent" : actor.Trim();

        if (string.IsNullOrWhiteSpace(taskId))
        {
            return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.InvalidArgument, "Task ID cannot be empty.") });
        }

        if (string.IsNullOrWhiteSpace(kind))
        {
            return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.InvalidArgument, "Record kind is required.") });
        }

        var normalizedKind = kind.Trim().ToLowerInvariant();
        if (!ValidKinds.Contains(normalizedKind))
        {
            return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.InvalidArgument, $"Invalid record kind '{kind}'. Supported kinds: verification, finding, resolution, completion, discussion.") });
        }

        if (string.IsNullOrWhiteSpace(summary))
        {
            return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.InvalidArgument, "Record summary is required.") });
        }

        var (discoverSuccess, root, discoverError) = WorkspaceDiscovery.FindWorkspaceRoot(workspaceRoot, Environment.CurrentDirectory);
        if (!discoverSuccess || discoverError != null)
        {
            return (false, null, new[] { discoverError ?? Diagnostic.Error(DiagnosticCodes.WorkspaceNotFound, "Workspace root could not be determined.") });
        }

        var (iterOk, resolvedIterId, iterErr) = ProgressionEngine.ResolveTargetIteration(root, iterationId);
        if (!iterOk || iterErr != null || string.IsNullOrEmpty(resolvedIterId))
        {
            return (false, null, new[] { iterErr ?? Diagnostic.Error(DiagnosticCodes.InvalidArgument, "Failed to resolve iteration.") });
        }

        var tasksPath = Path.Combine(root, resolvedIterId, "tasks.xml");
        if (!File.Exists(tasksPath))
        {
            return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.ResourceNotFound, $"tasks.xml not found for iteration '{resolvedIterId}'.", $"{resolvedIterId}/tasks.xml") });
        }

        XDocument tasksDoc;
        try
        {
            tasksDoc = XDocument.Load(tasksPath);
        }
        catch (Exception ex)
        {
            return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.XmlParseError, $"Failed to load tasks.xml: {ex.Message}", $"{resolvedIterId}/tasks.xml") });
        }

        var taskElem = tasksDoc.Descendants("task").FirstOrDefault(t => string.Equals((string?)t.Attribute("id"), taskId, StringComparison.Ordinal));
        if (taskElem == null)
        {
            return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.ResourceNotFound, $"Task '{taskId}' not found in iteration '{resolvedIterId}'.", $"{resolvedIterId}/tasks.xml") });
        }

        var currentStatus = (string?)taskElem.Attribute("status") ?? "pending";
        var isTerminal = currentStatus is "done" or "transferred" or "superseded" or "cancelled";

        string resolvedStatus;
        if (!string.IsNullOrWhiteSpace(status))
        {
            resolvedStatus = status.Trim().ToLowerInvariant();
            if (!ValidStatuses.Contains(resolvedStatus))
            {
                return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.InvalidArgument, $"Invalid record status '{status}'. Supported statuses: informational, active, resolved, superseded.") });
            }
        }
        else
        {
            if (isTerminal)
            {
                resolvedStatus = "informational";
            }
            else if (normalizedKind == "finding")
            {
                resolvedStatus = "active";
            }
            else if (normalizedKind == "resolution")
            {
                resolvedStatus = "resolved";
            }
            else
            {
                resolvedStatus = "informational";
            }
        }

        var (revOk, resolvedRev, revErr) = DocumentRevisionResolver.ResolveExpectedRevision(root, $"{resolvedIterId}/tasks.xml", expectedRevision);
        if (!revOk || revErr != null)
        {
            return (false, null, new[] { revErr ?? Diagnostic.Error(DiagnosticCodes.RevisionConflict, "Failed to resolve expected revision.") });
        }

        DateTimeOffset recordTime;
        if (!string.IsNullOrWhiteSpace(occurredAt))
        {
            if (!DateTimeOffset.TryParse(occurredAt, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal, out recordTime))
            {
                return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.InvalidArgument, $"Invalid occurred-at timestamp '{occurredAt}'. Expected ISO 8601 UTC format (e.g. 2026-09-25T12:00:00Z).") });
            }
        }
        else
        {
            recordTime = clock.UtcNow;
            var taskCreatedAtStr = (string?)taskElem.Attribute("created_at");
            if (!string.IsNullOrWhiteSpace(taskCreatedAtStr) && DateTimeOffset.TryParse(taskCreatedAtStr, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal, out var parsedCreated) && recordTime < parsedCreated)
            {
                recordTime = parsedCreated;
            }

            var taskUpdatedAtStr = (string?)taskElem.Attribute("updated_at");
            if (!string.IsNullOrWhiteSpace(taskUpdatedAtStr) && DateTimeOffset.TryParse(taskUpdatedAtStr, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal, out var parsedUpdated) && recordTime < parsedUpdated)
            {
                recordTime = parsedUpdated;
            }
        }

        var isoTime = recordTime.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
        var opId = !string.IsNullOrWhiteSpace(operationId) ? operationId.Trim() : $"{recordTime:yyyyMMddTHHmmssZ}-taskrecord-{Guid.NewGuid():N}";
        var recId = !string.IsNullOrWhiteSpace(recordId) ? recordId.Trim() : $"{recordTime:yyyyMMddTHHmmssZ}-record-{normalizedKind}-{Guid.NewGuid():N}";

        // Build resolve-records section if resolve targets specified
        var resolveList = resolve?.Where(r => !string.IsNullOrWhiteSpace(r)).Select(r => r.Trim()).Distinct(StringComparer.Ordinal).ToList() ?? new List<string>();
        var resolveRecordsSb = new StringBuilder();
        if (resolveList.Count > 0)
        {
            resolveRecordsSb.AppendLine("  <resolve-records>");
            foreach (var rId in resolveList)
            {
                resolveRecordsSb.AppendLine(CultureInfo.InvariantCulture, $"    <record target=\"{SecurityElement.Escape(rId)}\"/>");
            }
            resolveRecordsSb.Append("  </resolve-records>");
        }

        // Build covers section (includes --covers and --resolve)
        var coversList = covers?.Where(c => !string.IsNullOrWhiteSpace(c)).Select(c => c.Trim()).Distinct(StringComparer.Ordinal).ToList() ?? new List<string>();
        var coversSb = new StringBuilder();
        if (coversList.Count > 0 || resolveList.Count > 0)
        {
            coversSb.AppendLine("      <covers>");
            foreach (var cov in coversList)
            {
                coversSb.AppendLine(CultureInfo.InvariantCulture, $"        <ref scope=\"document\" target=\"{SecurityElement.Escape(cov)}\" relation=\"covers\"/>");
            }
            foreach (var res in resolveList)
            {
                if (!coversList.Contains(res, StringComparer.Ordinal))
                {
                    coversSb.AppendLine(CultureInfo.InvariantCulture, $"        <ref scope=\"document\" target=\"{SecurityElement.Escape(res)}\" relation=\"resolves\"/>");
                }
            }
            coversSb.Append("      </covers>");
        }

        var contextElem = !string.IsNullOrWhiteSpace(context)
            ? $"      <context>{SecurityElement.Escape(context.Trim())}</context>\n"
            : string.Empty;

        var impactElem = !string.IsNullOrWhiteSpace(impact)
            ? $"      <impact>{SecurityElement.Escape(impact.Trim())}</impact>\n"
            : string.Empty;

        var outcomeElem = !string.IsNullOrWhiteSpace(outcome)
            ? $"      <outcome>{SecurityElement.Escape(outcome.Trim())}</outcome>\n"
            : string.Empty;

        var resolveRecordsXml = resolveRecordsSb.Length > 0 ? resolveRecordsSb.ToString() : string.Empty;
        var coversXml = coversSb.Length > 0 ? coversSb.ToString() : string.Empty;

        var requestXml = $"""
<?xml version="1.0" encoding="utf-8"?>
<task-update
  id="{opId}"
  actor="{actor}"
  occurred_at="{isoTime}">
{resolveRecordsXml}
  <records>
    <record
      id="{recId}"
      kind="{normalizedKind}"
      status="{resolvedStatus}"
      created_at="{isoTime}"
      actor="{actor}"
      operation_id="{opId}">
      <summary>{SecurityElement.Escape(summary.Trim())}</summary>
{contextElem}{impactElem}{outcomeElem}{coversXml}
    </record>
  </records>
</task-update>
""";

        return TaskUpdater.Update(
            root,
            resolvedIterId,
            taskId,
            resolvedRev,
            requestXml,
            clock: clock);
    }
}
