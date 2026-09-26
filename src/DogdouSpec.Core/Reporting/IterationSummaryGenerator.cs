using System.Globalization;
using System.Xml.Linq;
using DogdouSpec.Core.Diagnostics;
using DogdouSpec.Core.Iterations;
using DogdouSpec.Core.Progression;
using DogdouSpec.Core.Security;
using DogdouSpec.Core.Tasks;
using DogdouSpec.Core.Workspace;

namespace DogdouSpec.Core.Reporting;

public static class IterationSummaryGenerator
{
    internal static readonly HashSet<string> KnownActiveStatuses = new(StringComparer.OrdinalIgnoreCase)
    {
        "pending",
        "in-progress",
        "blocked",
        "verification",
        "done"
    };

    internal static readonly HashSet<string> KnownInactiveStatuses = new(StringComparer.OrdinalIgnoreCase)
    {
        "transferred",
        "superseded",
        "cancelled"
    };
    public static (bool Success, IterationSummaryResult? Result, IReadOnlyList<Diagnostic> Diagnostics) Generate(
        string workspaceRoot,
        string? explicitIterationId = null)
    {
        var diagnostics = new List<Diagnostic>();

        var (enumSuccess, allDocs, enumDiags) = WorkspaceDiscovery.EnumerateDocuments(workspaceRoot);
        if (!enumSuccess || enumDiags.Count > 0)
        {
            return (false, null, enumDiags);
        }

        string? iterationId = explicitIterationId;

        if (string.IsNullOrWhiteSpace(iterationId))
        {
            var (listSuccess, listResult, listDiags) = IterationLister.List(workspaceRoot);
            if (!listSuccess || listDiags.Count > 0 || listResult == null)
            {
                return (false, null, listDiags.Count > 0 ? listDiags : new[] { Diagnostic.Error(DiagnosticCodes.DocumentNotFound, "Failed to list iterations.") });
            }

            var activeIters = listResult.Iterations.Where(i => string.Equals(i.Status, "active", StringComparison.OrdinalIgnoreCase)).ToList();
            if (activeIters.Count > 1)
            {
                return (false, null, new[] { Diagnostic.Error(
                    DiagnosticCodes.InvalidArgument,
                    $"Multiple active iterations found ({string.Join(", ", activeIters.Select(i => i.Id))}). Specify --iteration explicitly.") });
            }

            if (activeIters.Count == 1)
            {
                iterationId = activeIters[0].Id;
            }
            else
            {
                var replanningIter = listResult.Iterations.FirstOrDefault(i => string.Equals(i.Status, "replanning", StringComparison.OrdinalIgnoreCase));
                if (replanningIter != null)
                {
                    iterationId = replanningIter.Id;
                }
                else if (listResult.Iterations.Count == 1)
                {
                    iterationId = listResult.Iterations[0].Id;
                }
                else if (listResult.Iterations.Count > 1)
                {
                    var draftIter = listResult.Iterations.FirstOrDefault(i => string.Equals(i.Status, "draft", StringComparison.OrdinalIgnoreCase));
                    if (draftIter != null)
                    {
                        iterationId = draftIter.Id;
                    }
                    else
                    {
                        iterationId = listResult.Iterations[0].Id;
                    }
                }
                else
                {
                    return (false, null, new[] { Diagnostic.Error(
                        DiagnosticCodes.DocumentNotFound,
                        "No iterations found in workspace. Create one with 'dogdouspec iteration create'.") });
                }
                if (replanningIter == null && !string.IsNullOrWhiteSpace(iterationId))
                {
                    diagnostics.Add(Diagnostic.Info(
                        DiagnosticCodes.IterationAutoSelected,
                        $"Auto-selected iteration '{iterationId}' (no active iteration found). Use --iteration to specify explicitly."));
                }
            }
        }

        var (isIdValid, normIterId, idErr) = PathSecurity.ValidateIterationId(iterationId!);
        if (!isIdValid || idErr != null)
        {
            return (false, null, new[] { idErr! });
        }


        var iterDir = Path.Combine(workspaceRoot, normIterId);
        if (!Directory.Exists(iterDir))
        {
            return (false, null, new[] { Diagnostic.Error(
                DiagnosticCodes.DocumentNotFound,
                $"Iteration directory '{normIterId}' not found in workspace.") });
        }

        var specPath = Path.Combine(iterDir, "spec.xml");
        var tasksPath = Path.Combine(iterDir, "tasks.xml");

        if (!File.Exists(specPath))
        {
            return (false, null, new[] { Diagnostic.Error(
                DiagnosticCodes.DocumentNotFound,
                $"spec.xml not found for iteration '{normIterId}'.") });
        }

        XDocument specDoc;
        try
        {
            using var stream = File.OpenRead(specPath);
            using var reader = SecureXmlReaderFactory.CreateReader(stream);
            specDoc = XDocument.Load(reader);
        }
        catch (Exception ex)
        {
            return (false, null, new[] { Diagnostic.Error(
                DiagnosticCodes.XmlParseError,
                $"Failed to load spec.xml: {ex.Message}") });
        }

        XDocument? tasksDoc = null;
        if (File.Exists(tasksPath))
        {
            try
            {
                using var stream = File.OpenRead(tasksPath);
                using var reader = SecureXmlReaderFactory.CreateReader(stream);
                tasksDoc = XDocument.Load(reader);
            }
            catch (Exception ex)
            {
                return (false, null, new[] { Diagnostic.Error(
                    DiagnosticCodes.XmlParseError,
                    $"Failed to load tasks.xml: {ex.Message}") });
            }
        }

        var specRoot = specDoc.Root;
        var kind = specRoot?.Attribute("kind")?.Value ?? "feature";
        var status = specRoot?.Attribute("status")?.Value ?? "draft";
        var specRevStr = specRoot?.Attribute("revision")?.Value;
        var specRev = int.TryParse(specRevStr, CultureInfo.InvariantCulture, out var parsedSpecRev) ? parsedSpecRev : 1;

        var tasksRoot = tasksDoc?.Root;
        var tasksRevStr = tasksRoot?.Attribute("revision")?.Value;
        var tasksRev = int.TryParse(tasksRevStr, CultureInfo.InvariantCulture, out var parsedTasksRev) ? parsedTasksRev : 1;

        var title = specRoot?.Element("title")?.Value?.Trim() ?? string.Empty;
        var summaryText = specRoot?.Element("overview")?.Value?.Trim()
            ?? specRoot?.Element("summary")?.Value?.Trim()
            ?? string.Empty;

        // Parse tasks
        var taskElements = tasksRoot?.Elements("task").ToList() ?? new List<XElement>();
        var parsedTasks = new List<TaskSummaryItem>();
        var blockers = new List<BlockerSummaryItem>();

        var taskStatusMap = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var taskElem in taskElements)
        {
            var tId = taskElem.Attribute("id")?.Value ?? string.Empty;
            var tStatus = taskElem.Attribute("status")?.Value ?? "pending";
            if (!string.IsNullOrEmpty(tId))
            {
                taskStatusMap[tId] = tStatus;
            }
        }

