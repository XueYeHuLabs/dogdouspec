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

public static class TaskAdder
{
    private static readonly UTF8Encoding Utf8NoBom = new(false);

    public static (bool Success, MutationEnvelope? Envelope, IReadOnlyList<Diagnostic> Diagnostics) Add(
        string workspaceRoot,
        string iterationId,
        int expectedRevision,
        string requestXml,
        IClock? clock = null,
        IFaultInjector? faultInjector = null,
        string version = "1.0") => AddInternal(workspaceRoot, iterationId, expectedRevision, requestXml, false, false, "task add", clock, faultInjector, version, null);

    /// <summary>Creates a normal task from the compact quick-task request.  The start form is one write, not add then update.</summary>
    public static (bool Success, MutationEnvelope? Envelope, IReadOnlyList<Diagnostic> Diagnostics) AddQuick(
        string workspaceRoot, string iterationId, int expectedRevision, string requestXml, bool start, bool dryRun = false,
        IClock? clock = null, IFaultInjector? faultInjector = null, string version = "1.0",
        IReadOnlyList<TransactionReadPrecondition>? readPreconditions = null) =>
        AddInternal(workspaceRoot, iterationId, expectedRevision, requestXml, start, dryRun, "task quick", clock, faultInjector, version, readPreconditions);

