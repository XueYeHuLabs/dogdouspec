using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using System.Xml.Schema;
using DogdouSpec.Core.Append;
using DogdouSpec.Core.Diagnostics;
using DogdouSpec.Core.Formatting;
using DogdouSpec.Core.Resources;
using DogdouSpec.Core.Security;
using DogdouSpec.Core.Serialization;
using DogdouSpec.Core.Time;
using DogdouSpec.Core.Transactions;
using DogdouSpec.Core.Validation;
using DogdouSpec.Core.Workspace;
using DogdouSpec.Core.XPath;

namespace DogdouSpec.Core.Tasks;

public static class TaskReviser
{
    private static readonly UTF8Encoding Utf8NoBom = new(false);

    public static (bool Success, MutationEnvelope? Envelope, IReadOnlyList<Diagnostic> Diagnostics) Revise(
        string workspaceRoot,
        string iterationId,
        string taskId,
        int expectedRevision,
        string requestXml,
        IClock? clock = null,
        IFaultInjector? faultInjector = null,
        string version = "1.0",
        bool dryRun = false)
    {
        if (string.IsNullOrWhiteSpace(workspaceRoot))
        {
            return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.InvalidArgument, "Workspace root must be specified.") });
        }

        if (string.IsNullOrWhiteSpace(iterationId))
        {
            return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.InvalidArgument, "Iteration ID must be specified.") });
        }

        var normIterId = iterationId.Trim().Replace('\\', '/').Trim('/');
        if (!ProjectSemanticIndex.IsValidTimeFirstId(normIterId))
        {
            return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.InvalidIdGrammar, $"Iteration ID '{iterationId}' does not conform to the time-first ID grammar.") });
        }

        if (string.IsNullOrWhiteSpace(taskId))
        {
            return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.InvalidArgument, "Task ID must be specified.") });
        }

        if (expectedRevision <= 0)
        {
            return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.InvalidArgument, $"Expected revision must be positive. Received: {expectedRevision}.") });
        }

        if (string.IsNullOrWhiteSpace(requestXml))
        {
            return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.InvalidArgument, "task-revise request XML must be provided.") });
        }
        if (Encoding.UTF8.GetByteCount(requestXml) > XPathQueryLimits.MaxDocumentBytes)
        {
            return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.LimitExceeded, $"task-revise request exceeds maximum allowed size of {XPathQueryLimits.MaxDocumentBytes} bytes.") });
        }

        var (isWsSafe, wsErr) = PathSecurity.VerifyWorkspaceDirectorySecurity(workspaceRoot);
        if (!isWsSafe || wsErr != null)
        {
            return (false, null, new[] { wsErr ?? Diagnostic.Error(DiagnosticCodes.PathEscapeDetected, "Workspace directory security verification failed.") });
        }

        if (dryRun)
        {
            var dryRunBlocker = WorkspaceTransactionCommitter.GetDryRunBlocker(workspaceRoot);
            if (dryRunBlocker != null)
            {
                return (false, null, new[] { dryRunBlocker });
            }
        }

        var normTasksDocPath = $"{normIterId}/tasks.xml";
        var (isRelValid, _, relErr) = PathSecurity.ValidateRelativeDocumentPath(normTasksDocPath);
        if (!isRelValid || relErr != null)
        {
            return (false, null, new[] { relErr ?? Diagnostic.Error(DiagnosticCodes.InvalidArgument, $"Invalid document path '{normTasksDocPath}'.") });
        }

        var fullTasksDocPath = Path.Combine(workspaceRoot, normTasksDocPath.Replace('/', Path.DirectorySeparatorChar));
        var (isContained, contErr) = PathSecurity.CheckContainmentAndReparsePoints(workspaceRoot, fullTasksDocPath);
        if (!isContained || contErr != null)
        {
            return (false, null, new[] { contErr ?? Diagnostic.Error(DiagnosticCodes.PathEscapeDetected, $"Target path escapes workspace: '{normTasksDocPath}'.") });
        }

        // 1. Parse and Validate Request XML against requests.xsd
        var schemaSet = EmbeddedResources.GetCompiledSchemaSet("requests", version);
        var schemaDiagnostics = new List<Diagnostic>();
        var settings = SecureXmlReaderFactory.CreateSecureSettings(
            schemaSet: schemaSet,
            validationEventHandler: (sender, args) =>
            {
                var line = args.Exception?.LineNumber;
                var col = args.Exception?.LinePosition;
                var code = DiagnosticCodes.SchemaValidationError;

                var diag = args.Severity == XmlSeverityType.Error
                    ? Diagnostic.Error(code, args.Message, normTasksDocPath, line, col)
                    : Diagnostic.Warning(code, args.Message, normTasksDocPath, line, col);

                schemaDiagnostics.Add(diag);
            });

        XDocument requestDoc;
        try
        {
            using var sr = new StringReader(requestXml);
            using var reader = SecureXmlReaderFactory.CreateReader(sr, settings);
            requestDoc = XDocument.Load(reader, LoadOptions.PreserveWhitespace | LoadOptions.SetLineInfo);
        }
        catch (XmlException xmlEx)
        {
            var code = xmlEx.Message.Contains("DTD", StringComparison.OrdinalIgnoreCase)
                ? DiagnosticCodes.DtdProhibited
                : DiagnosticCodes.XmlParseError;
            return (false, null, new[] { Diagnostic.Error(code, $"Failed to parse task-revise request XML: {xmlEx.Message}", normTasksDocPath, xmlEx.LineNumber, xmlEx.LinePosition) });
        }
        catch (Exception ex)
        {
            return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.XmlParseError, $"Failed to parse task-revise request XML: {ex.Message}", normTasksDocPath) });
        }

        if (schemaDiagnostics.Any(d => d.Severity == "error"))
        {
            return (false, null, schemaDiagnostics);
        }

        var reqRoot = requestDoc.Root;
        if (reqRoot == null || !string.Equals(reqRoot.Name.LocalName, "task-revise", StringComparison.Ordinal))
        {
            return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.UnknownDocumentType, $"Expected root element <task-revise>, found <{reqRoot?.Name.LocalName}>.", normTasksDocPath) });
        }

        var reviseId = reqRoot.Attribute("id")?.Value;
        if (string.IsNullOrWhiteSpace(reviseId) || !ProjectSemanticIndex.IsValidTimeFirstId(reviseId))
        {
            return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.InvalidIdGrammar, $"task-revise @id '{reviseId}' is missing or invalid.", normTasksDocPath) });
        }

        var actor = reqRoot.Attribute("actor")?.Value;
        if (string.IsNullOrWhiteSpace(actor))
        {
            return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.InvalidArgument, "task-revise @actor is required.", normTasksDocPath) });
        }

        var occurredAt = reqRoot.Attribute("occurred_at")?.Value;
        if (string.IsNullOrWhiteSpace(occurredAt) || !IsValidUtcTimestamp(occurredAt, out var reqOccurredAt))
        {
            return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.InvalidArgument, $"task-revise @occurred_at '{occurredAt}' must be a valid UTC timestamp ending with 'Z'.", normTasksDocPath) });
        }
        var requestFingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(GenericAppender.ToCanonicalXmlString(reqRoot)))).ToLowerInvariant();

        // Stamp operation_id on records
        var requestedRecords = reqRoot.Element("records")?.Elements("record").ToList() ?? new List<XElement>();
        foreach (var rec in requestedRecords)
        {
            var recId = rec.Attribute("id")?.Value;
            if (string.IsNullOrWhiteSpace(recId) || !ProjectSemanticIndex.IsValidTimeFirstId(recId))
            {
                return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.InvalidIdGrammar, $"Record @id '{recId}' is invalid.", normTasksDocPath) });
            }

            var recOpId = rec.Attribute("operation_id")?.Value;
            if (!string.IsNullOrEmpty(recOpId) && !string.Equals(recOpId, reviseId, StringComparison.Ordinal))
            {
                return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.InvalidArgument, $"Record '{recId}' supplies conflicting operation_id '{recOpId}'.", normTasksDocPath) });
            }

            rec.SetAttributeValue("operation_id", reviseId);
        }

        // 2. Read tasks.xml
        if (!File.Exists(fullTasksDocPath))
        {
            return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.DocumentNotFound, $"Target tasks document '{normTasksDocPath}' not found.", normTasksDocPath) });
        }
        if (new FileInfo(fullTasksDocPath).Length > XPathQueryLimits.MaxDocumentBytes)
        {
            return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.LimitExceeded, $"Document '{normTasksDocPath}' exceeds maximum allowed size of {XPathQueryLimits.MaxDocumentBytes} bytes.", normTasksDocPath) });
        }

        XDocument tasksDoc;
        try
        {
            using var fs = File.OpenRead(fullTasksDocPath);
            using var reader = SecureXmlReaderFactory.CreateReader(fs);
            tasksDoc = XDocument.Load(reader, LoadOptions.PreserveWhitespace | LoadOptions.SetLineInfo);
        }
        catch (Exception ex)
        {
            return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.XmlParseError, $"Failed to read '{normTasksDocPath}': {ex.Message}", normTasksDocPath) });
        }

        var tasksRoot = tasksDoc.Root;
        if (tasksRoot == null || tasksRoot.Name.LocalName != "tasks")
        {
            return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.XmlParseError, $"Document '{normTasksDocPath}' has missing or invalid root element.", normTasksDocPath) });
        }

        var revStr = tasksRoot.Attribute("revision")?.Value;
        if (!int.TryParse(revStr, CultureInfo.InvariantCulture, out var actualRevision) || actualRevision <= 0)
        {
            return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.XmlParseError, $"Document '{normTasksDocPath}' revision is invalid.", normTasksDocPath) });
        }

        var matchingTasks = tasksRoot.Elements("task").Where(t => string.Equals((string?)t.Attribute("id"), taskId, StringComparison.Ordinal)).ToList();
        if (matchingTasks.Count == 0)
        {
            return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.CardinalityConflict, $"Task '{taskId}' was not found in '{normTasksDocPath}'.", normTasksDocPath) });
        }
        if (matchingTasks.Count > 1)
        {
            return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.CardinalityConflict, $"Task '{taskId}' matched {matchingTasks.Count} elements in '{normTasksDocPath}'.", normTasksDocPath) });
        }

        var targetTask = matchingTasks[0];
        if (DateTimeOffset.TryParse(targetTask.Attribute("updated_at")?.Value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var taskUpdatedAt) && reqOccurredAt < taskUpdatedAt)
        {
            return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.InvalidArgument, $"task-revise @occurred_at '{occurredAt}' cannot be earlier than task updated_at '{targetTask.Attribute("updated_at")?.Value}'.", normTasksDocPath) });
        }
        var currentStatus = targetTask.Attribute("status")?.Value ?? "pending";

        // Check terminal immutability
        var isTerminal = string.Equals(currentStatus, "done", StringComparison.Ordinal) ||
                         string.Equals(currentStatus, "transferred", StringComparison.Ordinal) ||
                         string.Equals(currentStatus, "superseded", StringComparison.Ordinal) ||
                         string.Equals(currentStatus, "cancelled", StringComparison.Ordinal);

        if (isTerminal)
        {
            return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.TaskImmutable, $"Cannot revise task '{taskId}': task is in terminal status '{currentStatus}' and is immutable.", normTasksDocPath) });
        }

        // Once implementation has started, a task's original technical
        // rationale is historical context. Scope may grow, but cannot narrow
        // or alter any existing repository/include/exclude entry.
        var hasStarted = !string.IsNullOrWhiteSpace(targetTask.Attribute("started_at")?.Value) ||
                         string.Equals(currentStatus, "in-progress", StringComparison.Ordinal) ||
                         string.Equals(currentStatus, "verification", StringComparison.Ordinal);
        var requestedRationale = reqRoot.Element("rationale");
        if (hasStarted && requestedRationale != null &&
            !string.Equals(targetTask.Element("rationale")?.Value, requestedRationale.Value, StringComparison.Ordinal))
        {
            return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.TaskRevisionNotAllowed, $"Cannot replace rationale for started task '{taskId}'. Record new reasoning as a discussion instead.", normTasksDocPath) });
        }
        var requestedScope = reqRoot.Element("scope");
        if (HasDuplicateRepositoryPaths(requestedScope) || HasDuplicateRepositoryPaths(targetTask.Element("scope")))
        {
            return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.CardinalityConflict, $"Task '{taskId}' scope contains duplicate repository paths; revise requests must identify each repository at most once.", normTasksDocPath) });
        }
        if (hasStarted && requestedScope != null && !IsAdditiveScopeSuperset(targetTask.Element("scope"), requestedScope))
        {
            return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.TaskRevisionNotAllowed, $"Started task '{taskId}' may only expand scope; existing repository/include/exclude entries cannot be removed or changed.", normTasksDocPath) });
        }

        // 3. Idempotency Check
        var (enumSuccess, allDocs, enumDiags) = WorkspaceDiscovery.EnumerateDocuments(workspaceRoot);
        if (!enumSuccess || enumDiags.Count > 0)
        {
            return (false, null, enumDiags);
        }

        var existingTaskRecords = targetTask.Element("records")?.Elements("record").ToList() ?? new List<XElement>();
        var existingOpRecords = existingTaskRecords.Where(r => string.Equals((string?)r.Attribute("operation_id"), reviseId, StringComparison.Ordinal)).ToList();

        if (existingOpRecords.Count > 0)
        {
            var receipt = existingOpRecords.FirstOrDefault(r => string.Equals(r.Attribute("id")?.Value, reviseId + "-receipt", StringComparison.Ordinal));
            var storedFingerprint = receipt?.Element("index")?.Elements("term")
                .FirstOrDefault(t => string.Equals(t.Attribute("key")?.Value, "request-sha256", StringComparison.Ordinal))?.Attribute("value")?.Value;
            var storedRequestedRecords = existingOpRecords.Where(r => !string.Equals(r.Attribute("id")?.Value, reviseId + "-receipt", StringComparison.Ordinal)).ToList();
            if (receipt != null && string.Equals(storedFingerprint, requestFingerprint, StringComparison.Ordinal) &&
                storedRequestedRecords.Count == requestedRecords.Count &&
                storedRequestedRecords.Zip(requestedRecords).All(pair => GenericAppender.AreElementsCanonicallyEqual(pair.First, pair.Second)) &&
                RevisionEffectsMatch(targetTask, reqRoot, occurredAt))
            {
                if (expectedRevision != actualRevision - 1)
                {
                    var diag = new Diagnostic(
                        DiagnosticCodes.RevisionConflict,
                        "error",
                        $"Task revise '{reviseId}' was already committed, but the workspace has drifted beyond its immediate post-commit revision.",
                        Document: normTasksDocPath,
                        ExpectedRevision: expectedRevision,
                        ActualRevision: actualRevision);
                    return (false, null, new[] { diag });
                }

                var alreadyAppliedEnv = new MutationEnvelope(
                    "task revise",
                    new[] { new MutatedDocument(normTasksDocPath, actualRevision) },
                    alreadyApplied: true);
                return (true, alreadyAppliedEnv, Array.Empty<Diagnostic>());
            }

            return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.IdempotencyConflict, $"Operation ID '{reviseId}' already exists in '{normTasksDocPath}' with different revision effects or receipt content.", normTasksDocPath) });
        }

        foreach (var doc in allDocs)
        {
            try
            {
                if (new FileInfo(doc.FullPath).Length > XPathQueryLimits.MaxDocumentBytes)
                {
                    return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.LimitExceeded, $"Document '{doc.RelativePath}' exceeds maximum allowed size of {XPathQueryLimits.MaxDocumentBytes} bytes.", doc.RelativePath) });
                }
                using var fs = File.OpenRead(doc.FullPath);
                using var r = SecureXmlReaderFactory.CreateReader(fs);
                var xDoc = XDocument.Load(r);
                if (xDoc.Descendants().Any(e => string.Equals((string?)e.Attribute("operation_id"), reviseId, StringComparison.Ordinal)))
                {
                    return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.IdempotencyConflict, $"Operation ID '{reviseId}' already exists in document '{doc.RelativePath}'.", normTasksDocPath) });
                }
            }
            catch { }
        }

        if (expectedRevision != actualRevision)
        {
            var diag = new Diagnostic(
                DiagnosticCodes.RevisionConflict,
                "error",
                $"Expected revision {expectedRevision} does not match actual revision {actualRevision} for document '{normTasksDocPath}'.",
                Document: normTasksDocPath,
                ExpectedRevision: expectedRevision,
                ActualRevision: actualRevision);
            return (false, null, new[] { diag });
        }

        // 4. Apply Revisions
        var reqRationale = reqRoot.Element("rationale");
        if (reqRationale != null)
        {
            var taskRationale = targetTask.Element("rationale");
            if (taskRationale != null)
            {
                taskRationale.SetValue(reqRationale.Value);
            }
        }

        var reqScope = reqRoot.Element("scope");
        if (reqScope != null)
        {
            var taskScope = targetTask.Element("scope");
            if (taskScope != null)
            {
                taskScope.ReplaceWith(new XElement(reqScope));
            }
        }

        var reqAddDeps = reqRoot.Element("add_dependencies");
        if (reqAddDeps != null)
        {
            var taskDeps = targetTask.Element("dependencies");
            if (taskDeps == null)
            {
                taskDeps = new XElement("dependencies");
                var originElem = targetTask.Element("origin");
                if (originElem != null)
                {
                    originElem.AddAfterSelf(taskDeps);
                }
                else
                {
                    targetTask.Add(taskDeps);
                }
            }

            var existingRefs = taskDeps.Elements("ref").ToDictionary(r => r.Attribute("target")?.Value ?? string.Empty, StringComparer.Ordinal);
            foreach (var r in reqAddDeps.Elements("ref"))
            {
                var target = r.Attribute("target")?.Value;
                if (!string.IsNullOrEmpty(target) && existingRefs.TryGetValue(target, out var existing))
                {
                    if (!GenericAppender.AreElementsCanonicallyEqual(existing, r))
                    {
                        return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.IdempotencyConflict, $"Dependency target '{target}' already exists with different content.", normTasksDocPath) });
                    }
                }
                else if (!string.IsNullOrEmpty(target))
                {
                    taskDeps.Add(new XElement(r));
                    existingRefs.Add(target, r);
                }
            }
        }

        var reqAddConstraints = reqRoot.Element("add_constraints");
        if (reqAddConstraints != null)
        {
            var taskConstraints = targetTask.Element("constraints");
            if (taskConstraints == null)
            {
                taskConstraints = new XElement("constraints");
                var depsElem = targetTask.Element("dependencies") ?? targetTask.Element("origin");
                if (depsElem != null)
                {
                    depsElem.AddAfterSelf(taskConstraints);
                }
                else
                {
                    targetTask.Add(taskConstraints);
                }
            }

            var existingConstraints = taskConstraints.Elements("constraint").ToDictionary(c => c.Attribute("id")?.Value ?? string.Empty, StringComparer.Ordinal);
            foreach (var c in reqAddConstraints.Elements("constraint"))
            {
                var cId = c.Attribute("id")?.Value;
                if (!string.IsNullOrEmpty(cId) && existingConstraints.TryGetValue(cId, out var existing))
                {
                    if (!GenericAppender.AreElementsCanonicallyEqual(existing, c))
                    {
                        return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.IdempotencyConflict, $"Constraint ID '{cId}' already exists with different content.", normTasksDocPath) });
                    }
                }
                else if (!string.IsNullOrEmpty(cId))
                {
                    taskConstraints.Add(new XElement(c));
                    existingConstraints.Add(cId, c);
                }
            }
        }

        var reqAddAcceptance = reqRoot.Element("add_acceptance");
        if (reqAddAcceptance != null)
        {
            var taskAcceptance = targetTask.Element("acceptance");
            if (taskAcceptance == null)
            {
                taskAcceptance = new XElement("acceptance");
                var constraintsElem = targetTask.Element("constraints");
                if (constraintsElem != null)
                {
                    constraintsElem.AddAfterSelf(taskAcceptance);
                }
                else
                {
                    targetTask.Add(taskAcceptance);
                }
            }

            var existingCriteria = taskAcceptance.Elements("criterion").ToDictionary(c => c.Attribute("id")?.Value ?? string.Empty, StringComparer.Ordinal);
            foreach (var crit in reqAddAcceptance.Elements("criterion"))
            {
                var critId = crit.Attribute("id")?.Value;
                if (!string.Equals(crit.Attribute("status")?.Value, "pending", StringComparison.Ordinal))
                {
                    return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.InvalidArgument, $"New acceptance criterion '{critId}' must have status='pending'.", normTasksDocPath) });
                }
                if (!string.IsNullOrEmpty(critId) && existingCriteria.TryGetValue(critId, out var existing))
                {
                    if (!GenericAppender.AreElementsCanonicallyEqual(existing, crit))
                    {
                        return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.IdempotencyConflict, $"Acceptance criterion ID '{critId}' already exists with different content.", normTasksDocPath) });
                    }
                }
                else if (!string.IsNullOrEmpty(critId))
                {
                    var newCrit = new XElement(crit);
                    taskAcceptance.Add(newCrit);
                    existingCriteria.Add(critId, newCrit);
                }
            }
        }

        if (requestedRecords.Count > 0)
        {
            var taskRecords = targetTask.Element("records");
            if (taskRecords == null)
            {
                taskRecords = new XElement("records");
                targetTask.Add(taskRecords);
            }

            foreach (var rec in requestedRecords)
            {
                taskRecords.Add(rec);
            }
            taskRecords.Add(CreateReceipt(reviseId, actor, occurredAt, requestFingerprint, "Task revision receipt."));
        }
        else
        {
            var taskRecords = targetTask.Element("records");
            if (taskRecords == null)
            {
                taskRecords = new XElement("records");
                targetTask.Add(taskRecords);
            }
            taskRecords.Add(CreateReceipt(reviseId, actor, occurredAt, requestFingerprint, "Task revision receipt."));
        }

        targetTask.SetAttributeValue("updated_at", occurredAt);
        var newRevision = actualRevision + 1;
        tasksRoot.SetAttributeValue("revision", newRevision.ToString(CultureInfo.InvariantCulture));

        // 5. Serialize and Commit
        var replacementContent = ManagedDocumentSerializer.Serialize(tasksDoc);

        var operation = new TransactionDocumentOperation(
            normTasksDocPath,
            replacementContent,
            actualRevision,
            newRevision);

        return WorkspaceTransactionCommitter.Commit(
            workspaceRoot,
            "task revise",
            new[] { operation },
            clock,
            faultInjector,
            version,
            correlationId: reviseId,
            dryRun: dryRun);
    }

    private static bool HasDuplicateRepositoryPaths(XElement? scope) => scope != null &&
        scope.Elements("repository")
            .GroupBy(repository => repository.Attribute("path")?.Value ?? string.Empty, StringComparer.Ordinal)
            .Any(group => group.Count() > 1);

    private static XElement CreateReceipt(string operationId, string actor, string occurredAt, string fingerprint, string summary) =>
        new("record",
            new XAttribute("id", operationId + "-receipt"),
            new XAttribute("kind", "discussion"),
            new XAttribute("status", "informational"),
            new XAttribute("created_at", occurredAt),
            new XAttribute("actor", actor),
            new XAttribute("operation_id", operationId),
            new XElement("index",
                new XElement("summary", summary),
                new XElement("term", new XAttribute("key", "request-sha256"), new XAttribute("value", fingerprint))),
            new XElement("summary", summary));

    private static bool IsValidUtcTimestamp(string? value, out DateTimeOffset dto)
    {
        dto = default;
        if (string.IsNullOrWhiteSpace(value) || !value.EndsWith('Z'))
        {
            return false;
        }

        if (!DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out dto))
        {
            return false;
        }

        return dto.Offset == TimeSpan.Zero;
    }

    private static bool RevisionEffectsMatch(XElement task, XElement request, string occurredAt)
    {
        if (!string.Equals(task.Attribute("updated_at")?.Value, occurredAt, StringComparison.Ordinal)) return false;
        var rationale = request.Element("rationale");
        if (rationale != null && !string.Equals(task.Element("rationale")?.Value, rationale.Value, StringComparison.Ordinal)) return false;
        var scope = request.Element("scope");
        if (scope != null && !GenericAppender.AreElementsCanonicallyEqual(task.Element("scope") ?? new XElement("scope"), scope)) return false;

        foreach (var dep in request.Element("add_dependencies")?.Elements("ref") ?? Enumerable.Empty<XElement>())
        {
            if (!(task.Element("dependencies")?.Elements("ref") ?? Enumerable.Empty<XElement>()).Any(existing => GenericAppender.AreElementsCanonicallyEqual(existing, dep))) return false;
        }
        foreach (var constraint in request.Element("add_constraints")?.Elements("constraint") ?? Enumerable.Empty<XElement>())
        {
            if (!(task.Element("constraints")?.Elements("constraint") ?? Enumerable.Empty<XElement>()).Any(existing => GenericAppender.AreElementsCanonicallyEqual(existing, constraint))) return false;
        }
        foreach (var criterion in request.Element("add_acceptance")?.Elements("criterion") ?? Enumerable.Empty<XElement>())
        {
            if (!(task.Element("acceptance")?.Elements("criterion") ?? Enumerable.Empty<XElement>()).Any(existing => GenericAppender.AreElementsCanonicallyEqual(existing, criterion))) return false;
        }
        return true;
    }

    private static bool IsAdditiveScopeSuperset(XElement? existingScope, XElement requestedScope)
    {
        if (existingScope == null) return false;
        var requestedRepositories = requestedScope.Elements("repository").ToList();
        foreach (var existingRepository in existingScope.Elements("repository"))
        {
            var repositoryPath = existingRepository.Attribute("path")?.Value;
            var requestedRepository = requestedRepositories.SingleOrDefault(r => string.Equals(r.Attribute("path")?.Value, repositoryPath, StringComparison.Ordinal));
            if (requestedRepository == null) return false;

            foreach (var kind in new[] { "include", "exclude" })
            {
                foreach (var existingEntry in existingRepository.Elements(kind))
                {
                    if (!requestedRepository.Elements(kind).Any(candidate => GenericAppender.AreElementsCanonicallyEqual(existingEntry, candidate)))
                    {
                        return false;
                    }
                }
            }
        }
        return true;
    }
}
