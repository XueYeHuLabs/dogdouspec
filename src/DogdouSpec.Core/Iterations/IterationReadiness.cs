using System.Globalization;
using System.Text;
using System.Xml.Linq;
using DogdouSpec.Core.Diagnostics;
using DogdouSpec.Core.Formatting;
using DogdouSpec.Core.Security;
using DogdouSpec.Core.Tasks;
using DogdouSpec.Core.Validation;
using DogdouSpec.Core.Workspace;
using DogdouSpec.Core.XPath;

namespace DogdouSpec.Core.Iterations;

/// <summary>
/// Read-only assessment engine for iteration activation and completion readiness.
/// Evaluates lifecycle prerequisites, pending product/design decisions, terminal task graph predicates,
/// and returns deterministic readiness status and required owner confirmation action.
/// </summary>
public static class IterationReadiness
{
    public static (bool Success, IterationReadinessResult? Result, IReadOnlyList<Diagnostic> Diagnostics) Assess(
        string workspaceRoot,
        string iterationId,
        string phase,
        string version = "1.0")
    {
        // 1. Validate iteration ID and phase arguments
        if (string.IsNullOrWhiteSpace(iterationId))
        {
            return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.InvalidArgument, "Iteration ID cannot be empty.") });
        }

        var (isIterValid, normIterId, iterErr) = WorkspaceDiscovery.ValidateIterationId(iterationId);
        if (!isIterValid || iterErr != null)
        {
            return (false, null, new[] { iterErr ?? Diagnostic.Error(DiagnosticCodes.InvalidArgument, $"Invalid iteration ID '{iterationId}'.") });
        }

        if (string.IsNullOrWhiteSpace(phase))
        {
            return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.InvalidArgument, "Phase cannot be empty.") });
        }

        var normPhase = phase.Trim().ToLowerInvariant();
        if (normPhase != "activation" && normPhase != "completion")
        {
            return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.InvalidArgument, $"Phase must be 'activation' or 'completion', but got '{phase}'.") });
        }

        // 2. Validate workspace security
        var (isWsSafe, wsErr) = PathSecurity.VerifyWorkspaceDirectorySecurity(workspaceRoot);
        if (!isWsSafe || wsErr != null)
        {
            return (false, null, new[] { wsErr ?? Diagnostic.Error(DiagnosticCodes.PathEscapeDetected, "Workspace directory security verification failed.") });
        }

        // 3. Validate relative paths and document existence
        var normSpecDocPath = $"{normIterId}/spec.xml";
        var normTasksDocPath = $"{normIterId}/tasks.xml";

        var (isSpecPathValid, _, specPathErr) = PathSecurity.ValidateRelativeDocumentPath(normSpecDocPath);
        if (!isSpecPathValid || specPathErr != null)
        {
            return (false, null, new[] { specPathErr ?? Diagnostic.Error(DiagnosticCodes.InvalidArgument, $"Invalid document path '{normSpecDocPath}'.") });
        }

        var (isTasksPathValid, _, tasksPathErr) = PathSecurity.ValidateRelativeDocumentPath(normTasksDocPath);
        if (!isTasksPathValid || tasksPathErr != null)
        {
            return (false, null, new[] { tasksPathErr ?? Diagnostic.Error(DiagnosticCodes.InvalidArgument, $"Invalid document path '{normTasksDocPath}'.") });
        }

        var fullSpecPath = Path.Combine(workspaceRoot, normSpecDocPath.Replace('/', Path.DirectorySeparatorChar));
        var (isSpecContained, specContErr) = PathSecurity.CheckContainmentAndReparsePoints(workspaceRoot, fullSpecPath);
        if (!isSpecContained || specContErr != null)
        {
            return (false, null, new[] { specContErr ?? Diagnostic.Error(DiagnosticCodes.PathEscapeDetected, $"Target path escapes workspace: '{normSpecDocPath}'.") });
        }

        var fullTasksPath = Path.Combine(workspaceRoot, normTasksDocPath.Replace('/', Path.DirectorySeparatorChar));
        var (isTasksContained, tasksContErr) = PathSecurity.CheckContainmentAndReparsePoints(workspaceRoot, fullTasksPath);
        if (!isTasksContained || tasksContErr != null)
        {
            return (false, null, new[] { tasksContErr ?? Diagnostic.Error(DiagnosticCodes.PathEscapeDetected, $"Target path escapes workspace: '{normTasksDocPath}'.") });
        }

        if (!File.Exists(fullSpecPath))
        {
            return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.DocumentNotFound, $"Document '{normSpecDocPath}' does not exist in workspace.", normSpecDocPath) });
        }

        if (!File.Exists(fullTasksPath))
        {
            return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.DocumentNotFound, $"Document '{normTasksDocPath}' does not exist in workspace.", normTasksDocPath) });
        }

        var specFileInfo = new FileInfo(fullSpecPath);
        if (specFileInfo.Length > XPathQueryLimits.MaxDocumentBytes)
        {
            return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.LimitExceeded, $"Document '{normSpecDocPath}' exceeds maximum allowed size.", normSpecDocPath) });
        }

        var tasksFileInfo = new FileInfo(fullTasksPath);
        if (tasksFileInfo.Length > XPathQueryLimits.MaxDocumentBytes)
        {
            return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.LimitExceeded, $"Document '{normTasksDocPath}' exceeds maximum allowed size.", normTasksDocPath) });
        }

        // 4. Validate schema and workspace semantic index
        var validationResult = SchemaValidator.Validate(workspaceRoot, iterationId: normIterId, version: version);
        if (!validationResult.IsValid)
        {
            var blockingDiagnostics = validationResult.Diagnostics
                .Where(d => normPhase != "completion" ||
                            !string.Equals(d.Code, DiagnosticCodes.TaskReviewRequired, StringComparison.Ordinal))
                .ToList();
            if (blockingDiagnostics.Any(d => string.Equals(d.Severity, "error", StringComparison.OrdinalIgnoreCase)))
            {
                return (false, null, blockingDiagnostics);
            }
        }

        // 5. Parse spec.xml and tasks.xml
        XDocument specDoc;
        XDocument tasksDoc;
        try
        {
            using var specStream = File.OpenRead(fullSpecPath);
            using var specReader = SecureXmlReaderFactory.CreateReader(specStream);
            specDoc = XDocument.Load(specReader, LoadOptions.PreserveWhitespace | LoadOptions.SetLineInfo);

            using var tasksStream = File.OpenRead(fullTasksPath);
            using var tasksReader = SecureXmlReaderFactory.CreateReader(tasksStream);
            tasksDoc = XDocument.Load(tasksReader, LoadOptions.PreserveWhitespace | LoadOptions.SetLineInfo);
        }
        catch (Exception ex)
        {
            return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.XmlParseError, $"Failed to parse XML documents in iteration '{normIterId}': {ex.Message}", normSpecDocPath) });
        }

        var specRoot = specDoc.Root;
        var tasksRoot = tasksDoc.Root;
        if (specRoot == null || tasksRoot == null)
        {
            return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.XmlParseError, $"Document root is null in iteration '{normIterId}'.", normSpecDocPath) });
        }

        var specRevStr = specRoot.Attribute("revision")?.Value;
        if (string.IsNullOrWhiteSpace(specRevStr) || !int.TryParse(specRevStr, CultureInfo.InvariantCulture, out var specRevision) || specRevision <= 0)
        {
            return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.XmlParseError, $"Invalid or missing root revision in '{normSpecDocPath}'.", normSpecDocPath) });
        }

        var tasksRevStr = tasksRoot.Attribute("revision")?.Value;
        if (string.IsNullOrWhiteSpace(tasksRevStr) || !int.TryParse(tasksRevStr, CultureInfo.InvariantCulture, out var tasksRevision) || tasksRevision <= 0)
        {
            return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.XmlParseError, $"Invalid or missing root revision in '{normTasksDocPath}'.", normTasksDocPath) });
        }

        var status = specRoot.Attribute("status")?.Value ?? "draft";
        var kind = specRoot.Attribute("kind")?.Value ?? "feature";

        if (normPhase == "activation")
        {
            return AssessActivation(
                normIterId,
                specRevision,
                tasksRevision,
                status,
                kind,
                specDoc,
                tasksDoc);
        }
        else
        {
            return AssessCompletion(
                normIterId,
                specRevision,
                tasksRevision,
                status,
                kind,
                specDoc,
                tasksDoc);
        }
    }

    private static (bool Success, IterationReadinessResult? Result, IReadOnlyList<Diagnostic> Diagnostics) AssessActivation(
        string iterId,
        int specRevision,
        int tasksRevision,
        string status,
        string kind,
        XDocument specDoc,
        XDocument tasksDoc)
    {
        var technicalChecks = new List<ReadinessTechnicalCheck>();

        // 1. Lifecycle state check: draft or replanning
        string requiredAction;
        bool lifecycleOk;
        if (string.Equals(status, "draft", StringComparison.Ordinal))
        {
            requiredAction = "activate";
            lifecycleOk = true;
            technicalChecks.Add(new ReadinessTechnicalCheck("lifecycle_state", "passed", "Iteration status is 'draft'"));
        }
        else if (string.Equals(status, "replanning", StringComparison.Ordinal))
        {
            requiredAction = "continue";
            lifecycleOk = true;
            technicalChecks.Add(new ReadinessTechnicalCheck("lifecycle_state", "passed", "Iteration status is 'replanning'"));
        }
        else
        {
            requiredAction = "activate";
            lifecycleOk = false;
            technicalChecks.Add(new ReadinessTechnicalCheck("lifecycle_state", "failed", $"Iteration status is '{status}', but activation requires 'draft' or 'replanning'"));
        }

        // 2. Schema and structural checks
        technicalChecks.Add(new ReadinessTechnicalCheck("schema_valid", "passed", "spec.xml and tasks.xml pass schema validation"));

        // 3. Required specification elements
        bool elementsOk = true;
        if (string.Equals(kind, "feature", StringComparison.Ordinal))
        {
            var productEl = specDoc.Root?.Element("product");
            var hasObjective = !string.IsNullOrWhiteSpace(productEl?.Element("objective")?.Value);
            var hasDeliverables = productEl?.Element("deliverables")?.Elements("deliverable").Any() == true;
            var hasScope = productEl?.Element("scope") != null;
            var hasRequirements = productEl?.Element("requirements")?.Elements("requirement").Any() == true;
            var hasAcceptance = productEl?.Element("acceptance")?.Elements("criterion").Any() == true;

            elementsOk = hasObjective && hasDeliverables && hasScope && hasRequirements && hasAcceptance;
            if (elementsOk)
            {
                technicalChecks.Add(new ReadinessTechnicalCheck("required_elements_present", "passed", "Product objective, deliverables, scope, requirements, and acceptance criteria are defined"));
            }
            else
            {
                technicalChecks.Add(new ReadinessTechnicalCheck("required_elements_present", "failed", "Product specification is missing required structural sections"));
            }
        }
        else
        {
            var researchEl = specDoc.Root?.Element("research");
            var hasObjective = !string.IsNullOrWhiteSpace(researchEl?.Element("objective")?.Value);
            var hasQuestions = researchEl?.Element("questions")?.Elements("question").Any() == true;
            var hasMethod = !string.IsNullOrWhiteSpace(researchEl?.Element("method")?.Value);
            var hasBoundaries = researchEl?.Element("boundaries") != null;
            var hasOutputs = researchEl?.Element("outputs") != null;
            var hasAcceptance = researchEl?.Element("acceptance")?.Elements("criterion").Any() == true;

            elementsOk = hasObjective && hasQuestions && hasMethod && hasBoundaries && hasOutputs && hasAcceptance;
            if (elementsOk)
            {
                technicalChecks.Add(new ReadinessTechnicalCheck("required_elements_present", "passed", "Research objective, questions, method, boundaries, outputs, and acceptance criteria are defined"));
            }
            else
            {
                technicalChecks.Add(new ReadinessTechnicalCheck("required_elements_present", "failed", "Research specification is missing required structural sections"));
            }
        }

        // 4. Pending product decisions
        var reqsEl = specDoc.Root?.Element("product")?.Element("requirements")?.Elements("requirement") ?? Enumerable.Empty<XElement>();
        var pendingRequirements = reqsEl.Count(r => string.Equals(r.Attribute("status")?.Value, "proposed", StringComparison.Ordinal));

        var designDecsEl = specDoc.Root?.Element("design")?.Element("decisions")?.Elements("decision") ?? Enumerable.Empty<XElement>();
        var pendingDesign = designDecsEl.Count(d => string.Equals(d.Attribute("status")?.Value, "proposed", StringComparison.Ordinal));

        var critEl = (specDoc.Root?.Element("product")?.Element("acceptance")?.Elements("criterion") ??
                      specDoc.Root?.Element("research")?.Element("acceptance")?.Elements("criterion") ??
                      Enumerable.Empty<XElement>());
        var pendingCriteria = critEl.Count(c => string.Equals(c.Attribute("decision")?.Value ?? "pending", "pending", StringComparison.Ordinal));

        var qEl = specDoc.Root?.Element("research")?.Element("questions")?.Elements("question") ?? Enumerable.Empty<XElement>();
        var pendingQuestions = qEl.Count(q => string.Equals(q.Attribute("status")?.Value ?? "open", "open", StringComparison.Ordinal));

        var productDecisions = new ReadinessProductDecisions(
            pendingRequirements,
            pendingDesign,
            pendingCriteria,
            pendingQuestions);

        bool technicallyReady = lifecycleOk && elementsOk;

        var result = new IterationReadinessResult(
            iterId,
            "activation",
            specRevision,
            tasksRevision,
            technicallyReady,
            ownerConfirmationRequired: true,
            technicalChecks,
            productDecisions,
            new ReadinessRequiredAction(requiredAction));

        return (true, result, Array.Empty<Diagnostic>());
    }

    private static (bool Success, IterationReadinessResult? Result, IReadOnlyList<Diagnostic> Diagnostics) AssessCompletion(
        string iterId,
        int specRevision,
        int tasksRevision,
        string status,
        string kind,
        XDocument specDoc,
        XDocument tasksDoc)
    {
        var technicalChecks = new List<ReadinessTechnicalCheck>();
        bool allChecksPassed = true;

        // 1. Lifecycle state check: must be active
        if (string.Equals(status, "active", StringComparison.Ordinal))
        {
            technicalChecks.Add(new ReadinessTechnicalCheck("lifecycle_state", "passed", "Iteration status is 'active'"));
        }
        else
        {
            technicalChecks.Add(new ReadinessTechnicalCheck("lifecycle_state", "failed", $"Iteration status is '{status}', but completion requires 'active'"));
            allChecksPassed = false;
        }

        // 2. Schema and structural validation check
        technicalChecks.Add(new ReadinessTechnicalCheck("schema_valid", "passed", "spec.xml and tasks.xml pass schema validation"));

        // 3. Task graph completion checks
        var tasks = tasksDoc.Descendants("task").ToList();
        if (tasks.Count == 0)
        {
            technicalChecks.Add(new ReadinessTechnicalCheck("tasks_present", "failed", "tasks.xml contains no tasks"));
            allChecksPassed = false;
        }
        else
        {
            technicalChecks.Add(new ReadinessTechnicalCheck("tasks_present", "passed", $"Found {tasks.Count} defined tasks"));

            // 3.1 All tasks terminal
            var nonTerminal = tasks.Where(t =>
            {
                var s = t.Attribute("status")?.Value;
                return !string.Equals(s, "done", StringComparison.Ordinal) &&
                       !string.Equals(s, "transferred", StringComparison.Ordinal) &&
                       !string.Equals(s, "superseded", StringComparison.Ordinal) &&
                       !string.Equals(s, "cancelled", StringComparison.Ordinal);
            }).ToList();

            if (nonTerminal.Count == 0)
            {
                technicalChecks.Add(new ReadinessTechnicalCheck("tasks_terminal", "passed", "All tasks are in a terminal state (done, transferred, superseded, or cancelled)"));
            }
            else
            {
                technicalChecks.Add(new ReadinessTechnicalCheck("tasks_terminal", "failed", $"{nonTerminal.Count} task(s) are not in a terminal state (e.g. '{nonTerminal[0].Attribute("id")?.Value}')"));
                allChecksPassed = false;
            }

            // 3.2 Done tasks have completed_at, completion records, covered criteria, and no active findings
            var doneTasks = tasks.Where(t => string.Equals(t.Attribute("status")?.Value, "done", StringComparison.Ordinal)).ToList();
            bool doneTasksOk = true;
            var doneFailures = new List<string>();

            foreach (var task in doneTasks)
            {
                var taskId = task.Attribute("id")?.Value ?? "unknown";
                var completedAt = task.Attribute("completed_at")?.Value;
                if (string.IsNullOrWhiteSpace(completedAt))
                {
                    doneTasksOk = false;
                    doneFailures.Add($"Task '{taskId}' missing completed_at timestamp");
                }

                var records = task.Element("records")?.Elements("record") ?? Enumerable.Empty<XElement>();
                var hasCompletionRecord = records.Any(r => string.Equals(r.Attribute("kind")?.Value, "completion", StringComparison.Ordinal));
                if (!hasCompletionRecord)
                {
                    doneTasksOk = false;
                    doneFailures.Add($"Task '{taskId}' lacks a completion record");
                }

                var hasActiveFinding = records.Any(r =>
                    string.Equals(r.Attribute("kind")?.Value, "finding", StringComparison.Ordinal) &&
                    string.Equals(r.Attribute("status")?.Value, "active", StringComparison.Ordinal));
                if (hasActiveFinding)
                {
                    doneTasksOk = false;
                    doneFailures.Add($"Task '{taskId}' has an active finding record blocking completion");
                }

                var criteria = task.Element("acceptance")?.Elements("criterion") ?? Enumerable.Empty<XElement>();
                var verifyingRecords = records.Where(r =>
                {
                    var k = r.Attribute("kind")?.Value;
                    return string.Equals(k, "verification", StringComparison.Ordinal) ||
                           string.Equals(k, "completion", StringComparison.Ordinal);
                }).ToList();

                foreach (var crit in criteria)
                {
                    var critId = crit.Attribute("id")?.Value;
                    var critResult = crit.Attribute("result")?.Value ?? crit.Attribute("status")?.Value ?? "pending";
                    if (!string.Equals(critResult, "passed", StringComparison.Ordinal) &&
                        !string.Equals(critResult, "not-applicable", StringComparison.Ordinal))
                    {
                        doneTasksOk = false;
                        doneFailures.Add($"Task '{taskId}' criterion '{critId}' has non-terminal result '{critResult}'");
                    }

                    var isCovered = verifyingRecords.Any(r =>
                        r.Element("covers")?.Elements("ref")
                            .Any(rf => string.Equals(rf.Attribute("target")?.Value, critId, StringComparison.Ordinal)) == true);
                    if (!isCovered)
                    {
                        doneTasksOk = false;
                        doneFailures.Add($"Task '{taskId}' criterion '{critId}' is not covered by verification or completion records");
                    }
                }
            }

            if (doneTasksOk)
            {
                technicalChecks.Add(new ReadinessTechnicalCheck("task_criteria_and_records_terminal", "passed", "All done tasks have valid completed_at, completion records, covered criteria, and no active findings"));
            }
            else
            {
                technicalChecks.Add(new ReadinessTechnicalCheck("task_criteria_and_records_terminal", "failed", string.Join("; ", doneFailures.Take(3))));
                allChecksPassed = false;
            }

            var reviewFailures = doneTasks
                .Select(task => (Id: task.Attribute("id")?.Value ?? "unknown", Evaluation: TaskReviewGate.Evaluate(task)))
                .Where(result => result.Evaluation.Required && !result.Evaluation.Satisfied)
                .Select(result => $"Task '{result.Id}': {result.Evaluation.Reason}")
                .ToList();
            if (reviewFailures.Count == 0)
            {
                technicalChecks.Add(new ReadinessTechnicalCheck("review_gates", "passed",
                    "All review-required done tasks have a latest independently attributed approval"));
            }
            else
            {
                technicalChecks.Add(new ReadinessTechnicalCheck("review_gates", "failed", string.Join("; ", reviewFailures.Take(3))));
                allChecksPassed = false;
            }
        }

        // 4. Pending product decisions
        var reqsEl = specDoc.Root?.Element("product")?.Element("requirements")?.Elements("requirement") ?? Enumerable.Empty<XElement>();
        var pendingRequirements = reqsEl.Count(r => string.Equals(r.Attribute("status")?.Value, "proposed", StringComparison.Ordinal));

        var designDecsEl = specDoc.Root?.Element("design")?.Element("decisions")?.Elements("decision") ?? Enumerable.Empty<XElement>();
        var pendingDesign = designDecsEl.Count(d => string.Equals(d.Attribute("status")?.Value, "proposed", StringComparison.Ordinal));

        var critEl = (specDoc.Root?.Element("product")?.Element("acceptance")?.Elements("criterion") ??
                      specDoc.Root?.Element("research")?.Element("acceptance")?.Elements("criterion") ??
                      Enumerable.Empty<XElement>());
        var pendingCriteria = critEl.Count(c => string.Equals(c.Attribute("decision")?.Value ?? "pending", "pending", StringComparison.Ordinal));

        var qEl = specDoc.Root?.Element("research")?.Element("questions")?.Elements("question") ?? Enumerable.Empty<XElement>();
        var pendingQuestions = qEl.Count(q => string.Equals(q.Attribute("status")?.Value ?? "open", "open", StringComparison.Ordinal));

        // Completion requires no proposed requirements and no proposed design decisions
        if (pendingRequirements > 0)
        {
            technicalChecks.Add(new ReadinessTechnicalCheck("no_proposed_requirements", "failed", $"{pendingRequirements} requirement(s) are still in 'proposed' status"));
            allChecksPassed = false;
        }
        else
        {
            technicalChecks.Add(new ReadinessTechnicalCheck("no_proposed_requirements", "passed", "No proposed requirements"));
        }

        if (pendingDesign > 0)
        {
            technicalChecks.Add(new ReadinessTechnicalCheck("no_proposed_design_decisions", "failed", $"{pendingDesign} design decision(s) are still in 'proposed' status"));
            allChecksPassed = false;
        }
        else
        {
            technicalChecks.Add(new ReadinessTechnicalCheck("no_proposed_design_decisions", "passed", "No proposed design decisions"));
        }

        var productDecisions = new ReadinessProductDecisions(
            pendingRequirements,
            pendingDesign,
            pendingCriteria,
            pendingQuestions);

        var result = new IterationReadinessResult(
            iterId,
            "completion",
            specRevision,
            tasksRevision,
            technicallyReady: allChecksPassed,
            ownerConfirmationRequired: true,
            technicalChecks,
            productDecisions,
            new ReadinessRequiredAction("complete"));

        return (true, result, Array.Empty<Diagnostic>());
    }
}
