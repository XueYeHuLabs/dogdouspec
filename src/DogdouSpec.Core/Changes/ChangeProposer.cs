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

public static class ChangeProposer
{
    private static readonly UTF8Encoding Utf8NoBom = new(false);

    public static (bool Success, MutationEnvelope? Envelope, IReadOnlyList<Diagnostic> Diagnostics) Propose(
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
            return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.InvalidArgument, "change-propose request XML must be provided.") });
        }
        if (Encoding.UTF8.GetByteCount(requestXml) > XPathQueryLimits.MaxDocumentBytes)
        {
            return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.LimitExceeded, $"change-propose request exceeds maximum allowed size of {XPathQueryLimits.MaxDocumentBytes} bytes.") });
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
            return (false, null, new[] { Diagnostic.Error(code, $"Failed to parse change-propose request XML: {xmlEx.Message}", normTasksDocPath, xmlEx.LineNumber, xmlEx.LinePosition) });
        }
        catch (Exception ex)
        {
            return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.XmlParseError, $"Failed to parse change-propose request XML: {ex.Message}", normTasksDocPath) });
        }

        if (schemaDiagnostics.Any(d => d.Severity == "error"))
        {
            return (false, null, schemaDiagnostics);
        }

        var reqRoot = requestDoc.Root;
        if (reqRoot == null || !string.Equals(reqRoot.Name.LocalName, "change-propose", StringComparison.Ordinal))
        {
            return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.UnknownDocumentType, $"Expected root element <change-propose>, found <{reqRoot?.Name.LocalName}>.", normTasksDocPath) });
        }

        var proposeId = reqRoot.Attribute("id")?.Value;
        if (string.IsNullOrWhiteSpace(proposeId) || !ProjectSemanticIndex.IsValidTimeFirstId(proposeId))
        {
            return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.InvalidIdGrammar, $"change-propose @id '{proposeId}' is missing or invalid.", normTasksDocPath) });
        }

        var actor = reqRoot.Attribute("actor")?.Value;
        if (string.IsNullOrWhiteSpace(actor))
        {
            return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.InvalidArgument, "change-propose @actor is required.", normTasksDocPath) });
        }

        var occurredAt = reqRoot.Attribute("occurred_at")?.Value;
        if (string.IsNullOrWhiteSpace(occurredAt) || !IsValidUtcTimestamp(occurredAt, out var reqOccurredAt))
        {
            return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.InvalidArgument, $"change-propose @occurred_at '{occurredAt}' must be a valid UTC timestamp ending with 'Z'.", normTasksDocPath) });
        }

        // This is an idempotency fingerprint only: it detects divergent replay
        // of the complete semantic request; it is not a signature or evidence.
        var requestFingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(GenericAppender.ToCanonicalXmlString(reqRoot)))).ToLowerInvariant();

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

        // A material-change proposal is an active-execution operation. The owner
        // alone moves the iteration into replanning afterwards.
        var specStatus = specRoot.Attribute("status")?.Value ?? string.Empty;
        if (!string.Equals(specStatus, "active", StringComparison.Ordinal))
        {
            return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.ChangeApplicationInvalid, $"Cannot propose a material change while iteration status is '{specStatus}'. change propose requires status 'active'.", normSpecDocPath) });
        }
        if (DateTimeOffset.TryParse(specRoot.Attribute("updated_at")?.Value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var specUpdatedAt) && reqOccurredAt < specUpdatedAt)
        {
            return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.InvalidArgument, $"change-propose @occurred_at '{occurredAt}' cannot be earlier than spec updated_at '{specRoot.Attribute("updated_at")?.Value}'.", normSpecDocPath) });
        }

        // 3. Validate Proposed Requirements
        var proposedReqsContainer = reqRoot.Element("proposed_requirements");
        var proposedReqs = proposedReqsContainer?.Elements("requirement").ToList() ?? new List<XElement>();
        var existingRequirementIds = (specRoot.Element("product")?.Element("requirements")?.Elements("requirement") ?? Enumerable.Empty<XElement>())
            .Select(r => r.Attribute("id")?.Value)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .ToHashSet(StringComparer.Ordinal);
        foreach (var req in proposedReqs)
        {
            var reqId = req.Attribute("id")?.Value;
            if (string.IsNullOrWhiteSpace(reqId) || !ProjectSemanticIndex.IsValidTimeFirstId(reqId))
            {
                return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.InvalidIdGrammar, $"Proposed requirement ID '{reqId}' is invalid.", normSpecDocPath) });
            }

            var status = req.Attribute("status")?.Value;
            if (!string.Equals(status, "proposed", StringComparison.Ordinal))
            {
                return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.OwnerDecisionRequired, $"Proposed requirement '{reqId}' must have status='proposed'. Found: '{status}'.", normSpecDocPath) });
            }

            // Stamp operation_id on records
            var records = req.Element("records")?.Elements("record");
            if (records != null)
            {
                foreach (var r in records)
                {
                    r.SetAttributeValue("operation_id", proposeId);
                }
            }
        }

        // 4. Validate Finding Records and Freeze Tasks
        var findingRecords = reqRoot.Elements("finding_record").ToList();
        if (findingRecords.Count == 0)
        {
            return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.SchemaValidationError, "change-propose requires at least one <finding_record>; the active finding is the durable proposal receipt.", normTasksDocPath) });
        }
        var findingRecordIds = new HashSet<string>(StringComparer.Ordinal);
        var findingTaskIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var fr in findingRecords)
        {
            var targetTaskId = fr.Attribute("task")?.Value;
            if (string.IsNullOrWhiteSpace(targetTaskId))
            {
                return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.CardinalityConflict, "change-propose finding_record requires a task target.", normTasksDocPath) });
            }
            var targetTask = tasksRoot.Elements("task").FirstOrDefault(t => string.Equals((string?)t.Attribute("id"), targetTaskId, StringComparison.Ordinal));
            if (targetTask == null)
            {
                return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.CardinalityConflict, $"Task '{targetTaskId}' specified in finding_record was not found in '{normTasksDocPath}'.", normTasksDocPath) });
            }

            var targetStatus = targetTask.Attribute("status")?.Value ?? "pending";
            if (targetStatus is "done" or "transferred" or "superseded" or "cancelled")
            {
                return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.TaskImmutable, $"change-propose cannot attach an active finding to terminal task '{targetTaskId}'.", normTasksDocPath) });
            }
            findingTaskIds.Add(targetTaskId);

            var rec = fr.Element("record");
            if (rec == null || !string.Equals(rec.Attribute("kind")?.Value, "finding", StringComparison.Ordinal) ||
                !string.Equals(rec.Attribute("status")?.Value, "active", StringComparison.Ordinal))
            {
                return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.SchemaValidationError, "change-propose finding_record must contain a kind='finding', status='active' record.", normTasksDocPath) });
            }

            if (DateTimeOffset.TryParse(targetTask.Attribute("updated_at")?.Value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var taskUpdatedAt) && reqOccurredAt < taskUpdatedAt)
            {
                return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.InvalidArgument, $"change-propose @occurred_at '{occurredAt}' cannot be earlier than task '{targetTaskId}' updated_at '{targetTask.Attribute("updated_at")?.Value}'.", normTasksDocPath) });
            }

            var recId = rec.Attribute("id")?.Value;
            if (string.IsNullOrWhiteSpace(recId) || !findingRecordIds.Add(recId))
            {
                return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.DuplicateId, $"change-propose finding record ID '{recId}' is missing or duplicated.", normTasksDocPath) });
            }

            if (rec != null)
            {
                rec.SetAttributeValue("operation_id", proposeId);
            }
        }

        // Durable idempotency is intentionally limited to the immediately
        // preceding committed state. A later owner decision or task edit must
        // be re-read and explicitly reconciled rather than replayed blindly.
        var existingOperationRecords = tasksRoot.Descendants("record")
            .Where(r => string.Equals(r.Attribute("operation_id")?.Value, proposeId, StringComparison.Ordinal))
            .ToList();
        var receiptId = proposeId + "-receipt";
        var existingReceipt = existingOperationRecords.FirstOrDefault(r => string.Equals(r.Attribute("id")?.Value, receiptId, StringComparison.Ordinal));
        if (existingOperationRecords.Count > 0)
        {
            if (existingReceipt == null)
            {
                return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.IdempotencyConflict, $"Change proposal operation '{proposeId}' exists without its durable request receipt.", normTasksDocPath) });
            }
            if (expectedSpecRevision != actualSpecRevision - 1 || expectedTasksRevision != actualTasksRevision - 1)
            {
                return (false, null, new[] { new Diagnostic(DiagnosticCodes.RevisionConflict, "error", $"Change proposal '{proposeId}' was already committed, but the workspace has drifted beyond its immediate post-commit revisions.", normTasksDocPath, ExpectedRevision: expectedTasksRevision, ActualRevision: actualTasksRevision) });
            }

            var storedFingerprint = existingReceipt.Element("index")?.Elements("term")
                .FirstOrDefault(t => string.Equals(t.Attribute("key")?.Value, "request-sha256", StringComparison.Ordinal))?.Attribute("value")?.Value;
            if (!string.Equals(storedFingerprint, requestFingerprint, StringComparison.Ordinal))
            {
                return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.IdempotencyConflict, $"Change proposal operation '{proposeId}' was already applied with a different semantic request fingerprint.", normTasksDocPath) });
            }

            return (true, new MutationEnvelope("change propose", new[]
            {
                new MutatedDocument(normSpecDocPath, actualSpecRevision),
                new MutatedDocument(normTasksDocPath, actualTasksRevision)
            }, alreadyApplied: true), Array.Empty<Diagnostic>());
        }

        if (expectedSpecRevision != actualSpecRevision)
        {
            return (false, null, new[] { new Diagnostic(DiagnosticCodes.RevisionConflict, "error", $"Expected spec revision {expectedSpecRevision} does not match actual revision {actualSpecRevision}.", normSpecDocPath, ExpectedRevision: expectedSpecRevision, ActualRevision: actualSpecRevision) });
        }

        if (expectedTasksRevision != actualTasksRevision)
        {
            return (false, null, new[] { new Diagnostic(DiagnosticCodes.RevisionConflict, "error", $"Expected tasks revision {expectedTasksRevision} does not match actual revision {actualTasksRevision}.", normTasksDocPath, ExpectedRevision: expectedTasksRevision, ActualRevision: actualTasksRevision) });
        }

        if (proposedReqs.Any(req => existingRequirementIds.Contains(req.Attribute("id")!.Value)))
        {
            return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.DuplicateId, "A proposed requirement ID already exists in the iteration specification.", normSpecDocPath) });
        }

        var (enumSuccess, allDocs, enumDiags) = WorkspaceDiscovery.EnumerateDocuments(workspaceRoot);
        if (!enumSuccess || enumDiags.Count > 0)
        {
            return (false, null, enumDiags);
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
                using var reader = SecureXmlReaderFactory.CreateReader(fs);
                var candidate = XDocument.Load(reader);
                if (candidate.Descendants().Any(e => string.Equals(e.Attribute("operation_id")?.Value, proposeId, StringComparison.Ordinal)))
                {
                    return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.IdempotencyConflict, $"Operation ID '{proposeId}' already exists in document '{doc.RelativePath}'.", normTasksDocPath) });
                }
            }
            catch { }
        }

        var freezeTasksContainer = reqRoot.Element("freeze_tasks");
        var freezeTasks = freezeTasksContainer?.Elements("task").ToList() ?? new List<XElement>();
        var freezeTaskIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var ft in freezeTasks)
        {
            var targetTaskId = ft.Attribute("target")?.Value;
            if (string.IsNullOrWhiteSpace(targetTaskId) || !freezeTaskIds.Add(targetTaskId))
            {
                return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.CardinalityConflict, $"change-propose freeze_tasks must name each finding task exactly once; duplicate or missing target '{targetTaskId}'.", normTasksDocPath) });
            }
            var targetTask = tasksRoot.Elements("task").FirstOrDefault(t => string.Equals((string?)t.Attribute("id"), targetTaskId, StringComparison.Ordinal));
            if (targetTask == null)
            {
                return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.CardinalityConflict, $"Target task '{targetTaskId}' specified in freeze_tasks was not found in '{normTasksDocPath}'.", normTasksDocPath) });
            }
            if (DateTimeOffset.TryParse(targetTask.Attribute("updated_at")?.Value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var frozenTaskUpdatedAt) && reqOccurredAt < frozenTaskUpdatedAt)
            {
                return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.InvalidArgument, $"change-propose @occurred_at '{occurredAt}' cannot be earlier than task '{targetTaskId}' updated_at '{targetTask.Attribute("updated_at")?.Value}'.", normTasksDocPath) });
            }
        }
        if (!findingTaskIds.SetEquals(freezeTaskIds))
        {
            return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.ChangeApplicationInvalid, "change-propose freeze_tasks must contain every finding target exactly once and no unrelated task.", normTasksDocPath) });
        }

        // 5. Apply Mutations to spec.xml
        var productElem = specRoot.Element("product");
        if (productElem != null && proposedReqs.Count > 0)
        {
            var reqsElem = productElem.Element("requirements");
            if (reqsElem == null)
            {
                reqsElem = new XElement("requirements");
                var scopeElem = productElem.Element("scope");
                if (scopeElem != null)
                {
                    scopeElem.AddAfterSelf(reqsElem);
                }
                else
                {
                    productElem.Add(reqsElem);
                }
            }

            foreach (var req in proposedReqs)
            {
                reqsElem.Add(new XElement(req));
            }
        }

        specRoot.SetAttributeValue("updated_at", occurredAt);
        var newSpecRevision = actualSpecRevision + 1;
        specRoot.SetAttributeValue("revision", newSpecRevision.ToString(CultureInfo.InvariantCulture));

        // 6. Apply Mutations to tasks.xml
        foreach (var fr in findingRecords)
        {
            var targetTaskId = fr.Attribute("task")?.Value;
            var targetTask = tasksRoot.Elements("task").First(t => string.Equals((string?)t.Attribute("id"), targetTaskId, StringComparison.Ordinal));
            var rec = fr.Element("record");
            if (rec != null)
            {
                var recordsContainer = targetTask.Element("records");
                if (recordsContainer == null)
                {
                    recordsContainer = new XElement("records");
                    targetTask.Add(recordsContainer);
                }
                recordsContainer.Add(new XElement(rec));
            }
        }

        // The first finding owns a compact, deterministic operation receipt.
        // It retains the change summary and full-request fingerprint without
        // making the finding itself less useful as the technical observation.
        var receiptTaskId = findingRecords[0].Attribute("task")?.Value;
        var receiptTask = tasksRoot.Elements("task").First(t => string.Equals(t.Attribute("id")?.Value, receiptTaskId, StringComparison.Ordinal));
        var receiptRecords = receiptTask.Element("records") ?? new XElement("records");
        if (receiptRecords.Parent == null) receiptTask.Add(receiptRecords);
        receiptRecords.Add(new XElement("record",
            new XAttribute("id", receiptId),
            new XAttribute("kind", "discussion"),
            new XAttribute("status", "informational"),
            new XAttribute("created_at", occurredAt),
            new XAttribute("actor", actor),
            new XAttribute("operation_id", proposeId),
            new XElement("index",
                new XElement("summary", "Change proposal receipt."),
                new XElement("term", new XAttribute("key", "request-sha256"), new XAttribute("value", requestFingerprint))),
            new XElement("summary", $"Change proposal '{proposeId}' recorded: {reqRoot.Element("summary")?.Value.Trim()}")));

        for (var freezeIndex = 0; freezeIndex < freezeTasks.Count; freezeIndex++)
        {
            var ft = freezeTasks[freezeIndex];
            var targetTaskId = ft.Attribute("target")?.Value;
            var targetTask = tasksRoot.Elements("task").First(t => string.Equals((string?)t.Attribute("id"), targetTaskId, StringComparison.Ordinal));
            var currentStatus = targetTask.Attribute("status")?.Value ?? "pending";
            if (!string.Equals(currentStatus, "done", StringComparison.Ordinal) &&
                !string.Equals(currentStatus, "transferred", StringComparison.Ordinal) &&
                !string.Equals(currentStatus, "superseded", StringComparison.Ordinal) &&
                !string.Equals(currentStatus, "cancelled", StringComparison.Ordinal))
            {
                targetTask.SetAttributeValue("status", "blocked");
                DogdouSpec.Core.Tasks.StatusTermHelper.SynchronizeStatusTerm(targetTask, "blocked");
                targetTask.SetAttributeValue("updated_at", occurredAt);
            }

            var freezeRecords = targetTask.Element("records") ?? new XElement("records");
            if (freezeRecords.Parent == null) targetTask.Add(freezeRecords);
            freezeRecords.Add(new XElement("record",
                new XAttribute("id", $"{proposeId}-freeze-{freezeIndex + 1}"),
                new XAttribute("kind", "discussion"),
                new XAttribute("status", "informational"),
                new XAttribute("created_at", occurredAt),
                new XAttribute("actor", actor),
                new XAttribute("operation_id", proposeId),
                new XElement("summary", $"Task frozen for change proposal '{proposeId}'."),
                new XElement("impact", ft.Attribute("reason")?.Value ?? "No explicit freeze reason was supplied.")));
        }

        var newTasksRevision = actualTasksRevision + 1;
        tasksRoot.SetAttributeValue("revision", newTasksRevision.ToString(CultureInfo.InvariantCulture));

        // 7. Serialize Both Documents
        var specReplacementContent = ManagedDocumentSerializer.Serialize(specDoc);
        var tasksReplacementContent = ManagedDocumentSerializer.Serialize(tasksDoc);

        var operations = new[]
        {
            new TransactionDocumentOperation(normSpecDocPath, specReplacementContent, actualSpecRevision, newSpecRevision),
            new TransactionDocumentOperation(normTasksDocPath, tasksReplacementContent, actualTasksRevision, newTasksRevision)
        };

        return WorkspaceTransactionCommitter.Commit(
            workspaceRoot,
            "change propose",
            operations,
            clock,
            faultInjector,
            version,
            correlationId: proposeId,
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