        foreach (var taskElem in taskElements)
        {
            var tId = taskElem.Attribute("id")?.Value ?? string.Empty;
            var tStatus = taskElem.Attribute("status")?.Value ?? "pending";
            if (!KnownActiveStatuses.Contains(tStatus) && !KnownInactiveStatuses.Contains(tStatus))
            {
                diagnostics.Add(Diagnostic.Warning(
                    DiagnosticCodes.SchemaValidationError,
                    $"Task '{tId}' has unrecognized status '{tStatus}'. It will be treated as pending in progress calculation.",
                    $"{normIterId}/tasks.xml"));
            }

            var tAgent = taskElem.Attribute("agent")?.Value;
            var tTitle = taskElem.Element("title")?.Value?.Trim()
                ?? taskElem.Element("index")?.Element("summary")?.Value?.Trim()
                ?? tId;

            var coveredCriteria = taskElem.Element("covers")?.Elements("ref")
                .Select(r => r.Attribute("target")?.Value)
                .Where(target => !string.IsNullOrEmpty(target))
                .Select(target => target!)
                .ToList() ?? new List<string>();

            var depRefs = taskElem.Element("dependencies")?.Elements("ref")
                .Where(r => string.Equals(r.Attribute("relation")?.Value, "depends-on", StringComparison.Ordinal))
                .ToList() ?? new List<XElement>();

            var isBlocked = false;
            string? blockedReason = null;

            if (string.Equals(tStatus, "pending", StringComparison.OrdinalIgnoreCase))
            {
                foreach (var dep in depRefs)
                {
                    var depTarget = dep.Attribute("target")?.Value ?? string.Empty;
                    if (taskStatusMap.TryGetValue(depTarget, out var depStatus))
                    {
                        if (!TaskDependencyGate.IsTerminalStatus(depStatus))
                        {
                            isBlocked = true;
                            blockedReason = $"Depends on '{depTarget}' ({depStatus})";
                            blockers.Add(new BlockerSummaryItem(tId, tTitle, depTarget, depStatus));
                        }
                    }
                }
            }

            parsedTasks.Add(new TaskSummaryItem(
                tId,
                tTitle,
                tStatus,
                tAgent,
                coveredCriteria,
                isBlocked,
                blockedReason));
        }

