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

namespace DogdouSpec.Core.Requirements;

public static class RequirementProposer
{
    private static readonly UTF8Encoding Utf8NoBom = new(false);

    public static (bool Success, MutationEnvelope? Envelope, IReadOnlyList<Diagnostic> Diagnostics) Propose(
        string workspaceRoot,
        string iterationId,
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

        if (expectedRevision <= 0)
        {
            return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.InvalidArgument, $"Expected revision must be positive. Received: {expectedRevision}.") });
        }

        if (string.IsNullOrWhiteSpace(requestXml))
        {
            return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.InvalidArgument, "requirement-propose request XML must be provided.") });
        }
        if (Encoding.UTF8.GetByteCount(requestXml) > XPathQueryLimits.MaxDocumentBytes)
        {
            return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.LimitExceeded, $"requirement-propose request exceeds maximum allowed size of {XPathQueryLimits.MaxDocumentBytes} bytes.") });
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
                    ? Diagnostic.Error(code, args.Message, normSpecDocPath, line, col)
                    : Diagnostic.Warning(code, args.Message, normSpecDocPath, line, col);

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
            return (false, null, new[] { Diagnostic.Error(code, $"Failed to parse requirement-propose request XML: {xmlEx.Message}", normSpecDocPath, xmlEx.LineNumber, xmlEx.LinePosition) });
        }
        catch (Exception ex)
        {
            return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.XmlParseError, $"Failed to parse requirement-propose request XML: {ex.Message}", normSpecDocPath) });
        }

        if (schemaDiagnostics.Any(d => d.Severity == "error"))
        {
            return (false, null, schemaDiagnostics);
        }

        var reqRoot = requestDoc.Root;
        if (reqRoot == null || !string.Equals(reqRoot.Name.LocalName, "requirement-propose", StringComparison.Ordinal))
        {
            return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.UnknownDocumentType, $"Expected root element <requirement-propose>, found <{reqRoot?.Name.LocalName}>.", normSpecDocPath) });
        }

        var proposeId = reqRoot.Attribute("id")?.Value;
        if (string.IsNullOrWhiteSpace(proposeId) || !ProjectSemanticIndex.IsValidTimeFirstId(proposeId))
        {
            return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.InvalidIdGrammar, $"requirement-propose @id '{proposeId}' is missing or invalid.", normSpecDocPath) });
        }

        var actor = reqRoot.Attribute("actor")?.Value;
        if (string.IsNullOrWhiteSpace(actor))
        {
            return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.InvalidArgument, "requirement-propose @actor is required.", normSpecDocPath) });
        }

        var occurredAt = reqRoot.Attribute("occurred_at")?.Value;
        if (string.IsNullOrWhiteSpace(occurredAt) || !IsValidUtcTimestamp(occurredAt, out var reqOccurredAt))
        {
            return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.InvalidArgument, $"requirement-propose @occurred_at '{occurredAt}' must be a valid UTC timestamp ending with 'Z'.", normSpecDocPath) });
        }
        var requestFingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(GenericAppender.ToCanonicalXmlString(reqRoot)))).ToLowerInvariant();

        var requirementElem = reqRoot.Element("requirement");
        if (requirementElem == null)
        {
            return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.SchemaValidationError, "requirement-propose must contain a <requirement> element.", normSpecDocPath) });
        }

        var reqId = requirementElem.Attribute("id")?.Value;
        if (string.IsNullOrWhiteSpace(reqId) || !ProjectSemanticIndex.IsValidTimeFirstId(reqId))
        {
            return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.InvalidIdGrammar, $"Requirement @id '{reqId}' is invalid.", normSpecDocPath) });
        }

        var reqStatus = requirementElem.Attribute("status")?.Value;
        if (!string.Equals(reqStatus, "proposed", StringComparison.Ordinal))
        {
            return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.OwnerDecisionRequired, $"Technical agents may only propose requirements with status='proposed'. Requirement '{reqId}' specifies status='{reqStatus}', which requires owner confirmation.", normSpecDocPath) });
        }

        // Requirement records are product-spec records, for which semantic
        // validation intentionally reserves operation_id to task-owned records.
        // The deterministic receipt below therefore stores its operation ID as
        // an indexed value rather than an attribute.
        var recordsElem = requirementElem.Element("records");
        if (recordsElem == null)
        {
            recordsElem = new XElement("records");
            requirementElem.Add(recordsElem);
        }
        recordsElem.Add(CreateReceipt(proposeId, actor, occurredAt, requestFingerprint, "Requirement proposal receipt."));

        // 2. Read spec.xml
        if (!File.Exists(fullSpecDocPath))
        {
            return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.DocumentNotFound, $"Target spec document '{normSpecDocPath}' not found.", normSpecDocPath) });
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

        var specRoot = specDoc.Root;
        if (specRoot == null || specRoot.Name.LocalName != "iteration")
        {
            return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.XmlParseError, $"Document '{normSpecDocPath}' has missing or invalid root element.", normSpecDocPath) });
        }
        if (DateTimeOffset.TryParse(specRoot.Attribute("updated_at")?.Value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var specUpdatedAt) && reqOccurredAt < specUpdatedAt)
        {
            return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.InvalidArgument, $"requirement-propose @occurred_at '{occurredAt}' cannot be earlier than spec updated_at '{specRoot.Attribute("updated_at")?.Value}'.", normSpecDocPath) });
        }

        var kind = specRoot.Attribute("kind")?.Value;
        if (!string.Equals(kind, "feature", StringComparison.Ordinal))
        {
            return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.WorkKindMismatch, $"Cannot add product requirements to a '{kind}' iteration. Only 'feature' iterations have product requirements.", normSpecDocPath) });
        }

        var revStr = specRoot.Attribute("revision")?.Value;
        if (!int.TryParse(revStr, CultureInfo.InvariantCulture, out var actualRevision) || actualRevision <= 0)
        {
            return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.XmlParseError, $"Document '{normSpecDocPath}' revision is invalid.", normSpecDocPath) });
        }

        var productElem = specRoot.Element("product");
        if (productElem == null)
        {
            return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.XmlParseError, $"Feature iteration '{normSpecDocPath}' is missing <product> element.", normSpecDocPath) });
        }

        var reqsContainer = productElem.Element("requirements");
        if (reqsContainer == null)
        {
            reqsContainer = new XElement("requirements");
            var scopeElem = productElem.Element("scope");
            if (scopeElem != null)
            {
                scopeElem.AddAfterSelf(reqsContainer);
            }
            else
            {
                productElem.Add(reqsContainer);
            }
        }

        // 3. Idempotency Check
        var (enumSuccess, allDocs, enumDiags) = WorkspaceDiscovery.EnumerateDocuments(workspaceRoot);
        if (!enumSuccess || enumDiags.Count > 0)
        {
            return (false, null, enumDiags);
        }

        var existingReq = reqsContainer.Elements("requirement").FirstOrDefault(r => string.Equals((string?)r.Attribute("id"), reqId, StringComparison.Ordinal));
        if (existingReq != null)
        {
            var storedReceipt = existingReq.Element("records")?.Elements("record")
                .FirstOrDefault(r => string.Equals(r.Attribute("id")?.Value, proposeId + "-receipt", StringComparison.Ordinal));
            var storedFingerprint = storedReceipt?.Element("index")?.Elements("term")
                .FirstOrDefault(t => string.Equals(t.Attribute("key")?.Value, "request-sha256", StringComparison.Ordinal))?.Attribute("value")?.Value;
            var storedClone = new XElement(existingReq);
            storedClone.Element("records")?.Elements("record").Where(r => string.Equals(r.Attribute("id")?.Value, proposeId + "-receipt", StringComparison.Ordinal)).Remove();
            var requestedClone = new XElement(requirementElem);
            requestedClone.Element("records")?.Elements("record").Where(r => string.Equals(r.Attribute("id")?.Value, proposeId + "-receipt", StringComparison.Ordinal)).Remove();
            if (storedReceipt != null && string.Equals(storedFingerprint, requestFingerprint, StringComparison.Ordinal) &&
                GenericAppender.AreElementsCanonicallyEqual(storedClone, requestedClone))
            {
                if (expectedRevision != actualRevision && expectedRevision != actualRevision - 1)
                {
                    var diag = new Diagnostic(
                        DiagnosticCodes.RevisionConflict,
                        "error",
                        $"Expected revision {expectedRevision} does not match actual revision {actualRevision}.",
                        Document: normSpecDocPath,
                        ExpectedRevision: expectedRevision,
                        ActualRevision: actualRevision);
                    return (false, null, new[] { diag });
                }

                var alreadyAppliedEnv = new MutationEnvelope(
                    "requirement propose",
                    new[] { new MutatedDocument(normSpecDocPath, actualRevision) },
                    alreadyApplied: true);
                return (true, alreadyAppliedEnv, Array.Empty<Diagnostic>());
            }

            return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.DuplicateId, $"Requirement with ID '{reqId}' already exists in '{normSpecDocPath}' with different content.", normSpecDocPath) });
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
                if (xDoc.Descendants().Any(e => string.Equals((string?)e.Attribute("operation_id"), proposeId, StringComparison.Ordinal) ||
                    (string.Equals(e.Name.LocalName, "record", StringComparison.Ordinal) &&
                     string.Equals(e.Attribute("id")?.Value, proposeId + "-receipt", StringComparison.Ordinal))))
                {
                    return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.IdempotencyConflict, $"Operation ID '{proposeId}' already exists in document '{doc.RelativePath}'.", normSpecDocPath) });
                }
            }
            catch { }
        }

        if (expectedRevision != actualRevision)
        {
            var diag = new Diagnostic(
                DiagnosticCodes.RevisionConflict,
                "error",
                $"Expected revision {expectedRevision} does not match actual revision {actualRevision} for document '{normSpecDocPath}'.",
                Document: normSpecDocPath,
                ExpectedRevision: expectedRevision,
                ActualRevision: actualRevision);
            return (false, null, new[] { diag });
        }

        // 4. Apply Mutation
        reqsContainer.Add(requirementElem);
        specRoot.SetAttributeValue("updated_at", occurredAt);
        var newRevision = actualRevision + 1;
        specRoot.SetAttributeValue("revision", newRevision.ToString(CultureInfo.InvariantCulture));

        // 5. Serialize and Commit
        var replacementContent = ManagedDocumentSerializer.Serialize(specDoc);

        var operation = new TransactionDocumentOperation(
            normSpecDocPath,
            replacementContent,
            actualRevision,
            newRevision);

        return WorkspaceTransactionCommitter.Commit(
            workspaceRoot,
            "requirement propose",
            new[] { operation },
            clock,
            faultInjector,
            version,
            correlationId: proposeId,
            dryRun: dryRun);
    }

    private static XElement CreateReceipt(string operationId, string actor, string occurredAt, string fingerprint, string summary) =>
        new("record",
            new XAttribute("id", operationId + "-receipt"),
            new XAttribute("kind", "discussion"),
            new XAttribute("status", "informational"),
            new XAttribute("created_at", occurredAt),
            new XAttribute("actor", actor),
            new XElement("index",
                new XElement("summary", summary),
                new XElement("term", new XAttribute("key", "request-sha256"), new XAttribute("value", fingerprint))),
            new XElement("summary", $"{summary} Operation {operationId}."));

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
