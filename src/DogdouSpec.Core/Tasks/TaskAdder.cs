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
        string version = "1.0")
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
        if (!string.Equals(taskStatus, "pending", StringComparison.Ordinal))
        {
            return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.InvalidArgument, $"Newly added task '{taskId}' must have status='pending'. Found status='{taskStatus}'.", normTasksDocPath) });
        }
        if (!string.Equals(taskElem.Attribute("created_at")?.Value, occurredAt, StringComparison.Ordinal) ||
            !string.Equals(taskElem.Attribute("updated_at")?.Value, occurredAt, StringComparison.Ordinal) ||
            taskElem.Attribute("started_at") != null || taskElem.Attribute("completed_at") != null)
        {
            return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.InvalidArgument, $"New task '{taskId}' must stamp created_at and updated_at exactly to task-add/@occurred_at and must not carry started_at or completed_at.", normTasksDocPath) });
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
        recordsElem.Add(CreateReceipt(addId, actor, occurredAt, requestFingerprint, "Task addition receipt."));

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

        // Validate origin requirement targets exist in spec.xml
        var originRefs = taskElem.Element("origin")?.Elements("ref").ToList() ?? new List<XElement>();
        if (originRefs.Count == 0)
        {
            return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.SemanticContextIncomplete, $"Task '{taskId}' must have at least one <origin> reference.", normTasksDocPath) });
        }

        var specReqIds = specDoc.Descendants("requirement").Select(r => (string?)r.Attribute("id")).Where(id => id != null).ToHashSet(StringComparer.Ordinal);
        foreach (var oRef in originRefs)
        {
            var target = oRef.Attribute("target")?.Value;
            if (!string.IsNullOrEmpty(target) && !specReqIds.Contains(target))
            {
                return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.DanglingReference, $"Origin target requirement '{target}' does not exist in '{normSpecDocPath}'.", normTasksDocPath) });
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
                    "task add",
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
            tasksDoc.Save(writer);
        }

        var replacementContent = Encoding.UTF8.GetString(memoryStream.ToArray());
        if (!replacementContent.EndsWith('\n'))
        {
            replacementContent += "\n";
        }

        var operation = new TransactionDocumentOperation(
            normTasksDocPath,
            replacementContent,
            actualRevision,
            newRevision);

        return WorkspaceTransactionCommitter.Commit(
            workspaceRoot,
            "task add",
            new[] { operation },
            clock,
            faultInjector,
            version,
            correlationId: addId);
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
}
