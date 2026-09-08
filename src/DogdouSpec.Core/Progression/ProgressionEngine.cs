using System.Globalization;
using System.Xml.Linq;
using DogdouSpec.Core.Diagnostics;
using DogdouSpec.Core.Security;
using DogdouSpec.Core.Tasks;
using DogdouSpec.Core.Validation;
using DogdouSpec.Core.Workspace;

namespace DogdouSpec.Core.Progression;

/// <summary>
/// Authoritative shared progression assessment engine.
/// Evaluates lifecycle, task state, dependencies, review gates, active findings,
/// and product confirmation status in a single unified read-only pass.
/// </summary>
public static class ProgressionEngine
{
    private const int DefaultMaxRetries = 3;

    public static (bool Success, ProgressionAssessmentResult? Result, IReadOnlyList<Diagnostic> Diagnostics) Assess(
        string workspaceRoot,
        string? requestedIterationId = null,
        string? agentFilter = null,
        string? requestedTaskId = null,
        int maxRetries = DefaultMaxRetries,
        ProjectSemanticIndex? index = null)
    {
        if (string.IsNullOrWhiteSpace(workspaceRoot))
        {
            return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.InvalidArgument, "Workspace root cannot be empty.") });
        }

        var (isWsSafe, wsErr) = PathSecurity.VerifyWorkspaceDirectorySecurity(workspaceRoot);
        if (!isWsSafe || wsErr != null)
        {
            return (false, null, new[] { wsErr ?? Diagnostic.Error(DiagnosticCodes.PathEscapeDetected, "Workspace directory security verification failed.") });
        }

        var (iterSuccess, targetIterationId, iterDiag) = ResolveTargetIteration(workspaceRoot, requestedIterationId);
        if (!iterSuccess || iterDiag != null)
        {
            return (false, null, new[] { iterDiag! });
        }

        if (index == null)
        {
            var (loadOk, loadedIndex, loadDiags) = ProjectSemanticIndex.LoadConsistent(workspaceRoot, maxRetries);
            if (!loadOk || loadedIndex == null)
            {
                return (false, null, loadDiags);
            }
            index = loadedIndex;
        }

        var targetTasksDoc = index.TasksDocuments.FirstOrDefault(td =>
            string.Equals(td.Document.IterationId, targetIterationId, StringComparison.Ordinal));
        var targetSpecDoc = index.Iterations.FirstOrDefault(sd =>
            string.Equals(sd.Document.IterationId, targetIterationId, StringComparison.Ordinal));

        if (targetSpecDoc == null)
        {
            return (false, null, new[] { Diagnostic.Error(
                DiagnosticCodes.DocumentNotFound,
                $"spec.xml not found for iteration '{targetIterationId}'.",
                $"{targetIterationId}/spec.xml") });
        }

        if (targetTasksDoc == null)
        {
            return (false, null, new[] { Diagnostic.Error(
                DiagnosticCodes.DocumentNotFound,
                $"tasks.xml not found for iteration '{targetIterationId}'.",
                $"{targetIterationId}/tasks.xml") });
        }

        return PerformAssessment(
            workspaceRoot,
            targetIterationId!,
            targetSpecDoc,
            targetTasksDoc,
            index,
            agentFilter,
            requestedTaskId);
    }

    private static (bool Success, ProgressionAssessmentResult? Result, IReadOnlyList<Diagnostic> Diagnostics) PerformAssessment(
        string workspaceRoot,
        string iterationId,
        ParsedIteration targetSpecDoc,
        ParsedTasksDocument targetTasksDoc,
        ProjectSemanticIndex index,
        string? agentFilter,
        string? requestedTaskId)
    {
        var specEl = targetSpecDoc.Element;
        var tasksEl = targetTasksDoc.Element;

        var specRevStr = specEl.Attribute("revision")?.Value;
        int.TryParse(specRevStr, CultureInfo.InvariantCulture, out var specRevision);

        var tasksRevStr = tasksEl.Attribute("revision")?.Value;
        int.TryParse(tasksRevStr, CultureInfo.InvariantCulture, out var tasksRevision);

        var iterationStatus = specEl.Attribute("status")?.Value ?? "draft";

        var docRevisions = new List<ProgressionDocumentRevision>
        {
            new(targetSpecDoc.Document.RelativePath, specRevision),
            new(targetTasksDoc.Document.RelativePath, tasksRevision)
        };
        var trackedDocPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            targetSpecDoc.Document.RelativePath,
            targetTasksDoc.Document.RelativePath
        };

        // Extract approved requirements from spec.xml
        var approvedReqIds = new HashSet<string>(StringComparer.Ordinal);
        var reqStatuses = new Dictionary<string, string>(StringComparer.Ordinal);
        var reqElements = specEl.Element("product")?.Element("requirements")?.Elements("requirement") ?? Enumerable.Empty<XElement>();
        foreach (var req in reqElements)
        {
            var reqId = req.Attribute("id")?.Value;
            var reqStatus = req.Attribute("status")?.Value ?? "proposed";
            if (!string.IsNullOrEmpty(reqId))
            {
                reqStatuses[reqId] = reqStatus;
                if (string.Equals(reqStatus, "approved", StringComparison.OrdinalIgnoreCase))
                {
                    approvedReqIds.Add(reqId);
                }
            }
        }

        // Check product confirmation
        var acceptanceCrits = (specEl.Element("product")?.Element("acceptance")?.Elements("criterion") ??
                               specEl.Element("research")?.Element("acceptance")?.Elements("criterion") ??
                               Enumerable.Empty<XElement>()).ToList();
        var pendingCritsCount = acceptanceCrits.Count(c => string.Equals(c.Attribute("decision")?.Value ?? "pending", "pending", StringComparison.OrdinalIgnoreCase));
        bool isProductConfirmed = string.Equals(iterationStatus, "completed", StringComparison.OrdinalIgnoreCase) && pendingCritsCount == 0;

        // Process tasks
        var taskElements = tasksEl.Elements("task").ToList();
        int totalTasks = taskElements.Count;
        int pendingTasks = 0;
        int inProgressTasks = 0;
        int verificationTasks = 0;
        int blockedTasks = 0;
        int doneTasks = 0;
        int cancelledTasks = 0;
        int transferredTasks = 0;
        int supersededTasks = 0;
        int activeFindingsCount = 0;
        int unresolvedBlockersCount = 0;

        var allCandidates = new List<ProgressionTaskCandidate>();
        var structuralDiagnostics = new List<Diagnostic>();

        for (int i = 0; i < taskElements.Count; i++)
        {
            var taskEl = taskElements[i];
            var taskId = taskEl.Attribute("id")?.Value ?? string.Empty;
            var status = taskEl.Attribute("status")?.Value ?? "pending";
            var agent = taskEl.Attribute("agent")?.Value;
            var title = taskEl.Element("title")?.Value ?? string.Empty;

            switch (status)
            {
                case "pending": pendingTasks++; break;
                case "in-progress": inProgressTasks++; break;
                case "verification": verificationTasks++; break;
                case "blocked": blockedTasks++; break;
                case "done": doneTasks++; break;
                case "cancelled": cancelledTasks++; break;
                case "transferred": transferredTasks++; break;
                case "superseded": supersededTasks++; break;
                default: pendingTasks++; break;
            }

            var records = taskEl.Element("records")?.Elements("record")?.ToList() ?? new List<XElement>();
            var taskActiveFindings = records.Where(r =>
                string.Equals(r.Attribute("kind")?.Value, "finding", StringComparison.Ordinal) &&
                string.Equals(r.Attribute("status")?.Value, "active", StringComparison.Ordinal)).ToList();

            activeFindingsCount += taskActiveFindings.Count;

            var taskBlockers = taskActiveFindings.Where(r =>
                r.Element("index")?.Elements("term").Any(t => string.Equals(t.Attribute("key")?.Value, "blocker-kind", StringComparison.Ordinal)) == true
                || string.Equals(status, "blocked", StringComparison.Ordinal)).ToList();

            unresolvedBlockersCount += taskBlockers.Count;

            // Check origin requirements
            var originRefs = taskEl.Element("origin")?.Elements("ref")
                .Where(r => string.Equals(r.Attribute("relation")?.Value, "implements", StringComparison.Ordinal))
                .ToList() ?? new List<XElement>();
            string? unapprovedReqId = null;
            string? unapprovedReqStatus = null;
            foreach (var oRef in originRefs)
            {
                var targetReq = oRef.Attribute("target")?.Value;
                if (!string.IsNullOrEmpty(targetReq) && !approvedReqIds.Contains(targetReq))
                {
                    unapprovedReqId = targetReq;
                    unapprovedReqStatus = reqStatuses.TryGetValue(targetReq, out var st) ? st : "unknown";
                    break;
                }
            }

            // Check review requirements
            var reviewEval = TaskReviewGate.Evaluate(taskEl);

            // Check dependencies
            var (depSatisfied, depDiagnostics, depReadPreconditions) = TaskDependencyGate.EvaluateTaskDependencies(
                workspaceRoot,
                taskId,
                taskEl,
                targetTasksDoc.Document.RelativePath,
                index);

            // Track dependency document revisions
            foreach (var pre in depReadPreconditions)
            {
                if (trackedDocPaths.Add(pre.RelativePath))
                {
                    docRevisions.Add(new ProgressionDocumentRevision(pre.RelativePath, pre.ExpectedRevision));
                }
            }

            // Collect structural dependency errors (fail-closed)
            var structDiags = depDiagnostics.Where(d =>
                !string.Equals(d.Code, DiagnosticCodes.TaskTransitionConflict, StringComparison.Ordinal)).ToList();
            if (structDiags.Count > 0)
            {
                structuralDiagnostics.AddRange(structDiags);
            }

            var blockingReasons = new List<string>();
            if (unapprovedReqId != null)
            {
                blockingReasons.Add($"Origin requirement '{unapprovedReqId}' is '{unapprovedReqStatus}' (requires owner approval)");
            }
            if (!depSatisfied)
            {
                blockingReasons.Add("Unmet upstream dependencies");
            }
            if (taskActiveFindings.Count > 0)
            {
                blockingReasons.Add($"{taskActiveFindings.Count} active finding(s)");
            }
            if (reviewEval.Required && !reviewEval.Satisfied)
            {
                blockingReasons.Add(reviewEval.Reason);
            }

            // Derive task progression category and reason
            string taskCategory;
            string taskReasonCode;
            string taskReason;
            string requiredRole;
            bool isActionable = false;

            if (string.Equals(iterationStatus, "replanning", StringComparison.OrdinalIgnoreCase))
            {
                taskCategory = ProgressionActionCategories.OwnerDecision;
                taskReasonCode = ProgressionReasonCodes.IterationReplanning;
                taskReason = "Iteration is in replanning; execution transitions frozen.";
                requiredRole = "owner";
            }
            else if (string.Equals(iterationStatus, "draft", StringComparison.OrdinalIgnoreCase) && status == "pending")
            {
                taskCategory = ProgressionActionCategories.OwnerDecision;
                taskReasonCode = ProgressionReasonCodes.IterationDraft;
                taskReason = "Iteration is in draft; activation required before starting tasks.";
                requiredRole = "owner";
            }
            else
            {
                switch (status)
                {
                    case "in-progress":
                        if (unapprovedReqId != null)
                        {
                            taskCategory = ProgressionActionCategories.OwnerDecision;
                            taskReasonCode = ProgressionReasonCodes.RequirementNotApproved;
                            taskReason = $"Origin requirement '{unapprovedReqId}' is '{unapprovedReqStatus}' (requires owner approval).";
                            requiredRole = "owner";
                        }
                        else if (taskActiveFindings.Count > 0)
                        {
                            taskCategory = ProgressionActionCategories.ResolveFindings;
                            taskReasonCode = ProgressionReasonCodes.ActiveTaskActiveFindings;
                            taskReason = $"Task has {taskActiveFindings.Count} active finding(s) that must be addressed.";
                            requiredRole = "implementer";
                            isActionable = true;
                        }
                        else
                        {
                            taskCategory = ProgressionActionCategories.ContinueWork;
                            taskReasonCode = ProgressionReasonCodes.ActiveTaskInProgress;
                            taskReason = "Task is in-progress and ready for implementation.";
                            requiredRole = "implementer";
                            isActionable = true;
                        }
                        break;

                    case "verification":
                        if (unapprovedReqId != null)
                        {
                            taskCategory = ProgressionActionCategories.OwnerDecision;
                            taskReasonCode = ProgressionReasonCodes.RequirementNotApproved;
                            taskReason = $"Origin requirement '{unapprovedReqId}' is '{unapprovedReqStatus}'.";
                            requiredRole = "owner";
                        }
                        else if (taskActiveFindings.Count > 0)
                        {
                            taskCategory = ProgressionActionCategories.ResolveFindings;
                            taskReasonCode = ProgressionReasonCodes.ActiveTaskActiveFindings;
                            taskReason = $"Task has {taskActiveFindings.Count} active finding(s) in verification.";
                            requiredRole = "implementer";
                            isActionable = true;
                        }
                        else if (reviewEval.Required && !reviewEval.Satisfied)
                        {
                            var latestSubmission = taskEl.Element("review")?.Elements("submission").LastOrDefault();
                            var disposition = (string?)latestSubmission?.Attribute("disposition");
                            if (string.Equals(disposition, "changes-requested", StringComparison.OrdinalIgnoreCase))
                            {
                                taskCategory = ProgressionActionCategories.ResolveFindings;
                                taskReasonCode = ProgressionReasonCodes.ActiveTaskReviewChangesRequested;
                                taskReason = "Review requested changes; findings must be resolved.";
                                requiredRole = "implementer";
                                isActionable = true;
                            }
                            else
                            {
                                taskCategory = ProgressionActionCategories.ReviewRequired;
                                taskReasonCode = ProgressionReasonCodes.ActiveTaskReviewRequired;
                                taskReason = "Task is in verification and requires independent review.";
                                requiredRole = "reviewer";
                                isActionable = true;
                            }
                        }
                        else
                        {
                            taskCategory = ProgressionActionCategories.VerifyWork;
                            taskReasonCode = ProgressionReasonCodes.ActiveTaskVerification;
                            taskReason = "Task is in verification and ready for checks / completion.";
                            requiredRole = "implementer";
                            isActionable = true;
                        }
                        break;

                    case "blocked":
                        var hasResolutionRecord = records.Any(r =>
                            string.Equals(r.Attribute("kind")?.Value, "resolution", StringComparison.Ordinal) ||
                            (string.Equals(r.Attribute("kind")?.Value, "finding", StringComparison.Ordinal) &&
                             string.Equals(r.Attribute("status")?.Value, "resolved", StringComparison.Ordinal)));

                        if (hasResolutionRecord && taskActiveFindings.Count == 0 && depSatisfied && unapprovedReqId == null)
                        {
                            taskCategory = ProgressionActionCategories.ResumeTask;
                            taskReasonCode = ProgressionReasonCodes.TasksBlocked;
                            taskReason = "All blockers resolved; ready for explicit recovery transition (task resume).";
                            requiredRole = "implementer";
                            isActionable = true;
                        }
                        else
                        {
                            taskCategory = ProgressionActionCategories.WaitExternal;
                            taskReasonCode = ProgressionReasonCodes.TasksBlocked;
                            taskReason = "Task is blocked waiting for external conditions or unresolved blockers.";
                            requiredRole = "blocker-owner";
                        }
                        break;

                    case "pending":
                        if (unapprovedReqId != null)
                        {
                            taskCategory = ProgressionActionCategories.OwnerDecision;
                            taskReasonCode = ProgressionReasonCodes.RequirementNotApproved;
                            taskReason = $"Origin requirement '{unapprovedReqId}' is '{unapprovedReqStatus}'.";
                            requiredRole = "owner";
                        }
                        else if (depSatisfied)
                        {
                            taskCategory = ProgressionActionCategories.StartWork;
                            taskReasonCode = ProgressionReasonCodes.PendingTaskReady;
                            taskReason = "Ready pending task with all dependencies satisfied.";
                            requiredRole = "implementer";
                            isActionable = true;
                        }
                        else
                        {
                            taskCategory = ProgressionActionCategories.WaitDependency;
                            taskReasonCode = ProgressionReasonCodes.PendingTaskDependenciesUnsatisfied;
                            taskReason = "Pending task blocked by unsatisfied dependencies.";
                            requiredRole = "any";
                        }
                        break;

                    case "done":
                        taskCategory = ProgressionActionCategories.ExecutionTerminal;
                        taskReasonCode = ProgressionReasonCodes.AllTasksDone;
                        taskReason = "Task is completed.";
                        requiredRole = "none";
                        break;

                    case "cancelled":
                    case "transferred":
                    case "superseded":
                        taskCategory = ProgressionActionCategories.ExecutionTerminal;
                        taskReasonCode = ProgressionReasonCodes.TasksTerminalIncomplete;
                        taskReason = $"Task has terminal disposition '{status}'.";
                        requiredRole = "none";
                        break;

                    default:
                        taskCategory = ProgressionActionCategories.WaitDependency;
                        taskReasonCode = ProgressionReasonCodes.PendingTaskDependenciesUnsatisfied;
                        taskReason = $"Task has unrecognized status '{status}' and is treated as active pending.";
                        requiredRole = "any";
                        break;
                }
            }

            allCandidates.Add(new ProgressionTaskCandidate(
                taskId,
                title,
                status,
                agent,
                i,
                taskCategory,
                taskReasonCode,
                taskReason,
                requiredRole,
                blockingReasons,
                isActionable));
        }

        // Fail-closed if structural dependency errors exist
        if (structuralDiagnostics.Count > 0)
        {
            return (false, null, structuralDiagnostics);
        }

        int terminalTasks = doneTasks + cancelledTasks + transferredTasks + supersededTasks;
        int nonTerminalTasks = pendingTasks + inProgressTasks + verificationTasks + blockedTasks;
        int inactiveTasks = cancelledTasks + transferredTasks + supersededTasks;
        int eligibleTasks = totalTasks - inactiveTasks;
        bool isAllTerminal = totalTasks > 0 && terminalTasks == totalTasks;
        bool isDeliverySuccessful = totalTasks > 0 && doneTasks == totalTasks;
        bool isTerminalIncomplete = isAllTerminal && doneTasks < totalTasks;
        double completionPct = eligibleTasks == 0 ? 0.0 : Math.Round((double)doneTasks / eligibleTasks * 100.0, 1);

        var facts = new ProgressionFactSummary(
            totalTasks,
            pendingTasks,
            inProgressTasks,
            verificationTasks,
            blockedTasks,
            doneTasks,
            cancelledTasks,
            transferredTasks,
            supersededTasks,
            terminalTasks,
            nonTerminalTasks,
            eligibleTasks,
            completionPct,
            isAllTerminal,
            isDeliverySuccessful,
            isTerminalIncomplete,
            activeFindingsCount,
            unresolvedBlockersCount);

        // Apply agent and task filters to actionable candidates
        var filteredActionable = allCandidates.Where(c => c.IsActionable).ToList();
        if (!string.IsNullOrWhiteSpace(agentFilter))
        {
            filteredActionable = filteredActionable.Where(c =>
            {
                if (c.ActionCategory == ProgressionActionCategories.ReviewRequired)
                {
                    // Reviewer role requires independent actor
                    return !string.Equals(c.Agent, agentFilter, StringComparison.OrdinalIgnoreCase);
                }
                // Implementer role: match agent or unassigned
                return string.IsNullOrWhiteSpace(c.Agent) || string.Equals(c.Agent, agentFilter, StringComparison.OrdinalIgnoreCase);
            }).ToList();
        }

        ProgressionTaskCandidate? matchedTaskCandidate = null;
        if (!string.IsNullOrWhiteSpace(requestedTaskId))
        {
            matchedTaskCandidate = allCandidates.FirstOrDefault(c => string.Equals(c.TaskId, requestedTaskId, StringComparison.OrdinalIgnoreCase));
            if (matchedTaskCandidate == null)
            {
                return (false, null, new[] { Diagnostic.Error(
                    DiagnosticCodes.ResourceNotFound,
                    $"Task '{requestedTaskId}' not found in iteration '{iterationId}'.",
                    $"{iterationId}/tasks.xml") });
            }

            filteredActionable = filteredActionable.Where(c => string.Equals(c.TaskId, requestedTaskId, StringComparison.OrdinalIgnoreCase)).ToList();
        }

        // Derive top-level recommendation
        ProgressionRecommendation recommendedAction;

        if (string.Equals(iterationStatus, "draft", StringComparison.OrdinalIgnoreCase))
        {
            recommendedAction = new ProgressionRecommendation(
                ProgressionActionCategories.OwnerDecision,
                ProgressionReasonCodes.IterationDraft,
                "Iteration is in draft; owner activation is required before execution.",
                null,
                null,
                "owner",
                "dogdouspec iteration activate",
                "/iteration/@status");
        }
        else if (string.Equals(iterationStatus, "replanning", StringComparison.OrdinalIgnoreCase))
        {
            recommendedAction = new ProgressionRecommendation(
                ProgressionActionCategories.OwnerDecision,
                ProgressionReasonCodes.IterationReplanning,
                "Iteration is in replanning; execution transitions are frozen pending owner replanning decision.",
                null,
                null,
                "owner",
                "dogdouspec iteration confirm",
                "/iteration/@status");
        }
        else if (string.Equals(iterationStatus, "completed", StringComparison.OrdinalIgnoreCase))
        {
            recommendedAction = new ProgressionRecommendation(
                ProgressionActionCategories.ExecutionTerminal,
                ProgressionReasonCodes.IterationCompleted,
                "Iteration is completed.",
                null,
                null,
                "owner",
                null,
                "/iteration/@status");
        }
        else if (string.Equals(iterationStatus, "superseded", StringComparison.OrdinalIgnoreCase))
        {
            recommendedAction = new ProgressionRecommendation(
                ProgressionActionCategories.ExecutionTerminal,
                ProgressionReasonCodes.IterationSuperseded,
                "Iteration has been superseded.",
                null,
                null,
                "owner",
                null,
                "/iteration/@status");
        }
        else if (string.Equals(iterationStatus, "cancelled", StringComparison.OrdinalIgnoreCase))
        {
            recommendedAction = new ProgressionRecommendation(
                ProgressionActionCategories.ExecutionTerminal,
                ProgressionReasonCodes.IterationCancelled,
                "Iteration is cancelled.",
                null,
                null,
                "owner",
                null,
                "/iteration/@status");
        }
        else if (totalTasks == 0)
        {
            recommendedAction = new ProgressionRecommendation(
                ProgressionActionCategories.NoTasks,
                ProgressionReasonCodes.NoTasks,
                "No tasks found in iteration.",
                null,
                null,
                "implementer",
                "dogdouspec task quick",
                "/tasks");
        }
        else if (filteredActionable.Count > 0)
        {
            // Select first actionable candidate in stable document order
            var first = filteredActionable[0];
            string? followUpCmd = first.ActionCategory switch
            {
                ProgressionActionCategories.StartWork => $"dogdouspec task start --task {first.TaskId}",
                ProgressionActionCategories.ContinueWork => $"dogdouspec task verify --task {first.TaskId}",
                ProgressionActionCategories.VerifyWork => $"dogdouspec task finish --task {first.TaskId}",
                ProgressionActionCategories.ReviewRequired => $"dogdouspec task review approve --task {first.TaskId}",
                ProgressionActionCategories.ResumeTask => $"dogdouspec task resume --task {first.TaskId}",
                _ => null
            };

            recommendedAction = new ProgressionRecommendation(
                first.ActionCategory,
                first.ReasonCode,
                first.Reason,
                first.TaskId,
                null,
                first.RequiredRole,
                followUpCmd,
                $"/tasks/task[@id='{first.TaskId}']");
        }
        else if (!string.IsNullOrWhiteSpace(requestedTaskId) && matchedTaskCandidate != null)
        {
            string? followUpCmd = matchedTaskCandidate.ActionCategory switch
            {
                ProgressionActionCategories.StartWork => $"dogdouspec task start --task {matchedTaskCandidate.TaskId}",
                ProgressionActionCategories.ContinueWork => $"dogdouspec task verify --task {matchedTaskCandidate.TaskId}",
                ProgressionActionCategories.VerifyWork => $"dogdouspec task finish --task {matchedTaskCandidate.TaskId}",
                ProgressionActionCategories.ReviewRequired => $"dogdouspec task review approve --task {matchedTaskCandidate.TaskId}",
                ProgressionActionCategories.ResumeTask => $"dogdouspec task resume --task {matchedTaskCandidate.TaskId}",
                ProgressionActionCategories.OwnerDecision => "dogdouspec iteration confirm",
                ProgressionActionCategories.WaitExternal => "dogdouspec task blockers",
                _ => null
            };

            recommendedAction = new ProgressionRecommendation(
                matchedTaskCandidate.ActionCategory,
                matchedTaskCandidate.ReasonCode,
                matchedTaskCandidate.Reason,
                matchedTaskCandidate.TaskId,
                null,
                matchedTaskCandidate.RequiredRole,
                followUpCmd,
                $"/tasks/task[@id='{matchedTaskCandidate.TaskId}']");
        }
        else
        {
            // No actionable candidate under current filters
            if (nonTerminalTasks > 0)
            {
                if (blockedTasks > 0 && inProgressTasks == 0 && verificationTasks == 0 && pendingTasks == 0)
                {
                    recommendedAction = new ProgressionRecommendation(
                        ProgressionActionCategories.WaitExternal,
                        ProgressionReasonCodes.TasksBlocked,
                        "All non-terminal tasks are currently blocked.",
                        null,
                        null,
                        "blocker-owner",
                        "dogdouspec task blockers",
                        "/tasks/task[@status='blocked']");
                }
                else if (pendingTasks > 0 && inProgressTasks == 0 && verificationTasks == 0 && blockedTasks == 0)
                {
                    var unapprovedPending = allCandidates.FirstOrDefault(c => c.Status == "pending" && c.ReasonCode == ProgressionReasonCodes.RequirementNotApproved);
                    if (unapprovedPending != null)
                    {
                        recommendedAction = new ProgressionRecommendation(
                            ProgressionActionCategories.OwnerDecision,
                            ProgressionReasonCodes.RequirementNotApproved,
                            unapprovedPending.Reason,
                            unapprovedPending.TaskId,
                            null,
                            "owner",
                            "dogdouspec iteration confirm",
                            $"/tasks/task[@id='{unapprovedPending.TaskId}']");
                    }
                    else
                    {
                        recommendedAction = new ProgressionRecommendation(
                            ProgressionActionCategories.WaitDependency,
                            ProgressionReasonCodes.PendingTaskDependenciesUnsatisfied,
                            "Remaining pending tasks are blocked by unsatisfied dependencies.",
                            null,
                            null,
                            "any",
                            null,
                            "/tasks/task[@status='pending']");
                    }
                }
                else
                {
                    recommendedAction = new ProgressionRecommendation(
                        ProgressionActionCategories.WaitExternal,
                        ProgressionReasonCodes.TasksBlocked,
                        "No actionable tasks available under current filters.",
                        null,
                        null,
                        "any",
                        null,
                        "/tasks");
                }
            }
            else
            {
                // All tasks terminal
                if (isDeliverySuccessful)
                {
                    recommendedAction = new ProgressionRecommendation(
                        ProgressionActionCategories.ExecutionTerminal,
                        ProgressionReasonCodes.AllTasksDone,
                        "All tasks in iteration are completed.",
                        null,
                        null,
                        "owner",
                        "dogdouspec iteration readiness --phase completion",
                        "/tasks");
                }
                else
                {
                    recommendedAction = new ProgressionRecommendation(
                        ProgressionActionCategories.ExecutionTerminal,
                        ProgressionReasonCodes.TasksTerminalIncomplete,
                        "All tasks in iteration are terminal (contains cancelled, transferred, or superseded tasks).",
                        null,
                        null,
                        "owner",
                        "dogdouspec iteration readiness --phase completion",
                        "/tasks");
                }
            }
        }

        ParsedTask? primaryTask = null;
        if (filteredActionable.Count > 0)
        {
            var firstCandidateId = filteredActionable[0].TaskId;
            primaryTask = targetTasksDoc.Tasks.FirstOrDefault(t => string.Equals(t.Id, firstCandidateId, StringComparison.Ordinal));
        }

        var result = new ProgressionAssessmentResult(
            iterationId,
            iterationStatus,
            specRevision,
            tasksRevision,
            docRevisions,
            facts,
            recommendedAction,
            filteredActionable,
            allCandidates,
            isAllTerminal,
            isProductConfirmed,
            primaryTask);

        return (true, result, Array.Empty<Diagnostic>());
    }

    internal static (bool Success, string? IterationId, Diagnostic? Error) ResolveTargetIteration(
        string workspaceRoot,
        string? requestedIterationId)
    {
        if (!string.IsNullOrWhiteSpace(requestedIterationId))
        {
            var (isValid, normalizedId, idErr) = PathSecurity.ValidateIterationId(requestedIterationId);
            if (!isValid || idErr != null)
            {
                return (false, null, idErr ?? Diagnostic.Error(DiagnosticCodes.InvalidArgument, $"Invalid iteration ID '{requestedIterationId}'."));
            }

            var specPath = Path.Combine(workspaceRoot, normalizedId, "spec.xml");
            if (!File.Exists(specPath))
            {
                return (false, null, Diagnostic.Error(DiagnosticCodes.IterationNotFound, $"Iteration '{normalizedId}' does not exist in workspace.", $"{normalizedId}/spec.xml"));
            }

            return (true, normalizedId, null);
        }

        // Auto-discover active iteration
        var activeIterations = new List<string>();
        foreach (var dir in Directory.EnumerateDirectories(workspaceRoot))
        {
            var dirName = Path.GetFileName(dir);
            if (dirName.StartsWith('.') || dirName.StartsWith('_'))
            {
                continue;
            }

            var specPath = Path.Combine(dir, "spec.xml");
            if (!File.Exists(specPath))
            {
                continue;
            }

            var (isContained, contErr) = PathSecurity.CheckContainmentAndReparsePoints(workspaceRoot, specPath);
            if (!isContained || contErr != null)
            {
                return (false, null, contErr);
            }

            try
            {
                using var fs = File.OpenRead(specPath);
                using var r = SecureXmlReaderFactory.CreateReader(fs);
                var xDoc = XDocument.Load(r);
                var status = xDoc.Root?.Attribute("status")?.Value;
                if (string.Equals(status, "active", StringComparison.OrdinalIgnoreCase))
                {
                    var id = xDoc.Root?.Attribute("id")?.Value ?? dirName;
                    activeIterations.Add(id);
                }
            }
            catch (Exception ex)
            {
                return (false, null, Diagnostic.Error(DiagnosticCodes.XmlParseError, $"Failed to read '{dirName}/spec.xml': {ex.Message}", $"{dirName}/spec.xml"));
            }
        }

        activeIterations.Sort(StringComparer.Ordinal);

        if (activeIterations.Count == 1)
        {
            return (true, activeIterations[0], null);
        }

        if (activeIterations.Count > 1)
        {
            return (false, null, Diagnostic.Error(
                DiagnosticCodes.InvalidArgument,
                $"Multiple active iterations found ({string.Join(", ", activeIterations)}). Specify an explicit iteration identifier."));
        }

        return (false, null, Diagnostic.Error(
            DiagnosticCodes.IterationNotFound,
            "No active iteration found in workspace. Specify an explicit iteration identifier."));
    }
}