        // Product Gating
        var pendingGates = new List<GatingSummaryItem>();

        var proposedReqs = specDoc.Descendants("requirement")
            .Where(r => string.Equals(r.Attribute("status")?.Value, "proposed", StringComparison.OrdinalIgnoreCase))
            .ToList();
        foreach (var r in proposedReqs)
        {
            var rId = r.Attribute("id")?.Value ?? string.Empty;
            var rStatement = r.Element("statement")?.Value?.Trim() ?? rId;
            pendingGates.Add(new GatingSummaryItem("requirement", rId, rStatement, "proposed"));
        }

        var proposedDecisions = specDoc.Descendants("decision")
            .Where(d => string.Equals(d.Attribute("status")?.Value, "proposed", StringComparison.OrdinalIgnoreCase))
            .ToList();
        foreach (var d in proposedDecisions)
        {
            var dId = d.Attribute("id")?.Value ?? string.Empty;
            var dTitle = d.Element("title")?.Value?.Trim() ?? d.Element("summary")?.Value?.Trim() ?? dId;
            pendingGates.Add(new GatingSummaryItem("decision", dId, dTitle, "proposed"));
        }

        var pendingCriteriaList = specDoc.Descendants("criterion")
            .Where(c => string.Equals(c.Attribute("decision")?.Value, "pending", StringComparison.OrdinalIgnoreCase))
            .ToList();
        foreach (var c in pendingCriteriaList)
        {
            var cId = c.Attribute("id")?.Value ?? string.Empty;
            var cStatement = c.Element("statement")?.Value?.Trim() ?? cId;
            pendingGates.Add(new GatingSummaryItem("acceptance", cId, cStatement, "pending"));
        }

        var (progSuccess, progResult, _) = ProgressionEngine.Assess(workspaceRoot, normIterId);

