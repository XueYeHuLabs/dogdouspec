using System.Globalization;
using System.Security;
using System.Xml.Linq;
using DogdouSpec.Core.Append;
using DogdouSpec.Core.Diagnostics;
using DogdouSpec.Core.Progression;
using DogdouSpec.Core.Revisions;
using DogdouSpec.Core.Security;
using DogdouSpec.Core.Time;
using DogdouSpec.Core.Transactions;
using DogdouSpec.Core.Validation;
using DogdouSpec.Core.Workspace;

namespace DogdouSpec.Core.Tasks;

public static class TaskBlock
{
    public static (bool Success, MutationEnvelope? Envelope, IReadOnlyList<Diagnostic> Diagnostics) Block(
        string workspaceRoot,
        string taskId,
        string? iterationId = null,
        int? expectedRevision = null,
        string? actor = null,
        string? summary = null,
        string? blockerKind = "external",
        string? blockerOwner = "owner",
        string? blockerReviewAt = null,
        string? condition = null,
        string? nextAction = null,
        IClock? clock = null)
    {
        clock ??= SystemClock.Instance;
        actor = string.IsNullOrWhiteSpace(actor) ? "agent" : actor.Trim();
        blockerKind = string.IsNullOrWhiteSpace(blockerKind) ? "external" : blockerKind.Trim();
        blockerOwner = string.IsNullOrWhiteSpace(blockerOwner) ? "owner" : blockerOwner.Trim();

        if (string.IsNullOrWhiteSpace(taskId))
        {
            return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.InvalidArgument, "Task ID cannot be empty.") });
        }

        if (string.IsNullOrWhiteSpace(summary))
        {
            return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.InvalidArgument, "Summary rationale is required when blocking a task.") });
        }

        if (!ProjectSemanticIndex.IsValidToken(blockerKind))
        {
            return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.InvalidArgument, $"Invalid blocker-kind '{blockerKind}'. Must conform to token grammar [a-zA-Z0-9][a-zA-Z0-9.-]*.") });
        }

        if (!ProjectSemanticIndex.IsValidToken(blockerOwner))
        {
            return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.InvalidArgument, $"Invalid blocker-owner '{blockerOwner}'. Must conform to token grammar [a-zA-Z0-9][a-zA-Z0-9.-]*.") });
        }

        if (!string.IsNullOrWhiteSpace(blockerReviewAt))
        {
            if (!CompactUtcTime.TryParse(blockerReviewAt, out _))
            {
                return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.InvalidArgument, $"Invalid blocker-review-at format '{blockerReviewAt}'. Expected compact UTC format 'yyyyMMddTHHmmssZ' (e.g. 20260909T120000Z).") });
            }
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
        if (string.Equals(currentStatus, "pending", StringComparison.OrdinalIgnoreCase))
        {
            return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.TaskTransitionConflict, $"Cannot block task '{taskId}': pending tasks cannot transition directly to blocked. Pending tasks wait on dependencies or start work.", $"{resolvedIterId}/tasks.xml") });
        }

        if (currentStatus is "done" or "transferred" or "superseded" or "cancelled")
        {
            return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.TaskImmutable, $"Cannot block task '{taskId}': task has terminal disposition '{currentStatus}' and is immutable.", $"{resolvedIterId}/tasks.xml") });
        }

        // Resolve expected revision
        var (revOk, resolvedRev, revErr) = DocumentRevisionResolver.ResolveExpectedRevision(root, $"{resolvedIterId}/tasks.xml", expectedRevision);
        if (!revOk || revErr != null)
        {
            return (false, null, new[] { revErr ?? Diagnostic.Error(DiagnosticCodes.RevisionConflict, "Failed to resolve expected revision.") });
        }

        var nowUtc = clock.UtcNow;
        var isoTime = nowUtc.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
        var opId = $"{nowUtc:yyyyMMddTHHmmssZ}-taskblock-{Guid.NewGuid():N}";
        var recId = $"{nowUtc:yyyyMMddTHHmmssZ}-record-finding-{Guid.NewGuid():N}";

        string transitionAttr = string.Equals(currentStatus, "blocked", StringComparison.OrdinalIgnoreCase)
            ? string.Empty
            : "transition=\"block\"";

        var reviewTerm = !string.IsNullOrWhiteSpace(blockerReviewAt)
            ? $"<term key=\"blocker-review-at\" value=\"{SecurityElement.Escape(blockerReviewAt.Trim())}\"/>"
            : string.Empty;

        var conditionText = SecurityElement.Escape(string.IsNullOrWhiteSpace(condition) ? "Resolution condition: external resolution required" : condition.Trim());
        var outcomeText = SecurityElement.Escape(string.IsNullOrWhiteSpace(nextAction) ? "Resume task once blocker is resolved" : nextAction.Trim());

        var requestXml = $"""
<?xml version="1.0" encoding="utf-8"?>
<task-update
  id="{opId}"
  {transitionAttr}
  actor="{actor}"
  occurred_at="{isoTime}">
  <records>
    <record
      id="{recId}"
      kind="finding"
      status="active"
      created_at="{isoTime}"
      actor="{actor}"
      operation_id="{opId}">
      <index>
        <summary>{SecurityElement.Escape(summary.Trim())}</summary>
        <term key="blocker-kind" value="{SecurityElement.Escape(blockerKind)}"/>
        <term key="blocker-owner" value="{SecurityElement.Escape(blockerOwner)}"/>
        {reviewTerm}
      </index>
      <summary>{SecurityElement.Escape(summary.Trim())}</summary>
      <context>{conditionText}</context>
      <outcome>{outcomeText}</outcome>
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
