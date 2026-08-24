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

public static class TaskSplitter
{
    private static readonly UTF8Encoding Utf8NoBom = new(false);

    public static (bool Success, MutationEnvelope? Envelope, IReadOnlyList<Diagnostic> Diagnostics) Split(
        string workspaceRoot,
        string iterationId,
        string taskId,
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
            return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.InvalidArgument, "task-split request XML must be provided.") });
        }
        if (Encoding.UTF8.GetByteCount(requestXml) > XPathQueryLimits.MaxDocumentBytes)
        {
            return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.LimitExceeded, $"task-split request exceeds maximum allowed size of {XPathQueryLimits.MaxDocumentBytes} bytes.") });
        }

        var (isWsSafe, wsErr) = PathSecurity.VerifyWorkspaceDirectorySecurity(workspaceRoot);
        if (!isWsSafe || wsErr != null)
        {
            return (false, null, new[] { wsErr ?? Diagnostic.Error(DiagnosticCodes.PathEscapeDetected, "Workspace directory security verification failed.") });
        }

        var normTasksDocPath = $"{normIterId}/tasks.xml";
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

        var normSpecDocPath = $"{normIterId}/spec.xml";
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
            return (false, null, new[] { Diagnostic.Error(code, $"Failed to parse task-split request XML: {xmlEx.Message}", normTasksDocPath, xmlEx.LineNumber, xmlEx.LinePosition) });
        }
        catch (Exception ex)
        {
            return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.XmlParseError, $"Failed to parse task-split request XML: {ex.Message}", normTasksDocPath) });
        }

        if (schemaDiagnostics.Any(d => d.Severity == "error"))
        {
            return (false, null, schemaDiagnostics);
        }

        var reqRoot = requestDoc.Root;
        if (reqRoot == null || !string.Equals(reqRoot.Name.LocalName, "task-split", StringComparison.Ordinal))
        {
            return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.UnknownDocumentType, $"Expected root element <task-split>, found <{reqRoot?.Name.LocalName}>.", normTasksDocPath) });
        }

        var splitId = reqRoot.Attribute("id")?.Value;
        if (string.IsNullOrWhiteSpace(splitId) || !ProjectSemanticIndex.IsValidTimeFirstId(splitId))
        {
            return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.InvalidIdGrammar, $"task-split @id '{splitId}' is missing or invalid.", normTasksDocPath) });
        }

        var actor = reqRoot.Attribute("actor")?.Value;
        if (string.IsNullOrWhiteSpace(actor))
        {
            return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.InvalidArgument, "task-split @actor is required.", normTasksDocPath) });
        }

        var occurredAt = reqRoot.Attribute("occurred_at")?.Value;
        if (string.IsNullOrWhiteSpace(occurredAt) || !IsValidUtcTimestamp(occurredAt, out var reqOccurredAt))
        {
            return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.InvalidArgument, $"task-split @occurred_at '{occurredAt}' must be a valid UTC timestamp ending with 'Z'.", normTasksDocPath) });
        }

        // Replay fingerprint only; it is not an authenticity or evidence hash.
        var requestFingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(GenericAppender.ToCanonicalXmlString(reqRoot)))).ToLowerInvariant();

        var parentDispElem = reqRoot.Element("parent_disposition");
        if (parentDispElem == null)
        {
            return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.SchemaValidationError, "task-split must contain <parent_disposition>.", normTasksDocPath) });
        }

        var parentTransition = parentDispElem.Attribute("transition")?.Value;
        if (string.IsNullOrWhiteSpace(parentTransition) ||
            (!string.Equals(parentTransition, "supersede", StringComparison.Ordinal) &&
             !string.Equals(parentTransition, "transfer", StringComparison.Ordinal) &&
             !string.Equals(parentTransition, "cancel", StringComparison.Ordinal)))
        {
            return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.TaskTransitionConflict, $"Parent disposition transition must be 'supersede', 'transfer', or 'cancel'. Found: '{parentTransition}'.", normTasksDocPath) });
        }

        var targetParentStatus = parentTransition switch
        {
            "supersede" => "superseded",
            "transfer" => "transferred",
            "cancel" => "cancelled",
            _ => parentTransition
        };

        var subtasksContainer = reqRoot.Element("subtasks");
        var subtaskElems = subtasksContainer?.Elements("task").ToList() ?? new List<XElement>();
        if (subtaskElems.Count < 2)
        {
            return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.SchemaValidationError, "task-split must specify at least 2 subtasks.", normTasksDocPath) });
        }

        var seenSubtaskIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var st in subtaskElems)
        {
            var stId = st.Attribute("id")?.Value;
            if (string.IsNullOrWhiteSpace(stId) || !ProjectSemanticIndex.IsValidTimeFirstId(stId))
            {
                return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.InvalidIdGrammar, $"Subtask ID '{stId}' is invalid.", normTasksDocPath) });
            }

            if (!seenSubtaskIds.Add(stId))
            {
                return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.DuplicateId, $"Duplicate subtask ID '{stId}' in request.", normTasksDocPath) });
            }

            var stStatus = st.Attribute("status")?.Value;
            if (!string.Equals(stStatus, "pending", StringComparison.Ordinal))
            {
                return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.InvalidArgument, $"Subtask '{stId}' must have status='pending'. Found: '{stStatus}'.", normTasksDocPath) });
            }
            if (!string.Equals(st.Attribute("created_at")?.Value, occurredAt, StringComparison.Ordinal) ||
                !string.Equals(st.Attribute("updated_at")?.Value, occurredAt, StringComparison.Ordinal) ||
                st.Attribute("started_at") != null || st.Attribute("completed_at") != null)
            {
                return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.InvalidArgument, $"Subtask '{stId}' must stamp created_at and updated_at exactly to task-split/@occurred_at and must not carry started_at or completed_at.", normTasksDocPath) });
            }

            // Stamp operation_id on subtask records
            var stRecords = st.Element("records")?.Elements("record");
            if (stRecords != null)
            {
                foreach (var rec in stRecords)
                {
                    rec.SetAttributeValue("operation_id", splitId);
                }
            }
        }

        // Stamp operation_id on parent disposition records
        var dispRecords = parentDispElem.Elements("record").ToList();
        foreach (var rec in dispRecords)
        {
            rec.SetAttributeValue("operation_id", splitId);
        }

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

        var matchingTasks = tasksRoot.Elements("task").Where(t => string.Equals((string?)t.Attribute("id"), taskId, StringComparison.Ordinal)).ToList();
        if (matchingTasks.Count == 0)
        {
            return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.CardinalityConflict, $"Parent task '{taskId}' was not found in '{normTasksDocPath}'.", normTasksDocPath) });
        }
        if (matchingTasks.Count > 1)
        {
            return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.CardinalityConflict, $"Parent task '{taskId}' matched {matchingTasks.Count} elements in '{normTasksDocPath}'.", normTasksDocPath) });
        }

        var parentTask = matchingTasks[0];
        if (DateTimeOffset.TryParse(parentTask.Attribute("updated_at")?.Value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parentUpdatedAt) && reqOccurredAt < parentUpdatedAt)
        {
            return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.InvalidArgument, $"task-split @occurred_at '{occurredAt}' cannot be earlier than parent task updated_at '{parentTask.Attribute("updated_at")?.Value}'.", normTasksDocPath) });
        }
        var parentStatus = parentTask.Attribute("status")?.Value ?? "pending";

        // Check if parent is terminal
        var isTerminal = string.Equals(parentStatus, "done", StringComparison.Ordinal) ||
                         string.Equals(parentStatus, "transferred", StringComparison.Ordinal) ||
                         string.Equals(parentStatus, "superseded", StringComparison.Ordinal) ||
                         string.Equals(parentStatus, "cancelled", StringComparison.Ordinal);

        if (isTerminal && !(parentTask.Element("records")?.Elements("record")
                .Any(r => string.Equals(r.Attribute("operation_id")?.Value, splitId, StringComparison.Ordinal)) ?? false))
        {
            return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.TaskImmutable, $"Cannot split task '{taskId}': task is in terminal status '{parentStatus}' and is immutable.", normTasksDocPath) });
        }

        // Validate origin requirement references of subtasks against spec.xml
        if (File.Exists(fullSpecDocPath))
        {
            if (new FileInfo(fullSpecDocPath).Length > XPathQueryLimits.MaxDocumentBytes)
            {
                return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.LimitExceeded, $"Document '{normSpecDocPath}' exceeds maximum allowed size of {XPathQueryLimits.MaxDocumentBytes} bytes.", normSpecDocPath) });
            }
            try
            {
                using var fs = File.OpenRead(fullSpecDocPath);
                using var reader = SecureXmlReaderFactory.CreateReader(fs);
                var specDoc = XDocument.Load(reader, LoadOptions.PreserveWhitespace | LoadOptions.SetLineInfo);
                var specReqIds = specDoc.Descendants("requirement").Select(r => (string?)r.Attribute("id")).Where(id => id != null).ToHashSet(StringComparer.Ordinal);

                foreach (var st in subtaskElems)
                {
                    var originRefs = st.Element("origin")?.Elements("ref").ToList() ?? new List<XElement>();
                    foreach (var oRef in originRefs)
                    {
                        var target = oRef.Attribute("target")?.Value;
                        if (!string.IsNullOrEmpty(target) && !specReqIds.Contains(target))
                        {
                            return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.DanglingReference, $"Subtask origin target '{target}' does not exist in '{normSpecDocPath}'.", normTasksDocPath) });
                        }
                    }
                }
            }
            catch { }
        }

        // 3. Idempotency Check
        var (enumSuccess, allDocs, enumDiags) = WorkspaceDiscovery.EnumerateDocuments(workspaceRoot);
        if (!enumSuccess || enumDiags.Count > 0)
        {
            return (false, null, enumDiags);
        }

        var allSubtasksPresent = subtaskElems.All(st => tasksRoot.Elements("task").Any(t => string.Equals((string?)t.Attribute("id"), (string?)st.Attribute("id"), StringComparison.Ordinal)));
        if (string.Equals(parentStatus, targetParentStatus, StringComparison.Ordinal) && allSubtasksPresent)
        {
            var parentReceipts = parentTask.Element("records")?.Elements("record")
                .Where(r => string.Equals(r.Attribute("operation_id")?.Value, splitId, StringComparison.Ordinal) &&
                            !string.Equals(r.Attribute("id")?.Value, splitId + "-receipt", StringComparison.Ordinal)).ToList() ?? new List<XElement>();
            var splitReceipt = parentTask.Element("records")?.Elements("record")
                .FirstOrDefault(r => string.Equals(r.Attribute("id")?.Value, splitId + "-receipt", StringComparison.Ordinal) &&
                                     string.Equals(r.Attribute("operation_id")?.Value, splitId, StringComparison.Ordinal));
            var storedFingerprint = splitReceipt?.Element("index")?.Elements("term")
                .FirstOrDefault(t => string.Equals(t.Attribute("key")?.Value, "request-sha256", StringComparison.Ordinal))?.Attribute("value")?.Value;
            var receiptMatches = parentReceipts.Count == dispRecords.Count &&
                parentReceipts.Zip(dispRecords).All(pair => GenericAppender.AreElementsCanonicallyEqual(pair.First, pair.Second)) &&
                splitReceipt != null && string.Equals(storedFingerprint, requestFingerprint, StringComparison.Ordinal);
            var subtasksMatch = subtaskElems.All(requested =>
            {
                var stored = tasksRoot.Elements("task").FirstOrDefault(t => string.Equals(t.Attribute("id")?.Value, requested.Attribute("id")?.Value, StringComparison.Ordinal));
                return stored != null && GenericAppender.AreElementsCanonicallyEqual(stored, requested);
            });

            if (!receiptMatches || !subtasksMatch || !string.Equals(parentTask.Attribute("updated_at")?.Value, occurredAt, StringComparison.Ordinal))
            {
                return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.IdempotencyConflict, $"Task split operation '{splitId}' already exists with different subtasks, disposition receipt, or parent state.", normTasksDocPath) });
            }

            if (expectedRevision != actualRevision - 1)
            {
                var diag = new Diagnostic(
                    DiagnosticCodes.RevisionConflict,
                    "error",
                    $"Task split '{splitId}' was already committed, but the workspace has drifted beyond its immediate post-commit revision.",
                    Document: normTasksDocPath,
                    ExpectedRevision: expectedRevision,
                    ActualRevision: actualRevision);
                return (false, null, new[] { diag });
            }

            var alreadyAppliedEnv = new MutationEnvelope(
                "task split",
                new[] { new MutatedDocument(normTasksDocPath, actualRevision) },
                alreadyApplied: true);
            return (true, alreadyAppliedEnv, Array.Empty<Diagnostic>());
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
                if (xDoc.Descendants().Any(e => string.Equals((string?)e.Attribute("operation_id"), splitId, StringComparison.Ordinal)))
                {
                    return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.IdempotencyConflict, $"Operation ID '{splitId}' already exists in document '{doc.RelativePath}'.", normTasksDocPath) });
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

        // 4. Apply Mutations
        parentTask.SetAttributeValue("status", targetParentStatus);
        parentTask.SetAttributeValue("updated_at", occurredAt);

        if (dispRecords.Count > 0)
        {
            var parentRecords = parentTask.Element("records");
            if (parentRecords == null)
            {
                parentRecords = new XElement("records");
                parentTask.Add(parentRecords);
            }

            foreach (var rec in dispRecords)
            {
                parentRecords.Add(rec);
            }
        }

        var generatedRecords = parentTask.Element("records") ?? new XElement("records");
        if (generatedRecords.Parent == null) parentTask.Add(generatedRecords);
        generatedRecords.Add(new XElement("record",
            new XAttribute("id", splitId + "-receipt"),
            new XAttribute("kind", "decision"),
            new XAttribute("status", "informational"),
            new XAttribute("created_at", occurredAt),
            new XAttribute("actor", actor),
            new XAttribute("operation_id", splitId),
            new XElement("index",
                new XElement("summary", "Task split receipt."),
                new XElement("term", new XAttribute("key", "request-sha256"), new XAttribute("value", requestFingerprint))),
            new XElement("summary", $"Task split '{splitId}' applied."),
            new XElement("impact", parentDispElem.Attribute("rationale")?.Value ?? "No explicit split rationale was supplied.")));

        foreach (var st in subtaskElems)
        {
            tasksRoot.Add(st);
        }

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
            "task split",
            new[] { operation },
            clock,
            faultInjector,
            version,
            correlationId: splitId);
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
