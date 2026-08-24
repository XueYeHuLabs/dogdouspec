using System.Globalization;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using System.Xml.Schema;
using DogdouSpec.Core.Append;
using DogdouSpec.Core.Diagnostics;
using DogdouSpec.Core.Resources;
using DogdouSpec.Core.Security;
using DogdouSpec.Core.Time;
using DogdouSpec.Core.Transactions;
using DogdouSpec.Core.Validation;
using DogdouSpec.Core.Workspace;
using DogdouSpec.Core.XPath;

namespace DogdouSpec.Core.Tasks;

/// <summary>
/// Authoritative execution engine for atomic Task update operations.
/// </summary>
public static class TaskUpdater
{
    private static readonly UTF8Encoding Utf8NoBom = new(false);

    public static (bool Success, MutationEnvelope? Envelope, IReadOnlyList<Diagnostic> Diagnostics) Update(
        string workspaceRoot,
        string iterationId,
        string taskId,
        int expectedRevision,
        string requestXml,
        IClock? clock = null,
        IFaultInjector? faultInjector = null,
        string version = "1.0")
    {
        clock ??= SystemClock.Instance;

        // 1. Validate inputs
        if (string.IsNullOrWhiteSpace(iterationId))
        {
            return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.InvalidArgument, "Iteration ID cannot be empty.") });
        }

        var (isIterValid, normIterId, iterErr) = PathSecurity.ValidateIterationId(iterationId);
        if (!isIterValid || iterErr != null)
        {
            return (false, null, new[] { iterErr ?? Diagnostic.Error(DiagnosticCodes.InvalidArgument, $"Invalid iteration ID '{iterationId}'.") });
        }

        if (string.IsNullOrWhiteSpace(taskId))
        {
            return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.InvalidArgument, "Task ID cannot be empty.") });
        }

        if (!ProjectSemanticIndex.IsValidTimeFirstId(taskId))
        {
            return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.InvalidIdGrammar, $"Task identifier '{taskId}' does not conform to the time-first ID grammar (YYYYMMDD-name or YYYYMMDDThhmmssZ-name).") });
        }

        if (expectedRevision <= 0)
        {
            return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.InvalidArgument, $"Expected revision must be a positive integer, but got {expectedRevision}.") });
        }

        if (string.IsNullOrWhiteSpace(requestXml))
        {
            return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.InvalidArgument, "Task update request XML cannot be empty.") });
        }

        // 2. Validate workspace directory security
        var (isWsSafe, wsErr) = PathSecurity.VerifyWorkspaceDirectorySecurity(workspaceRoot);
        if (!isWsSafe || wsErr != null)
        {
            return (false, null, new[] { wsErr ?? Diagnostic.Error(DiagnosticCodes.PathEscapeDetected, "Workspace directory security verification failed.") });
        }

        // 3. Validate relative document path for tasks.xml
        var normDocPath = $"{normIterId}/tasks.xml";
        var (isRelValid, validatedDocPath, relErr) = PathSecurity.ValidateRelativeDocumentPath(normDocPath);
        if (!isRelValid || relErr != null)
        {
            return (false, null, new[] { relErr ?? Diagnostic.Error(DiagnosticCodes.InvalidArgument, $"Invalid document path '{normDocPath}'.") });
        }

        // 4. Verify containment and existence
        var fullTargetDocPath = Path.Combine(workspaceRoot, normDocPath.Replace('/', Path.DirectorySeparatorChar));
        var (isContained, contErr) = PathSecurity.CheckContainmentAndReparsePoints(workspaceRoot, fullTargetDocPath);
        if (!isContained || contErr != null)
        {
            return (false, null, new[] { contErr ?? Diagnostic.Error(DiagnosticCodes.PathEscapeDetected, $"Target path escapes workspace: '{normDocPath}'.") });
        }

        if (!File.Exists(fullTargetDocPath))
        {
            return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.DocumentNotFound, $"Document '{normDocPath}' does not exist in workspace.", normDocPath) });
        }

        var docFileInfo = new FileInfo(fullTargetDocPath);
        if (docFileInfo.Length > XPathQueryLimits.MaxDocumentBytes)
        {
            return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.LimitExceeded, $"Document '{normDocPath}' exceeds maximum allowed size of {XPathQueryLimits.MaxDocumentBytes} bytes.", normDocPath) });
        }

        if (Encoding.UTF8.GetByteCount(requestXml) > XPathQueryLimits.MaxDocumentBytes)
        {
            return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.LimitExceeded, $"Task update XML request exceeds maximum allowed size of {XPathQueryLimits.MaxDocumentBytes} bytes.", normDocPath) });
        }

        // 5. Secure parse of XML request & schema validation against requests.xsd
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
                    ? Diagnostic.Error(code, args.Message, normDocPath, line, col)
                    : Diagnostic.Warning(code, args.Message, normDocPath, line, col);

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
            return (false, null, new[] { Diagnostic.Error(code, $"Failed to parse task-update XML request: {xmlEx.Message}", normDocPath, xmlEx.LineNumber, xmlEx.LinePosition) });
        }
        catch (Exception ex)
        {
            return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.XmlParseError, $"Failed to parse task-update XML request: {ex.Message}", normDocPath) });
        }

        if (schemaDiagnostics.Any(d => d.Severity == "error"))
        {
            return (false, null, schemaDiagnostics);
        }

        var reqRoot = requestDoc.Root;
        if (reqRoot == null || reqRoot.Name.LocalName != "task-update")
        {
            return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.SchemaValidationError, "Request XML root must be '<task-update>'.", normDocPath) });
        }

        var updateId = reqRoot.Attribute("id")?.Value;
        if (string.IsNullOrWhiteSpace(updateId) || !ProjectSemanticIndex.IsValidTimeFirstId(updateId))
        {
            return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.InvalidIdGrammar, $"task-update @id '{updateId}' is missing or does not conform to the time-first ID grammar (YYYYMMDD-name or YYYYMMDDThhmmssZ-name).", normDocPath) });
        }

        var transition = reqRoot.Attribute("transition")?.Value;
        var actor = reqRoot.Attribute("actor")?.Value;
        var occurredAt = reqRoot.Attribute("occurred_at")?.Value;

        if (string.IsNullOrWhiteSpace(occurredAt) || !IsValidUtcTimestamp(occurredAt, out var reqOccurredAt))
        {
            return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.InvalidArgument, $"task-update @occurred_at '{occurredAt}' must be a valid UTC timestamp ending with 'Z'.", normDocPath) });
        }

        var recordsElem = reqRoot.Element("records");
        if (recordsElem == null)
        {
            return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.SchemaValidationError, "task-update must contain a <records> element.", normDocPath) });
        }

        var requestedRecords = recordsElem.Elements("record").ToList();
        if (requestedRecords.Count == 0)
        {
            return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.SchemaValidationError, "task-update <records> must contain at least one <record>.", normDocPath) });
        }

        var requestRecIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var rec in requestedRecords)
        {
            var recId = rec.Attribute("id")?.Value;
            if (string.IsNullOrWhiteSpace(recId) || !ProjectSemanticIndex.IsValidTimeFirstId(recId))
            {
                return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.InvalidIdGrammar, $"Appended record ID '{recId}' is missing or does not conform to the time-first ID grammar.", normDocPath) });
            }

            if (!requestRecIds.Add(recId))
            {
                return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.DuplicateId, $"Duplicate record ID '{recId}' within task-update request.", normDocPath) });
            }

            var recCreatedAt = rec.Attribute("created_at")?.Value;
            if (string.IsNullOrWhiteSpace(recCreatedAt) || !IsValidUtcTimestamp(recCreatedAt, out _))
            {
                return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.InvalidArgument, $"Record '{recId}' @created_at '{recCreatedAt}' must be a valid UTC timestamp ending with 'Z'.", normDocPath) });
            }

            var recOpId = rec.Attribute("operation_id")?.Value;
            if (!string.IsNullOrEmpty(recOpId) && !string.Equals(recOpId, updateId, StringComparison.Ordinal))
            {
                return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.InvalidArgument, $"Record '{recId}' supplies conflicting operation_id '{recOpId}'. Must equal task-update/@id '{updateId}' or be omitted.", normDocPath) });
            }

            // Stamp operation_id
            rec.SetAttributeValue("operation_id", updateId);
        }

        var reqAcceptance = reqRoot.Element("acceptance");
        if (reqAcceptance != null)
        {
            var seenAcceptanceTargets = new HashSet<string>(StringComparer.Ordinal);
            foreach (var crit in reqAcceptance.Elements("criterion"))
            {
                var targetId = crit.Attribute("target")?.Value;
                if (string.IsNullOrWhiteSpace(targetId) || !ProjectSemanticIndex.IsValidTimeFirstId(targetId))
                {
                    return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.InvalidIdGrammar, $"Acceptance criterion target ID '{targetId}' is invalid.", normDocPath) });
                }

                if (!seenAcceptanceTargets.Add(targetId))
                {
                    return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.IdempotencyConflict, $"Duplicate acceptance criterion target '{targetId}' within task-update request.", normDocPath) });
                }
            }
        }

        var reqResolve = reqRoot.Element("resolve-records");
        if (reqResolve != null)
        {
            var seenResolveTargets = new HashSet<string>(StringComparer.Ordinal);
            foreach (var res in reqResolve.Elements("record"))
            {
                var targetRecId = res.Attribute("target")?.Value;
                if (string.IsNullOrWhiteSpace(targetRecId) || !ProjectSemanticIndex.IsValidTimeFirstId(targetRecId))
                {
                    return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.InvalidIdGrammar, $"Resolve record target ID '{targetRecId}' is invalid.", normDocPath) });
                }

                if (!seenResolveTargets.Add(targetRecId))
                {
                    return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.IdempotencyConflict, $"Duplicate resolve record target '{targetRecId}' within task-update request.", normDocPath) });
                }
            }
        }

        var reqContextUpdate = reqRoot.Element("context_update");

        // 6. Read target document
        XDocument targetDoc;
        try
        {
            using var fs = File.OpenRead(fullTargetDocPath);
            using var reader = SecureXmlReaderFactory.CreateReader(fs, baseUri: "dogdou://managed/" + normDocPath);
            targetDoc = XDocument.Load(reader, LoadOptions.PreserveWhitespace | LoadOptions.SetLineInfo | LoadOptions.SetBaseUri);
        }
        catch (XmlException xmlEx)
        {
            var code = xmlEx.Message.Contains("DTD", StringComparison.OrdinalIgnoreCase)
                ? DiagnosticCodes.DtdProhibited
                : DiagnosticCodes.XmlParseError;
            return (false, null, new[] { Diagnostic.Error(code, $"Failed to parse target XML document '{normDocPath}': {xmlEx.Message}", normDocPath, xmlEx.LineNumber, xmlEx.LinePosition) });
        }
        catch (Exception ex)
        {
            return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.XmlParseError, $"Failed to read target XML document '{normDocPath}': {ex.Message}", normDocPath) });
        }

        var tasksRoot = targetDoc.Root;
        if (tasksRoot == null || tasksRoot.Name.LocalName != "tasks")
        {
            return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.XmlParseError, $"Document '{normDocPath}' has missing or invalid root element.", normDocPath) });
        }

        var docIteration = tasksRoot.Attribute("iteration")?.Value;
        if (!string.Equals(docIteration, normIterId, StringComparison.Ordinal))
        {
            return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.TasksIterationMismatch, $"Document '{normDocPath}' iteration attribute '{docIteration}' does not match requested iteration '{normIterId}'.", normDocPath) });
        }

        var revStr = tasksRoot.Attribute("revision")?.Value;
        if (string.IsNullOrWhiteSpace(revStr) || !int.TryParse(revStr, CultureInfo.InvariantCulture, out var actualRevision) || actualRevision <= 0)
        {
            return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.XmlParseError, $"Document '{normDocPath}' root revision attribute is missing, non-positive, or malformed.", normDocPath) });
        }

        var matchingTasks = tasksRoot.Elements("task").Where(t => string.Equals((string?)t.Attribute("id"), taskId, StringComparison.Ordinal)).ToList();
        if (matchingTasks.Count == 0)
        {
            return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.CardinalityConflict, $"Task '{taskId}' was not found in document '{normDocPath}'. Expected exactly 1 task.", normDocPath) });
        }
        if (matchingTasks.Count > 1)
        {
            return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.CardinalityConflict, $"Task '{taskId}' matched {matchingTasks.Count} elements in document '{normDocPath}'. Expected exactly 1 task.", normDocPath) });
        }

        var targetTask = matchingTasks[0];

        // 7. Project-Wide Idempotency Check
        var (enumSuccess, allDocs, enumDiags) = WorkspaceDiscovery.EnumerateDocuments(workspaceRoot);
        if (!enumSuccess || enumDiags.Count > 0)
        {
            return (false, null, enumDiags);
        }

        var opOccurrences = new List<(ManagedDocument Doc, XElement Element, string? ContainingTaskId)>();
        foreach (var doc in allDocs)
        {
            try
            {
                using var fs = File.OpenRead(doc.FullPath);
                using var r = SecureXmlReaderFactory.CreateReader(fs);
                var xDoc = XDocument.Load(r);
                var found = xDoc.Descendants().Where(e => string.Equals((string?)e.Attribute("operation_id"), updateId, StringComparison.Ordinal));
                foreach (var elem in found)
                {
                    var containingTaskId = elem.Ancestors("task").FirstOrDefault()?.Attribute("id")?.Value;
                    opOccurrences.Add((doc, elem, containingTaskId));
                }
            }
            catch
            {
                // Handled during prospective validation
            }
        }

        foreach (var occ in opOccurrences)
        {
            if (!string.Equals(occ.Doc.RelativePath, normDocPath, StringComparison.Ordinal))
            {
                return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.IdempotencyConflict, $"Operation ID '{updateId}' already exists in document '{occ.Doc.RelativePath}'.", normDocPath) });
            }

            if (!string.Equals(occ.ContainingTaskId, taskId, StringComparison.Ordinal))
            {
                return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.IdempotencyConflict, $"Operation ID '{updateId}' already exists under task '{occ.ContainingTaskId}' in '{normDocPath}'.", normDocPath) });
            }

            if (!string.Equals(occ.Element.Name.LocalName, "record", StringComparison.Ordinal) ||
                !string.Equals(occ.Element.Parent?.Name.LocalName, "records", StringComparison.Ordinal))
            {
                return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.IdempotencyConflict, $"Operation ID '{updateId}' exists on a non-record element.", normDocPath) });
            }
        }

        var existingTaskRecords = targetTask.Element("records")?.Elements("record").ToList() ?? new List<XElement>();
        var existingOpRecords = existingTaskRecords.Where(r => string.Equals((string?)r.Attribute("operation_id"), updateId, StringComparison.Ordinal)).ToList();

        if (existingOpRecords.Count > 0)
        {
            // Idempotent retry evaluation
            if (existingOpRecords.Count != requestedRecords.Count)
            {
                return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.IdempotencyConflict, $"Operation ID '{updateId}' already applied with a different number of records ({existingOpRecords.Count} found, {requestedRecords.Count} requested).", normDocPath) });
            }

            foreach (var reqRec in requestedRecords)
            {
                var reqRecId = reqRec.Attribute("id")!.Value;
                var matched = existingOpRecords.FirstOrDefault(r => string.Equals((string?)r.Attribute("id"), reqRecId, StringComparison.Ordinal));
                if (matched == null)
                {
                    return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.IdempotencyConflict, $"Operation ID '{updateId}' already applied but missing record '{reqRecId}'.", normDocPath) });
                }

                if (!GenericAppender.AreElementsCanonicallyEqual(matched, reqRec))
                {
                    return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.IdempotencyConflict, $"Operation ID '{updateId}' already applied with different content for record '{reqRecId}'.", normDocPath) });
                }
            }

            // Check task status against transition
            if (!string.IsNullOrEmpty(transition))
            {
                var expectedFinalStatus = GetTargetStatusForTransition(transition);
                if (!string.Equals(targetTask.Attribute("status")?.Value, expectedFinalStatus, StringComparison.Ordinal))
                {
                    return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.IdempotencyConflict, $"Task status '{targetTask.Attribute("status")?.Value}' does not match transition outcome '{expectedFinalStatus}' for operation '{updateId}'.", normDocPath) });
                }
            }

            // Check acceptance criteria results
            var acceptanceElem = reqRoot.Element("acceptance");
            if (acceptanceElem != null)
            {
                foreach (var crit in acceptanceElem.Elements("criterion"))
                {
                    var targetCritId = crit.Attribute("target")?.Value;
                    var expectedResult = crit.Attribute("result")?.Value;
                    var taskCrit = targetTask.Element("acceptance")?.Elements("criterion").FirstOrDefault(c => string.Equals((string?)c.Attribute("id"), targetCritId, StringComparison.Ordinal));
                    if (taskCrit == null || !string.Equals((string?)taskCrit.Attribute("status"), expectedResult, StringComparison.Ordinal))
                    {
                        return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.IdempotencyConflict, $"Task acceptance criterion '{targetCritId}' status does not match requested result '{expectedResult}' for operation '{updateId}'.", normDocPath) });
                    }
                }
            }

            // Check record resolutions
            var resolveElem = reqRoot.Element("resolve-records");
            if (resolveElem != null)
            {
                foreach (var res in resolveElem.Elements("record"))
                {
                    var targetRecId = res.Attribute("target")?.Value;
                    var taskRec = existingTaskRecords.FirstOrDefault(r => string.Equals((string?)r.Attribute("id"), targetRecId, StringComparison.Ordinal));
                    if (taskRec == null || !string.Equals((string?)taskRec.Attribute("status"), "resolved", StringComparison.Ordinal))
                    {
                        return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.IdempotencyConflict, $"Record '{targetRecId}' status is not 'resolved' for operation '{updateId}'.", normDocPath) });
                    }
                }
            }

            // Check context update
            var contextUpdateElem = reqRoot.Element("context_update");
            if (contextUpdateElem != null)
            {
                var summaryUpdate = contextUpdateElem.Element("summary");
                if (summaryUpdate != null)
                {
                    var currentSummary = targetTask.Element("context")?.Element("summary")?.Value;
                    if (!string.Equals(currentSummary, summaryUpdate.Value, StringComparison.Ordinal))
                    {
                        return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.IdempotencyConflict, $"Task context summary does not match requested update for operation '{updateId}'.", normDocPath) });
                    }
                }

                var dsUpdate = contextUpdateElem.Element("design_snapshot");
                if (dsUpdate != null)
                {
                    var currentDs = targetTask.Element("context")?.Element("design_snapshot")?.Value;
                    if (!string.Equals(currentDs, dsUpdate.Value, StringComparison.Ordinal))
                    {
                        return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.IdempotencyConflict, $"Task context design snapshot does not match requested update for operation '{updateId}'.", normDocPath) });
                    }
                }
            }

            // Informational annotations on terminal tasks deliberately preserve
            // historical metadata. All other updates set updated_at to occurred_at.
            var replayStatus = targetTask.Attribute("status")?.Value;
            var replayIsTerminal = replayStatus is "done" or "transferred" or "superseded" or "cancelled";
            if (!replayIsTerminal && !string.Equals(targetTask.Attribute("updated_at")?.Value, occurredAt, StringComparison.Ordinal))
            {
                return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.IdempotencyConflict, $"Task updated_at '{targetTask.Attribute("updated_at")?.Value}' does not match occurred_at '{occurredAt}' for operation '{updateId}'.", normDocPath) });
            }

            if (string.Equals(transition, "start", StringComparison.Ordinal) && !string.Equals(targetTask.Attribute("started_at")?.Value, occurredAt, StringComparison.Ordinal))
            {
                return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.IdempotencyConflict, $"Task started_at '{targetTask.Attribute("started_at")?.Value}' does not match occurred_at '{occurredAt}' for operation '{updateId}'.", normDocPath) });
            }

            if (string.Equals(transition, "complete", StringComparison.Ordinal) && !string.Equals(targetTask.Attribute("completed_at")?.Value, occurredAt, StringComparison.Ordinal))
            {
                return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.IdempotencyConflict, $"Task completed_at does not match occurred_at '{occurredAt}' for operation '{updateId}'.", normDocPath) });
            }

            // Valid already applied retry!
            if (expectedRevision != actualRevision && expectedRevision != actualRevision - 1)
            {
                var diag = new Diagnostic(
                    DiagnosticCodes.RevisionConflict,
                    "error",
                    $"Expected revision {expectedRevision} does not match actual revision {actualRevision} for document '{normDocPath}'.",
                    Document: normDocPath,
                    ExpectedRevision: expectedRevision,
                    ActualRevision: actualRevision);
                return (false, null, new[] { diag });
            }

            var alreadyAppliedEnv = new MutationEnvelope(
                "task update",
                new[] { new MutatedDocument(normDocPath, actualRevision) },
                alreadyApplied: true);
            return (true, alreadyAppliedEnv, Array.Empty<Diagnostic>());
        }

        // 8. New operation checks
        foreach (var reqRec in requestedRecords)
        {
            var reqRecId = reqRec.Attribute("id")!.Value;
            foreach (var doc in allDocs)
            {
                try
                {
                    using var fs = File.OpenRead(doc.FullPath);
                    using var r = SecureXmlReaderFactory.CreateReader(fs);
                    var xDoc = XDocument.Load(r);
                    if (xDoc.Descendants().Any(e => string.Equals((string?)e.Attribute("id"), reqRecId, StringComparison.Ordinal)))
                    {
                        return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.IdempotencyConflict, $"Element with ID '{reqRecId}' already exists in document '{doc.RelativePath}'.", normDocPath) });
                    }
                }
                catch { }
            }
        }

        if (expectedRevision != actualRevision)
        {
            var diag = new Diagnostic(
                DiagnosticCodes.RevisionConflict,
                "error",
                $"Expected revision {expectedRevision} does not match actual revision {actualRevision} for document '{normDocPath}'.",
                Document: normDocPath,
                ExpectedRevision: expectedRevision,
                ActualRevision: actualRevision);
            return (false, null, new[] { diag });
        }

        var taskCreatedAtStr = targetTask.Attribute("created_at")?.Value;
        if (!string.IsNullOrWhiteSpace(taskCreatedAtStr) &&
            DateTimeOffset.TryParse(taskCreatedAtStr, CultureInfo.InvariantCulture, DateTimeStyles.None, out var taskCreatedAt) &&
            reqOccurredAt < taskCreatedAt)
        {
            return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.InvalidArgument, $"task-update @occurred_at '{occurredAt}' cannot be earlier than task created_at '{taskCreatedAtStr}'.", normDocPath) });
        }

        var taskUpdatedAtStr = targetTask.Attribute("updated_at")?.Value;
        if (!string.IsNullOrWhiteSpace(taskUpdatedAtStr) &&
            DateTimeOffset.TryParse(taskUpdatedAtStr, CultureInfo.InvariantCulture, DateTimeStyles.None, out var taskUpdatedAt) &&
            reqOccurredAt < taskUpdatedAt)
        {
            return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.InvalidArgument, $"task-update @occurred_at '{occurredAt}' cannot be earlier than current task updated_at '{taskUpdatedAtStr}'.", normDocPath) });
        }

        // 9. Validate Transition and Immutability
        var currentStatus = targetTask.Attribute("status")?.Value ?? "pending";
        var isTerminal = string.Equals(currentStatus, "done", StringComparison.Ordinal) ||
                         string.Equals(currentStatus, "transferred", StringComparison.Ordinal) ||
                         string.Equals(currentStatus, "superseded", StringComparison.Ordinal) ||
                         string.Equals(currentStatus, "cancelled", StringComparison.Ordinal);

        if (isTerminal)
        {
            if (!string.IsNullOrEmpty(transition))
            {
                return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.TaskImmutable, $"Cannot transition task '{taskId}': task is in terminal status '{currentStatus}' and is immutable.", normDocPath) });
            }
            if (reqAcceptance != null)
            {
                return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.TaskImmutable, $"Cannot modify acceptance criteria for task '{taskId}': task is in terminal status '{currentStatus}' and is immutable.", normDocPath) });
            }
            if (reqResolve != null)
            {
                return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.TaskImmutable, $"Cannot resolve records on task '{taskId}': task is in terminal status '{currentStatus}' and is immutable.", normDocPath) });
            }
            if (reqContextUpdate != null)
            {
                return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.TaskImmutable, $"Cannot update context for task '{taskId}': task is in terminal status '{currentStatus}' and is immutable.", normDocPath) });
            }
            foreach (var rec in requestedRecords)
            {
                var kind = rec.Attribute("kind")?.Value;
                var status = rec.Attribute("status")?.Value;
                if (!string.Equals(status, "informational", StringComparison.Ordinal) ||
                    (!string.Equals(kind, "discussion", StringComparison.Ordinal) &&
                     !string.Equals(kind, "finding", StringComparison.Ordinal) &&
                     !string.Equals(kind, "handoff", StringComparison.Ordinal)))
                {
                    return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.TaskImmutable, $"Terminal task '{taskId}' only accepts informational discussion, finding, or handoff records; received kind='{kind}', status='{status}'.", normDocPath) });
                }
            }
        }

        // Check if owning iteration is in status replanning
        var specDocPath = Path.Combine(workspaceRoot, normIterId, "spec.xml");
        if (!File.Exists(specDocPath))
        {
            return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.DocumentNotFound, $"Iteration spec document '{normIterId}/spec.xml' is required to evaluate the execution freeze.", $"{normIterId}/spec.xml") });
        }

        try
        {
            using var fs = File.OpenRead(specDocPath);
            using var r = SecureXmlReaderFactory.CreateReader(fs);
            var specDoc = XDocument.Load(r);
            if (specDoc.Root == null || !string.Equals(specDoc.Root.Name.LocalName, "iteration", StringComparison.Ordinal))
            {
                return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.XmlParseError, $"Iteration spec document '{normIterId}/spec.xml' has a missing or invalid root element; execution freeze cannot be evaluated.", $"{normIterId}/spec.xml") });
            }

            var iterStatus = specDoc.Root.Attribute("status")?.Value;
            if (string.Equals(iterStatus, "replanning", StringComparison.OrdinalIgnoreCase))
            {
                if (string.Equals(transition, "start", StringComparison.Ordinal) ||
                    string.Equals(transition, "resume", StringComparison.Ordinal) ||
                    string.Equals(transition, "verify", StringComparison.Ordinal) ||
                    string.Equals(transition, "complete", StringComparison.Ordinal))
                {
                    return (false, null, new[]
                    {
                        Diagnostic.Error(
                            DiagnosticCodes.IterationReplanningExecutionFrozen,
                            $"Cannot execute transition '{transition}' on task '{taskId}': iteration '{normIterId}' is currently in status 'replanning'. Execution transitions (start, resume, verify, complete) are frozen during replanning.",
                            normDocPath)
                    });
                }
            }
        }
        catch (XmlException ex)
        {
            return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.XmlParseError, $"Unable to parse iteration spec document '{normIterId}/spec.xml' while evaluating execution freeze: {ex.Message}", $"{normIterId}/spec.xml", ex.LineNumber, ex.LinePosition) });
        }
        catch (Exception ex)
        {
            return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.XmlParseError, $"Unable to read iteration spec document '{normIterId}/spec.xml' while evaluating execution freeze: {ex.Message}", $"{normIterId}/spec.xml") });
        }

        string? targetStatus = null;
        if (!string.IsNullOrEmpty(transition))
        {
            var (isLegal, resolvedStatus, transError) = ValidateTransition(currentStatus, transition, taskId);
            if (!isLegal || transError != null)
            {
                return (false, null, new[] { transError ?? Diagnostic.Error(DiagnosticCodes.TaskTransitionConflict, $"Illegal transition '{transition}' from status '{currentStatus}'.", normDocPath) });
            }

            if (string.Equals(transition, "start", StringComparison.Ordinal) && !string.IsNullOrWhiteSpace((string?)targetTask.Attribute("started_at")))
            {
                return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.TaskTransitionConflict, $"Cannot start task '{taskId}': task already specifies started_at.", normDocPath) });
            }

            targetStatus = resolvedStatus;
        }

        if (transition is "start" or "resume" or "verify" or "complete")
        {
            try
            {
                using var fs = File.OpenRead(specDocPath);
                using var reader = SecureXmlReaderFactory.CreateReader(fs);
                var specDoc = XDocument.Load(reader);
                var requirements = (specDoc.Root?.Element("product")?.Element("requirements")?.Elements("requirement") ?? Enumerable.Empty<XElement>())
                    .ToDictionary(r => r.Attribute("id")?.Value ?? string.Empty, r => r.Attribute("status")?.Value ?? string.Empty, StringComparer.Ordinal);
                foreach (var origin in targetTask.Element("origin")?.Elements("ref") ?? Enumerable.Empty<XElement>())
                {
                    if (string.Equals(origin.Attribute("relation")?.Value, "supports", StringComparison.Ordinal) &&
                        string.Equals(origin.Attribute("target")?.Value, normIterId, StringComparison.Ordinal))
                    {
                        continue;
                    }
                    var requirementId = origin.Attribute("target")?.Value ?? string.Empty;
                    if (!requirements.TryGetValue(requirementId, out var requirementStatus) ||
                        !string.Equals(requirementStatus, "approved", StringComparison.Ordinal))
                    {
                        return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.OwnerDecisionRequired, $"Cannot execute transition '{transition}' for task '{taskId}': origin requirement '{requirementId}' is missing or not approved.", normDocPath) });
                    }
                }
            }
            catch (XmlException ex)
            {
                return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.XmlParseError, $"Unable to parse iteration spec document '{normIterId}/spec.xml' while checking approved task origins: {ex.Message}", $"{normIterId}/spec.xml", ex.LineNumber, ex.LinePosition) });
            }
            catch (Exception ex)
            {
                return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.XmlParseError, $"Unable to read iteration spec document '{normIterId}/spec.xml' while checking approved task origins: {ex.Message}", $"{normIterId}/spec.xml") });
            }
        }

        // 10. Pre-validate Acceptance targets
        if (reqAcceptance != null)
        {
            var taskAcceptance = targetTask.Element("acceptance");
            foreach (var crit in reqAcceptance.Elements("criterion"))
            {
                var targetId = crit.Attribute("target")?.Value;
                var taskCrit = taskAcceptance?.Elements("criterion").FirstOrDefault(c => string.Equals((string?)c.Attribute("id"), targetId, StringComparison.Ordinal));
                if (taskCrit == null)
                {
                    return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.IdempotencyConflict, $"Acceptance criterion '{targetId}' not found in task '{taskId}'.", normDocPath) });
                }
            }
        }

        // 11. Pre-validate Resolve targets
        if (reqResolve != null)
        {
            var taskRecords = targetTask.Element("records");
            foreach (var res in reqResolve.Elements("record"))
            {
                var targetRecId = res.Attribute("target")?.Value;
                var taskRec = taskRecords?.Elements("record").FirstOrDefault(r => string.Equals((string?)r.Attribute("id"), targetRecId, StringComparison.Ordinal));
                if (taskRec == null)
                {
                    return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.IdempotencyConflict, $"Resolve target record '{targetRecId}' not found in task '{taskId}'.", normDocPath) });
                }

                var recStatus = taskRec.Attribute("status")?.Value;
                if (!string.Equals(recStatus, "active", StringComparison.Ordinal))
                {
                    return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.IdempotencyConflict, $"Cannot resolve record '{targetRecId}' in task '{taskId}': current status is '{recStatus}', but only 'active' records can be resolved.", normDocPath) });
                }
            }
        }

        // Apply mutations
        if (!string.IsNullOrEmpty(targetStatus))
        {
            targetTask.SetAttributeValue("status", targetStatus);
        }

        if (reqAcceptance != null)
        {
            var taskAcceptance = targetTask.Element("acceptance");
            if (taskAcceptance == null)
            {
                taskAcceptance = new XElement("acceptance");
                var contextElem = targetTask.Element("context");
                if (contextElem != null)
                {
                    contextElem.AddBeforeSelf(taskAcceptance);
                }
                else
                {
                    targetTask.Add(taskAcceptance);
                }
            }

            foreach (var crit in reqAcceptance.Elements("criterion"))
            {
                var targetId = crit.Attribute("target")?.Value;
                var result = crit.Attribute("result")?.Value;
                var taskCrit = taskAcceptance.Elements("criterion").First(c => string.Equals((string?)c.Attribute("id"), targetId, StringComparison.Ordinal));
                taskCrit.SetAttributeValue("status", result);
            }
        }

        if (reqResolve != null)
        {
            var taskRecords = targetTask.Element("records");
            foreach (var res in reqResolve.Elements("record"))
            {
                var targetRecId = res.Attribute("target")?.Value;
                var taskRec = taskRecords!.Elements("record").First(r => string.Equals((string?)r.Attribute("id"), targetRecId, StringComparison.Ordinal));
                taskRec.SetAttributeValue("status", "resolved");
            }
        }

        // 12. Apply Context update
        if (reqContextUpdate != null)
        {
            var taskContext = targetTask.Element("context");
            if (taskContext == null)
            {
                taskContext = new XElement("context");
                var recordsContainer = targetTask.Element("records");
                if (recordsContainer != null)
                {
                    recordsContainer.AddBeforeSelf(taskContext);
                }
                else
                {
                    targetTask.Add(taskContext);
                }
            }

            var summaryElem = reqContextUpdate.Element("summary");
            if (summaryElem != null)
            {
                var taskSummary = taskContext.Element("summary");
                if (taskSummary != null)
                {
                    taskSummary.SetValue(summaryElem.Value);
                }
                else
                {
                    taskContext.AddFirst(new XElement("summary", summaryElem.Value));
                }
            }

            var dsElem = reqContextUpdate.Element("design_snapshot");
            if (dsElem != null)
            {
                var taskDs = taskContext.Element("design_snapshot");
                if (taskDs != null)
                {
                    taskDs.SetValue(dsElem.Value);
                }
                else
                {
                    taskContext.Add(new XElement("design_snapshot", dsElem.Value));
                }
            }
        }

        // 13. Append Stamped Records
        var targetRecordsElem = targetTask.Element("records");
        if (targetRecordsElem == null)
        {
            targetRecordsElem = new XElement("records");
            targetTask.Add(targetRecordsElem);
        }

        foreach (var rec in requestedRecords)
        {
            targetRecordsElem.Add(rec);
        }

        // 14. Update Timestamps & Revision
        // Terminal tasks are historical facts. Informational records are append-only
        // annotations and must not rewrite any task metadata, including updated_at.
        if (!isTerminal)
        {
            targetTask.SetAttributeValue("updated_at", occurredAt);
        }
        if (string.Equals(transition, "start", StringComparison.Ordinal))
        {
            targetTask.SetAttributeValue("started_at", occurredAt);
        }
        if (string.Equals(transition, "complete", StringComparison.Ordinal))
        {
            targetTask.SetAttributeValue("completed_at", occurredAt);
        }

        var newRevision = actualRevision + 1;
        tasksRoot.SetAttributeValue("revision", newRevision.ToString(CultureInfo.InvariantCulture));

        // 15. Serialize and Commit
        var writerSettings = new XmlWriterSettings
        {
            Indent = true,
            IndentChars = "  ",
            OmitXmlDeclaration = false,
            Encoding = Utf8NoBom,
            NewLineHandling = NewLineHandling.Replace,
            NewLineChars = "\n"
        };

        using var memoryStream = new MemoryStream();
        using (var writer = XmlWriter.Create(memoryStream, writerSettings))
        {
            targetDoc.Save(writer);
        }

        var replacementContent = Encoding.UTF8.GetString(memoryStream.ToArray());
        if (!replacementContent.EndsWith('\n'))
        {
            replacementContent += "\n";
        }

        var operation = new TransactionDocumentOperation(
            normDocPath,
            replacementContent,
            actualRevision,
            newRevision);

        return WorkspaceTransactionCommitter.Commit(
            workspaceRoot,
            "task update",
            new[] { operation },
            clock,
            faultInjector,
            version);
    }

    private static bool IsValidUtcTimestamp(string? value, out DateTimeOffset dto)
    {
        dto = default;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        if (!value.EndsWith('Z'))
        {
            return false;
        }

        if (!DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out dto))
        {
            return false;
        }

        return dto.Offset == TimeSpan.Zero;
    }

    private static (bool IsLegal, string? TargetStatus, Diagnostic? Error) ValidateTransition(
        string currentStatus,
        string transition,
        string taskId)
    {
        string targetStatus;
        bool legal;

        switch (transition)
        {
            case "start":
                legal = string.Equals(currentStatus, "pending", StringComparison.Ordinal);
                targetStatus = "in-progress";
                break;
            case "block":
                legal = string.Equals(currentStatus, "in-progress", StringComparison.Ordinal) ||
                        string.Equals(currentStatus, "verification", StringComparison.Ordinal);
                targetStatus = "blocked";
                break;
            case "resume":
                legal = string.Equals(currentStatus, "blocked", StringComparison.Ordinal);
                targetStatus = "in-progress";
                break;
            case "verify":
                legal = string.Equals(currentStatus, "in-progress", StringComparison.Ordinal);
                targetStatus = "verification";
                break;
            case "complete":
                legal = string.Equals(currentStatus, "verification", StringComparison.Ordinal);
                targetStatus = "done";
                break;
            case "transfer":
                legal = string.Equals(currentStatus, "pending", StringComparison.Ordinal) ||
                        string.Equals(currentStatus, "in-progress", StringComparison.Ordinal) ||
                        string.Equals(currentStatus, "blocked", StringComparison.Ordinal) ||
                        string.Equals(currentStatus, "verification", StringComparison.Ordinal);
                targetStatus = "transferred";
                break;
            case "supersede":
                legal = string.Equals(currentStatus, "pending", StringComparison.Ordinal) ||
                        string.Equals(currentStatus, "in-progress", StringComparison.Ordinal) ||
                        string.Equals(currentStatus, "blocked", StringComparison.Ordinal) ||
                        string.Equals(currentStatus, "verification", StringComparison.Ordinal);
                targetStatus = "superseded";
                break;
            case "cancel":
                legal = string.Equals(currentStatus, "pending", StringComparison.Ordinal) ||
                        string.Equals(currentStatus, "in-progress", StringComparison.Ordinal) ||
                        string.Equals(currentStatus, "blocked", StringComparison.Ordinal) ||
                        string.Equals(currentStatus, "verification", StringComparison.Ordinal);
                targetStatus = "cancelled";
                break;
            default:
                return (false, null, Diagnostic.Error(DiagnosticCodes.TaskTransitionConflict, $"Unknown task transition '{transition}'."));
        }

        if (!legal)
        {
            return (false, null, Diagnostic.Error(
                DiagnosticCodes.TaskTransitionConflict,
                $"Illegal task transition '{transition}' from current status '{currentStatus}' for task '{taskId}'."));
        }

        if (string.Equals(currentStatus, targetStatus, StringComparison.Ordinal))
        {
            return (false, null, Diagnostic.Error(
                DiagnosticCodes.TaskTransitionConflict,
                $"Cannot transition task '{taskId}' to '{targetStatus}': task is already in status '{currentStatus}'."));
        }

        return (true, targetStatus, null);
    }

    private static string GetTargetStatusForTransition(string transition) =>
        transition switch
        {
            "start" => "in-progress",
            "block" => "blocked",
            "resume" => "in-progress",
            "verify" => "verification",
            "complete" => "done",
            "transfer" => "transferred",
            "supersede" => "superseded",
            "cancel" => "cancelled",
            _ => transition
        };
}
