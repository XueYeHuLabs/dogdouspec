using System.Globalization;
using System.Xml.Linq;
using DogdouSpec.Core.Diagnostics;
using DogdouSpec.Core.Progression;
using DogdouSpec.Core.Time;
using DogdouSpec.Core.Validation;
using DogdouSpec.Core.Workspace;

namespace DogdouSpec.Core.Tasks;

public static class TaskBlockers
{
    public static (bool Success, TaskBlockersResult? Result, IReadOnlyList<Diagnostic> Diagnostics) Query(
        string workspaceRoot,
        string? iterationId = null,
        string? taskId = null,
        string? owner = null,
        string? kind = null,
        bool dueOnly = false,
        IClock? clock = null,
        ProjectSemanticIndex? index = null)
    {
        clock ??= SystemClock.Instance;
        var nowUtc = clock.UtcNow;

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

        if (index == null)
        {
            var (loadOk, loadedIndex, loadDiags) = ProjectSemanticIndex.LoadConsistent(root);
            if (!loadOk || loadedIndex == null)
            {
                return (false, null, loadDiags);
            }
            index = loadedIndex;
        }

        var targetTasksDoc = index.TasksDocuments.FirstOrDefault(td =>
            string.Equals(td.Document.IterationId, resolvedIterId, StringComparison.Ordinal));
        if (targetTasksDoc == null)
        {
            return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.ResourceNotFound, $"tasks.xml not found for iteration '{resolvedIterId}'.", $"{resolvedIterId}/tasks.xml") });
        }

        var tasksRoot = targetTasksDoc.Element;
        if (tasksRoot == null || tasksRoot.Name.LocalName != "tasks")
        {
            return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.XmlParseError, $"Invalid or missing root element in '{resolvedIterId}/tasks.xml'.", $"{resolvedIterId}/tasks.xml") });
        }

        int tasksRevision = 1;
        var revStr = tasksRoot.Attribute("revision")?.Value;
        if (!string.IsNullOrWhiteSpace(revStr) && int.TryParse(revStr, CultureInfo.InvariantCulture, out var parsedRev))
        {
            tasksRevision = parsedRev;
        }

        var taskElements = tasksRoot.Elements("task").ToList();

        if (!string.IsNullOrWhiteSpace(taskId))
        {
            var exists = taskElements.Any(t => string.Equals((string?)t.Attribute("id"), taskId.Trim(), StringComparison.Ordinal));
            if (!exists)
            {
                return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.ResourceNotFound, $"Task '{taskId}' not found in iteration '{resolvedIterId}'.", $"{resolvedIterId}/tasks.xml") });
            }
        }

        var rawItems = new List<TaskBlockerItem>();
        int originalIndex = 0;

        foreach (var t in taskElements)
        {
            var tId = (string?)t.Attribute("id") ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(taskId) && !string.Equals(tId, taskId.Trim(), StringComparison.Ordinal))
            {
                continue;
            }

            var currentStatus = (string?)t.Attribute("status") ?? "pending";
            var title = t.Element("title")?.Value
                        ?? t.Element("index")?.Element("summary")?.Value
                        ?? tId;

            // A. Explicit findings on the task
            var recordsElem = t.Element("records");
            if (recordsElem != null)
            {
                foreach (var rec in recordsElem.Elements("record"))
                {
                    var rKind = (string?)rec.Attribute("kind");
                    var rStatus = (string?)rec.Attribute("status");

                    if (string.Equals(rKind, "finding", StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(rStatus, "active", StringComparison.OrdinalIgnoreCase))
                    {
                        var findingId = (string?)rec.Attribute("id") ?? string.Empty;
                        var createdAt = (string?)rec.Attribute("created_at");
                        var summary = rec.Element("summary")?.Value
                                      ?? rec.Element("index")?.Element("summary")?.Value
                                      ?? string.Empty;
                        var condition = rec.Element("context")?.Value;
                        var nextAction = rec.Element("outcome")?.Value;

                        var indexElem = rec.Element("index");
                        string blockerKind = "unknown";
                        string blockerOwner = "unspecified";
                        string blockerReviewAt = "none";

                        if (indexElem != null)
                        {
                            foreach (var term in indexElem.Elements("term"))
                            {
                                var key = (string?)term.Attribute("key");
                                var val = (string?)term.Attribute("value");
                                if (string.IsNullOrEmpty(val)) continue;

                                if (string.Equals(key, "blocker-kind", StringComparison.OrdinalIgnoreCase))
                                {
                                    blockerKind = val;
                                }
                                else if (string.Equals(key, "blocker-owner", StringComparison.OrdinalIgnoreCase))
                                {
                                    blockerOwner = val;
                                }
                                else if (string.Equals(key, "blocker-review-at", StringComparison.OrdinalIgnoreCase))
                                {
                                    blockerReviewAt = val;
                                }
                            }
                        }

                        DateTime? reviewAtUtc = null;
                        string dueStatus = "undated";
                        bool isDue = false;

                        if (!string.Equals(blockerReviewAt, "none", StringComparison.OrdinalIgnoreCase) &&
                            CompactUtcTime.TryParse(blockerReviewAt, out var parsedUtc))
                        {
                            reviewAtUtc = parsedUtc;
                            if (parsedUtc <= nowUtc)
                            {
                                dueStatus = "overdue";
                                isDue = true;
                            }
                            else
                            {
                                dueStatus = "upcoming";
                                isDue = false;
                            }
                        }

                        rawItems.Add(new TaskBlockerItem(
                            TaskId: tId,
                            TaskTitle: title,
                            TaskStatus: currentStatus,
                            FindingId: findingId,
                            Kind: blockerKind,
                            Owner: blockerOwner,
                            ReviewAt: blockerReviewAt,
                            ReviewAtUtc: reviewAtUtc,
                            DueStatus: dueStatus,
                            IsDue: isDue,
                            Summary: summary,
                            Condition: condition,
                            NextAction: nextAction,
                            CreatedAt: createdAt,
                            IsDerived: false,
                            OriginalIndex: originalIndex++));
                    }
                }
            }

            // B. Derived dependency blockers for non-terminal tasks
            var isTerminal = TaskDependencyGate.IsTerminalStatus(currentStatus);
            if (!isTerminal)
            {
                var (depSatisfied, depDiagnostics, depPreconditions) = TaskDependencyGate.EvaluateTaskDependencies(
                    root,
                    tId,
                    t,
                    targetTasksDoc.Document.RelativePath,
                    index);

                // Fail-closed on structural dependency errors
                var structDiags = depDiagnostics.Where(d =>
                    !string.Equals(d.Code, DiagnosticCodes.TaskTransitionConflict, StringComparison.Ordinal)).ToList();
                if (structDiags.Count > 0)
                {
                    return (false, null, structDiags);
                }

                var depsElem = t.Element("dependencies");
                if (depsElem != null)
                {
                    foreach (var depRef in depsElem.Elements("ref"))
                    {
                        var relation = (string?)depRef.Attribute("relation");
                        if (string.Equals(relation, "depends-on", StringComparison.OrdinalIgnoreCase))
                        {
                            var depTarget = (string?)depRef.Attribute("target");
                            if (!string.IsNullOrWhiteSpace(depTarget) && index.ObjectsById.TryGetValue(depTarget, out var targets) && targets.Count == 1)
                            {
                                var targetObj = targets[0];
                                var upstreamStatus = targetObj.Element.Attribute("status")?.Value ?? "pending";

                                if (!TaskDependencyGate.IsTerminalStatus(upstreamStatus))
                                {
                                    rawItems.Add(new TaskBlockerItem(
                                        TaskId: tId,
                                        TaskTitle: title,
                                        TaskStatus: currentStatus,
                                        FindingId: null,
                                        Kind: "dependency",
                                        Owner: "agent",
                                        ReviewAt: "none",
                                        ReviewAtUtc: null,
                                        DueStatus: "undated",
                                        IsDue: false,
                                        Summary: $"Waiting on upstream task '{depTarget}' (status: {upstreamStatus})",
                                        Condition: $"Upstream task '{depTarget}' must be completed (done, transferred, superseded, or cancelled)",
                                        NextAction: $"Complete upstream task '{depTarget}'",
                                        CreatedAt: null,
                                        IsDerived: true,
                                        OriginalIndex: originalIndex++));
                                }
                            }
                        }
                    }
                }
            }
        }

        // Apply filters
        var filtered = rawItems.AsEnumerable();

        if (!string.IsNullOrWhiteSpace(owner))
        {
            var filterOwner = owner.Trim();
            filtered = filtered.Where(i => string.Equals(i.Owner, filterOwner, StringComparison.OrdinalIgnoreCase));
        }

        if (!string.IsNullOrWhiteSpace(kind))
        {
            var filterKind = kind.Trim();
            filtered = filtered.Where(i => string.Equals(i.Kind, filterKind, StringComparison.OrdinalIgnoreCase));
        }

        if (dueOnly)
        {
            filtered = filtered.Where(i => i.IsDue);
        }

        // Deterministic sort: overdue first, then upcoming due items, then undated, in stable order
        static int GetCategoryRank(TaskBlockerItem item)
        {
            if (item.IsDue) return 0;
            if (item.ReviewAtUtc.HasValue) return 1;
            return 2;
        }

        var sorted = filtered
            .OrderBy(GetCategoryRank)
            .ThenBy(i => i.ReviewAtUtc ?? DateTime.MaxValue)
            .ThenBy(i => i.OriginalIndex)
            .ToList();

        var result = new TaskBlockersResult(resolvedIterId, tasksRevision, sorted);
        return (true, result, Array.Empty<Diagnostic>());
    }
}
