using System.Buffers;
using System.Globalization;
using System.Xml;
using DogdouSpec.Core.Diagnostics;
using DogdouSpec.Core.Tasks;

namespace DogdouSpec.Core.Validation;

/// <summary>
/// Authoritative semantic validator that evaluates domain and relational rules
/// over schema-valid managed XML documents.
/// </summary>
public static class SemanticValidator
{
    private static readonly HashSet<string> ReservedDeviceNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
    };
    private static readonly SearchValues<char> InvalidScopePathCharacters = SearchValues.Create("<>\"|");
    private static readonly HashSet<string> AllowedBacklogSeverities = new(
        new[] { "p0", "p1", "p2", "p3" }, StringComparer.Ordinal);

    public static List<Diagnostic> Validate(
        ProjectSemanticIndex index,
        string? iterationFilter = null,
        string? documentFilter = null)
    {
        var diagnostics = new List<Diagnostic>();

        ValidateIdentityAndOwnership(index, diagnostics);
        ValidateOperationReceipts(index, diagnostics);
        ValidateReferences(index, diagnostics);
        ValidateBacklogItems(index, diagnostics);
        ValidateConfirmationTargets(index, diagnostics);
        ValidateTaskGraphsAndTerminalPredicates(index, diagnostics);
        ValidateTaskReviews(index, diagnostics);
        ValidateTaskScopePaths(index, diagnostics);
        ValidateProtectedProductStateAndCompletion(index, diagnostics);
        ValidateStatusIndexTerms(index, diagnostics);

        // Filter diagnostics to the requested scope if specified
        IEnumerable<Diagnostic> filtered = diagnostics;
        if (!string.IsNullOrWhiteSpace(documentFilter))
        {
            filtered = filtered.Where(d => string.Equals(d.Document, documentFilter, StringComparison.Ordinal));
        }
        else if (!string.IsNullOrWhiteSpace(iterationFilter))
        {
            filtered = filtered.Where(d =>
                d.Document != null &&
                (d.Document.StartsWith(iterationFilter + "/", StringComparison.Ordinal) ||
                 d.Document.StartsWith(iterationFilter + "\\", StringComparison.Ordinal)));
        }

        // Sort deterministically: Document -> Line -> Column -> Code -> Message
        return filtered
            .OrderBy(d => d.Document, StringComparer.Ordinal)
            .ThenBy(d => d.Line ?? int.MaxValue)
            .ThenBy(d => d.Column ?? int.MaxValue)
            .ThenBy(d => d.Code, StringComparer.Ordinal)
            .ThenBy(d => d.Message, StringComparer.Ordinal)
            .ToList();
    }

    private static void ValidateIdentityAndOwnership(
        ProjectSemanticIndex index,
        List<Diagnostic> diagnostics)
    {
        // 1. Check duplicate IDs across the project
        foreach (var (id, instances) in index.ObjectsById)
        {
            if (instances.Count > 1)
            {
                foreach (var inst in instances)
                {
                    diagnostics.Add(Diagnostic.Error(
                        DiagnosticCodes.DuplicateId,
                        $"Duplicate identifier '{id}' found. Identifier is declared {instances.Count} times across the project.",
                        inst.Document.RelativePath,
                        inst.LineNumber,
                        inst.LinePosition));
                }
            }
        }

        // 2. Check time-first ID grammar
        foreach (var obj in index.AllObjects)
        {
            if (!ProjectSemanticIndex.IsValidTimeFirstId(obj.Id))
            {
                diagnostics.Add(Diagnostic.Error(
                    DiagnosticCodes.InvalidIdGrammar,
                    $"Identifier '{obj.Id}' does not conform to the time-first ID grammar (YYYYMMDD-name or YYYYMMDDThhmmssZ-name).",
                    obj.Document.RelativePath,
                    obj.LineNumber,
                    obj.LinePosition));
            }
        }

        // 3. Check spec.xml root ID vs iteration directory name
        foreach (var iter in index.Iterations)
        {
            if (iter.Document.IterationId != null &&
                !string.Equals(iter.Id, iter.Document.IterationId, StringComparison.Ordinal))
            {
                diagnostics.Add(Diagnostic.Error(
                    DiagnosticCodes.IterationIdMismatch,
                    $"Iteration root ID '{iter.Id}' in '{iter.Document.RelativePath}' does not match iteration directory name '{iter.Document.IterationId}'.",
                    iter.Document.RelativePath,
                    iter.LineNumber,
                    iter.LinePosition));
            }

            // Kind vs body agreement
            if (string.Equals(iter.Kind, "feature", StringComparison.Ordinal))
            {
                if (!iter.HasProduct || iter.HasResearch)
                {
                    diagnostics.Add(Diagnostic.Error(
                        DiagnosticCodes.WorkKindMismatch,
                        "Iteration with kind='feature' must contain a <product> element and no <research> element.",
                        iter.Document.RelativePath,
                        iter.LineNumber,
                        iter.LinePosition));
                }
            }
            else if (string.Equals(iter.Kind, "research", StringComparison.Ordinal))
            {
                if (!iter.HasResearch || iter.HasProduct)
                {
                    diagnostics.Add(Diagnostic.Error(
                        DiagnosticCodes.WorkKindMismatch,
                        "Iteration with kind='research' must contain a <research> element and no <product> element.",
                        iter.Document.RelativePath,
                        iter.LineNumber,
                        iter.LinePosition));
                }
            }
        }

        // 4. Check tasks.xml iteration attribute vs iteration directory & matching spec root ID
        foreach (var tasksDoc in index.TasksDocuments)
        {
            if (tasksDoc.Document.IterationId != null &&
                !string.Equals(tasksDoc.IterationAttribute, tasksDoc.Document.IterationId, StringComparison.Ordinal))
            {
                diagnostics.Add(Diagnostic.Error(
                    DiagnosticCodes.TasksIterationMismatch,
                    $"Tasks iteration attribute '{tasksDoc.IterationAttribute}' in '{tasksDoc.Document.RelativePath}' does not match iteration directory name '{tasksDoc.Document.IterationId}'.",
                    tasksDoc.Document.RelativePath,
                    tasksDoc.LineNumber,
                    tasksDoc.LinePosition));
            }

            var matchingSpec = index.Iterations.FirstOrDefault(i =>
                string.Equals(i.Document.IterationId, tasksDoc.Document.IterationId, StringComparison.Ordinal));

            if (matchingSpec != null &&
                !string.Equals(tasksDoc.IterationAttribute, matchingSpec.Id, StringComparison.Ordinal))
            {
                diagnostics.Add(Diagnostic.Error(
                    DiagnosticCodes.TasksIterationMismatch,
                    $"Tasks iteration attribute '{tasksDoc.IterationAttribute}' in '{tasksDoc.Document.RelativePath}' does not match spec root ID '{matchingSpec.Id}'.",
                    tasksDoc.Document.RelativePath,
                    tasksDoc.LineNumber,
                    tasksDoc.LinePosition));
            }
        }
    }

    private static void ValidateOperationReceipts(
        ProjectSemanticIndex index,
        List<Diagnostic> diagnostics)
    {
        foreach (var receipt in index.AllOperationReceipts)
        {
            // 1. Time-first grammar check for operation_id
            if (!ProjectSemanticIndex.IsValidTimeFirstId(receipt.OperationId))
            {
                diagnostics.Add(Diagnostic.Error(
                    DiagnosticCodes.InvalidIdGrammar,
                    $"Operation ID '{receipt.OperationId}' does not conform to the time-first ID grammar (YYYYMMDD-name or YYYYMMDDThhmmssZ-name).",
                    receipt.Document.RelativePath,
                    receipt.LineNumber,
                    receipt.LinePosition));
            }

            // 2. Must only be used on a receipt record owned by a Task or Backlog item.
            var isTaskOwnedRecord = string.Equals(receipt.ElementName, "record", StringComparison.Ordinal) &&
                                    string.Equals(receipt.ParentElementName, "records", StringComparison.Ordinal) &&
                                    !string.IsNullOrEmpty(receipt.ContainingTaskId) &&
                                    receipt.Document.RelativePath.EndsWith("tasks.xml", StringComparison.OrdinalIgnoreCase);
            var backlogItemId = receipt.Element.Ancestors("item").FirstOrDefault()?.Attribute("id")?.Value;
            var isBacklogOwnedRecord = string.Equals(receipt.ElementName, "record", StringComparison.Ordinal) &&
                                       string.Equals(receipt.ParentElementName, "records", StringComparison.Ordinal) &&
                                       !string.IsNullOrEmpty(backlogItemId) &&
                                       string.Equals(receipt.Document.RelativePath, "backlog.xml", StringComparison.OrdinalIgnoreCase);

            if (!isTaskOwnedRecord && !isBacklogOwnedRecord)
            {
                diagnostics.Add(Diagnostic.Error(
                    DiagnosticCodes.InvalidReferenceTargetType,
                    $"Operation ID '{receipt.OperationId}' is used on <{receipt.ElementName}> in '{receipt.Document.RelativePath}'. Operation IDs are only permitted on Task- or Backlog-item-owned records.",
                    receipt.Document.RelativePath,
                    receipt.LineNumber,
                    receipt.LinePosition));
            }

            // 3. Collision with element IDs (an operation_id must not collide with an element @id)
            if (index.ObjectsById.TryGetValue(receipt.OperationId, out var collidedObjects))
            {
                diagnostics.Add(Diagnostic.Error(
                    DiagnosticCodes.DuplicateId,
                    $"Identifier '{receipt.OperationId}' is used as both an element ID and an operation_id.",
                    receipt.Document.RelativePath,
                    receipt.LineNumber,
                    receipt.LinePosition));
            }
        }

        // 4. Check if any operation_id is spread across multiple owners or documents.
        foreach (var (opId, receipts) in index.OperationReceiptsById)
        {
            var distinctOwners = receipts
                .Select(r =>
                {
                    var backlogItemId = r.Element.Ancestors("item").FirstOrDefault()?.Attribute("id")?.Value;
                    return (Doc: r.Document.RelativePath, Owner: r.ContainingTaskId ?? backlogItemId ?? string.Empty);
                })
                .Distinct()
                .ToList();

            if (distinctOwners.Count > 1)
            {
                foreach (var r in receipts)
                {
                    diagnostics.Add(Diagnostic.Error(
                        DiagnosticCodes.AmbiguousReference,
                        $"Operation ID '{opId}' is spread across multiple Task or Backlog-item owners ({string.Join(", ", distinctOwners.Select(d => $"{d.Doc}:{d.Owner}"))}).",
                        r.Document.RelativePath,
                        r.LineNumber,
                        r.LinePosition));
                }
            }
        }
    }

    private static void ValidateReferences(
        ProjectSemanticIndex index,
        List<Diagnostic> diagnostics)
    {
        foreach (var r in index.AllReferences)
        {
            // 1. Resolve target in project index
            if (!index.ObjectsById.TryGetValue(r.Target, out var targetObjs) || targetObjs.Count == 0)
            {
                diagnostics.Add(Diagnostic.Error(
                    DiagnosticCodes.DanglingReference,
                    $"Reference target '{r.Target}' could not be resolved in the project index.",
                    r.Document.RelativePath,
                    r.LineNumber,
                    r.LinePosition));
                continue;
            }

            if (targetObjs.Count > 1)
            {
                diagnostics.Add(Diagnostic.Error(
                    DiagnosticCodes.AmbiguousReference,
                    $"Reference target '{r.Target}' is ambiguous and matches {targetObjs.Count} elements across the project.",
                    r.Document.RelativePath,
                    r.LineNumber,
                    r.LinePosition));
                continue;
            }

            var targetObj = targetObjs[0];

            // 2. Determine narrowest sufficient scope
            string expectedNarrowestScope;
            if (string.Equals(targetObj.Document.FullPath, r.Document.FullPath, StringComparison.OrdinalIgnoreCase))
            {
                expectedNarrowestScope = "document";
            }
            else if (targetObj.Document.IterationId != null &&
                     r.Document.IterationId != null &&
                     string.Equals(targetObj.Document.IterationId, r.Document.IterationId, StringComparison.Ordinal))
            {
                expectedNarrowestScope = "iteration";
            }
            else
            {
                expectedNarrowestScope = "project";
            }

            // Check declared scope against expected narrowest scope
            if (string.Equals(r.Scope, "document", StringComparison.Ordinal))
            {
                if (expectedNarrowestScope != "document")
                {
                    diagnostics.Add(Diagnostic.Error(
                        DiagnosticCodes.ReferenceScopeViolation,
                        $"Reference with scope='document' targets '{r.Target}' in '{targetObj.Document.RelativePath}', which is outside the containing document '{r.Document.RelativePath}'.",
                        r.Document.RelativePath,
                        r.LineNumber,
                        r.LinePosition));
                }
            }
            else if (string.Equals(r.Scope, "iteration", StringComparison.Ordinal))
            {
                if (expectedNarrowestScope == "project")
                {
                    diagnostics.Add(Diagnostic.Error(
                        DiagnosticCodes.ReferenceScopeViolation,
                        $"Reference with scope='iteration' targets '{r.Target}' in '{targetObj.Document.RelativePath}', which is outside the containing iteration '{r.Document.IterationId ?? "root"}'.",
                        r.Document.RelativePath,
                        r.LineNumber,
                        r.LinePosition));
                }
                else if (expectedNarrowestScope == "document")
                {
                    diagnostics.Add(Diagnostic.Error(
                        DiagnosticCodes.ReferenceScopeNotNarrowest,
                        $"Reference targets '{r.Target}' within the same document '{r.Document.RelativePath}', but declares scope='iteration'. Narrowest sufficient scope 'document' is required.",
                        r.Document.RelativePath,
                        r.LineNumber,
                        r.LinePosition));
                }
            }
            else if (string.Equals(r.Scope, "project", StringComparison.Ordinal))
            {
                if (expectedNarrowestScope == "document")
                {
                    diagnostics.Add(Diagnostic.Error(
                        DiagnosticCodes.ReferenceScopeNotNarrowest,
                        $"Reference targets '{r.Target}' within the same document '{r.Document.RelativePath}', but declares scope='project'. Narrowest sufficient scope 'document' is required.",
                        r.Document.RelativePath,
                        r.LineNumber,
                        r.LinePosition));
                }
                else if (expectedNarrowestScope == "iteration")
                {
                    diagnostics.Add(Diagnostic.Error(
                        DiagnosticCodes.ReferenceScopeNotNarrowest,
                        $"Reference targets '{r.Target}' within the same iteration '{r.Document.IterationId}', but declares scope='project'. Narrowest sufficient scope 'iteration' is required.",
                        r.Document.RelativePath,
                        r.LineNumber,
                        r.LinePosition));
                }
            }

            // 3. Relation-specific type checks
            var isTaskOrigin = r.Element.Parent?.Name.LocalName == "origin" &&
                               r.Element.Parent?.Parent?.Name.LocalName == "task";
            if (isTaskOrigin)
            {
                var isOperational = string.Equals(r.Relation, "supports", StringComparison.Ordinal) &&
                                    string.Equals(r.Scope, "iteration", StringComparison.Ordinal) &&
                                    string.Equals(targetObj.ElementName, "iteration", StringComparison.Ordinal) &&
                                    string.Equals(targetObj.Id, r.Document.IterationId, StringComparison.Ordinal);
                var isRequirement = string.Equals(r.Relation, "implements", StringComparison.Ordinal) &&
                                    string.Equals(r.Scope, "iteration", StringComparison.Ordinal) &&
                                    string.Equals(targetObj.ElementName, "requirement", StringComparison.Ordinal);
                if (!isOperational && !isRequirement)
                {
                    diagnostics.Add(Diagnostic.Error(
                        DiagnosticCodes.InvalidReferenceTargetType,
                        $"Task origin must be an iteration implements reference to a requirement or the owning iteration supports reference; found relation '{r.Relation}' targeting <{targetObj.ElementName}>.",
                        r.Document.RelativePath, r.LineNumber, r.LinePosition));
                }
            }
            // Task dependency: depends-on structurally inside task/dependencies must target a task
            var isTaskDependency = string.Equals(r.Relation, "depends-on", StringComparison.Ordinal) &&
                                   r.Element.Parent?.Name.LocalName == "dependencies" &&
                                   r.Element.Parent?.Parent?.Name.LocalName == "task";

            if (isTaskDependency)
            {
                if (!string.Equals(targetObj.ElementName, "task", StringComparison.Ordinal))
                {
                    diagnostics.Add(Diagnostic.Error(
                        DiagnosticCodes.InvalidReferenceTargetType,
                        $"Task dependency reference with relation 'depends-on' must target a task, but targets '{r.Target}' which is a <{targetObj.ElementName}>.",
                        r.Document.RelativePath,
                        r.LineNumber,
                        r.LinePosition));
                }
            }
            // Record covers: must target a criterion
            else if (string.Equals(r.Relation, "covers", StringComparison.Ordinal) ||
                     (r.Element.Parent?.Name.LocalName == "covers"))
            {
                if (!string.Equals(targetObj.ElementName, "criterion", StringComparison.Ordinal))
                {
                    diagnostics.Add(Diagnostic.Error(
                        DiagnosticCodes.InvalidReferenceTargetType,
                        $"Record covers reference must target an acceptance criterion, but targets '{r.Target}' which is a <{targetObj.ElementName}>.",
                        r.Document.RelativePath,
                        r.LineNumber,
                        r.LinePosition));
                }
            }

            if (string.Equals(r.Document.RelativePath, "backlog.xml", StringComparison.OrdinalIgnoreCase))
            {
                var isBacklogSource = r.Element.Parent?.Name.LocalName == "source" &&
                                      r.Element.Parent?.Parent?.Name.LocalName == "item";
                var isBacklogTarget = r.Element.Parent?.Name.LocalName == "target" &&
                                      r.Element.Parent?.Parent?.Name.LocalName == "item";
                var isResolvingTask = string.Equals(r.Relation, "resolved-by", StringComparison.Ordinal) &&
                                      r.Element.Ancestors("record").Any() &&
                                      r.Element.Ancestors("item").Any();
                if (isBacklogSource && targetObj.ElementName is not ("iteration" or "task"))
                {
                    diagnostics.Add(Diagnostic.Error(
                        DiagnosticCodes.InvalidReferenceTargetType,
                        $"Backlog source reference must target an iteration or task, but targets <{targetObj.ElementName}>.",
                        r.Document.RelativePath, r.LineNumber, r.LinePosition));
                }
                if (isBacklogTarget &&
                    (!string.Equals(r.Relation, "target-iteration", StringComparison.Ordinal) ||
                     !string.Equals(targetObj.ElementName, "iteration", StringComparison.Ordinal)))
                {
                    diagnostics.Add(Diagnostic.Error(
                        DiagnosticCodes.InvalidReferenceTargetType,
                        $"Backlog target reference must use relation 'target-iteration' and target an iteration; found relation '{r.Relation}' targeting <{targetObj.ElementName}>.",
                        r.Document.RelativePath, r.LineNumber, r.LinePosition));
                }
                if (isResolvingTask && !string.Equals(targetObj.ElementName, "task", StringComparison.Ordinal))
                {
                    diagnostics.Add(Diagnostic.Error(
                        DiagnosticCodes.InvalidReferenceTargetType,
                        $"Backlog resolved-by reference must target a task, but targets <{targetObj.ElementName}>.",
                        r.Document.RelativePath, r.LineNumber, r.LinePosition));
                }
            }
        }
    }

    private static void ValidateTaskReviews(ProjectSemanticIndex index, List<Diagnostic> diagnostics)
    {
        foreach (var parsedTask in index.AllTasks)
        {
            var task = parsedTask.Element;
            var review = task.Element("review");
            if (review == null)
            {
                continue;
            }
            var required = bool.TryParse((string?)review.Attribute("required"), out var parsedRequired) && parsedRequired;
            var submissions = review.Elements("submission").ToList();
            if (!required && submissions.Count > 0)
            {
                diagnostics.Add(Diagnostic.Error(DiagnosticCodes.TaskReviewStateInvalid,
                    $"Task '{parsedTask.Id}' has review submissions while review required=false.",
                    parsedTask.Document.RelativePath));
            }
            if (submissions.Count > 0 && parsedTask.Status is "pending" or "blocked")
            {
                diagnostics.Add(Diagnostic.Error(DiagnosticCodes.TaskReviewStateInvalid,
                    $"Task '{parsedTask.Id}' cannot retain review submissions while status is '{parsedTask.Status}'.",
                    parsedTask.Document.RelativePath));
            }
            if (required && string.IsNullOrWhiteSpace((string?)task.Attribute("agent")))
            {
                diagnostics.Add(Diagnostic.Error(DiagnosticCodes.TaskReviewImplementerUnknown,
                    $"Review-required task '{parsedTask.Id}' must declare immutable implementer attribution in @agent.",
                    parsedTask.Document.RelativePath));
            }
            var records = task.Element("records")?.Elements("record").ToList() ?? new List<System.Xml.Linq.XElement>();
            foreach (var submission in submissions)
            {
                var recordId = (string?)submission.Attribute("record");
                var matches = records.Where(r => string.Equals((string?)r.Attribute("id"), recordId, StringComparison.Ordinal)).ToList();
                var disposition = (string?)submission.Attribute("disposition");
                var validKind = disposition == "approved" ? "decision" : "finding";
                var dispositionTerm = matches.FirstOrDefault()?.Element("index")?.Elements("term")
                    .FirstOrDefault(t => string.Equals((string?)t.Attribute("key"), "review-disposition", StringComparison.Ordinal))
                    ?.Attribute("value")?.Value;
                var fingerprintTerm = matches.FirstOrDefault()?.Element("index")?.Elements("term")
                    .FirstOrDefault(t => string.Equals((string?)t.Attribute("key"), "request-sha256", StringComparison.Ordinal))
                    ?.Attribute("value")?.Value;
                if (matches.Count != 1 ||
                    !string.Equals((string?)matches[0].Attribute("kind"), validKind, StringComparison.Ordinal) ||
                    !string.Equals((string?)matches[0].Attribute("actor"), (string?)submission.Attribute("actor"), StringComparison.Ordinal) ||
                    !string.Equals((string?)matches[0].Attribute("created_at"), (string?)submission.Attribute("reviewed_at"), StringComparison.Ordinal) ||
                    string.IsNullOrWhiteSpace((string?)matches[0].Attribute("operation_id")) ||
                    !string.Equals(dispositionTerm, disposition, StringComparison.Ordinal) ||
                    string.IsNullOrWhiteSpace(fingerprintTerm))
                {
                    diagnostics.Add(Diagnostic.Error(DiagnosticCodes.TaskReviewStateInvalid,
                        $"Task '{parsedTask.Id}' review submission '{submission.Attribute("id")?.Value}' does not link to one matching local durable {validKind} record.",
                        parsedTask.Document.RelativePath));
                }
            }
            if (string.Equals(parsedTask.Status, "done", StringComparison.Ordinal))
            {
                var evaluation = TaskReviewGate.Evaluate(task);
                if (evaluation.Required && !evaluation.Satisfied)
                {
                    diagnostics.Add(Diagnostic.Error(DiagnosticCodes.TaskReviewRequired,
                        $"Task '{parsedTask.Id}' cannot be done: {evaluation.Reason}", parsedTask.Document.RelativePath));
                }
            }
        }
    }

    private static void ValidateBacklogItems(ProjectSemanticIndex index, List<Diagnostic> diagnostics)
    {
        var backlog = index.LoadedDocuments.FirstOrDefault(d =>
            string.Equals(d.Document.RelativePath, "backlog.xml", StringComparison.OrdinalIgnoreCase));
        if (backlog.XDoc?.Root == null)
        {
            return;
        }

        foreach (var item in backlog.XDoc.Root.Element("items")?.Elements("item") ?? Enumerable.Empty<System.Xml.Linq.XElement>())
        {
            var id = (string?)item.Attribute("id") ?? "(missing)";
            var terms = item.Element("index")?.Elements("term").ToList() ?? new List<System.Xml.Linq.XElement>();
            var kindTerms = terms.Where(t => string.Equals((string?)t.Attribute("key"), "kind", StringComparison.Ordinal)).ToList();
            var severityTerms = terms.Where(t => string.Equals((string?)t.Attribute("key"), "severity", StringComparison.Ordinal)).ToList();
            var kind = kindTerms.SingleOrDefault()?.Attribute("value")?.Value;

            if (kindTerms.Count > 1 || (kindTerms.Count == 1 && string.IsNullOrWhiteSpace(kind)))
            {
                diagnostics.Add(Diagnostic.Error(DiagnosticCodes.InvalidArgument,
                    $"Backlog item '{id}' may declare at most one non-empty index term key='kind'.", "backlog.xml"));
            }
            if (string.Equals(kind, "defect", StringComparison.Ordinal) &&
                (severityTerms.Count != 1 || !AllowedBacklogSeverities.Contains(severityTerms[0].Attribute("value")?.Value ?? string.Empty)))
            {
                diagnostics.Add(Diagnostic.Error(DiagnosticCodes.InvalidArgument,
                    $"Defect backlog item '{id}' must declare exactly one severity term p0, p1, p2, or p3.", "backlog.xml"));
            }
            if (string.IsNullOrWhiteSpace(item.Element("impact")?.Value))
            {
                diagnostics.Add(Diagnostic.Error(DiagnosticCodes.InvalidArgument,
                    $"Backlog item '{id}' must state a non-empty impact.", "backlog.xml"));
            }
            if (item.Element("source")?.Elements("ref").Any() != true)
            {
                diagnostics.Add(Diagnostic.Error(DiagnosticCodes.InvalidArgument,
                    $"Backlog item '{id}' must contain at least one source reference.", "backlog.xml"));
            }
            var target = item.Element("target");
            var review = item.Element("review_condition");
            if ((target == null) == (review == null) || (review != null && string.IsNullOrWhiteSpace(review.Value)))
            {
                diagnostics.Add(Diagnostic.Error(DiagnosticCodes.InvalidArgument,
                    $"Backlog item '{id}' must contain exactly one target or non-empty review_condition.", "backlog.xml"));
            }
        }
    }

    private static void ValidateConfirmationTargets(
        ProjectSemanticIndex index,
        List<Diagnostic> diagnostics)
    {
        foreach (var iter in index.Iterations)
        {
            foreach (var conf in iter.Confirmations)
            {
                // 1. Detect duplicate / contradictory decisions within one confirmation
                var seenInConf = new Dictionary<string, (string Decision, ParsedConfirmationTarget Target)>(StringComparer.Ordinal);
                var allTargets = conf.Requirements
                    .Concat(conf.Questions)
                    .Concat(conf.DesignDecisions)
                    .Concat(conf.AcceptanceCriteria);

                foreach (var t in allTargets)
                {
                    if (seenInConf.TryGetValue(t.Target, out var prev))
                    {
                        if (!string.Equals(prev.Decision, t.Decision, StringComparison.Ordinal))
                        {
                            diagnostics.Add(Diagnostic.Error(
                                DiagnosticCodes.ContradictoryConfirmationDecision,
                                $"Contradictory decisions '{prev.Decision}' and '{t.Decision}' specified for target '{t.Target}' in confirmation '{conf.Id}'.",
                                iter.Document.RelativePath,
                                t.LineNumber ?? conf.LineNumber,
                                t.LinePosition ?? conf.LinePosition));
                        }
                        else
                        {
                            diagnostics.Add(Diagnostic.Error(
                                DiagnosticCodes.DuplicateConfirmationTarget,
                                $"Duplicate confirmation target '{t.Target}' found in confirmation '{conf.Id}'.",
                                iter.Document.RelativePath,
                                t.LineNumber ?? conf.LineNumber,
                                t.LinePosition ?? conf.LinePosition));
                        }
                    }
                    else
                    {
                        seenInConf[t.Target] = (t.Decision, t);
                    }
                }

                // 2. Validate requirement targets
                var allowedReqDecs = new[] { "approved", "superseded", "withdrawn" };
                foreach (var t in conf.Requirements)
                {
                    if (!allowedReqDecs.Contains(t.Decision, StringComparer.Ordinal))
                    {
                        diagnostics.Add(Diagnostic.Error(
                            DiagnosticCodes.InvalidArgument,
                            $"Invalid requirement decision '{t.Decision}' for target '{t.Target}' in confirmation '{conf.Id}'. Allowed requirement decisions: {string.Join(", ", allowedReqDecs)}.",
                            iter.Document.RelativePath,
                            t.LineNumber ?? conf.LineNumber,
                            t.LinePosition ?? conf.LinePosition));
                    }

                    if (!index.ObjectsById.TryGetValue(t.Target, out var targetObjs) || targetObjs.Count == 0)
                    {
                        diagnostics.Add(Diagnostic.Error(
                            DiagnosticCodes.DanglingReference,
                            $"Confirmation requirement target '{t.Target}' could not be resolved in the project index.",
                            iter.Document.RelativePath,
                            t.LineNumber ?? conf.LineNumber,
                            t.LinePosition ?? conf.LinePosition));
                    }
                    else if (targetObjs.Count > 1)
                    {
                        diagnostics.Add(Diagnostic.Error(
                            DiagnosticCodes.AmbiguousReference,
                            $"Confirmation requirement target '{t.Target}' is ambiguous and matches {targetObjs.Count} elements across the project.",
                            iter.Document.RelativePath,
                            t.LineNumber ?? conf.LineNumber,
                            t.LinePosition ?? conf.LinePosition));
                    }
                    else
                    {
                        var targetObj = targetObjs[0];
                        if (!string.Equals(targetObj.Document.IterationId, iter.Id, StringComparison.Ordinal))
                        {
                            diagnostics.Add(Diagnostic.Error(
                                DiagnosticCodes.ReferenceScopeViolation,
                                $"Confirmation requirement target '{t.Target}' resolves to '{targetObj.Document.RelativePath}', which is outside the containing iteration '{iter.Id}'.",
                                iter.Document.RelativePath,
                                t.LineNumber ?? conf.LineNumber,
                                t.LinePosition ?? conf.LinePosition));
                        }
                        else if (!string.Equals(targetObj.ElementName, "requirement", StringComparison.Ordinal))
                        {
                            diagnostics.Add(Diagnostic.Error(
                                DiagnosticCodes.InvalidReferenceTargetType,
                                $"Confirmation requirement target '{t.Target}' must target a <requirement>, but targets a <{targetObj.ElementName}>.",
                                iter.Document.RelativePath,
                                t.LineNumber ?? conf.LineNumber,
                                t.LinePosition ?? conf.LinePosition));
                        }
                    }
                }

                // 3. Validate question targets
                var allowedQDecs = new[] { "answered", "deferred", "withdrawn" };
                foreach (var t in conf.Questions)
                {
                    if (!allowedQDecs.Contains(t.Decision, StringComparer.Ordinal))
                    {
                        diagnostics.Add(Diagnostic.Error(
                            DiagnosticCodes.InvalidArgument,
                            $"Invalid question decision '{t.Decision}' for target '{t.Target}' in confirmation '{conf.Id}'. Allowed question decisions: {string.Join(", ", allowedQDecs)}.",
                            iter.Document.RelativePath,
                            t.LineNumber ?? conf.LineNumber,
                            t.LinePosition ?? conf.LinePosition));
                    }

                    if (!index.ObjectsById.TryGetValue(t.Target, out var targetObjs) || targetObjs.Count == 0)
                    {
                        diagnostics.Add(Diagnostic.Error(
                            DiagnosticCodes.DanglingReference,
                            $"Confirmation question target '{t.Target}' could not be resolved in the project index.",
                            iter.Document.RelativePath,
                            t.LineNumber ?? conf.LineNumber,
                            t.LinePosition ?? conf.LinePosition));
                    }
                    else if (targetObjs.Count > 1)
                    {
                        diagnostics.Add(Diagnostic.Error(
                            DiagnosticCodes.AmbiguousReference,
                            $"Confirmation question target '{t.Target}' is ambiguous and matches {targetObjs.Count} elements across the project.",
                            iter.Document.RelativePath,
                            t.LineNumber ?? conf.LineNumber,
                            t.LinePosition ?? conf.LinePosition));
                    }
                    else
                    {
                        var targetObj = targetObjs[0];
                        if (!string.Equals(targetObj.Document.IterationId, iter.Id, StringComparison.Ordinal))
                        {
                            diagnostics.Add(Diagnostic.Error(
                                DiagnosticCodes.ReferenceScopeViolation,
                                $"Confirmation question target '{t.Target}' resolves to '{targetObj.Document.RelativePath}', which is outside the containing iteration '{iter.Id}'.",
                                iter.Document.RelativePath,
                                t.LineNumber ?? conf.LineNumber,
                                t.LinePosition ?? conf.LinePosition));
                        }
                        else if (!string.Equals(targetObj.ElementName, "question", StringComparison.Ordinal))
                        {
                            diagnostics.Add(Diagnostic.Error(
                                DiagnosticCodes.InvalidReferenceTargetType,
                                $"Confirmation question target '{t.Target}' must target a <question>, but targets a <{targetObj.ElementName}>.",
                                iter.Document.RelativePath,
                                t.LineNumber ?? conf.LineNumber,
                                t.LinePosition ?? conf.LinePosition));
                        }
                    }
                }

                // 4. Validate design decision targets
                var allowedDDecs = new[] { "accepted", "rejected", "superseded" };
                foreach (var t in conf.DesignDecisions)
                {
                    if (!allowedDDecs.Contains(t.Decision, StringComparer.Ordinal))
                    {
                        diagnostics.Add(Diagnostic.Error(
                            DiagnosticCodes.InvalidArgument,
                            $"Invalid design decision disposition '{t.Decision}' for target '{t.Target}' in confirmation '{conf.Id}'. Allowed design decision dispositions: {string.Join(", ", allowedDDecs)}.",
                            iter.Document.RelativePath,
                            t.LineNumber ?? conf.LineNumber,
                            t.LinePosition ?? conf.LinePosition));
                    }

                    if (!index.ObjectsById.TryGetValue(t.Target, out var targetObjs) || targetObjs.Count == 0)
                    {
                        diagnostics.Add(Diagnostic.Error(
                            DiagnosticCodes.DanglingReference,
                            $"Confirmation design decision target '{t.Target}' could not be resolved in the project index.",
                            iter.Document.RelativePath,
                            t.LineNumber ?? conf.LineNumber,
                            t.LinePosition ?? conf.LinePosition));
                    }
                    else if (targetObjs.Count > 1)
                    {
                        diagnostics.Add(Diagnostic.Error(
                            DiagnosticCodes.AmbiguousReference,
                            $"Confirmation design decision target '{t.Target}' is ambiguous and matches {targetObjs.Count} elements across the project.",
                            iter.Document.RelativePath,
                            t.LineNumber ?? conf.LineNumber,
                            t.LinePosition ?? conf.LinePosition));
                    }
                    else
                    {
                        var targetObj = targetObjs[0];
                        if (!string.Equals(targetObj.Document.IterationId, iter.Id, StringComparison.Ordinal))
                        {
                            diagnostics.Add(Diagnostic.Error(
                                DiagnosticCodes.ReferenceScopeViolation,
                                $"Confirmation design decision target '{t.Target}' resolves to '{targetObj.Document.RelativePath}', which is outside the containing iteration '{iter.Id}'.",
                                iter.Document.RelativePath,
                                t.LineNumber ?? conf.LineNumber,
                                t.LinePosition ?? conf.LinePosition));
                        }
                        else if (!string.Equals(targetObj.ElementName, "decision", StringComparison.Ordinal))
                        {
                            diagnostics.Add(Diagnostic.Error(
                                DiagnosticCodes.InvalidReferenceTargetType,
                                $"Confirmation design decision target '{t.Target}' must target a <decision>, but targets a <{targetObj.ElementName}>.",
                                iter.Document.RelativePath,
                                t.LineNumber ?? conf.LineNumber,
                                t.LinePosition ?? conf.LinePosition));
                        }
                    }
                }

                // 5. Validate acceptance criteria targets
                var allowedCritDecs = new[] { "accepted", "rejected", "waived" };
                foreach (var t in conf.AcceptanceCriteria)
                {
                    if (!allowedCritDecs.Contains(t.Decision, StringComparer.Ordinal))
                    {
                        diagnostics.Add(Diagnostic.Error(
                            DiagnosticCodes.InvalidArgument,
                            $"Invalid acceptance criterion decision '{t.Decision}' for target '{t.Target}' in confirmation '{conf.Id}'. Allowed acceptance criterion decisions: {string.Join(", ", allowedCritDecs)}.",
                            iter.Document.RelativePath,
                            t.LineNumber ?? conf.LineNumber,
                            t.LinePosition ?? conf.LinePosition));
                    }

                    if (!index.ObjectsById.TryGetValue(t.Target, out var targetObjs) || targetObjs.Count == 0)
                    {
                        diagnostics.Add(Diagnostic.Error(
                            DiagnosticCodes.DanglingReference,
                            $"Confirmation acceptance criterion target '{t.Target}' could not be resolved in the project index.",
                            iter.Document.RelativePath,
                            t.LineNumber ?? conf.LineNumber,
                            t.LinePosition ?? conf.LinePosition));
                    }
                    else if (targetObjs.Count > 1)
                    {
                        diagnostics.Add(Diagnostic.Error(
                            DiagnosticCodes.AmbiguousReference,
                            $"Confirmation acceptance criterion target '{t.Target}' is ambiguous and matches {targetObjs.Count} elements across the project.",
                            iter.Document.RelativePath,
                            t.LineNumber ?? conf.LineNumber,
                            t.LinePosition ?? conf.LinePosition));
                    }
                    else
                    {
                        var targetObj = targetObjs[0];
                        if (!string.Equals(targetObj.Document.IterationId, iter.Id, StringComparison.Ordinal))
                        {
                            diagnostics.Add(Diagnostic.Error(
                                DiagnosticCodes.ReferenceScopeViolation,
                                $"Confirmation acceptance criterion target '{t.Target}' resolves to '{targetObj.Document.RelativePath}', which is outside the containing iteration '{iter.Id}'.",
                                iter.Document.RelativePath,
                                t.LineNumber ?? conf.LineNumber,
                                t.LinePosition ?? conf.LinePosition));
                        }
                        else if (!string.Equals(targetObj.ElementName, "criterion", StringComparison.Ordinal) ||
                                 !string.Equals(targetObj.Document.FullPath, iter.Document.FullPath, StringComparison.OrdinalIgnoreCase))
                        {
                            diagnostics.Add(Diagnostic.Error(
                                DiagnosticCodes.InvalidReferenceTargetType,
                                $"Confirmation acceptance criterion target '{t.Target}' must target a product/research <criterion> in '{iter.Document.RelativePath}', but targets a <{targetObj.ElementName}> in '{targetObj.Document.RelativePath}'.",
                                iter.Document.RelativePath,
                                t.LineNumber ?? conf.LineNumber,
                                t.LinePosition ?? conf.LinePosition));
                        }
                    }
                }
            }
        }
    }

    private static void ValidateTaskScopePaths(
        ProjectSemanticIndex index,
        List<Diagnostic> diagnostics)
    {
        foreach (var task in index.AllTasks)
        {
            foreach (var repository in task.Element.Element("scope")?.Elements("repository") ?? Enumerable.Empty<System.Xml.Linq.XElement>())
            {
                ValidateTaskScopePathValue(task, repository, repository.Attribute("path")?.Value, isRepositoryBase: true, diagnostics);

                foreach (var include in repository.Elements("include"))
                {
                    ValidateTaskScopePathValue(task, include, include.Attribute("path")?.Value, isRepositoryBase: false, diagnostics);
                }

                foreach (var exclude in repository.Elements("exclude"))
                {
                    ValidateTaskScopePathValue(task, exclude, exclude.Attribute("path")?.Value, isRepositoryBase: false, diagnostics);
                }
            }
        }
    }

    private static void ValidateTaskScopePathValue(
        ParsedTask task,
        System.Xml.Linq.XElement element,
        string? rawPath,
        bool isRepositoryBase,
        List<Diagnostic> diagnostics)
    {
        var lineInfo = (IXmlLineInfo)element;
        Diagnostic BuildDiagnostic(string reason) => Diagnostic.Error(
            DiagnosticCodes.InvalidPath,
            $"Task '{task.Id}' declares unsafe scope path '{rawPath ?? string.Empty}': {reason}",
            task.Document.RelativePath,
            lineInfo.HasLineInfo() ? lineInfo.LineNumber : task.LineNumber,
            lineInfo.HasLineInfo() ? lineInfo.LinePosition : task.LinePosition);

        if (string.IsNullOrWhiteSpace(rawPath))
        {
            diagnostics.Add(BuildDiagnostic("the value must not be empty."));
            return;
        }

        var path = rawPath.Trim();
        if (!string.Equals(rawPath, path, StringComparison.Ordinal) || path.Any(char.IsControl))
        {
            diagnostics.Add(BuildDiagnostic("leading or trailing whitespace and control characters are not allowed."));
            return;
        }

        if (path.StartsWith('/') ||
            path.StartsWith('\\') ||
            (path.Length >= 2 && path[1] == ':') ||
            path.Contains(':'))
        {
            diagnostics.Add(BuildDiagnostic("absolute, rooted, drive-qualified, device, and ADS forms are not allowed."));
            return;
        }

        if (isRepositoryBase && (path.Contains('*') || path.Contains('?')))
        {
            diagnostics.Add(BuildDiagnostic("repository base paths must be literal and cannot contain wildcards."));
            return;
        }

        var segments = path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        foreach (var segment in segments)
        {
            if (segment == "..")
            {
                diagnostics.Add(BuildDiagnostic("parent traversal segments are not allowed."));
                return;
            }

            if (segment.AsSpan().IndexOfAny(InvalidScopePathCharacters) >= 0)
            {
                diagnostics.Add(BuildDiagnostic("Windows-unsafe path characters are not allowed."));
                return;
            }

            if (segment.Length > 1 && (segment.EndsWith(' ') || segment.EndsWith('.')))
            {
                diagnostics.Add(BuildDiagnostic("segments ending in a space or period are not Windows-safe."));
                return;
            }

            if (!segment.Contains('*') && !segment.Contains('?'))
            {
                var baseName = Path.GetFileNameWithoutExtension(segment);
                if (ReservedDeviceNames.Contains(segment) || ReservedDeviceNames.Contains(baseName))
                {
                    diagnostics.Add(BuildDiagnostic($"reserved device segment '{segment}' is not allowed."));
                    return;
                }
            }
        }
    }

    private static void ValidateTaskGraphsAndTerminalPredicates(
        ProjectSemanticIndex index,
        List<Diagnostic> diagnostics)
    {
        foreach (var task in index.AllTasks)
        {
            var operationalOrigins = task.Origin.Where(o => string.Equals(o.Relation, "supports", StringComparison.Ordinal)).ToList();
            if (operationalOrigins.Count > 0 && (operationalOrigins.Count != 1 || task.Origin.Count != 1))
            {
                diagnostics.Add(Diagnostic.Error(
                    DiagnosticCodes.InvalidReferenceTargetType,
                    $"Task '{task.Id}' mixes operational and requirement origins. An operational task must have exactly one supports origin.",
                    task.Document.RelativePath, task.LineNumber, task.LinePosition));
            }
        }

        // 1. Build dependency adjacency map across all tasks in the project semantic index
        var taskMap = new Dictionary<string, ParsedTask>(StringComparer.Ordinal);
        foreach (var t in index.AllTasks)
        {
            taskMap.TryAdd(t.Id, t);
        }

        var adj = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var task in index.AllTasks.OrderBy(t => t.Id, StringComparer.Ordinal))
        {
            var deps = new List<string>();
            foreach (var dep in task.Dependencies)
            {
                if (string.Equals(dep.Relation, "depends-on", StringComparison.Ordinal))
                {
                    // Self dependency check
                    if (string.Equals(dep.Target, task.Id, StringComparison.Ordinal))
                    {
                        diagnostics.Add(Diagnostic.Error(
                            DiagnosticCodes.DependencyCycle,
                            $"Self-dependency detected: task '{task.Id}' depends on itself.",
                            task.Document.RelativePath,
                            dep.LineNumber ?? task.LineNumber,
                            dep.LinePosition ?? task.LinePosition));
                    }
                    else if (taskMap.ContainsKey(dep.Target))
                    {
                        deps.Add(dep.Target);
                    }
                }
            }
            adj[task.Id] = deps;
        }

        // 2. Detect cycles using DFS with deterministic ordering across all tasks
        var state = new Dictionary<string, int>(StringComparer.Ordinal); // 0 = unvisited, 1 = visiting, 2 = visited
        var pathStack = new List<string>();
        var reportedCycles = new HashSet<string>(StringComparer.Ordinal);

        foreach (var task in index.AllTasks.OrderBy(t => t.Id, StringComparer.Ordinal))
        {
            if (!state.TryGetValue(task.Id, out var s) || s == 0)
            {
                DfsCycleCheck(task.Id, adj, taskMap, state, pathStack, reportedCycles, diagnostics);
            }
        }

        // 3. Check terminal predicates for each task in the project
        foreach (var task in index.AllTasks)
        {
            if (string.Equals(task.Status, "done", StringComparison.Ordinal))
            {
                // 1. All task acceptance criteria statuses passed or not-applicable
                foreach (var crit in task.Criteria)
                {
                    var status = crit.Status ?? "pending";
                    if (!string.Equals(status, "passed", StringComparison.Ordinal) &&
                        !string.Equals(status, "not-applicable", StringComparison.Ordinal))
                    {
                        diagnostics.Add(Diagnostic.Error(
                            DiagnosticCodes.TaskCriterionNotTerminal,
                            $"Task '{task.Id}' has status 'done' but criterion '{crit.Id}' has non-terminal status '{status}'. All criteria must be 'passed' or 'not-applicable'.",
                            task.Document.RelativePath,
                            crit.LineNumber ?? task.LineNumber,
                            crit.LinePosition ?? task.LinePosition));
                    }
                }

                // 2. completed_at present
                if (string.IsNullOrWhiteSpace(task.CompletedAt))
                {
                    diagnostics.Add(Diagnostic.Error(
                        DiagnosticCodes.TaskCompletedAtMissing,
                        $"Task '{task.Id}' has status 'done' but is missing the required 'completed_at' timestamp attribute.",
                        task.Document.RelativePath,
                        task.LineNumber,
                        task.LinePosition));
                }

                // 3. At least one task-local completion record
                var hasCompletionRecord = task.Records.Any(r =>
                    string.Equals(r.Kind, "completion", StringComparison.Ordinal));

                if (!hasCompletionRecord)
                {
                    diagnostics.Add(Diagnostic.Error(
                        DiagnosticCodes.TaskCompletionRecordMissing,
                        $"Task '{task.Id}' has status 'done' but does not contain a task-local completion record (record with kind='completion').",
                        task.Document.RelativePath,
                        task.LineNumber,
                        task.LinePosition));
                }

                // 4. Each task criterion covered by at least one task-local verification or completion record
                foreach (var crit in task.Criteria)
                {
                    var isCovered = task.Records.Any(r =>
                        (string.Equals(r.Kind, "verification", StringComparison.Ordinal) ||
                         string.Equals(r.Kind, "completion", StringComparison.Ordinal)) &&
                        r.Covers.Any(cov => string.Equals(cov.Target, crit.Id, StringComparison.Ordinal)));

                    if (!isCovered)
                    {
                        diagnostics.Add(Diagnostic.Error(
                            DiagnosticCodes.TaskCriterionNotCovered,
                            $"Task '{task.Id}' has status 'done' but acceptance criterion '{crit.Id}' is not covered by any task-local verification or completion record.",
                            task.Document.RelativePath,
                            crit.LineNumber ?? task.LineNumber,
                            crit.LinePosition ?? task.LinePosition));
                    }
                }

                // 5. No active task-local finding record
                foreach (var rec in task.Records)
                {
                    if (string.Equals(rec.Kind, "finding", StringComparison.Ordinal) &&
                        string.Equals(rec.Status, "active", StringComparison.Ordinal))
                    {
                        diagnostics.Add(Diagnostic.Error(
                            DiagnosticCodes.TaskActiveFindingBlocksCompletion,
                            $"Task '{task.Id}' has status 'done' but contains an active finding record '{rec.Id}'. Active findings must be resolved or superseded before completing the task.",
                            task.Document.RelativePath,
                            rec.LineNumber ?? task.LineNumber,
                            rec.LinePosition ?? task.LinePosition));
                    }
                }
            }
            else
            {
                // Non-done task must not claim completed_at
                if (!string.IsNullOrWhiteSpace(task.CompletedAt))
                {
                    diagnostics.Add(Diagnostic.Error(
                        DiagnosticCodes.TaskNonDoneHasCompletedAt,
                        $"Task '{task.Id}' has status '{task.Status}' but specifies 'completed_at'. Non-done tasks must not claim completed_at.",
                        task.Document.RelativePath,
                        task.LineNumber,
                        task.LinePosition));
                }

                // Pending task must not claim started_at
                if (string.Equals(task.Status, "pending", StringComparison.Ordinal) && !string.IsNullOrWhiteSpace(task.StartedAt))
                {
                    diagnostics.Add(Diagnostic.Error(
                        DiagnosticCodes.TaskPendingHasStartedAt,
                        $"Task '{task.Id}' has status 'pending' but specifies 'started_at'. Pending tasks must not claim started_at.",
                        task.Document.RelativePath,
                        task.LineNumber,
                        task.LinePosition));
                }
            }
        }
    }

    private static void DfsCycleCheck(
        string currentId,
        Dictionary<string, List<string>> adj,
        Dictionary<string, ParsedTask> taskMap,
        Dictionary<string, int> state,
        List<string> pathStack,
        HashSet<string> reportedCycles,
        List<Diagnostic> diagnostics)
    {
        state[currentId] = 1; // Visiting
        pathStack.Add(currentId);

        if (adj.TryGetValue(currentId, out var neighbors))
        {
            foreach (var neighbor in neighbors.OrderBy(n => n, StringComparer.Ordinal))
            {
                if (state.TryGetValue(neighbor, out var nState) && nState == 1)
                {
                    // Cycle found
                    var cycleStartIndex = pathStack.IndexOf(neighbor);
                    if (cycleStartIndex >= 0)
                    {
                        var cycleNodes = pathStack.Skip(cycleStartIndex).Concat(new[] { neighbor }).ToList();

                        // Normalize cycle key for deduplication (smallest node first)
                        var minIndex = 0;
                        for (int i = 1; i < cycleNodes.Count - 1; i++)
                        {
                            if (string.Compare(cycleNodes[i], cycleNodes[minIndex], StringComparison.Ordinal) < 0)
                            {
                                minIndex = i;
                            }
                        }

                        var normalizedNodes = cycleNodes.Take(cycleNodes.Count - 1).Skip(minIndex)
                            .Concat(cycleNodes.Take(minIndex))
                            .ToList();
                        normalizedNodes.Add(normalizedNodes[0]);
                        var normalizedKey = string.Join(" -> ", normalizedNodes);

                        if (reportedCycles.Add(normalizedKey))
                        {
                            var taskObj = taskMap[currentId];
                            diagnostics.Add(Diagnostic.Error(
                                DiagnosticCodes.DependencyCycle,
                                $"Dependency cycle detected in task dependencies: {normalizedKey}.",
                                taskObj.Document.RelativePath,
                                taskObj.LineNumber,
                                taskObj.LinePosition));
                        }
                    }
                }
                else if (!state.TryGetValue(neighbor, out var neighborState) || neighborState == 0)
                {
                    DfsCycleCheck(neighbor, adj, taskMap, state, pathStack, reportedCycles, diagnostics);
                }
            }
        }

        pathStack.RemoveAt(pathStack.Count - 1);
        state[currentId] = 2; // Visited
    }

    private static void ValidateProtectedProductStateAndCompletion(
        ProjectSemanticIndex index,
        List<Diagnostic> diagnostics)
    {
        foreach (var iter in index.Iterations)
        {
            var acceptedConfirmations = iter.Confirmations
                .Where(c => string.Equals(c.Decision, "accepted", StringComparison.OrdinalIgnoreCase))
                .ToList();

            var hasAcceptedActivation = acceptedConfirmations.Any(c =>
                string.Equals(c.Action, "activate", StringComparison.OrdinalIgnoreCase));
            var hasAcceptedReplan = acceptedConfirmations.Any(c =>
                string.Equals(c.Action, "replan", StringComparison.OrdinalIgnoreCase));
            var hasAcceptedCompletion = acceptedConfirmations.Any(c =>
                string.Equals(c.Action, "complete", StringComparison.OrdinalIgnoreCase));
            var hasAcceptedCancellation = acceptedConfirmations.Any(c =>
                string.Equals(c.Action, "cancel", StringComparison.OrdinalIgnoreCase));
            var hasAcceptedSupersession = acceptedConfirmations.Any(c =>
                string.Equals(c.Action, "supersede", StringComparison.OrdinalIgnoreCase));

            // Targeted confirmations index
            var reqDecisions = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            var questionDecisions = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            var designDecisions = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            var critDecisions = new Dictionary<string, List<(string Decision, string Rationale)>>(StringComparer.Ordinal);

            foreach (var conf in acceptedConfirmations)
            {
                foreach (var (target, dec) in conf.Requirements)
                {
                    if (!reqDecisions.TryGetValue(target, out var list))
                    {
                        list = new List<string>();
                        reqDecisions[target] = list;
                    }
                    list.Add(dec);
                }

                foreach (var (target, dec) in conf.Questions)
                {
                    if (!questionDecisions.TryGetValue(target, out var list))
                    {
                        list = new List<string>();
                        questionDecisions[target] = list;
                    }
                    list.Add(dec);
                }

                foreach (var (target, dec) in conf.DesignDecisions)
                {
                    if (!designDecisions.TryGetValue(target, out var list))
                    {
                        list = new List<string>();
                        designDecisions[target] = list;
                    }
                    list.Add(dec);
                }

                foreach (var (target, dec) in conf.AcceptanceCriteria)
                {
                    if (!critDecisions.TryGetValue(target, out var list))
                    {
                        list = new List<(string Decision, string Rationale)>();
                        critDecisions[target] = list;
                    }
                    list.Add((dec, conf.Rationale ?? string.Empty));
                }
            }

            // 1. Iteration status provenance
            if (string.Equals(iter.Status, "active", StringComparison.Ordinal))
            {
                if (!hasAcceptedActivation)
                {
                    diagnostics.Add(Diagnostic.Error(
                        DiagnosticCodes.MissingConfirmationProvenance,
                        $"Iteration '{iter.Id}' has status 'active' but lacks an accepted activation confirmation (action='activate' or action='continue', decision='accepted').",
                        iter.Document.RelativePath,
                        iter.LineNumber,
                        iter.LinePosition));
                }
            }
            else if (string.Equals(iter.Status, "replanning", StringComparison.Ordinal))
            {
                if (!hasAcceptedReplan)
                {
                    diagnostics.Add(Diagnostic.Error(
                        DiagnosticCodes.MissingConfirmationProvenance,
                        $"Iteration '{iter.Id}' has status 'replanning' but lacks an accepted replan confirmation (action='replan', decision='accepted').",
                        iter.Document.RelativePath,
                        iter.LineNumber,
                        iter.LinePosition));
                }
            }
            else if (string.Equals(iter.Status, "completed", StringComparison.Ordinal))
            {
                if (!hasAcceptedCompletion)
                {
                    diagnostics.Add(Diagnostic.Error(
                        DiagnosticCodes.MissingConfirmationProvenance,
                        $"Iteration '{iter.Id}' has status 'completed' but lacks an accepted completion confirmation (action='complete', decision='accepted').",
                        iter.Document.RelativePath,
                        iter.LineNumber,
                        iter.LinePosition));
                }
            }
            else if (string.Equals(iter.Status, "cancelled", StringComparison.Ordinal))
            {
                if (!hasAcceptedCancellation)
                {
                    diagnostics.Add(Diagnostic.Error(
                        DiagnosticCodes.MissingConfirmationProvenance,
                        $"Iteration '{iter.Id}' has status 'cancelled' but lacks an accepted cancellation confirmation (action='cancel', decision='accepted').",
                        iter.Document.RelativePath,
                        iter.LineNumber,
                        iter.LinePosition));
                }
            }
            else if (string.Equals(iter.Status, "superseded", StringComparison.Ordinal))
            {
                if (!hasAcceptedSupersession)
                {
                    diagnostics.Add(Diagnostic.Error(
                        DiagnosticCodes.MissingConfirmationProvenance,
                        $"Iteration '{iter.Id}' has status 'superseded' but lacks an accepted supersession confirmation (action='supersede', decision='accepted').",
                        iter.Document.RelativePath,
                        iter.LineNumber,
                        iter.LinePosition));
                }
            }

            // 2. Requirements status provenance
            foreach (var req in iter.Requirements)
            {
                if (string.Equals(req.Status, "approved", StringComparison.Ordinal))
                {
                    var isConfirmed = hasAcceptedActivation ||
                                      (reqDecisions.TryGetValue(req.Id, out var decs) && decs.Contains("approved"));
                    if (!isConfirmed)
                    {
                        diagnostics.Add(Diagnostic.Error(
                            DiagnosticCodes.MissingConfirmationProvenance,
                            $"Requirement '{req.Id}' has status 'approved' but lacks confirmation provenance (accepted activation confirmation or targeted confirmation entry).",
                            iter.Document.RelativePath,
                            req.LineNumber ?? iter.LineNumber,
                            req.LinePosition ?? iter.LinePosition));
                    }
                }
                else if (string.Equals(req.Status, "superseded", StringComparison.Ordinal))
                {
                    var isConfirmed = reqDecisions.TryGetValue(req.Id, out var decs) && decs.Contains("superseded");
                    if (!isConfirmed)
                    {
                        diagnostics.Add(Diagnostic.Error(
                            DiagnosticCodes.MissingConfirmationProvenance,
                            $"Requirement '{req.Id}' has status 'superseded' but lacks an accepted confirmation entry with decision='superseded'.",
                            iter.Document.RelativePath,
                            req.LineNumber ?? iter.LineNumber,
                            req.LinePosition ?? iter.LinePosition));
                    }
                }
                else if (string.Equals(req.Status, "withdrawn", StringComparison.Ordinal))
                {
                    var isConfirmed = reqDecisions.TryGetValue(req.Id, out var decs) && decs.Contains("withdrawn");
                    if (!isConfirmed)
                    {
                        diagnostics.Add(Diagnostic.Error(
                            DiagnosticCodes.MissingConfirmationProvenance,
                            $"Requirement '{req.Id}' has status 'withdrawn' but lacks an accepted confirmation entry with decision='withdrawn'.",
                            iter.Document.RelativePath,
                            req.LineNumber ?? iter.LineNumber,
                            req.LinePosition ?? iter.LinePosition));
                    }
                }
            }

            // 3. Research questions status provenance
            foreach (var q in iter.Questions)
            {
                if (!string.Equals(q.Status, "open", StringComparison.Ordinal))
                {
                    var isConfirmed = questionDecisions.TryGetValue(q.Id, out var decs) && decs.Contains(q.Status);
                    if (!isConfirmed)
                    {
                        diagnostics.Add(Diagnostic.Error(
                            DiagnosticCodes.MissingConfirmationProvenance,
                            $"Research question '{q.Id}' has status '{q.Status}' but lacks an accepted confirmation entry with decision='{q.Status}'.",
                            iter.Document.RelativePath,
                            q.LineNumber ?? iter.LineNumber,
                            q.LinePosition ?? iter.LinePosition));
                    }
                }
            }

            // 4. Design decisions status provenance
            foreach (var dec in iter.DesignDecisions)
            {
                if (string.Equals(dec.Status, "accepted", StringComparison.Ordinal))
                {
                    var isConfirmed = hasAcceptedActivation ||
                                      (designDecisions.TryGetValue(dec.Id, out var decs) && decs.Contains("accepted"));
                    if (!isConfirmed)
                    {
                        diagnostics.Add(Diagnostic.Error(
                            DiagnosticCodes.MissingConfirmationProvenance,
                            $"Design decision '{dec.Id}' has status 'accepted' but lacks confirmation provenance (accepted activation confirmation or targeted confirmation entry).",
                            iter.Document.RelativePath,
                            dec.LineNumber ?? iter.LineNumber,
                            dec.LinePosition ?? iter.LinePosition));
                    }
                }
                else if (string.Equals(dec.Status, "rejected", StringComparison.Ordinal))
                {
                    var isConfirmed = designDecisions.TryGetValue(dec.Id, out var decs) && decs.Contains("rejected");
                    if (!isConfirmed)
                    {
                        diagnostics.Add(Diagnostic.Error(
                            DiagnosticCodes.MissingConfirmationProvenance,
                            $"Design decision '{dec.Id}' has status 'rejected' but lacks an accepted confirmation entry with decision='rejected'.",
                            iter.Document.RelativePath,
                            dec.LineNumber ?? iter.LineNumber,
                            dec.LinePosition ?? iter.LinePosition));
                    }
                }
                else if (string.Equals(dec.Status, "superseded", StringComparison.Ordinal))
                {
                    var isConfirmed = designDecisions.TryGetValue(dec.Id, out var decs) && decs.Contains("superseded");
                    if (!isConfirmed)
                    {
                        diagnostics.Add(Diagnostic.Error(
                            DiagnosticCodes.MissingConfirmationProvenance,
                            $"Design decision '{dec.Id}' has status 'superseded' but lacks an accepted confirmation entry with decision='superseded'.",
                            iter.Document.RelativePath,
                            dec.LineNumber ?? iter.LineNumber,
                            dec.LinePosition ?? iter.LinePosition));
                    }
                }
            }

            // 5. Product acceptance criteria decision provenance
            foreach (var crit in iter.AcceptanceCriteria)
            {
                if (!string.Equals(crit.Decision, "pending", StringComparison.Ordinal))
                {
                    var isConfirmed = critDecisions.TryGetValue(crit.Id, out var entries) &&
                                      entries.Any(e => string.Equals(e.Decision, crit.Decision, StringComparison.OrdinalIgnoreCase));
                    if (!isConfirmed)
                    {
                        diagnostics.Add(Diagnostic.Error(
                            DiagnosticCodes.MissingConfirmationProvenance,
                            $"Acceptance criterion '{crit.Id}' has decision '{crit.Decision}' but lacks an accepted confirmation entry with decision='{crit.Decision}'.",
                            iter.Document.RelativePath,
                            crit.LineNumber ?? iter.LineNumber,
                            crit.LinePosition ?? iter.LinePosition));
                    }
                }

                // Waived criterion rationale check
                if (string.Equals(crit.Decision, "waived", StringComparison.Ordinal))
                {
                    var hasRationale = critDecisions.TryGetValue(crit.Id, out var entries) &&
                                       entries.Any(e => string.Equals(e.Decision, "waived", StringComparison.OrdinalIgnoreCase) &&
                            !string.IsNullOrWhiteSpace(e.Rationale));

                    if (!hasRationale)
                    {
                        diagnostics.Add(Diagnostic.Error(
                            DiagnosticCodes.WaiverRationaleMissing,
                            $"Acceptance criterion '{crit.Id}' is waived, but the owning accepted confirmation lacks an explicit rationale summary.",
                            iter.Document.RelativePath,
                            crit.LineNumber ?? iter.LineNumber,
                            crit.LinePosition ?? iter.LinePosition));
                    }
                }
            }

            // 6. Completed iteration predicates
            if (string.Equals(iter.Status, "completed", StringComparison.Ordinal))
            {
                // completed_at present on /iteration
                if (string.IsNullOrWhiteSpace(iter.CompletedAt))
                {
                    diagnostics.Add(Diagnostic.Error(
                        DiagnosticCodes.IterationCompletedAtMissing,
                        $"Iteration '{iter.Id}' has status 'completed' but is missing the required 'completed_at' timestamp attribute.",
                        iter.Document.RelativePath,
                        iter.LineNumber,
                        iter.LinePosition));
                }

                // All tasks terminal
                var iterTasks = index.AllTasks.Where(t =>
                    t.Document.IterationId != null &&
                    string.Equals(t.Document.IterationId, iter.Document.IterationId, StringComparison.Ordinal)).ToList();

                foreach (var task in iterTasks)
                {
                    if (!string.Equals(task.Status, "done", StringComparison.Ordinal) &&
                        !string.Equals(task.Status, "transferred", StringComparison.Ordinal) &&
                        !string.Equals(task.Status, "superseded", StringComparison.Ordinal) &&
                        !string.Equals(task.Status, "cancelled", StringComparison.Ordinal))
                    {
                        diagnostics.Add(Diagnostic.Error(
                            DiagnosticCodes.IterationCompletionPredicateFailed,
                            $"Iteration '{iter.Id}' has status 'completed' but task '{task.Id}' is non-terminal (status='{task.Status}').",
                            iter.Document.RelativePath,
                            iter.LineNumber,
                            iter.LinePosition));
                    }
                }

                // Criteria and question predicates
                if (string.Equals(iter.Kind, "feature", StringComparison.Ordinal))
                {
                    foreach (var crit in iter.AcceptanceCriteria)
                    {
                        if (!string.Equals(crit.Decision, "accepted", StringComparison.Ordinal) &&
                            !string.Equals(crit.Decision, "waived", StringComparison.Ordinal))
                        {
                            diagnostics.Add(Diagnostic.Error(
                                DiagnosticCodes.IterationCompletionPredicateFailed,
                                $"Iteration '{iter.Id}' has status 'completed' but product criterion '{crit.Id}' has decision '{crit.Decision ?? "pending"}'. All product criteria must be 'accepted' or 'waived'.",
                                iter.Document.RelativePath,
                                crit.LineNumber ?? iter.LineNumber,
                                crit.LinePosition ?? iter.LinePosition));
                        }
                    }
                }
                else if (string.Equals(iter.Kind, "research", StringComparison.Ordinal))
                {
                    foreach (var q in iter.Questions)
                    {
                        if (string.Equals(q.Status, "open", StringComparison.Ordinal))
                        {
                            diagnostics.Add(Diagnostic.Error(
                                DiagnosticCodes.IterationCompletionPredicateFailed,
                                $"Research iteration '{iter.Id}' has status 'completed' but research question '{q.Id}' is still 'open'.",
                                iter.Document.RelativePath,
                                q.LineNumber ?? iter.LineNumber,
                                q.LinePosition ?? iter.LinePosition));
                        }
                    }

                    foreach (var crit in iter.AcceptanceCriteria)
                    {
                        if (!string.Equals(crit.Decision, "accepted", StringComparison.Ordinal) &&
                            !string.Equals(crit.Decision, "waived", StringComparison.Ordinal))
                        {
                            diagnostics.Add(Diagnostic.Error(
                                DiagnosticCodes.IterationCompletionPredicateFailed,
                                $"Research iteration '{iter.Id}' has status 'completed' but research acceptance criterion '{crit.Id}' has decision '{crit.Decision ?? "pending"}'. All research criteria must be 'accepted' or 'waived'.",
                                iter.Document.RelativePath,
                                crit.LineNumber ?? iter.LineNumber,
                                crit.LinePosition ?? iter.LinePosition));
                        }
                    }
                }
            }
        }
    }

    private static void ValidateStatusIndexTerms(
        ProjectSemanticIndex index,
        List<Diagnostic> diagnostics)
    {
        // 1. Tasks in all tasks.xml
        foreach (var task in index.AllTasks)
        {
            var indexElem = task.Element.Element("index");
            if (indexElem == null) continue;

            var statusTerms = indexElem.Elements("term")
                .Where(t => string.Equals(t.Attribute("key")?.Value, "status", StringComparison.Ordinal))
                .ToList();

            if (statusTerms.Count > 1)
            {
                foreach (var term in statusTerms)
                {
                    var lineInfo = (System.Xml.IXmlLineInfo)term;
                    diagnostics.Add(Diagnostic.Error(
                        DiagnosticCodes.AmbiguousReference,
                        $"Task '{task.Id}' index contains ambiguous duplicate 'status' terms ({statusTerms.Count} found).",
                        task.Document.RelativePath,
                        lineInfo.HasLineInfo() ? lineInfo.LineNumber : task.LineNumber,
                        lineInfo.HasLineInfo() ? lineInfo.LinePosition : task.LinePosition));
                }
            }
            else if (statusTerms.Count == 1)
            {
                var termVal = statusTerms[0].Attribute("value")?.Value;
                if (!string.Equals(termVal, task.Status, StringComparison.Ordinal))
                {
                    var lineInfo = (System.Xml.IXmlLineInfo)statusTerms[0];
                    diagnostics.Add(Diagnostic.Error(
                        DiagnosticCodes.TaskTransitionConflict,
                        $"Task '{task.Id}' has status '{task.Status}' but index status term has stale value '{termVal}'.",
                        task.Document.RelativePath,
                        lineInfo.HasLineInfo() ? lineInfo.LineNumber : task.LineNumber,
                        lineInfo.HasLineInfo() ? lineInfo.LinePosition : task.LinePosition));
                }
            }
        }

        // 2. Iteration and its sub-elements in spec.xml
        foreach (var iter in index.Iterations)
        {
            var indexElem = iter.Element.Element("index");
            if (indexElem != null)
            {
                var statusTerms = indexElem.Elements("term")
                    .Where(t => string.Equals(t.Attribute("key")?.Value, "status", StringComparison.Ordinal))
                    .ToList();

                if (statusTerms.Count > 1)
                {
                    foreach (var term in statusTerms)
                    {
                        var lineInfo = (System.Xml.IXmlLineInfo)term;
                        diagnostics.Add(Diagnostic.Error(
                            DiagnosticCodes.AmbiguousReference,
                            $"Iteration '{iter.Id}' index contains ambiguous duplicate 'status' terms ({statusTerms.Count} found).",
                            iter.Document.RelativePath,
                            lineInfo.HasLineInfo() ? lineInfo.LineNumber : iter.LineNumber,
                            lineInfo.HasLineInfo() ? lineInfo.LinePosition : iter.LinePosition));
                    }
                }
                else if (statusTerms.Count == 1)
                {
                    var termVal = statusTerms[0].Attribute("value")?.Value;
                    if (!string.Equals(termVal, iter.Status, StringComparison.Ordinal))
                    {
                        var lineInfo = (System.Xml.IXmlLineInfo)statusTerms[0];
                        diagnostics.Add(Diagnostic.Error(
                            DiagnosticCodes.WorkKindMismatch,
                            $"Iteration '{iter.Id}' has status '{iter.Status}' but index status term has stale value '{termVal}'.",
                            iter.Document.RelativePath,
                            lineInfo.HasLineInfo() ? lineInfo.LineNumber : iter.LineNumber,
                            lineInfo.HasLineInfo() ? lineInfo.LinePosition : iter.LinePosition));
                    }
                }
            }

            // Requirements
            foreach (var req in iter.Requirements)
            {
                var reqIndexElem = req.Element.Element("index");
                if (reqIndexElem == null) continue;

                var statusTerms = reqIndexElem.Elements("term")
                    .Where(t => string.Equals(t.Attribute("key")?.Value, "status", StringComparison.Ordinal))
                    .ToList();

                if (statusTerms.Count > 1)
                {
                    foreach (var term in statusTerms)
                    {
                        var lineInfo = (System.Xml.IXmlLineInfo)term;
                        diagnostics.Add(Diagnostic.Error(
                            DiagnosticCodes.AmbiguousReference,
                            $"Requirement '{req.Id}' index contains ambiguous duplicate 'status' terms ({statusTerms.Count} found).",
                            iter.Document.RelativePath,
                            lineInfo.HasLineInfo() ? lineInfo.LineNumber : req.LineNumber,
                            lineInfo.HasLineInfo() ? lineInfo.LinePosition : req.LinePosition));
                    }
                }
                else if (statusTerms.Count == 1)
                {
                    var termVal = statusTerms[0].Attribute("value")?.Value;
                    if (!string.Equals(termVal, req.Status, StringComparison.Ordinal))
                    {
                        var lineInfo = (System.Xml.IXmlLineInfo)statusTerms[0];
                        diagnostics.Add(Diagnostic.Error(
                            DiagnosticCodes.WorkKindMismatch,
                            $"Requirement '{req.Id}' has status '{req.Status}' but index status term has stale value '{termVal}'.",
                            iter.Document.RelativePath,
                            lineInfo.HasLineInfo() ? lineInfo.LineNumber : req.LineNumber,
                            lineInfo.HasLineInfo() ? lineInfo.LinePosition : req.LinePosition));
                    }
                }
            }

            // Research Questions
            foreach (var q in iter.Questions)
            {
                var qIndexElem = q.Element.Element("index");
                if (qIndexElem == null) continue;

                var statusTerms = qIndexElem.Elements("term")
                    .Where(t => string.Equals(t.Attribute("key")?.Value, "status", StringComparison.Ordinal))
                    .ToList();

                if (statusTerms.Count > 1)
                {
                    foreach (var term in statusTerms)
                    {
                        var lineInfo = (System.Xml.IXmlLineInfo)term;
                        diagnostics.Add(Diagnostic.Error(
                            DiagnosticCodes.AmbiguousReference,
                            $"Research question '{q.Id}' index contains ambiguous duplicate 'status' terms ({statusTerms.Count} found).",
                            iter.Document.RelativePath,
                            lineInfo.HasLineInfo() ? lineInfo.LineNumber : q.LineNumber,
                            lineInfo.HasLineInfo() ? lineInfo.LinePosition : q.LinePosition));
                    }
                }
                else if (statusTerms.Count == 1)
                {
                    var termVal = statusTerms[0].Attribute("value")?.Value;
                    if (!string.Equals(termVal, q.Status, StringComparison.Ordinal))
                    {
                        var lineInfo = (System.Xml.IXmlLineInfo)statusTerms[0];
                        diagnostics.Add(Diagnostic.Error(
                            DiagnosticCodes.WorkKindMismatch,
                            $"Research question '{q.Id}' has status '{q.Status}' but index status term has stale value '{termVal}'.",
                            iter.Document.RelativePath,
                            lineInfo.HasLineInfo() ? lineInfo.LineNumber : q.LineNumber,
                            lineInfo.HasLineInfo() ? lineInfo.LinePosition : q.LinePosition));
                    }
                }
            }

            // Design Decisions
            foreach (var d in iter.DesignDecisions)
            {
                var dIndexElem = d.Element.Element("index");
                if (dIndexElem == null) continue;

                var statusTerms = dIndexElem.Elements("term")
                    .Where(t => string.Equals(t.Attribute("key")?.Value, "status", StringComparison.Ordinal))
                    .ToList();

                if (statusTerms.Count > 1)
                {
                    foreach (var term in statusTerms)
                    {
                        var lineInfo = (System.Xml.IXmlLineInfo)term;
                        diagnostics.Add(Diagnostic.Error(
                            DiagnosticCodes.AmbiguousReference,
                            $"Design decision '{d.Id}' index contains ambiguous duplicate 'status' terms ({statusTerms.Count} found).",
                            iter.Document.RelativePath,
                            lineInfo.HasLineInfo() ? lineInfo.LineNumber : d.LineNumber,
                            lineInfo.HasLineInfo() ? lineInfo.LinePosition : d.LinePosition));
                    }
                }
                else if (statusTerms.Count == 1)
                {
                    var termVal = statusTerms[0].Attribute("value")?.Value;
                    if (!string.Equals(termVal, d.Status, StringComparison.Ordinal))
                    {
                        var lineInfo = (System.Xml.IXmlLineInfo)statusTerms[0];
                        diagnostics.Add(Diagnostic.Error(
                            DiagnosticCodes.WorkKindMismatch,
                            $"Design decision '{d.Id}' has status '{d.Status}' but index status term has stale value '{termVal}'.",
                            iter.Document.RelativePath,
                            lineInfo.HasLineInfo() ? lineInfo.LineNumber : d.LineNumber,
                            lineInfo.HasLineInfo() ? lineInfo.LinePosition : d.LinePosition));
                    }
                }
            }
        }
    }
}