        var totalTasks = progResult?.Facts.TotalTasks ?? parsedTasks.Count;
        var doneTasks = progResult?.Facts.DoneTasks ?? parsedTasks.Count(t => string.Equals(t.Status, "done", StringComparison.OrdinalIgnoreCase));
        var inProgressTasks = progResult?.Facts.InProgressTasks ?? parsedTasks.Count(t => string.Equals(t.Status, "in-progress", StringComparison.OrdinalIgnoreCase));
        var verificationTasks = progResult?.Facts.VerificationTasks ?? parsedTasks.Count(t => string.Equals(t.Status, "verification", StringComparison.OrdinalIgnoreCase));
        var pendingTasksCount = progResult != null ? (progResult.Facts.PendingTasks + progResult.Facts.BlockedTasks) : parsedTasks.Count(t =>
            string.Equals(t.Status, "pending", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(t.Status, "blocked", StringComparison.OrdinalIgnoreCase) ||
            (!KnownActiveStatuses.Contains(t.Status) && !KnownInactiveStatuses.Contains(t.Status)));
        var inactiveTasks = progResult?.Facts.InactiveTasks ?? parsedTasks.Count(t => KnownInactiveStatuses.Contains(t.Status));

        var activeTotal = progResult?.Facts.EligibleTasks ?? (totalTasks - inactiveTasks);
        var progressPct = progResult?.Facts.CompletionPercentage ?? (activeTotal > 0 ? ((double)doneTasks / activeTotal) * 100.0 : 0.0);

        // Next Recommended Action
        string nextAction;
        string? actionCategory = progResult?.RecommendedAction.ActionCategory;
        string? reasonCode = progResult?.RecommendedAction.ReasonCode;

        if (progResult != null)
        {
            var rec = progResult.RecommendedAction;
            switch (rec.ActionCategory)
            {
                case ProgressionActionCategories.OwnerDecision:
                    if (string.Equals(status, "replanning", StringComparison.OrdinalIgnoreCase))
                    {
                        nextAction = "Iteration is frozen in 'replanning' status. Resolve proposed changes or confirm with 'dogdouspec iteration confirm'.";
                    }
                    else if (string.Equals(status, "draft", StringComparison.OrdinalIgnoreCase))
                    {
                        nextAction = $"Activate draft iteration: 'dogdouspec iteration activate --iteration {normIterId}'";
                    }
                    else
                    {
                        nextAction = $"{rec.Reason} Run: '{rec.FollowUpCommand}'.";
                    }
                    break;

                case ProgressionActionCategories.ExecutionTerminal:
                    if (string.Equals(status, "completed", StringComparison.OrdinalIgnoreCase))
                    {
                        nextAction = "Iteration is completed and archived.";
                    }
                    else if (progResult.Facts.IsDeliverySuccessful)
                    {
                        if (pendingGates.Any(g => g.Kind == "acceptance"))
                        {
                            nextAction = "All tasks completed. Review acceptance criteria and run 'dogdouspec iteration complete' to archive iteration.";
                        }
                        else
                        {
                            nextAction = $"Ready for iteration completion: 'dogdouspec iteration complete --iteration {normIterId}'";
                        }
                    }
                    else
                    {
                        var nonDoneCount = progResult.Facts.CancelledTasks + progResult.Facts.TransferredTasks + progResult.Facts.SupersededTasks;
                        nextAction = $"All tasks in iteration are terminal ({nonDoneCount} non-done disposition(s)). Check readiness: 'dogdouspec iteration readiness --phase completion'";
                    }
                    break;

                case ProgressionActionCategories.ReviewRequired:
                    nextAction = $"Task {rec.TargetTaskId} requires independent review: '{rec.FollowUpCommand}'";
                    break;

                case ProgressionActionCategories.WaitExternal:
                    nextAction = $"{rec.Reason} Inspect blockers with '{rec.FollowUpCommand}'.";
                    break;

                case ProgressionActionCategories.ResolveFindings:
                    nextAction = $"{rec.Reason} Address findings before proceeding.";
                    break;

                case ProgressionActionCategories.ResumeTask:
                    nextAction = $"Task {rec.TargetTaskId} blockers resolved: '{rec.FollowUpCommand}'";
                    break;

                case ProgressionActionCategories.StartWork:
                    nextAction = $"Start next ready task {rec.TargetTaskId}: '{rec.FollowUpCommand}'";
                    break;

                case ProgressionActionCategories.ContinueWork:
                    nextAction = $"Complete task {rec.TargetTaskId} and run: '{rec.FollowUpCommand}'";
                    break;

                case ProgressionActionCategories.VerifyWork:
                    nextAction = $"Verify task {rec.TargetTaskId} and run: '{rec.FollowUpCommand}'";
                    break;

                case ProgressionActionCategories.WaitDependency:
                    nextAction = "All pending tasks are blocked by incomplete prerequisites. Resolve prerequisite blockers.";
                    break;

                case ProgressionActionCategories.NoTasks:
                default:
                    nextAction = "No tasks defined. Add tasks with 'dogdouspec task quick' or 'dogdouspec task add'.";
                    break;
            }
        }
        else
        {
            nextAction = "Unable to evaluate progression recommendations.";
        }

        var summary = new IterationSummary(
            normIterId,
            kind,
            status,
            specRev,
            tasksRev,
            title,
            summaryText,
            totalTasks,
            doneTasks,
            inProgressTasks,
            verificationTasks,
            pendingTasksCount,
            inactiveTasks,
            progressPct,
            parsedTasks,
            blockers,
            pendingGates,
            nextAction,
            actionCategory,
            reasonCode);

        return (true, new IterationSummaryResult(summary), diagnostics);
    }
}