    private static (bool Success, MutationEnvelope? Envelope, IReadOnlyList<Diagnostic> Diagnostics) AddInternal(
        string workspaceRoot, string iterationId, int expectedRevision, string requestXml, bool start, bool dryRun,
        string commandName, IClock? clock, IFaultInjector? faultInjector, string version,
        IReadOnlyList<TransactionReadPrecondition>? readPreconditions)
    {
        if (string.IsNullOrWhiteSpace(workspaceRoot))
        {
            return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.InvalidArgument, "Workspace root must be specified.") });
        }

        if (string.IsNullOrWhiteSpace(iterationId))
        {
            return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.InvalidArgument, "Iteration ID must be specified.") });
        }

        var (iterationValid, normIterId, iterationError) = PathSecurity.ValidateIterationId(iterationId);
        if (!iterationValid || iterationError != null)
        {
            return (false, null, new[] { iterationError ?? Diagnostic.Error(DiagnosticCodes.InvalidArgument, $"Iteration ID '{iterationId}' is invalid.") });
        }

        if (expectedRevision <= 0)
        {
            return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.InvalidArgument, $"Expected revision must be positive. Received: {expectedRevision}.") });
        }

        if (string.IsNullOrWhiteSpace(requestXml))
        {
            return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.InvalidArgument, "task-add request XML must be provided.") });
        }
        if (Encoding.UTF8.GetByteCount(requestXml) > XPathQueryLimits.MaxDocumentBytes)
        {
            return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.LimitExceeded, $"task-add request exceeds maximum allowed size of {XPathQueryLimits.MaxDocumentBytes} bytes.") });
        }

        var (isWsSafe, wsErr) = PathSecurity.VerifyWorkspaceDirectorySecurity(workspaceRoot);
        if (!isWsSafe || wsErr != null)
        {
            return (false, null, new[] { wsErr ?? Diagnostic.Error(DiagnosticCodes.PathEscapeDetected, "Workspace directory security verification failed.") });
        }

        var normTasksDocPath = $"{normIterId}/tasks.xml";
        var normSpecDocPath = $"{normIterId}/spec.xml";

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
            return (false, null, new[] { Diagnostic.Error(code, $"Failed to parse task-add request XML: {xmlEx.Message}", normTasksDocPath, xmlEx.LineNumber, xmlEx.LinePosition) });
        }
        catch (Exception ex)
        {
            return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.XmlParseError, $"Failed to parse task-add request XML: {ex.Message}", normTasksDocPath) });
        }

        if (schemaDiagnostics.Any(d => d.Severity == "error"))
        {
            return (false, null, schemaDiagnostics);
        }

        var reqRoot = requestDoc.Root;
        if (reqRoot == null || !string.Equals(reqRoot.Name.LocalName, "task-add", StringComparison.Ordinal))
        {
            return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.UnknownDocumentType, $"Expected root element <task-add>, found <{reqRoot?.Name.LocalName}>.", normTasksDocPath) });
        }

        var addId = reqRoot.Attribute("id")?.Value;
        if (string.IsNullOrWhiteSpace(addId) || !ProjectSemanticIndex.IsValidTimeFirstId(addId))
        {
            return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.InvalidIdGrammar, $"task-add @id '{addId}' is missing or does not conform to the time-first ID grammar.", normTasksDocPath) });
        }

        var actor = reqRoot.Attribute("actor")?.Value;
        if (string.IsNullOrWhiteSpace(actor))
        {
            return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.InvalidArgument, "task-add @actor is required.", normTasksDocPath) });
        }

        var occurredAt = reqRoot.Attribute("occurred_at")?.Value;
        if (string.IsNullOrWhiteSpace(occurredAt) || !IsValidUtcTimestamp(occurredAt, out var reqOccurredAt))
        {
            return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.InvalidArgument, $"task-add @occurred_at '{occurredAt}' must be a valid UTC timestamp ending with 'Z'.", normTasksDocPath) });
        }
        var requestFingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(GenericAppender.ToCanonicalXmlString(reqRoot)))).ToLowerInvariant();

        var taskElem = reqRoot.Element("task");
        if (taskElem == null)
        {
            return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.SchemaValidationError, "task-add must contain a <task> element.", normTasksDocPath) });
        }

        var taskId = taskElem.Attribute("id")?.Value;
        if (string.IsNullOrWhiteSpace(taskId) || !ProjectSemanticIndex.IsValidTimeFirstId(taskId))
        {
            return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.InvalidIdGrammar, $"Task @id '{taskId}' is missing or invalid.", normTasksDocPath) });
        }

        var taskStatus = taskElem.Attribute("status")?.Value;
        var expectedStatus = start ? "in-progress" : "pending";
        if (!string.Equals(taskStatus, expectedStatus, StringComparison.Ordinal))
        {
            return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.InvalidArgument, $"New task '{taskId}' must have status='{expectedStatus}'. Found status='{taskStatus}'.", normTasksDocPath) });
        }
        if (!string.Equals(taskElem.Attribute("created_at")?.Value, occurredAt, StringComparison.Ordinal) ||
            !string.Equals(taskElem.Attribute("updated_at")?.Value, occurredAt, StringComparison.Ordinal) ||
            (!start && taskElem.Attribute("started_at") != null) ||
            (start && !string.Equals(taskElem.Attribute("started_at")?.Value, occurredAt, StringComparison.Ordinal)) ||
            taskElem.Attribute("completed_at") != null)
        {
            return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.InvalidArgument, $"New task '{taskId}' must stamp created_at and updated_at to the request time; quick --start must also stamp started_at, and completed_at is forbidden.", normTasksDocPath) });
        }

        // Stamp operation_id on records in task
        var recordsElem = taskElem.Element("records");
        if (recordsElem != null)
        {
            foreach (var rec in recordsElem.Elements("record"))
            {
                var recId = rec.Attribute("id")?.Value;
                if (string.IsNullOrWhiteSpace(recId) || !ProjectSemanticIndex.IsValidTimeFirstId(recId))
                {
                    return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.InvalidIdGrammar, $"Record @id '{recId}' is invalid.", normTasksDocPath) });
                }

                var recOpId = rec.Attribute("operation_id")?.Value;
                if (!string.IsNullOrEmpty(recOpId) && !string.Equals(recOpId, addId, StringComparison.Ordinal))
                {
                    return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.InvalidArgument, $"Record '{recId}' supplies conflicting operation_id '{recOpId}'.", normTasksDocPath) });
                }

                rec.SetAttributeValue("operation_id", addId);
            }
        }
        else
        {
            recordsElem = new XElement("records");
            taskElem.Add(recordsElem);
        }
        if (start)
        {
            var startRecord = recordsElem.Elements("record").SingleOrDefault(r => string.Equals(r.Attribute("kind")?.Value, "start", StringComparison.Ordinal));
            if (startRecord == null ||
                !string.Equals(startRecord.Attribute("status")?.Value, "informational", StringComparison.Ordinal) ||
                !string.Equals(startRecord.Attribute("created_at")?.Value, occurredAt, StringComparison.Ordinal) ||
                string.IsNullOrWhiteSpace(startRecord.Attribute("actor")?.Value))
            {
                return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.InvalidArgument, "Quick --start requires exactly one informational start record stamped at occurred_at with an actor.", normTasksDocPath) });
            }
        }
        recordsElem.Add(CreateReceipt(addId, actor, occurredAt, requestFingerprint, start ? "Quick task creation-and-start receipt." : "Task addition receipt."));

        // 2. Read tasks.xml and spec.xml
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

        if (!File.Exists(fullSpecDocPath))
        {
            return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.DocumentNotFound, $"spec.xml not found for iteration '{normIterId}'.", normSpecDocPath) });
        }
        if (new FileInfo(fullSpecDocPath).Length > XPathQueryLimits.MaxDocumentBytes)
        {
            return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.LimitExceeded, $"Document '{normSpecDocPath}' exceeds maximum allowed size of {XPathQueryLimits.MaxDocumentBytes} bytes.", normSpecDocPath) });
        }

        XDocument specDoc;
        try
        {
            using var fs = File.OpenRead(fullSpecDocPath);
            using var reader = SecureXmlReaderFactory.CreateReader(fs);
            specDoc = XDocument.Load(reader, LoadOptions.PreserveWhitespace | LoadOptions.SetLineInfo);
        }
        catch (Exception ex)
        {
            return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.XmlParseError, $"Failed to read '{normSpecDocPath}': {ex.Message}", normSpecDocPath) });
        }
        if (start && !string.Equals(specDoc.Root?.Attribute("status")?.Value, "active", StringComparison.Ordinal))
        {
            return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.IterationReplanningExecutionFrozen, $"Quick --start requires iteration '{normIterId}' to be active.", normSpecDocPath) });
        }

        // A normal task implements one or more requirements.  Quick operational work
        // instead has exactly one supports edge to the owning iteration.
        var originRefs = taskElem.Element("origin")?.Elements("ref").ToList() ?? new List<XElement>();
        if (originRefs.Count == 0)
        {
            return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.SemanticContextIncomplete, $"Task '{taskId}' must have at least one <origin> reference.", normTasksDocPath) });
        }

        var requirementDefinitions = specDoc.Root?.Element("product")?.Element("requirements")?.Elements("requirement")
            .Where(r => !string.IsNullOrWhiteSpace(r.Attribute("id")?.Value)).ToList() ?? new List<XElement>();
        if (requirementDefinitions.GroupBy(r => r.Attribute("id")!.Value, StringComparer.Ordinal).Any(g => g.Count() != 1))
        {
            return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.DuplicateId, "Iteration contains duplicate requirement IDs.", normSpecDocPath) });
        }
        var specReqIds = requirementDefinitions.Select(r => r.Attribute("id")!.Value).ToHashSet(StringComparer.Ordinal);
        var operational = originRefs.Any(r =>
            string.Equals(r.Attribute("relation")?.Value, "supports", StringComparison.Ordinal) ||
            string.Equals(r.Attribute("target")?.Value, normIterId, StringComparison.Ordinal));
        if (operational)
        {
            var isStructurallyValid = originRefs.Count == 1 &&
                string.Equals(originRefs[0].Attribute("scope")?.Value, "iteration", StringComparison.Ordinal) &&
                string.Equals(originRefs[0].Attribute("relation")?.Value, "supports", StringComparison.Ordinal) &&
                string.Equals(originRefs[0].Attribute("target")?.Value, normIterId, StringComparison.Ordinal);

            if (!isStructurallyValid)
            {
                var actualCount = originRefs.Count.ToString(CultureInfo.InvariantCulture);
                var actualScope = FormatOriginValues(originRefs, "scope");
                var actualRelation = FormatOriginValues(originRefs, "relation");
                var actualTarget = FormatOriginValues(originRefs, "target");

                return (false, null, new[] { Diagnostic.Error(
                    DiagnosticCodes.InvalidReferenceTargetType,
                    $"Operational origin for task '{taskId}' must be exactly one iteration supports reference to '{normIterId}'. " +
                    $"Expected: count=1, scope='iteration', relation='supports', target='{normIterId}'. " +
                    $"Actual: count={actualCount}, scope={actualScope}, relation={actualRelation}, target={actualTarget}.",
                    normTasksDocPath) });
            }
        }
        else
        {
            foreach (var oRef in originRefs)
            {
                var target = oRef.Attribute("target")?.Value;
                if (!string.Equals(oRef.Attribute("scope")?.Value, "iteration", StringComparison.Ordinal) ||
                    !string.Equals(oRef.Attribute("relation")?.Value, "implements", StringComparison.Ordinal) ||
                    string.IsNullOrEmpty(target) || !specReqIds.Contains(target))
                {
                    return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.DanglingReference, $"Origin target requirement '{target}' does not exist or is not an iteration implements reference in '{normSpecDocPath}'.", normTasksDocPath) });
                }
            }
            if (start)
            {
                var statuses = requirementDefinitions.ToDictionary(r => r.Attribute("id")!.Value, r => r.Attribute("status")?.Value ?? string.Empty, StringComparer.Ordinal);
                if (originRefs.Any(r => !statuses.TryGetValue(r.Attribute("target")?.Value ?? string.Empty, out var status) || !string.Equals(status, "approved", StringComparison.Ordinal)))
                    return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.OwnerDecisionRequired, "Quick --start requires every origin requirement to be approved.", normTasksDocPath) });
            }
        }

        // 3. Project-wide Idempotency Check
        var (enumSuccess, allDocs, enumDiags) = WorkspaceDiscovery.EnumerateDocuments(workspaceRoot);
        if (!enumSuccess || enumDiags.Count > 0)
        {
            return (false, null, enumDiags);
        }

        var existingTask = tasksRoot.Elements("task").FirstOrDefault(t => string.Equals((string?)t.Attribute("id"), taskId, StringComparison.Ordinal));
        if (existingTask != null)
        {
            var storedReceipt = existingTask.Element("records")?.Elements("record")
                .FirstOrDefault(r => string.Equals(r.Attribute("id")?.Value, addId + "-receipt", StringComparison.Ordinal));
            var storedFingerprint = storedReceipt?.Element("index")?.Elements("term")
                .FirstOrDefault(t => string.Equals(t.Attribute("key")?.Value, "request-sha256", StringComparison.Ordinal))?.Attribute("value")?.Value;
            var storedClone = new XElement(existingTask);
            storedClone.Element("records")?.Elements("record").Where(r => string.Equals(r.Attribute("id")?.Value, addId + "-receipt", StringComparison.Ordinal)).Remove();
            var requestedClone = new XElement(taskElem);
            requestedClone.Element("records")?.Elements("record").Where(r => string.Equals(r.Attribute("id")?.Value, addId + "-receipt", StringComparison.Ordinal)).Remove();
            if (storedReceipt != null && string.Equals(storedFingerprint, requestFingerprint, StringComparison.Ordinal) &&
                GenericAppender.AreElementsCanonicallyEqual(storedClone, requestedClone))
            {
                if (expectedRevision != actualRevision && expectedRevision != actualRevision - 1)
                {
                    var diag = new Diagnostic(
                        DiagnosticCodes.RevisionConflict,
                        "error",
                        $"Expected revision {expectedRevision} does not match actual revision {actualRevision}.",
                        Document: normTasksDocPath,
                        ExpectedRevision: expectedRevision,
                        ActualRevision: actualRevision);
                    return (false, null, new[] { diag });
                }

                var alreadyAppliedEnv = new MutationEnvelope(
                    commandName,
                    new[] { new MutatedDocument(normTasksDocPath, actualRevision) },
                    alreadyApplied: true);
                return (true, alreadyAppliedEnv, Array.Empty<Diagnostic>());
            }

            return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.DuplicateId, $"Task with ID '{taskId}' already exists in '{normTasksDocPath}' with different content.", normTasksDocPath) });
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
                if (xDoc.Descendants().Any(e => string.Equals((string?)e.Attribute("operation_id"), addId, StringComparison.Ordinal)))
                {
                    return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.IdempotencyConflict, $"Operation ID '{addId}' already exists in document '{doc.RelativePath}'.", normTasksDocPath) });
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

        // 4. Apply mutation
        tasksRoot.Add(taskElem);
        var newRevision = actualRevision + 1;
        tasksRoot.SetAttributeValue("revision", newRevision.ToString(CultureInfo.InvariantCulture));

        // 5. Serialize and Commit
        var replacementContent = ManagedDocumentSerializer.Serialize(tasksDoc);

        var operation = new TransactionDocumentOperation(
            normTasksDocPath,
            replacementContent,
            actualRevision,
            newRevision);

        if (dryRun)
        {
            // This is intentionally the same prospective whole-workspace validation
            // used by the committer, but does not acquire a writer lock or create
            // transaction staging/recovery state.
            var previewValidation = SchemaValidator.ValidateProspective(
                workspaceRoot,
                new[] { new ProspectiveDocument(normTasksDocPath, replacementContent, IsNew: false, ExpectedRevision: actualRevision) },
                version);
            if (!previewValidation.IsValid)
            {
                return (false, null, previewValidation.Diagnostics);
            }
            return (true, new MutationEnvelope(commandName, new[] { new MutatedDocument(normTasksDocPath, newRevision, actualRevision) }), Array.Empty<Diagnostic>());
        }

        return WorkspaceTransactionCommitter.Commit(
            workspaceRoot,
            commandName,
            new[] { operation },
            clock,
            faultInjector,
            version,
            correlationId: addId,
            readPreconditions: readPreconditions);
    }

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

    private static string FormatOriginValues(List<XElement> refs, string attributeName)
    {
        if (refs.Count == 1)
        {
            return FormatSingleOriginValue(refs[0].Attribute(attributeName)?.Value);
        }

        var items = refs.Take(5).Select(r => FormatSingleOriginValue(r.Attribute(attributeName)?.Value));
        var suffix = refs.Count > 5 ? ", ..." : string.Empty;
        return $"[{string.Join(", ", items)}{suffix}]";
    }

    private static string FormatSingleOriginValue(string? raw)
    {
        if (raw == null)
        {
            return "<missing>";
        }

        if (raw.Length == 0)
        {
            return "<empty>";
        }

        return $"'{SanitizeOriginValue(raw)}'";
    }

    private static string SanitizeOriginValue(string raw)
    {
        var sb = new StringBuilder(Math.Min(raw.Length, 64));
        foreach (var c in raw)
        {
            if (sb.Length >= 64)
            {
                sb.Append("...");
                break;
            }

            sb.Append(char.IsControl(c) ? ' ' : c);
        }

        var trimmed = sb.ToString().Trim();
        return trimmed.Length == 0 ? "<empty>" : trimmed;
    }
}
