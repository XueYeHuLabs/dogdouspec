using System.Globalization;
using System.Xml.Linq;
using DogdouSpec.Core.Diagnostics;
using DogdouSpec.Core.Iterations;
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

            var activeIter = listResult.Iterations.FirstOrDefault(i => string.Equals(i.Status, "active", StringComparison.OrdinalIgnoreCase));
            if (activeIter != null)
            {
                iterationId = activeIter.Id;
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

        var totalTasks = parsedTasks.Count;
        var doneTasks = parsedTasks.Count(t => string.Equals(t.Status, "done", StringComparison.OrdinalIgnoreCase));
        var inProgressTasks = parsedTasks.Count(t => string.Equals(t.Status, "in-progress", StringComparison.OrdinalIgnoreCase));
        var verificationTasks = parsedTasks.Count(t => string.Equals(t.Status, "verification", StringComparison.OrdinalIgnoreCase));
        var pendingTasksCount = parsedTasks.Count(t =>
            string.Equals(t.Status, "pending", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(t.Status, "blocked", StringComparison.OrdinalIgnoreCase) ||
            (!KnownActiveStatuses.Contains(t.Status) && !KnownInactiveStatuses.Contains(t.Status)));
        var inactiveTasks = parsedTasks.Count(t => KnownInactiveStatuses.Contains(t.Status));

        var activeTotal = totalTasks - inactiveTasks;
        var progressPct = activeTotal > 0 ? ((double)doneTasks / activeTotal) * 100.0 : 0.0;

        // Next Recommended Action
        string nextAction;
        if (string.Equals(status, "replanning", StringComparison.OrdinalIgnoreCase))
        {
            nextAction = "Iteration is frozen in 'replanning' status. Resolve proposed changes or confirm with 'dogdouspec iteration confirm'.";
        }
        else if (string.Equals(status, "completed", StringComparison.OrdinalIgnoreCase))
        {
            nextAction = "Iteration is completed and archived.";
        }
        else if (string.Equals(status, "draft", StringComparison.OrdinalIgnoreCase))
        {
            nextAction = $"Activate draft iteration: 'dogdouspec iteration activate --iteration {normIterId}'";
        }
        else
        {
            var verTask = parsedTasks.FirstOrDefault(t => t.Status == "verification");
            if (verTask != null)
            {
                nextAction = $"Verify & finish task {verTask.Id}: 'dogdouspec task finish --task {verTask.Id}'";
            }
            else
            {
                var inProgTask = parsedTasks.FirstOrDefault(t => t.Status == "in-progress");
                if (inProgTask != null)
                {
                    nextAction = $"Complete task {inProgTask.Id} and run: 'dogdouspec task verify --task {inProgTask.Id}'";
                }
                else
                {
                    var readyPending = parsedTasks.FirstOrDefault(t => t.Status == "pending" && !t.IsBlocked);
                    if (readyPending != null)
                    {
                        nextAction = $"Start next ready task {readyPending.Id}: 'dogdouspec task start --task {readyPending.Id}'";
                    }
                    else if (pendingTasksCount > 0)
                    {
                        nextAction = "All pending tasks are blocked by incomplete prerequisites. Resolve prerequisite blockers.";
                    }
                    else if (totalTasks > 0 && doneTasks == activeTotal)
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
                        nextAction = $"No tasks defined. Add tasks with 'dogdouspec task quick' or 'dogdouspec task add'.";
                    }
                }
            }
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
            nextAction);

        return (true, new IterationSummaryResult(summary), diagnostics);
    }
}
