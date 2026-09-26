using System.Globalization;
using System.Text;
using System.Xml.Linq;
using DogdouSpec.Core.Diagnostics;
using DogdouSpec.Core.Progression;
using DogdouSpec.Core.Security;
using DogdouSpec.Core.Time;
using DogdouSpec.Core.Validation;
using DogdouSpec.Core.Workspace;

namespace DogdouSpec.Core.Tasks;

public static class TaskContextQuery
{
    private const int DefaultMaxBytes = 32768; // 32 KB
    private const int MaxSnapshotRetries = 3;

    public static (bool Success, TaskContextResult? Result, IReadOnlyList<Diagnostic> Diagnostics) Query(
        string workspaceRoot,
        string taskId,
        string? iterationId = null,
        int maxBytes = DefaultMaxBytes,
        IClock? clock = null)
    {
        clock ??= SystemClock.Instance;

        if (string.IsNullOrWhiteSpace(taskId))
        {
            return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.InvalidArgument, "Task ID cannot be empty.") });
        }

        if (maxBytes <= 0)
        {
            return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.InvalidArgument, "max-bytes must be a positive integer.") });
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

        var (loadOk, index, loadDiags) = ProjectSemanticIndex.LoadConsistent(root, MaxSnapshotRetries);
        if (!loadOk || index == null)
        {
            return (false, null, loadDiags);
        }

        var targetTasksDoc = index.TasksDocuments.FirstOrDefault(td =>
            string.Equals(td.Document.IterationId, resolvedIterId, StringComparison.Ordinal));
        var targetSpecDoc = index.Iterations.FirstOrDefault(sd =>
            string.Equals(sd.Document.IterationId, resolvedIterId, StringComparison.Ordinal));

        if (targetSpecDoc == null)
        {
            return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.DocumentNotFound, $"spec.xml not found for iteration '{resolvedIterId}'.", $"{resolvedIterId}/spec.xml") });
        }

        if (targetTasksDoc == null)
        {
            return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.DocumentNotFound, $"tasks.xml not found for iteration '{resolvedIterId}'.", $"{resolvedIterId}/tasks.xml") });
        }

        var specRoot = targetSpecDoc.Element;
        var tasksRoot = targetTasksDoc.Element;

        if (specRoot == null || tasksRoot == null)
        {
            return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.XmlParseError, "Missing root element in specification or tasks document.", $"{resolvedIterId}/tasks.xml") });
        }

        var taskElem = tasksRoot.Elements("task").FirstOrDefault(t => string.Equals((string?)t.Attribute("id"), taskId.Trim(), StringComparison.Ordinal));
        if (taskElem == null)
        {
            return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.ResourceNotFound, $"Task '{taskId}' not found in iteration '{resolvedIterId}'.", $"{resolvedIterId}/tasks.xml") });
        }

        var taskStatus = (string?)taskElem.Attribute("status") ?? "pending";
        var agent = (string?)taskElem.Attribute("agent");

        int tasksRevision = 1;
        if (int.TryParse(tasksRoot.Attribute("revision")?.Value, CultureInfo.InvariantCulture, out var tRev))
        {
            tasksRevision = tRev;
        }

        int specRevision = 1;
        if (int.TryParse(specRoot.Attribute("revision")?.Value, CultureInfo.InvariantCulture, out var sRev))
        {
            specRevision = sRev;
        }

        // Essential: Title, Objective, Rationale, Scope
        var title = taskElem.Element("title")?.Value
                    ?? taskElem.Element("index")?.Element("summary")?.Value
                    ?? taskId;
        var objective = taskElem.Element("objective")?.Value ?? string.Empty;
        var rationale = taskElem.Element("rationale")?.Value ?? string.Empty;
        var scopeElement = taskElem.Element("scope");

        // Essential: Constraints
        var constraintsList = new List<(string Id, string Text)>();
        var constraintsElem = taskElem.Element("constraints");
        if (constraintsElem != null)
        {
            foreach (var c in constraintsElem.Elements("constraint"))
            {
                var cId = (string?)c.Attribute("id") ?? string.Empty;
                var cText = c.Value.Trim();
                constraintsList.Add((cId, cText));
            }
        }

        // Essential: Origin Requirements
        var originReqs = new List<TaskContextRequirement>();
        var originElem = taskElem.Element("origin");
        if (originElem != null)
        {
            var reqElements = specRoot.Element("product")?.Element("requirements")?.Elements("requirement").ToList()
                              ?? specRoot.Element("requirements")?.Elements("requirement").ToList()
                              ?? new List<XElement>();

            foreach (var originRef in originElem.Elements("ref"))
            {
                var targetId = (string?)originRef.Attribute("target");
                var relation = (string?)originRef.Attribute("relation");

                if (string.Equals(relation, "supports", StringComparison.Ordinal) &&
                    string.Equals(targetId, resolvedIterId, StringComparison.Ordinal))
                {
                    continue; // iteration-level reference
                }

                if (string.IsNullOrEmpty(targetId)) continue;

                var reqElem = reqElements.FirstOrDefault(r => string.Equals((string?)r.Attribute("id"), targetId, StringComparison.Ordinal));
                if (reqElem == null)
                {
                    return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.ResourceNotFound, $"Origin requirement '{targetId}' referenced by task '{taskId}' not found in '{resolvedIterId}/spec.xml'.", $"{resolvedIterId}/spec.xml") });
                }

                var rStatus = (string?)reqElem.Attribute("status") ?? "proposed";
                var rSummary = reqElem.Element("index")?.Element("summary")?.Value ?? string.Empty;
                var rStatement = reqElem.Element("statement")?.Value?.Trim() ?? string.Empty;
                var rRationale = reqElem.Element("rationale")?.Value?.Trim();

                var keyPoints = new List<(string Id, string Text)>();
                var kpElem = reqElem.Element("key_points");
                if (kpElem != null)
                {
                    foreach (var pt in kpElem.Elements("point"))
                    {
                        var ptId = (string?)pt.Attribute("id") ?? string.Empty;
                        keyPoints.Add((ptId, pt.Value.Trim()));
                    }
                }

                originReqs.Add(new TaskContextRequirement(targetId, rStatus, rSummary, rStatement, rRationale, keyPoints));
            }
        }

        // Essential: Acceptance Criteria
        var acceptanceList = new List<(string Id, string Status, string Text)>();
        var acceptanceElem = taskElem.Element("acceptance");
        if (acceptanceElem != null)
        {
            foreach (var crit in acceptanceElem.Elements("criterion"))
            {
                var cId = (string?)crit.Attribute("id") ?? string.Empty;
                var cStatus = (string?)crit.Attribute("status") ?? "pending";
                var cText = crit.Value.Trim();
                acceptanceList.Add((cId, cStatus, cText));
            }
        }

        // Essential: Active Blockers
        var (bOk, bResult, bDiags) = TaskBlockers.Query(root, resolvedIterId, taskId: taskId, clock: clock, index: index);
        if (!bOk || bResult == null)
        {
            return (false, null, bDiags);
        }
        var activeBlockers = bResult.Blockers;

        // Essential: Latest Status Records (start, verification, decision, completion, handoff, resolution)
        var recordsContainer = taskElem.Element("records");
        var allTaskRecords = recordsContainer?.Elements("record").ToList() ?? new List<XElement>();

        var latestStatusRecords = allTaskRecords
            .Where(r =>
            {
                var k = (string?)r.Attribute("kind");
                return k is "start" or "verification" or "decision" or "completion" or "handoff" or "resolution";
            })
            .TakeLast(3)
            .ToList();

        // Essential: Progression Facts
        var (assessOk, assessResult, assessDiags) = ProgressionEngine.Assess(root, resolvedIterId, requestedTaskId: taskId, index: index);
        if (!assessOk || assessResult == null)
        {
            return (false, null, assessDiags);
        }
        var actionCategory = assessResult.RecommendedAction.ActionCategory;
        var reasonCode = assessResult.RecommendedAction.ReasonCode;
        var recommendedAction = assessResult.RecommendedAction.Reason;

        // Upstream Dependencies
        var (depSatisfied, depDiags, depReadPreconditions) = TaskDependencyGate.EvaluateTaskDependencies(
            root,
            taskId,
            taskElem,
            targetTasksDoc.Document.RelativePath,
            index);

        var structDiags = depDiags.Where(d =>
            !string.Equals(d.Code, DiagnosticCodes.TaskTransitionConflict, StringComparison.Ordinal)).ToList();
        if (structDiags.Count > 0)
        {
            return (false, null, structDiags);
        }

        var dependenciesList = new List<TaskContextDependency>();
        var depsElem = taskElem.Element("dependencies");
        if (depsElem != null)
        {
            foreach (var depRef in depsElem.Elements("ref"))
            {
                var rel = (string?)depRef.Attribute("relation");
                if (!string.Equals(rel, "depends-on", StringComparison.OrdinalIgnoreCase)) continue;

                var depTarget = (string?)depRef.Attribute("target");
                if (string.IsNullOrEmpty(depTarget)) continue;

                if (!index.ObjectsById.TryGetValue(depTarget, out var targetObjects) || targetObjects.Count == 0)
                {
                    return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.ResourceNotFound, $"Upstream dependency task '{depTarget}' referenced by task '{taskId}' could not be resolved.", $"{resolvedIterId}/tasks.xml") });
                }

                var depTaskObj = targetObjects[0];
                var depStatus = depTaskObj.Element.Attribute("status")?.Value ?? "pending";
                var depTitle = depTaskObj.Element.Element("title")?.Value
                               ?? depTaskObj.Element.Element("index")?.Element("summary")?.Value
                               ?? depTarget;
                var satisfied = TaskDependencyGate.IsTerminalStatus(depStatus);

                dependenciesList.Add(new TaskContextDependency(depTarget, depStatus, depTitle, satisfied));
            }
        }

        var iterStatus = specRoot.Attribute("status")?.Value ?? "draft";
        bool isReplanning = string.Equals(iterStatus, "replanning", StringComparison.OrdinalIgnoreCase);
        bool isDraft = string.Equals(iterStatus, "draft", StringComparison.OrdinalIgnoreCase);
        bool hasUnapprovedReq = originReqs.Any(r => !string.Equals(r.Status, "approved", StringComparison.OrdinalIgnoreCase));
        var reviewEval = TaskReviewGate.Evaluate(taskElem);

        var permittedActions = new List<string>();
        var prohibitedActions = new List<string>();
        var authorityBoundaries = new List<string>
        {
            "Technical agents cannot auto-complete requirements, design decisions, or iterations.",
            "Product acceptance and final iteration confirmation remain exclusive human owner decisions.",
            "Do not commit or push to Git unless explicitly requested by the user."
        };

        prohibitedActions.Add("Do not edit managed XML directly");

        if (isReplanning)
        {
            prohibitedActions.Add("Execution transitions (start, resume, verify, finish) are frozen during iteration replanning");
            if (string.Equals(taskStatus, "pending", StringComparison.OrdinalIgnoreCase))
            {
                permittedActions.Add("Revise task (task revise)");
                permittedActions.Add("Split task into subtasks (task split)");
            }
            else if (!TaskDependencyGate.IsTerminalStatus(taskStatus))
            {
                permittedActions.Add("Split task into subtasks (task split)");
            }
            permittedActions.Add("Confirm iteration replanning decision (iteration confirm)");
        }
        else if (isDraft)
        {
            if (string.Equals(taskStatus, "pending", StringComparison.OrdinalIgnoreCase))
            {
                permittedActions.Add("Revise task (task revise)");
                permittedActions.Add("Split task into subtasks (task split)");
                prohibitedActions.Add("Do not start task (iteration is in draft; owner activation required)");
            }
            permittedActions.Add("Activate iteration (iteration activate)");
        }
        else
        {
            switch (taskStatus.ToLowerInvariant())
            {
                case "pending":
                    permittedActions.Add("Revise task (task revise)");
                    permittedActions.Add("Split task into subtasks (task split)");
                    if (hasUnapprovedReq)
                    {
                        var unapproved = originReqs.First(r => !string.Equals(r.Status, "approved", StringComparison.OrdinalIgnoreCase));
                        prohibitedActions.Add($"Do not start task (origin requirement '{unapproved.Id}' is '{unapproved.Status}', requires owner approval)");
                    }
                    else if (!depSatisfied)
                    {
                        prohibitedActions.Add("Do not start task (prerequisite dependencies are not satisfied)");
                    }
                    else
                    {
                        permittedActions.Insert(0, "Start task (task start)");
                    }
                    prohibitedActions.Add("Do not mark task blocked directly (pending tasks wait on dependencies or start)");
                    prohibitedActions.Add("Do not complete task directly (must start and verify first)");
                    break;

                case "in-progress":
                    if (hasUnapprovedReq)
                    {
                        var unapproved = originReqs.First(r => !string.Equals(r.Status, "approved", StringComparison.OrdinalIgnoreCase));
                        prohibitedActions.Add($"Origin requirement '{unapproved.Id}' is '{unapproved.Status}' (requires owner approval)");
                    }
                    else if (activeBlockers.Count > 0)
                    {
                        permittedActions.Add("Record blocker finding (task block)");
                        permittedActions.Add("Split task into subtasks (task split)");
                        prohibitedActions.Add($"Task has {activeBlockers.Count} active finding(s) that must be addressed before verification");
                    }
                    else
                    {
                        permittedActions.Add("Verify task implementation (task verify)");
                        permittedActions.Add("Record blocker finding (task block)");
                        permittedActions.Add("Split task into subtasks (task split)");
                    }
                    prohibitedActions.Add("Do not start task again (already started)");
                    prohibitedActions.Add("Do not complete directly without verification (task finish will verify then complete)");
                    break;

                case "verification":
                    permittedActions.Add("Record blocker found during verification (task block)");
                    permittedActions.Add("Split task into subtasks (task split)");
                    if (reviewEval.Required && !reviewEval.Satisfied)
                    {
                        permittedActions.Add("Approve independent technical review (task review approve)");
                        permittedActions.Add("Request review changes (task review request-changes)");
                        prohibitedActions.Add("Do not complete task directly (independent review approval is required)");
                        prohibitedActions.Add("Do not self-approve technical review if actor equals task agent");
                    }
                    else
                    {
                        permittedActions.Add("Finish task upon successful verification (task finish)");
                    }
                    prohibitedActions.Add("Do not complete if acceptance criteria or review gates fail");
                    break;

                case "blocked":
                    if (activeBlockers.Count > 0)
                    {
                        permittedActions.Add("Resolve blocker finding and resume task (task resume)");
                        permittedActions.Add("Record additional blocker finding (task block)");
                        permittedActions.Add("Split task into subtasks (task split)");
                        prohibitedActions.Add("Do not verify or complete while active blockers remain");
                        prohibitedActions.Add("Do not auto-resume without satisfying dependencies and approved requirements");
                    }
                    else
                    {
                        permittedActions.Add("Resume task to in-progress (task resume)");
                        permittedActions.Add("Record blocker finding (task block)");
                        permittedActions.Add("Split task into subtasks (task split)");
                    }
                    break;

                default: // Terminal dispositions
                    permittedActions.Add("Append informational record only");
                    prohibitedActions.Add($"Task has terminal status '{taskStatus}' and is immutable; execution transitions are prohibited");
                    break;
            }
        }

        // Traceability: source documents
        var sourceDocs = new List<(string Path, int Revision)>
        {
            ($"{resolvedIterId}/tasks.xml", tasksRevision),
            ($"{resolvedIterId}/spec.xml", specRevision)
        };

        foreach (var pre in depReadPreconditions)
        {
            if (sourceDocs.All(s => !string.Equals(s.Path, pre.RelativePath, StringComparison.OrdinalIgnoreCase)))
            {
                sourceDocs.Add((pre.RelativePath, pre.ExpectedRevision));
            }
        }

        var queryLocator = $"dogdouspec query --document \"{resolvedIterId}/tasks.xml\" --xpath \"/tasks/task[@id='{taskId}']/records\" --format xml";

        // Build result container without supplemental records first to test Essential Context size
        var result = new TaskContextResult
        {
            IterationId = resolvedIterId,
            TaskId = taskId,
            TaskStatus = taskStatus,
            Agent = agent,
            SpecRevision = specRevision,
            TasksRevision = tasksRevision,
            MaxBytes = maxBytes,
            QueryLocator = queryLocator,

            Title = title,
            Objective = objective,
            Rationale = rationale,
            ScopeElement = scopeElement != null ? new XElement(scopeElement) : null,
            Constraints = constraintsList,
            OriginRequirements = originReqs,
            AcceptanceCriteria = acceptanceList,
            ActiveBlockers = activeBlockers,
            LatestStatusRecords = latestStatusRecords,

            ActionCategory = actionCategory,
            ReasonCode = reasonCode,
            RecommendedAction = recommendedAction,
            PermittedActions = permittedActions,
            ProhibitedActions = prohibitedActions,
            AuthorityBoundaries = authorityBoundaries,

            Dependencies = dependenciesList,

            TotalSupplementalRecords = allTaskRecords.Count,
            IncludedSupplementalRecords = Array.Empty<XElement>(),
            SourceDocuments = sourceDocs
        };

        var essentialXml = result.ToXmlString();
        var essentialBytes = Encoding.UTF8.GetByteCount(essentialXml);

        if (essentialBytes > maxBytes)
        {
            return (false, null, new[]
            {
                Diagnostic.Error(
                    DiagnosticCodes.LimitExceeded,
                    $"Essential task context ({essentialBytes} bytes) exceeds the byte limit of {maxBytes} bytes. Increase --max-bytes or refine task scope.",
                    $"{resolvedIterId}/tasks.xml")
            });
        }

        // Greedily include supplemental records in reverse chronological order
        var supplementalCandidates = allTaskRecords.ToList();
        var includedRecords = new List<XElement>();

        for (int i = 0; i < supplementalCandidates.Count; i++)
        {
            var candidate = supplementalCandidates[i];
            includedRecords.Add(candidate);

            result.IncludedSupplementalRecords = includedRecords;
            var testBytes = Encoding.UTF8.GetByteCount(result.ToXmlString());
            if (testBytes > maxBytes)
            {
                // Exceeded limit: remove this candidate and stop
                includedRecords.RemoveAt(includedRecords.Count - 1);
                break;
            }
        }

        result.IncludedSupplementalRecords = includedRecords;
        var omitted = supplementalCandidates.Count - includedRecords.Count;
        result.Truncated = omitted > 0;
        result.OmittedRecords = omitted;
        result.TotalBytes = Encoding.UTF8.GetByteCount(result.ToXmlString());

        return (true, result, Array.Empty<Diagnostic>());
    }
}
