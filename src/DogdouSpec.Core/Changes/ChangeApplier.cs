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

namespace DogdouSpec.Core.Changes;

public static class ChangeApplier
{
    private static readonly UTF8Encoding Utf8NoBom = new(false);

    public static (bool Success, MutationEnvelope? Envelope, IReadOnlyList<Diagnostic> Diagnostics) Apply(
        string workspaceRoot,
        string iterationId,
        int expectedSpecRevision,
        int expectedTasksRevision,
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

        if (expectedSpecRevision <= 0)
        {
            return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.InvalidArgument, $"Expected spec revision must be positive. Received: {expectedSpecRevision}.") });
        }

        if (expectedTasksRevision <= 0)
        {
            return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.InvalidArgument, $"Expected tasks revision must be positive. Received: {expectedTasksRevision}.") });
        }

        if (string.IsNullOrWhiteSpace(requestXml))
        {
            return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.InvalidArgument, "change-apply request XML must be provided.") });
        }
        if (Encoding.UTF8.GetByteCount(requestXml) > XPathQueryLimits.MaxDocumentBytes)
        {
            return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.LimitExceeded, $"change-apply request exceeds maximum allowed size of {XPathQueryLimits.MaxDocumentBytes} bytes.") });
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

        var normSpecDocPath = $"{normIterId}/spec.xml";
        var normTasksDocPath = $"{normIterId}/tasks.xml";

        var (isSpecRelValid, _, specRelErr) = PathSecurity.ValidateRelativeDocumentPath(normSpecDocPath);
        if (!isSpecRelValid || specRelErr != null)
        {
            return (false, null, new[] { specRelErr ?? Diagnostic.Error(DiagnosticCodes.InvalidArgument, $"Invalid document path '{normSpecDocPath}'.") });
        }

        var fullSpecDocPath = Path.Combine(workspaceRoot, normSpecDocPath.Replace('/', Path.DirectorySeparatorChar));
        var (isSpecContained, specContErr) = PathSecurity.CheckContainmentAndReparsePoints(workspaceRoot, fullSpecDocPath);
        if (!isSpecContained || specContErr != null)
        {
            return (false, null, new[] { specContErr ?? Diagnostic.Error(DiagnosticCodes.PathEscapeDetected, $"Target path escapes workspace: '{normSpecDocPath}'.") });
        }

        var (isTasksRelValid, _, tasksRelErr) = PathSecurity.ValidateRelativeDocumentPath(normTasksDocPath);
        if (!isTasksRelValid || tasksRelErr != null)
        {
            return (false, null, new[] { tasksRelErr ?? Diagnostic.Error(DiagnosticCodes.InvalidArgument, $"Invalid document path '{normTasksDocPath}'.") });
        }

        var fullTasksDocPath = Path.Combine(workspaceRoot, normTasksDocPath.Replace('/', Path.DirectorySeparatorChar));
        var (isTasksContained, tasksContErr) = PathSecurity.CheckContainmentAndReparsePoints(workspaceRoot, fullTasksDocPath);
        if (!isTasksContained || tasksContErr != null)
        {
            return (false, null, new[] { tasksContErr ?? Diagnostic.Error(DiagnosticCodes.PathEscapeDetected, $"Target path escapes workspace: '{normTasksDocPath}'.") });
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
            return (false, null, new[] { Diagnostic.Error(code, $"Failed to parse change-apply request XML: {xmlEx.Message}", normTasksDocPath, xmlEx.LineNumber, xmlEx.LinePosition) });
        }
        catch (Exception ex)
        {
            return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.XmlParseError, $"Failed to parse change-apply request XML: {ex.Message}", normTasksDocPath) });
        }

        if (schemaDiagnostics.Any(d => d.Severity == "error"))
        {
            return (false, null, schemaDiagnostics);
        }

        var reqRoot = requestDoc.Root;
        if (reqRoot == null || !string.Equals(reqRoot.Name.LocalName, "change-apply", StringComparison.Ordinal))
        {
            return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.UnknownDocumentType, $"Expected root element <change-apply>, found <{reqRoot?.Name.LocalName}>.", normTasksDocPath) });
        }

        var applyId = reqRoot.Attribute("id")?.Value;
        if (string.IsNullOrWhiteSpace(applyId) || !ProjectSemanticIndex.IsValidTimeFirstId(applyId))
        {
            return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.InvalidIdGrammar, $"change-apply @id '{applyId}' is missing or invalid.", normTasksDocPath) });
        }

        var actor = reqRoot.Attribute("actor")?.Value;
        if (string.IsNullOrWhiteSpace(actor))
        {
            return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.InvalidArgument, "change-apply @actor is required.", normTasksDocPath) });
        }

        var occurredAt = reqRoot.Attribute("occurred_at")?.Value;
        if (string.IsNullOrWhiteSpace(occurredAt) || !IsValidUtcTimestamp(occurredAt, out var reqOccurredAt))
        {
            return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.InvalidArgument, $"change-apply @occurred_at '{occurredAt}' must be a valid UTC timestamp ending with 'Z'.", normTasksDocPath) });
        }

        // Fingerprints are replay guards over canonical request XML, not a
        // signature, evidence record, or authenticity mechanism.
        var requestFingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(GenericAppender.ToCanonicalXmlString(reqRoot)))).ToLowerInvariant();

        var resolveFindingsContainer = reqRoot.Element("resolve_findings");
        var resolveFindings = resolveFindingsContainer?.Elements("finding").ToList() ?? new List<XElement>();
        var taskDispositionsContainer = reqRoot.Element("task_dispositions");
        var taskDispositions = taskDispositionsContainer?.Elements("task").ToList() ?? new List<XElement>();
        var addTasksContainer = reqRoot.Element("add_tasks");
        var addTasks = addTasksContainer?.Elements("task").ToList() ?? new List<XElement>();
        if (resolveFindings.Count == 0 && taskDispositions.Count == 0 && addTasks.Count == 0)
        {
            return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.ChangeApplicationInvalid, "change-apply must resolve a finding, dispose a task, or add a successor task; no-op applications are rejected.", normTasksDocPath) });
        }

        // Stamp all request-carried records before idempotency comparison.
        foreach (var disposition in taskDispositions)
        {
            foreach (var record in disposition.Elements("record")) record.SetAttributeValue("operation_id", applyId);
        }
        foreach (var task in addTasks)
        {
            foreach (var record in task.Element("records")?.Elements("record") ?? Enumerable.Empty<XElement>()) record.SetAttributeValue("operation_id", applyId);
        }

        // 2. Load Documents
        if (!File.Exists(fullSpecDocPath))
        {
            return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.DocumentNotFound, $"Target spec document '{normSpecDocPath}' not found.", normSpecDocPath) });
        }
        if (!File.Exists(fullTasksDocPath))
        {
            return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.DocumentNotFound, $"Target tasks document '{normTasksDocPath}' not found.", normTasksDocPath) });
        }
        if (new FileInfo(fullSpecDocPath).Length > XPathQueryLimits.MaxDocumentBytes)
        {
            return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.LimitExceeded, $"Document '{normSpecDocPath}' exceeds maximum allowed size of {XPathQueryLimits.MaxDocumentBytes} bytes.", normSpecDocPath) });
        }
        if (new FileInfo(fullTasksDocPath).Length > XPathQueryLimits.MaxDocumentBytes)
        {
            return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.LimitExceeded, $"Document '{normTasksDocPath}' exceeds maximum allowed size of {XPathQueryLimits.MaxDocumentBytes} bytes.", normTasksDocPath) });
        }

        XDocument specDoc;
        XDocument tasksDoc;
        try
        {
            using var fsSpec = File.OpenRead(fullSpecDocPath);
            using var rSpec = SecureXmlReaderFactory.CreateReader(fsSpec);
            specDoc = XDocument.Load(rSpec, LoadOptions.PreserveWhitespace | LoadOptions.SetLineInfo);

            using var fsTasks = File.OpenRead(fullTasksDocPath);
            using var rTasks = SecureXmlReaderFactory.CreateReader(fsTasks);
            tasksDoc = XDocument.Load(rTasks, LoadOptions.PreserveWhitespace | LoadOptions.SetLineInfo);
        }
        catch (Exception ex)
        {
            return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.XmlParseError, $"Failed to read managed documents: {ex.Message}") });
        }

        var specRoot = specDoc.Root;
        var tasksRoot = tasksDoc.Root;
        if (specRoot == null || specRoot.Name.LocalName != "iteration" ||
            tasksRoot == null || tasksRoot.Name.LocalName != "tasks")
        {
            return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.XmlParseError, "Managed document root elements are missing or invalid.") });
        }

        var specStatus = specRoot.Attribute("status")?.Value ?? string.Empty;
        if (!string.Equals(specStatus, "replanning", StringComparison.Ordinal))
        {
            return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.ChangeApplicationInvalid, $"Cannot apply change during iteration status '{specStatus}'. change apply requires iteration status 'replanning'.", normSpecDocPath) });
        }

        var specRevStr = specRoot.Attribute("revision")?.Value;
        if (!int.TryParse(specRevStr, CultureInfo.InvariantCulture, out var actualSpecRevision) || actualSpecRevision <= 0)
        {
            return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.XmlParseError, $"Document '{normSpecDocPath}' revision is invalid.", normSpecDocPath) });
        }

        var tasksRevStr = tasksRoot.Attribute("revision")?.Value;
        if (!int.TryParse(tasksRevStr, CultureInfo.InvariantCulture, out var actualTasksRevision) || actualTasksRevision <= 0)
        {
            return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.XmlParseError, $"Document '{normTasksDocPath}' revision is invalid.", normTasksDocPath) });
        }

        var receiptId = applyId + "-receipt";
        var existingReceipt = tasksRoot.Descendants("record")
            .FirstOrDefault(r => string.Equals(r.Attribute("id")?.Value, receiptId, StringComparison.Ordinal) &&
                                 string.Equals(r.Attribute("operation_id")?.Value, applyId, StringComparison.Ordinal));
        if (existingReceipt != null)
        {
            if (expectedSpecRevision != actualSpecRevision || expectedTasksRevision != actualTasksRevision - 1)
            {
                return (false, null, new[] { new Diagnostic(DiagnosticCodes.RevisionConflict, "error", $"Change application '{applyId}' was already committed, but the workspace has drifted beyond its immediate post-commit revisions.", normTasksDocPath, ExpectedRevision: expectedTasksRevision, ActualRevision: actualTasksRevision) });
            }

            var expectedReceiptSummary = $"Change application '{applyId}' committed: {reqRoot.Element("summary")?.Value.Trim()}";
            var storedFingerprint = existingReceipt.Element("index")?.Elements("term")
                .FirstOrDefault(t => string.Equals(t.Attribute("key")?.Value, "request-sha256", StringComparison.Ordinal))?.Attribute("value")?.Value;
            var replayValid = string.Equals(existingReceipt.Element("summary")?.Value, expectedReceiptSummary, StringComparison.Ordinal) &&
                string.Equals(storedFingerprint, requestFingerprint, StringComparison.Ordinal);
            foreach (var rf in resolveFindings)
            {
                var record = tasksRoot.Elements("task").FirstOrDefault(t => string.Equals(t.Attribute("id")?.Value, rf.Attribute("task")?.Value, StringComparison.Ordinal))
                    ?.Element("records")?.Elements("record").FirstOrDefault(r => string.Equals(r.Attribute("id")?.Value, rf.Attribute("target")?.Value, StringComparison.Ordinal));
                replayValid &= record != null && string.Equals(record.Attribute("status")?.Value, "resolved", StringComparison.Ordinal);
            }
            foreach (var td in taskDispositions)
            {
                var wanted = td.Attribute("transition")?.Value switch { "supersede" => "superseded", "transfer" => "transferred", "cancel" => "cancelled", _ => string.Empty };
                var task = tasksRoot.Elements("task").FirstOrDefault(t => string.Equals(t.Attribute("id")?.Value, td.Attribute("target")?.Value, StringComparison.Ordinal));
                replayValid &= task != null && string.Equals(task.Attribute("status")?.Value, wanted, StringComparison.Ordinal);
                foreach (var requestedRecord in td.Elements("record"))
                {
                    var storedRecord = task?.Element("records")?.Elements("record")
                        .FirstOrDefault(r => string.Equals(r.Attribute("id")?.Value, requestedRecord.Attribute("id")?.Value, StringComparison.Ordinal));
                    replayValid &= storedRecord != null && GenericAppender.AreElementsCanonicallyEqual(storedRecord, requestedRecord);
                }
            }
            foreach (var added in addTasks)
            {
                var stored = tasksRoot.Elements("task").FirstOrDefault(t => string.Equals(t.Attribute("id")?.Value, added.Attribute("id")?.Value, StringComparison.Ordinal));
                if (stored == null) { replayValid = false; continue; }
                var storedClone = new XElement(stored);
                storedClone.Element("records")?.Elements("record").Where(r => string.Equals(r.Attribute("id")?.Value, receiptId, StringComparison.Ordinal)).Remove();
                replayValid &= GenericAppender.AreElementsCanonicallyEqual(storedClone, added);
            }
            if (!replayValid)
            {
                return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.IdempotencyConflict, $"Change application operation '{applyId}' was already applied with different effects.", normTasksDocPath) });
            }

            return (true, new MutationEnvelope("change apply", new[] { new MutatedDocument(normTasksDocPath, actualTasksRevision) }, alreadyApplied: true), Array.Empty<Diagnostic>());
        }

        if (tasksRoot.Descendants().Any(e => string.Equals(e.Attribute("operation_id")?.Value, applyId, StringComparison.Ordinal)))
        {
            return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.IdempotencyConflict, $"Operation ID '{applyId}' already exists without its durable change-apply receipt.", normTasksDocPath) });
        }

        if (expectedSpecRevision != actualSpecRevision)
        {
            return (false, null, new[] { new Diagnostic(DiagnosticCodes.RevisionConflict, "error", $"Expected spec revision {expectedSpecRevision} does not match actual revision {actualSpecRevision}.", normSpecDocPath, ExpectedRevision: expectedSpecRevision, ActualRevision: actualSpecRevision) });
        }

        if (expectedTasksRevision != actualTasksRevision)
        {
            return (false, null, new[] { new Diagnostic(DiagnosticCodes.RevisionConflict, "error", $"Expected tasks revision {expectedTasksRevision} does not match actual revision {actualTasksRevision}.", normTasksDocPath, ExpectedRevision: expectedTasksRevision, ActualRevision: actualTasksRevision) });
        }

        // 3. Process Resolve Findings
        foreach (var rf in resolveFindings)
        {
            var targetTaskId = rf.Attribute("task")?.Value;
            var targetRecordId = rf.Attribute("target")?.Value;

            var targetTask = tasksRoot.Elements("task").FirstOrDefault(t => string.Equals((string?)t.Attribute("id"), targetTaskId, StringComparison.Ordinal));
            if (targetTask == null)
            {
                return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.CardinalityConflict, $"Task '{targetTaskId}' in resolve_findings was not found in '{normTasksDocPath}'.", normTasksDocPath) });
            }
            var taskStatus = targetTask.Attribute("status")?.Value ?? "pending";
            if (taskStatus is "done" or "transferred" or "superseded" or "cancelled")
            {
                return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.TaskImmutable, $"change-apply cannot resolve a finding on terminal task '{targetTaskId}'.", normTasksDocPath) });
            }
            if (DateTimeOffset.TryParse(targetTask.Attribute("updated_at")?.Value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var taskUpdatedAt) && reqOccurredAt < taskUpdatedAt)
            {
                return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.InvalidArgument, $"change-apply @occurred_at '{occurredAt}' cannot be earlier than task '{targetTaskId}' updated_at '{targetTask.Attribute("updated_at")?.Value}'.", normTasksDocPath) });
            }

            var targetRecord = targetTask.Element("records")?.Elements("record").FirstOrDefault(r => string.Equals((string?)r.Attribute("id"), targetRecordId, StringComparison.Ordinal));
            if (targetRecord == null)
            {
                return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.CardinalityConflict, $"Finding record '{targetRecordId}' was not found in task '{targetTaskId}'.", normTasksDocPath) });
            }
            if (!string.Equals(targetRecord.Attribute("kind")?.Value, "finding", StringComparison.Ordinal) ||
                !string.Equals(targetRecord.Attribute("status")?.Value, "active", StringComparison.Ordinal))
            {
                return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.ChangeApplicationInvalid, $"Finding record '{targetRecordId}' on task '{targetTaskId}' must be kind='finding' and status='active' before resolution.", normTasksDocPath) });
            }

            targetRecord.SetAttributeValue("status", "resolved");
            targetTask.SetAttributeValue("updated_at", occurredAt);
        }

        // 4. Process Task Dispositions
        for (var dispositionIndex = 0; dispositionIndex < taskDispositions.Count; dispositionIndex++)
        {
            var td = taskDispositions[dispositionIndex];
            var targetTaskId = td.Attribute("target")?.Value;
            var transition = td.Attribute("transition")?.Value;

            var targetTask = tasksRoot.Elements("task").FirstOrDefault(t => string.Equals((string?)t.Attribute("id"), targetTaskId, StringComparison.Ordinal));
            if (targetTask == null)
            {
                return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.CardinalityConflict, $"Task '{targetTaskId}' in task_dispositions was not found in '{normTasksDocPath}'.", normTasksDocPath) });
            }
            if (DateTimeOffset.TryParse(targetTask.Attribute("updated_at")?.Value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var taskUpdatedAt) && reqOccurredAt < taskUpdatedAt)
            {
                return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.InvalidArgument, $"change-apply @occurred_at '{occurredAt}' cannot be earlier than task '{targetTaskId}' updated_at '{targetTask.Attribute("updated_at")?.Value}'.", normTasksDocPath) });
            }

            var currentStatus = targetTask.Attribute("status")?.Value ?? "pending";
            var isTerminal = string.Equals(currentStatus, "done", StringComparison.Ordinal) ||
                             string.Equals(currentStatus, "transferred", StringComparison.Ordinal) ||
                             string.Equals(currentStatus, "superseded", StringComparison.Ordinal) ||
                             string.Equals(currentStatus, "cancelled", StringComparison.Ordinal);

            if (isTerminal)
            {
                return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.TaskImmutable, $"Cannot dispose task '{targetTaskId}': task is already in terminal status '{currentStatus}' and is immutable.", normTasksDocPath) });
            }

            var targetStatus = transition switch
            {
                "supersede" => "superseded",
                "transfer" => "transferred",
                "cancel" => "cancelled",
                _ => null
            };

            if (targetStatus == null)
            {
                return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.TaskTransitionConflict, $"Task disposition transition must be 'supersede', 'transfer', or 'cancel'. Found: '{transition}'.", normTasksDocPath) });
            }

            targetTask.SetAttributeValue("status", targetStatus);
            DogdouSpec.Core.Tasks.StatusTermHelper.SynchronizeStatusTerm(targetTask, targetStatus);
            targetTask.SetAttributeValue("updated_at", occurredAt);

            var dispRecords = td.Elements("record").ToList();
            if (dispRecords.Count > 0)
            {
                var recordsContainer = targetTask.Element("records");
                if (recordsContainer == null)
                {
                    recordsContainer = new XElement("records");
                    targetTask.Add(recordsContainer);
                }

                foreach (var rec in dispRecords)
                {
                    rec.SetAttributeValue("operation_id", applyId);
                    recordsContainer.Add(new XElement(rec));
                }
            }

            var generatedRecords = targetTask.Element("records") ?? new XElement("records");
            if (generatedRecords.Parent == null) targetTask.Add(generatedRecords);
            generatedRecords.Add(new XElement("record",
                new XAttribute("id", $"{applyId}-disposition-{dispositionIndex + 1}"),
                new XAttribute("kind", "decision"),
                new XAttribute("status", "informational"),
                new XAttribute("created_at", occurredAt),
                new XAttribute("actor", actor),
                new XAttribute("operation_id", applyId),
                new XElement("summary", $"Task disposition '{transition}' applied during change '{applyId}'."),
                new XElement("impact", td.Attribute("rationale")?.Value ?? "No explicit disposition rationale was supplied.")));
        }

        // 5. Process Add Tasks
        foreach (var taskElem in addTasks)
        {
            var newTaskId = taskElem.Attribute("id")?.Value;
            if (string.IsNullOrWhiteSpace(newTaskId) || !ProjectSemanticIndex.IsValidTimeFirstId(newTaskId))
            {
                return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.InvalidIdGrammar, $"New task ID '{newTaskId}' is invalid.", normTasksDocPath) });
            }

            var status = taskElem.Attribute("status")?.Value;
            if (!string.Equals(status, "pending", StringComparison.Ordinal))
            {
                return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.InvalidArgument, $"New task '{newTaskId}' must have status='pending'. Found: '{status}'.", normTasksDocPath) });
            }
            if (!string.Equals(taskElem.Attribute("created_at")?.Value, occurredAt, StringComparison.Ordinal) ||
                !string.Equals(taskElem.Attribute("updated_at")?.Value, occurredAt, StringComparison.Ordinal) ||
                taskElem.Attribute("started_at") != null || taskElem.Attribute("completed_at") != null)
            {
                return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.InvalidArgument, $"New task '{newTaskId}' must stamp created_at and updated_at exactly to change-apply/@occurred_at and must not carry started_at or completed_at.", normTasksDocPath) });
            }

            tasksRoot.Add(new XElement(taskElem));
        }

        // Every apply has exactly one deterministic receipt. It is attached to
        // the first explicitly impacted task in request order, or the first
        // newly-created successor when no existing task was targeted.
        var receiptTaskId = resolveFindings.FirstOrDefault()?.Attribute("task")?.Value
            ?? taskDispositions.FirstOrDefault()?.Attribute("target")?.Value
            ?? addTasks.FirstOrDefault()?.Attribute("id")?.Value;
        var receiptTask = tasksRoot.Elements("task").FirstOrDefault(t => string.Equals(t.Attribute("id")?.Value, receiptTaskId, StringComparison.Ordinal));
        if (receiptTask == null)
        {
            return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.ChangeApplicationInvalid, "change-apply could not determine an impacted task for its durable receipt.", normTasksDocPath) });
        }
        var receiptRecords = receiptTask.Element("records");
        if (receiptRecords == null)
        {
            receiptRecords = new XElement("records");
            receiptTask.Add(receiptRecords);
        }
        receiptRecords.Add(new XElement("record",
            new XAttribute("id", receiptId),
            new XAttribute("kind", "discussion"),
            new XAttribute("status", "informational"),
            new XAttribute("created_at", occurredAt),
            new XAttribute("actor", actor),
            new XAttribute("operation_id", applyId),
            new XElement("index",
                new XElement("summary", "Change application receipt."),
                new XElement("term", new XAttribute("key", "request-sha256"), new XAttribute("value", requestFingerprint))),
            new XElement("summary", $"Change application '{applyId}' committed: {reqRoot.Element("summary")?.Value.Trim()}")));

        var newTasksRevision = actualTasksRevision + 1;
        tasksRoot.SetAttributeValue("revision", newTasksRevision.ToString(CultureInfo.InvariantCulture));

        // 6. Serialize and Commit
        var tasksReplacementContent = ManagedDocumentSerializer.Serialize(tasksDoc);

        var operations = new[]
        {
            new TransactionDocumentOperation(normTasksDocPath, tasksReplacementContent, actualTasksRevision, newTasksRevision)
        };

        return WorkspaceTransactionCommitter.Commit(
            workspaceRoot,
            "change apply",
            operations,
            clock,
            faultInjector,
            version,
            correlationId: applyId,
            readPreconditions: new[] { new TransactionReadPrecondition(normSpecDocPath, actualSpecRevision) },
            dryRun: dryRun);
    }

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
}
