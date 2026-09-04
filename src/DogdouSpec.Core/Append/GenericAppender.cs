using System.Globalization;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using System.Xml.XPath;
using DogdouSpec.Core.Diagnostics;
using DogdouSpec.Core.Security;
using DogdouSpec.Core.Serialization;
using DogdouSpec.Core.Time;
using DogdouSpec.Core.Transactions;
using DogdouSpec.Core.Validation;
using DogdouSpec.Core.Workspace;
using DogdouSpec.Core.XPath;

namespace DogdouSpec.Core.Append;

/// <summary>
/// Authoritative execution engine for generic XML append operations.
/// Evaluates parent XPath fresh against current source, validates project-unique time-first IDs,
/// enforces protected-state authority rules, performs idempotency checks, and atomically commits via WorkspaceTransactionCommitter.
/// </summary>
public static class GenericAppender
{
    private static readonly UTF8Encoding Utf8NoBom = new(false);

    public static (bool Success, MutationEnvelope? Envelope, IReadOnlyList<Diagnostic> Diagnostics) Append(
        string workspaceRoot,
        string documentPath,
        string parentXPath,
        int expectedRevision,
        string fragmentXml,
        IReadOnlyDictionary<string, string>? variables = null,
        IClock? clock = null,
        IFaultInjector? faultInjector = null,
        string version = "1.0")
    {
        clock ??= SystemClock.Instance;

        // 1. Validate inputs
        if (string.IsNullOrWhiteSpace(documentPath))
        {
            return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.InvalidArgument, "Document path cannot be empty.") });
        }

        if (string.IsNullOrWhiteSpace(parentXPath))
        {
            return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.InvalidArgument, "Parent XPath expression cannot be empty.") });
        }

        if (expectedRevision <= 0)
        {
            return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.InvalidArgument, $"Expected revision must be a positive integer, but got {expectedRevision}.") });
        }

        if (string.IsNullOrWhiteSpace(fragmentXml))
        {
            return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.InvalidArgument, "Appended XML fragment cannot be empty.") });
        }

        // 2. Validate workspace directory security
        var (isWsSafe, wsErr) = PathSecurity.VerifyWorkspaceDirectorySecurity(workspaceRoot);
        if (!isWsSafe || wsErr != null)
        {
            return (false, null, new[] { wsErr ?? Diagnostic.Error(DiagnosticCodes.PathEscapeDetected, "Workspace directory security verification failed.") });
        }

        // 3. Validate relative document path
        var (isRelValid, normDocPath, relErr) = PathSecurity.ValidateRelativeDocumentPath(documentPath);
        if (!isRelValid || relErr != null)
        {
            return (false, null, new[] { relErr ?? Diagnostic.Error(DiagnosticCodes.InvalidArgument, $"Invalid document path '{documentPath}'.") });
        }

        // 4. Verify containment and reparse points before opening target
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

        // 5. Check fragment size limit
        if (Encoding.UTF8.GetByteCount(fragmentXml) > XPathQueryLimits.MaxDocumentBytes)
        {
            return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.LimitExceeded, $"Appended XML fragment exceeds maximum allowed size of {XPathQueryLimits.MaxDocumentBytes} bytes.", normDocPath) });
        }

        // 6. Secure parse of XML fragment (must be exactly one complete root element, no DTD)
        XElement fragmentElement;
        try
        {
            using var sr = new StringReader(fragmentXml);
            using var reader = SecureXmlReaderFactory.CreateReader(sr);
            var fragmentDoc = XDocument.Load(reader, LoadOptions.PreserveWhitespace | LoadOptions.SetLineInfo);
            if (fragmentDoc.Root == null)
            {
                return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.XmlParseError, "Appended fragment contains no XML root element.", normDocPath) });
            }
            fragmentElement = fragmentDoc.Root;
        }
        catch (XmlException xmlEx)
        {
            var code = xmlEx.Message.Contains("DTD", StringComparison.OrdinalIgnoreCase)
                ? DiagnosticCodes.DtdProhibited
                : DiagnosticCodes.XmlParseError;
            return (false, null, new[] { Diagnostic.Error(code, $"Failed to parse XML fragment: {xmlEx.Message}", normDocPath, xmlEx.LineNumber, xmlEx.LinePosition) });
        }
        catch (Exception ex)
        {
            return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.XmlParseError, $"Failed to parse XML fragment: {ex.Message}", normDocPath) });
        }

        // 7. Validate root element ID and time-first grammar
        var submittedId = fragmentElement.Attribute("id")?.Value;
        if (string.IsNullOrWhiteSpace(submittedId))
        {
            return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.InvalidIdGrammar, $"Root appended element '<{fragmentElement.Name.LocalName}>' must have an 'id' attribute.", normDocPath) });
        }

        if (!ProjectSemanticIndex.IsValidTimeFirstId(submittedId))
        {
            return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.InvalidIdGrammar, $"Identifier '{submittedId}' does not conform to the time-first ID grammar (YYYYMMDD-name or YYYYMMDDThhmmssZ-name).", normDocPath) });
        }

        // Reject fragment containing operation_id attribute (cannot spoof task update receipts)
        if (fragmentElement.DescendantsAndSelf().Any(e => e.Attribute("operation_id") != null))
        {
            return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.InvalidArgument, "Appended XML fragment cannot contain an 'operation_id' attribute. Operation receipts can only be created via 'task update'.", normDocPath) });
        }

        // 8. Read target document
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

        var root = targetDoc.Root;
        if (root == null)
        {
            return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.XmlParseError, $"Document '{normDocPath}' has no root element.", normDocPath) });
        }

        var revStr = root.Attribute("revision")?.Value;
        if (string.IsNullOrWhiteSpace(revStr) || !int.TryParse(revStr, CultureInfo.InvariantCulture, out var actualRevision) || actualRevision <= 0)
        {
            return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.XmlParseError, $"Document '{normDocPath}' root revision attribute is missing, non-positive, or malformed.", normDocPath) });
        }

        // 9. Evaluate parent XPath against target document
        var evalContext = new XPathEvaluationContext();
        var xsltContext = new DogdouXsltContext(variables, evalContext);

        XPathExpression xpathExpr;
        try
        {
            xpathExpr = XPathExpression.Compile(parentXPath);
            xpathExpr.SetContext(xsltContext);
        }
        catch (DogdouXPathException dxEx)
        {
            return (false, null, new[] { dxEx.ToDiagnostic() });
        }
        catch (Exception ex)
        {
            return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.InvalidArgument, $"Invalid parent XPath expression '{parentXPath}': {ex.Message}", normDocPath) });
        }

        object evalResult;
        try
        {
            var nav = targetDoc.CreateNavigator();
            evalResult = nav.Evaluate(xpathExpr);
        }
        catch (DogdouXPathException dxEx)
        {
            return (false, null, new[] { dxEx.ToDiagnostic() });
        }
        catch (Exception ex)
        {
            return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.InvalidArgument, $"Parent XPath evaluation failed in '{normDocPath}': {ex.Message}", normDocPath) });
        }

        if (evalContext.Derived)
        {
            return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.InvalidArgument, "Parent XPath cannot use projection functions (ds:filter, ds:filter-out) as mutation addresses.", normDocPath) });
        }

        if (evalResult is not XPathNodeIterator iterator)
        {
            return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.InvalidArgument, $"Parent XPath expression '{parentXPath}' evaluated to a scalar value ({evalResult?.GetType().Name}), not an element.", normDocPath) });
        }

        var matchingElements = new List<XElement>();
        var it = iterator.Clone();
        while (it.MoveNext())
        {
            if (it.Current != null)
            {
                if (it.Current.NodeType != XPathNodeType.Element)
                {
                    return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.InvalidArgument, $"Parent XPath expression '{parentXPath}' selected a non-element node ({it.Current.NodeType}). Expected exactly 1 element.", normDocPath) });
                }

                if (it.Current.UnderlyingObject is XElement xElem)
                {
                    matchingElements.Add(xElem);
                }
            }
        }

        if (matchingElements.Count == 0)
        {
            return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.CardinalityConflict, $"Parent XPath expression '{parentXPath}' selected 0 elements in document '{normDocPath}'. Expected exactly 1 element.", normDocPath) });
        }

        if (matchingElements.Count > 1)
        {
            return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.CardinalityConflict, $"Parent XPath expression '{parentXPath}' selected {matchingElements.Count} elements in document '{normDocPath}'. Expected exactly 1 element.", normDocPath) });
        }

        var parentElement = matchingElements[0];

        // 11. Idempotency and Project-Wide Uniqueness Check
        var (enumSuccess, allDocs, enumDiags) = WorkspaceDiscovery.EnumerateDocuments(workspaceRoot);
        if (!enumSuccess || enumDiags.Count > 0)
        {
            return (false, null, enumDiags);
        }

        var occurrences = new List<(ManagedDocument Doc, XElement Element)>();
        foreach (var doc in allDocs)
        {
            try
            {
                using var fs = File.OpenRead(doc.FullPath);
                using var r = SecureXmlReaderFactory.CreateReader(fs);
                var xDoc = XDocument.Load(r);
                var found = xDoc.Descendants().Where(e => string.Equals((string?)e.Attribute("id"), submittedId, StringComparison.Ordinal));
                foreach (var elem in found)
                {
                    occurrences.Add((doc, elem));
                }
            }
            catch
            {
                // Unreadable files will be caught by prospective validation
            }
        }

        if (occurrences.Count > 0)
        {
            // If it exists in multiple places or in a different document
            if (occurrences.Count > 1 || !string.Equals(occurrences[0].Doc.RelativePath, normDocPath, StringComparison.Ordinal))
            {
                return (false, null, new[] { Diagnostic.Error(
                    DiagnosticCodes.IdempotencyConflict,
                    $"Element with ID '{submittedId}' already exists in document '{occurrences[0].Doc.RelativePath}'.",
                    normDocPath) });
            }

            var existingInDoc = occurrences[0].Element;
            var existingParent = existingInDoc.Parent;

            if (existingParent == null || !IsSameElementContext(existingParent, parentElement))
            {
                return (false, null, new[] { Diagnostic.Error(
                    DiagnosticCodes.IdempotencyConflict,
                    $"Element with ID '{submittedId}' already exists under a different parent in '{normDocPath}'.",
                    normDocPath) });
            }

            if (!AreElementsCanonicallyEqual(existingInDoc, fragmentElement))
            {
                return (false, null, new[] { Diagnostic.Error(
                    DiagnosticCodes.IdempotencyConflict,
                    $"Element with ID '{submittedId}' already exists under the target parent but with different XML content.",
                    normDocPath) });
            }

            // It is an already applied idempotent retry!
            // Validate expected revision: retry may supply current revision (actualRevision) or pre-commit revision (actualRevision - 1)
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
                "append",
                new[] { new MutatedDocument(normDocPath, actualRevision) },
                alreadyApplied: true);
            return (true, alreadyAppliedEnv, Array.Empty<Diagnostic>());
        }

        // If ID does not exist anywhere in the project, revision must match actualRevision exactly
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

        // 12. Perform mutation on in-memory target document
        var originalDoc = new XDocument(targetDoc);
        parentElement.Add(fragmentElement);

        // 12a. Protected-state resolver (Authority gate, exit 5) - post-mutation whole-doc comparison
        var protectedDiag = ProtectedStateGuard.CheckProtectedState(normDocPath, originalDoc, targetDoc);
        if (protectedDiag != null)
        {
            return (false, null, new[] { protectedDiag });
        }

        var newRevision = actualRevision + 1;
        root.SetAttributeValue("revision", newRevision.ToString(CultureInfo.InvariantCulture));

        if (root.Attribute("updated_at") != null)
        {
            root.SetAttributeValue("updated_at", clock.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture));
        }

        var replacementContent = ManagedDocumentSerializer.Serialize(targetDoc);

        // 13. Commit atomically via WorkspaceTransactionCommitter
        var operation = new TransactionDocumentOperation(
            normDocPath,
            replacementContent,
            actualRevision,
            newRevision);

        return WorkspaceTransactionCommitter.Commit(
            workspaceRoot,
            "append",
            new[] { operation },
            clock,
            faultInjector,
            version);
    }


    public static bool IsSameElementContext(XElement a, XElement b)
    {
        if (!string.Equals(a.Name.LocalName, b.Name.LocalName, StringComparison.Ordinal))
        {
            return false;
        }

        var idA = a.Attribute("id")?.Value;
        var idB = b.Attribute("id")?.Value;
        if (!string.IsNullOrEmpty(idA) || !string.IsNullOrEmpty(idB))
        {
            return string.Equals(idA, idB, StringComparison.Ordinal);
        }

        var lineageA = a.AncestorsAndSelf().Select(x => (x.Name.LocalName, (string?)x.Attribute("id"))).ToList();
        var lineageB = b.AncestorsAndSelf().Select(x => (x.Name.LocalName, (string?)x.Attribute("id"))).ToList();

        if (lineageA.Count != lineageB.Count) return false;

        for (var i = 0; i < lineageA.Count; i++)
        {
            if (!string.Equals(lineageA[i].LocalName, lineageB[i].LocalName, StringComparison.Ordinal) ||
                !string.Equals(lineageA[i].Item2, lineageB[i].Item2, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    public static bool AreElementsCanonicallyEqual(XElement a, XElement b)
    {
        var canonA = ToCanonicalXmlString(a);
        var canonB = ToCanonicalXmlString(b);
        return string.Equals(canonA, canonB, StringComparison.Ordinal);
    }

    public static string ToCanonicalXmlString(XElement elem)
    {
        var sb = new StringBuilder();
        SerializeCanonical(elem, sb);
        return sb.ToString();
    }

    private static void SerializeCanonical(XElement elem, StringBuilder sb)
    {
        sb.Append('<').Append(elem.Name.LocalName);

        foreach (var attr in elem.Attributes().OrderBy(a => a.Name.LocalName, StringComparer.Ordinal))
        {
            sb.Append(' ')
              .Append(attr.Name.LocalName)
              .Append("=\"")
              .Append(System.Security.SecurityElement.Escape(attr.Value))
              .Append('"');
        }

        var childElements = elem.Elements().ToList();
        if (childElements.Count == 0 && string.IsNullOrWhiteSpace(elem.Value))
        {
            sb.Append("/>");
            return;
        }

        sb.Append('>');

        if (childElements.Count > 0)
        {
            foreach (var child in childElements)
            {
                SerializeCanonical(child, sb);
            }
        }
        else
        {
            var text = elem.Value.Replace("\r\n", "\n").Replace("\r", "\n");
            sb.Append(System.Security.SecurityElement.Escape(text));
        }

        sb.Append("</").Append(elem.Name.LocalName).Append('>');
    }
}
