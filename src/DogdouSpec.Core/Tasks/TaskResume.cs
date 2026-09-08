using System.Globalization;
using System.Security;
using System.Xml.Linq;
using DogdouSpec.Core.Append;
using DogdouSpec.Core.Diagnostics;
using DogdouSpec.Core.Progression;
using DogdouSpec.Core.Revisions;
using DogdouSpec.Core.Time;
using DogdouSpec.Core.Transactions;
using DogdouSpec.Core.Validation;
using DogdouSpec.Core.Workspace;

namespace DogdouSpec.Core.Tasks;

public static class TaskResume
{
    public static (bool Success, MutationEnvelope? Envelope, IReadOnlyList<Diagnostic> Diagnostics) Resume(
        string workspaceRoot,
        string taskId,
        string? iterationId = null,
        int? expectedRevision = null,
        string? actor = null,
        string? summary = null,
        string? findingId = null,
        bool all = false,
        IClock? clock = null)
    {
        clock ??= SystemClock.Instance;
        actor = string.IsNullOrWhiteSpace(actor) ? "agent" : actor.Trim();

        if (string.IsNullOrWhiteSpace(taskId))
        {
            return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.InvalidArgument, "Task ID cannot be empty.") });
        }

        var (discoverSuccess, root, discoverError) = WorkspaceDiscovery.FindWorkspaceRoot(workspaceRoot, Environment.CurrentDirectory);
        if (!discoverSuccess || discoverError != null)
        {
            return (false, null, new[] { discoverError ?? Diagnostic.Error(DiagnosticCodes.WorkspaceNotFound, "Workspace root could not be determined.") });
        }

        // Resolve iteration ID
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
        if (!string.Equals(currentStatus, "blocked", StringComparison.OrdinalIgnoreCase))
        {
            return (false, null, new[] { Diagnostic.Error(
                DiagnosticCodes.TaskTransitionConflict,
                $"Cannot resume task '{taskId}': task is in status '{currentStatus}', but only 'blocked' tasks can be resumed.",
                $"{resolvedIterId}/tasks.xml") });
        }

        // Check iteration status
        var specPath = Path.Combine(root, resolvedIterId, "spec.xml");
        if (File.Exists(specPath))
        {
            try
            {
                var specDoc = XDocument.Load(specPath);
                var iterStatus = specDoc.Root?.Attribute("status")?.Value ?? "draft";
                if (string.Equals(iterStatus, "replanning", StringComparison.OrdinalIgnoreCase))
                {
                    return (false, null, new[] { Diagnostic.Error(
                        DiagnosticCodes.OwnerDecisionRequired,
                        $"Cannot resume task '{taskId}': iteration '{resolvedIterId}' is currently in status 'replanning'. Execution transitions are frozen during replanning.",
                        $"{resolvedIterId}/tasks.xml") });
                }
            }
            catch { }
        }

        // Collect active findings on task
        var recordsElem = taskElem.Element("records");
        var activeFindings = recordsElem?.Elements("record")
            .Where(r => string.Equals(r.Attribute("kind")?.Value, "finding", StringComparison.Ordinal) &&
                        string.Equals(r.Attribute("status")?.Value, "active", StringComparison.Ordinal))
            .ToList() ?? new List<XElement>();

        if (activeFindings.Count > 0)
        {
            if (all && !string.IsNullOrWhiteSpace(findingId))
            {
                return (false, null, new[] { Diagnostic.Error(
                    DiagnosticCodes.InvalidArgument,
                    $"Cannot specify both '--finding' and '--all' when resuming task '{taskId}'.",
                    $"{resolvedIterId}/tasks.xml") });
            }

            if (!all && string.IsNullOrWhiteSpace(findingId))
            {
                return (false, null, new[] { Diagnostic.Error(
                    DiagnosticCodes.InvalidArgument,
                    $"Task '{taskId}' has {activeFindings.Count} active blocker finding(s). You must explicitly specify resolution intent using either '--finding <id>' or '--all'.",
                    $"{resolvedIterId}/tasks.xml") });
            }

            if (string.IsNullOrWhiteSpace(summary))
            {
                return (false, null, new[] { Diagnostic.Error(
                    DiagnosticCodes.InvalidArgument,
                    $"A non-empty summary explaining the blocker resolution is required when resolving active findings on task '{taskId}'.",
                    $"{resolvedIterId}/tasks.xml") });
            }
        }

        var (revOk, resolvedRev, revErr) = DocumentRevisionResolver.ResolveExpectedRevision(root, $"{resolvedIterId}/tasks.xml", expectedRevision);
        if (!revOk || revErr != null)
        {
            return (false, null, new[] { revErr ?? Diagnostic.Error(DiagnosticCodes.RevisionConflict, "Failed to resolve expected revision.") });
        }

        var nowUtc = clock.UtcNow;
        var isoTime = nowUtc.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
        var opId = $"{nowUtc:yyyyMMddTHHmmssZ}-taskresume-{Guid.NewGuid():N}";
        var recId = $"{nowUtc:yyyyMMddTHHmmssZ}-record-resolution-{Guid.NewGuid():N}";

        string resolveRecordsXml;
        string coversXml;
        string transitionAttr;

        if (!string.IsNullOrWhiteSpace(findingId))
        {
            var targetFinding = activeFindings.FirstOrDefault(r => string.Equals((string?)r.Attribute("id"), findingId, StringComparison.Ordinal));
            if (targetFinding == null)
            {
                return (false, null, new[] { Diagnostic.Error(
                    DiagnosticCodes.ResourceNotFound,
                    $"Active finding '{findingId}' not found on task '{taskId}'.",
                    $"{resolvedIterId}/tasks.xml") });
            }

            resolveRecordsXml = $"""
  <resolve-records>
    <record target="{findingId}"/>
  </resolve-records>
""";
            coversXml = $"""
      <covers>
        <ref scope="document" target="{findingId}" relation="resolves"/>
      </covers>
""";

            var otherActiveFindings = activeFindings.Where(r => !string.Equals((string?)r.Attribute("id"), findingId, StringComparison.Ordinal)).ToList();
            if (otherActiveFindings.Count > 0)
            {
                // Other blockers remain; resolve this finding without transitioning to in-progress
                transitionAttr = string.Empty;
            }
            else
            {
                // All blockers resolved; transition to in-progress
                transitionAttr = "transition=\"resume\"";
            }
        }
        else
        {
            // Resuming all active findings on task
            if (activeFindings.Count > 0)
            {
                var sbResolve = new System.Text.StringBuilder();
                sbResolve.AppendLine("  <resolve-records>");
                foreach (var f in activeFindings)
                {
                    var fId = (string?)f.Attribute("id") ?? string.Empty;
                    sbResolve.AppendLine(CultureInfo.InvariantCulture, $"    <record target=\"{fId}\"/>");
                }
                sbResolve.Append("  </resolve-records>");
                resolveRecordsXml = sbResolve.ToString();

                var sbCovers = new System.Text.StringBuilder();
                sbCovers.AppendLine("      <covers>");
                foreach (var f in activeFindings)
                {
                    var fId = (string?)f.Attribute("id") ?? string.Empty;
                    sbCovers.AppendLine(CultureInfo.InvariantCulture, $"        <ref scope=\"document\" target=\"{fId}\" relation=\"resolves\"/>");
                }
                sbCovers.Append("      </covers>");
                coversXml = sbCovers.ToString();
            }
            else
            {
                resolveRecordsXml = string.Empty;
                coversXml = string.Empty;
            }

            transitionAttr = "transition=\"resume\"";
        }

        var resumeSummary = SecurityElement.Escape(string.IsNullOrWhiteSpace(summary) ? $"Resumed task {taskId}." : summary.Trim());

        var requestXml = $"""
<?xml version="1.0" encoding="utf-8"?>
<task-update
  id="{opId}"
  {transitionAttr}
  actor="{actor}"
  occurred_at="{isoTime}">
{resolveRecordsXml}
  <records>
    <record
      id="{recId}"
      kind="resolution"
      status="resolved"
      created_at="{isoTime}"
      actor="{actor}"
      operation_id="{opId}">
      <summary>{resumeSummary}</summary>
{coversXml}
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
