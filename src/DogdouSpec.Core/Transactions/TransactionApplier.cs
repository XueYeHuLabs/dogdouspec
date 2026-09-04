using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using System.Xml.Schema;
using System.Xml.XPath;
using DogdouSpec.Core.Append;
using DogdouSpec.Core.Diagnostics;
using DogdouSpec.Core.Resources;
using DogdouSpec.Core.Security;
using DogdouSpec.Core.Serialization;
using DogdouSpec.Core.Time;
using DogdouSpec.Core.Validation;
using DogdouSpec.Core.Workspace;
using DogdouSpec.Core.XPath;

namespace DogdouSpec.Core.Transactions;

/// <summary>
/// Authoritative execution engine for low-level XML transactions.
/// Validates requests against requests.xsd, processes variables, executes assertions and mutating operations
/// sequentially per document working tree, enforces revision ownership, checks resolved-state authority guards,
/// validates prospectively against the entire workspace, and commits atomically via WorkspaceTransactionCommitter.
/// </summary>
public static class TransactionApplier
{
    private static readonly UTF8Encoding Utf8NoBom = new(false);
    private static readonly Regex VariableNameRegex = new(@"^[a-z][a-z0-9_]*$", RegexOptions.Compiled);
    private const int MaxVariables = 256;
    private const int MaxDocuments = 64;
    private const int MaxOperationsPerDocument = 1_024;
    private const int MaxTotalOperations = 4_096;

    public static (bool Success, MutationEnvelope? Envelope, IReadOnlyList<Diagnostic> Diagnostics) Apply(
        string workspaceRoot,
        string requestXml,
        IClock? clock = null,
        IFaultInjector? faultInjector = null,
        string version = "1.0")
    {
        clock ??= SystemClock.Instance;

        // 1. Validate basic inputs
        if (string.IsNullOrWhiteSpace(requestXml))
        {
            return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.InvalidArgument, "Transaction XML request cannot be empty.") });
        }

        if (Encoding.UTF8.GetByteCount(requestXml) > XPathQueryLimits.MaxDocumentBytes)
        {
            return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.LimitExceeded, $"Transaction XML request exceeds maximum allowed size of {XPathQueryLimits.MaxDocumentBytes} bytes.") });
        }

        // 2. Validate workspace directory security
        var (isWsSafe, wsErr) = PathSecurity.VerifyWorkspaceDirectorySecurity(workspaceRoot);
        if (!isWsSafe || wsErr != null)
        {
            return (false, null, new[] { wsErr ?? Diagnostic.Error(DiagnosticCodes.PathEscapeDetected, "Workspace directory security verification failed.") });
        }

        // 3. Secure parse and validate against requests.xsd
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
                    ? Diagnostic.Error(code, args.Message, null, line, col)
                    : Diagnostic.Warning(code, args.Message, null, line, col);

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
            string code;
            if (xmlEx.Message.Contains("DTD", StringComparison.OrdinalIgnoreCase))
            {
                code = DiagnosticCodes.DtdProhibited;
            }
            else if (xmlEx.Message.Contains("characters in the document", StringComparison.OrdinalIgnoreCase) ||
                     xmlEx.Message.Contains("MaxCharactersInDocument", StringComparison.OrdinalIgnoreCase) ||
                     (xmlEx.Message.Contains("limit", StringComparison.OrdinalIgnoreCase) && xmlEx.Message.Contains("exceeded", StringComparison.OrdinalIgnoreCase)))
            {
                code = DiagnosticCodes.LimitExceeded;
            }
            else
            {
                code = DiagnosticCodes.XmlParseError;
            }

            return (false, null, new[] { Diagnostic.Error(code, $"Failed to parse transaction XML request: {xmlEx.Message}", null, xmlEx.LineNumber, xmlEx.LinePosition) });
        }
        catch (Exception ex)
        {
            return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.XmlParseError, $"Failed to parse transaction XML request: {ex.Message}") });
        }

        if (schemaDiagnostics.Any(d => d.Severity == "error"))
        {
            return (false, null, schemaDiagnostics);
        }

        var reqRoot = requestDoc.Root;
        if (reqRoot == null || !string.Equals(reqRoot.Name.LocalName, "transaction", StringComparison.Ordinal))
        {
            return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.InvalidArgument, "Transaction request must have a <transaction> root element.") });
        }

        // 4. Validate operation_id
        var operationId = reqRoot.Attribute("operation_id")?.Value;
        if (string.IsNullOrWhiteSpace(operationId))
        {
            return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.InvalidArgument, "Transaction request must have an 'operation_id' attribute.") });
        }

        if (!ProjectSemanticIndex.IsValidTimeFirstId(operationId))
        {
            return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.InvalidIdGrammar, $"Transaction operation_id '{operationId}' does not conform to the time-first ID grammar (YYYYMMDD-name or YYYYMMDDThhmmssZ-name).") });
        }

        // 5. Parse and validate variables
        var variables = new Dictionary<string, string>(StringComparer.Ordinal);
        var variablesElem = reqRoot.Element("variables");
        if (variablesElem != null)
        {
            foreach (var varElem in variablesElem.Elements("variable"))
            {
                if (variables.Count >= MaxVariables)
                {
                    return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.LimitExceeded, $"Transaction contains more than the maximum {MaxVariables} variables.") });
                }

                var name = varElem.Attribute("name")?.Value;
                if (string.IsNullOrWhiteSpace(name) || !VariableNameRegex.IsMatch(name))
                {
                    return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.InvalidArgument, $"Variable name '{name}' is invalid. Variable names must match [a-z][a-z0-9_]*.") });
                }

                if (variables.ContainsKey(name))
                {
                    return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.InvalidArgument, $"Duplicate variable name '{name}'. Each variable may only be bound once.") });
                }

                variables[name] = varElem.Value;
            }
        }

        // 6. Validate document entries
        var docElements = reqRoot.Elements("document").ToList();
        if (docElements.Count == 0)
        {
            return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.InvalidArgument, "Transaction must contain at least one document entry.") });
        }

        if (docElements.Count > MaxDocuments)
        {
            return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.LimitExceeded, $"Transaction contains {docElements.Count} documents, exceeding the maximum {MaxDocuments}.") });
        }

        var seenDocPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var docPlans = new List<(string NormalizedPath, string FullPath, int ExpectedRevision, XElement DocElement)>();
        var totalOperationCount = 0;

        foreach (var docElem in docElements)
        {
            var rawPath = docElem.Attribute("path")?.Value;
            if (string.IsNullOrWhiteSpace(rawPath))
            {
                return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.InvalidArgument, "Document entry must specify a 'path' attribute.") });
            }

            var (isRelValid, normPath, relErr) = PathSecurity.ValidateRelativeDocumentPath(rawPath);
            if (!isRelValid || relErr != null)
            {
                return (false, null, new[] { relErr ?? Diagnostic.Error(DiagnosticCodes.InvalidArgument, $"Invalid document path '{rawPath}'.") });
            }

            if (!seenDocPaths.Add(normPath))
            {
                return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.InvalidArgument, $"Duplicate document path '{normPath}' in transaction.") });
            }

            var expRevStr = docElem.Attribute("expected_revision")?.Value;
            if (string.IsNullOrWhiteSpace(expRevStr) || !int.TryParse(expRevStr, CultureInfo.InvariantCulture, out var expectedRev) || expectedRev <= 0)
            {
                return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.InvalidArgument, $"Expected revision for '{normPath}' must be a positive integer, but got '{expRevStr}'.", normPath) });
            }

            var opElements = docElem.Elements().ToList();
            if (opElements.Count == 0)
            {
                return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.InvalidArgument, $"Document '{normPath}' contains no operations.", normPath) });
            }

            if (opElements.Count > MaxOperationsPerDocument)
            {
                return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.LimitExceeded, $"Document '{normPath}' contains {opElements.Count} operations, exceeding the per-document maximum {MaxOperationsPerDocument}.", normPath) });
            }

            totalOperationCount += opElements.Count;
            if (totalOperationCount > MaxTotalOperations)
            {
                return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.LimitExceeded, $"Transaction contains more than the maximum {MaxTotalOperations} total operations.") });
            }

            var fullPath = Path.Combine(workspaceRoot, normPath.Replace('/', Path.DirectorySeparatorChar));
            var (isContained, contErr) = PathSecurity.CheckContainmentAndReparsePoints(workspaceRoot, fullPath);
            if (!isContained || contErr != null)
            {
                return (false, null, new[] { contErr ?? Diagnostic.Error(DiagnosticCodes.PathEscapeDetected, $"Target path escapes workspace: '{normPath}'.") });
            }

            if (!File.Exists(fullPath))
            {
                return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.DocumentNotFound, $"Target document '{normPath}' does not exist in workspace.", normPath) });
            }

            if (new FileInfo(fullPath).Length > XPathQueryLimits.MaxDocumentBytes)
            {
                return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.LimitExceeded, $"Document '{normPath}' exceeds maximum allowed size of {XPathQueryLimits.MaxDocumentBytes} bytes.", normPath) });
            }

            docPlans.Add((normPath, fullPath, expectedRev, docElem));
        }

        // 7. Anti-spoofing check on payloads across all operations
        foreach (var plan in docPlans)
        {
            foreach (var opElem in plan.DocElement.Elements())
            {
                var opName = opElem.Name.LocalName;
                if (opName is "append-child" or "replace-node")
                {
                    var childElems = opElem.Elements().ToList();
                    if (childElems.Count != 1)
                    {
                        return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.InvalidArgument, $"Payload for '{opName}' in '{plan.NormalizedPath}' must contain exactly one root element.", plan.NormalizedPath) });
                    }

                    if (childElems[0].DescendantsAndSelf().Any(e => e.Attribute("operation_id") != null))
                    {
                        return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.InvalidArgument, "Payload cannot contain an 'operation_id' attribute. Operation receipts can only be created via 'task update'.", plan.NormalizedPath) });
                    }
                }
                else if (opName == "set-attribute")
                {
                    var attrName = opElem.Attribute("name")?.Value;
                    if (string.Equals(attrName, "operation_id", StringComparison.OrdinalIgnoreCase))
                    {
                        return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.InvalidArgument, "Cannot set 'operation_id' attribute via transaction. Operation receipts can only be created via 'task update'.", plan.NormalizedPath) });
                    }
                    if (string.Equals(attrName, "revision", StringComparison.OrdinalIgnoreCase))
                    {
                        return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.InvalidArgument, "Cannot set 'revision' attribute directly. The engine owns document root revision.", plan.NormalizedPath) });
                    }
                }
            }
        }

        // 8. Load documents and check expected revisions
        var loadedDocs = new List<(string NormPath, string FullPath, int ExpectedRevision, XDocument OriginalDoc, XDocument WorkingDoc, XElement DocElement)>();

        foreach (var plan in docPlans)
        {
            XDocument targetDoc;
            try
            {
                using var fs = File.OpenRead(plan.FullPath);
                using var reader = SecureXmlReaderFactory.CreateReader(fs, baseUri: "dogdou://managed/" + plan.NormalizedPath);
                targetDoc = XDocument.Load(reader, LoadOptions.PreserveWhitespace | LoadOptions.SetLineInfo | LoadOptions.SetBaseUri);
            }
            catch (XmlException xmlEx)
            {
                var code = xmlEx.Message.Contains("DTD", StringComparison.OrdinalIgnoreCase)
                    ? DiagnosticCodes.DtdProhibited
                    : DiagnosticCodes.XmlParseError;
                return (false, null, new[] { Diagnostic.Error(code, $"Failed to parse XML document '{plan.NormalizedPath}': {xmlEx.Message}", plan.NormalizedPath, xmlEx.LineNumber, xmlEx.LinePosition) });
            }
            catch (Exception ex)
            {
                return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.XmlParseError, $"Failed to read XML document '{plan.NormalizedPath}': {ex.Message}", plan.NormalizedPath) });
            }

            var root = targetDoc.Root;
            if (root == null)
            {
                return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.XmlParseError, $"Document '{plan.NormalizedPath}' has no root element.", plan.NormalizedPath) });
            }

            var revStr = root.Attribute("revision")?.Value;
            if (string.IsNullOrWhiteSpace(revStr) || !int.TryParse(revStr, CultureInfo.InvariantCulture, out var actualRev) || actualRev <= 0)
            {
                return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.XmlParseError, $"Document '{plan.NormalizedPath}' root revision attribute is missing, non-positive, or malformed.", plan.NormalizedPath) });
            }

            if (actualRev != plan.ExpectedRevision)
            {
                var diag = new Diagnostic(
                    DiagnosticCodes.RevisionConflict,
                    "error",
                    $"Expected revision {plan.ExpectedRevision} does not match actual revision {actualRev} for document '{plan.NormalizedPath}'.",
                    Document: plan.NormalizedPath,
                    ExpectedRevision: plan.ExpectedRevision,
                    ActualRevision: actualRev);
                return (false, null, new[] { diag });
            }

            var origDoc = new XDocument(targetDoc);
            loadedDocs.Add((plan.NormalizedPath, plan.FullPath, plan.ExpectedRevision, origDoc, targetDoc, plan.DocElement));
        }

        // 9. Execute sequential operations per document working tree
        foreach (var item in loadedDocs)
        {
            var normPath = item.NormPath;
            var workingDoc = item.WorkingDoc;

            foreach (var opElem in item.DocElement.Elements())
            {
                var opName = opElem.Name.LocalName;
                var evalContext = new XPathEvaluationContext();
                var xsltContext = new DogdouXsltContext(variables, evalContext);

                if (opName == "assert")
                {
                    var test = opElem.Attribute("test")?.Value;
                    if (string.IsNullOrWhiteSpace(test))
                    {
                        return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.InvalidArgument, "assert operation must specify a 'test' attribute.", normPath) });
                    }

                    XPathExpression xpathExpr;
                    try
                    {
                        xpathExpr = XPathExpression.Compile(test);
                        xpathExpr.SetContext(xsltContext);
                    }
                    catch (DogdouXPathException dxEx)
                    {
                        return (false, null, new[] { dxEx.ToDiagnostic() });
                    }
                    catch (Exception ex)
                    {
                        return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.InvalidArgument, $"Invalid assert test XPath '{test}': {ex.Message}", normPath) });
                    }

                    object evalResult;
                    try
                    {
                        var nav = workingDoc.CreateNavigator();
                        evalResult = nav.Evaluate(xpathExpr);
                    }
                    catch (DogdouXPathException dxEx)
                    {
                        return (false, null, new[] { dxEx.ToDiagnostic() });
                    }
                    catch (Exception ex)
                    {
                        return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.InvalidArgument, $"Assert test XPath evaluation failed in '{normPath}': {ex.Message}", normPath) });
                    }

                    var isTrue = ToEffectiveBooleanValue(evalResult);
                    if (!isTrue)
                    {
                        return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.CardinalityConflict, $"Assertion failed: XPath test '{test}' evaluated to false in document '{normPath}'.", normPath) });
                    }
                }
                else if (opName is "append-child" or "replace-node" or "set-attribute" or "remove-node")
                {
                    var select = opElem.Attribute("select")?.Value;
                    if (string.IsNullOrWhiteSpace(select))
                    {
                        return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.InvalidArgument, $"{opName} operation must specify a 'select' attribute.", normPath) });
                    }

                    var expStr = opElem.Attribute("expect")?.Value;
                    if (string.IsNullOrWhiteSpace(expStr) || !int.TryParse(expStr, CultureInfo.InvariantCulture, out var expect) || expect < 0)
                    {
                        return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.InvalidArgument, $"{opName} operation must specify a non-negative integer 'expect' attribute, got '{expStr}'.", normPath) });
                    }

                    XPathExpression xpathExpr;
                    try
                    {
                        xpathExpr = XPathExpression.Compile(select);
                        xpathExpr.SetContext(xsltContext);
                    }
                    catch (DogdouXPathException dxEx)
                    {
                        return (false, null, new[] { dxEx.ToDiagnostic() });
                    }
                    catch (Exception ex)
                    {
                        return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.InvalidArgument, $"Invalid selector XPath '{select}': {ex.Message}", normPath) });
                    }

                    object evalResult;
                    try
                    {
                        var nav = workingDoc.CreateNavigator();
                        evalResult = nav.Evaluate(xpathExpr);
                    }
                    catch (DogdouXPathException dxEx)
                    {
                        return (false, null, new[] { dxEx.ToDiagnostic() });
                    }
                    catch (Exception ex)
                    {
                        return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.InvalidArgument, $"Selector XPath evaluation failed in '{normPath}': {ex.Message}", normPath) });
                    }

                    if (evalContext.Derived)
                    {
                        return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.InvalidArgument, $"Mutating selector '{select}' cannot use projection functions (ds:filter, ds:filter-out) as mutation addresses.", normPath) });
                    }

                    if (evalResult is not XPathNodeIterator iterator)
                    {
                        return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.InvalidArgument, $"Mutating selector '{select}' evaluated to a scalar value ({evalResult?.GetType().Name}), not a node-set.", normPath) });
                    }

                    var matchedItems = new List<(XPathNodeType NodeType, object UnderlyingObject, string Name)>();
                    var it = iterator.Clone();
                    while (it.MoveNext())
                    {
                        if (it.Current != null && it.Current.UnderlyingObject != null)
                        {
                            matchedItems.Add((it.Current.NodeType, it.Current.UnderlyingObject, it.Current.LocalName));
                            if (matchedItems.Count > XPathQueryLimits.MaxResultNodes)
                            {
                                return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.LimitExceeded, $"Selector '{select}' matched more than the maximum {XPathQueryLimits.MaxResultNodes} nodes in document '{normPath}'.", normPath) });
                            }
                        }
                    }

                    if (matchedItems.Count != expect)
                    {
                        return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.CardinalityConflict, $"Selector '{select}' matched {matchedItems.Count} nodes in document '{normPath}', but expected {expect}.", normPath) });
                    }

                    if (expect > 0)
                    {
                        if (opName == "append-child")
                        {
                            foreach (var m in matchedItems)
                            {
                                if (m.NodeType != XPathNodeType.Element || m.UnderlyingObject is not XElement)
                                {
                                    return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.InvalidArgument, $"append-child selector '{select}' selected non-element node ({m.NodeType}). append-child targets elements only.", normPath) });
                                }
                            }

                            var payload = opElem.Elements().First();
                            foreach (var m in matchedItems)
                            {
                                ((XElement)m.UnderlyingObject).Add(new XElement(payload));
                            }
                        }
                        else if (opName == "replace-node")
                        {
                            foreach (var m in matchedItems)
                            {
                                if (m.NodeType != XPathNodeType.Element || m.UnderlyingObject is not XElement elem)
                                {
                                    return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.InvalidArgument, $"replace-node selector '{select}' selected non-element node ({m.NodeType}). replace-node targets non-root elements only.", normPath) });
                                }

                                if (elem.Parent == null || elem == workingDoc.Root)
                                {
                                    return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.InvalidArgument, $"replace-node cannot replace document root element in '{normPath}'.", normPath) });
                                }
                            }

                            var payload = opElem.Elements().First();
                            foreach (var m in matchedItems)
                            {
                                var elem = (XElement)m.UnderlyingObject;
                                if (elem.Parent != null)
                                {
                                    elem.ReplaceWith(new XElement(payload));
                                }
                            }
                        }
                        else if (opName == "set-attribute")
                        {
                            foreach (var m in matchedItems)
                            {
                                if (m.NodeType != XPathNodeType.Element || m.UnderlyingObject is not XElement)
                                {
                                    return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.InvalidArgument, $"set-attribute selector '{select}' selected non-element node ({m.NodeType}). set-attribute targets elements only.", normPath) });
                                }
                            }

                            var attrName = opElem.Attribute("name")?.Value;
                            var attrValue = opElem.Attribute("value")?.Value ?? string.Empty;

                            if (string.IsNullOrWhiteSpace(attrName))
                            {
                                return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.InvalidArgument, "set-attribute operation must specify a 'name' attribute.", normPath) });
                            }

                            if (string.Equals(attrName, "revision", StringComparison.OrdinalIgnoreCase))
                            {
                                return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.InvalidArgument, "Cannot set 'revision' attribute directly. The engine owns document root revision.", normPath) });
                            }

                            if (string.Equals(attrName, "operation_id", StringComparison.OrdinalIgnoreCase))
                            {
                                return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.InvalidArgument, "Cannot set 'operation_id' attribute. Operation receipts can only be created via 'task update'.", normPath) });
                            }

                            foreach (var m in matchedItems)
                            {
                                ((XElement)m.UnderlyingObject).SetAttributeValue(attrName, attrValue);
                            }
                        }
                        else if (opName == "remove-node")
                        {
                            foreach (var m in matchedItems)
                            {
                                if (m.NodeType == XPathNodeType.Root)
                                {
                                    return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.InvalidArgument, $"remove-node cannot remove document root in '{normPath}'.", normPath) });
                                }

                                if (m.NodeType == XPathNodeType.Element)
                                {
                                    var elem = (XElement)m.UnderlyingObject;
                                    if (elem.Parent == null || elem == workingDoc.Root)
                                    {
                                        return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.InvalidArgument, $"remove-node cannot remove document root element in '{normPath}'.", normPath) });
                                    }
                                }
                                else if (m.NodeType == XPathNodeType.Attribute)
                                {
                                    if (string.Equals(m.Name, "revision", StringComparison.OrdinalIgnoreCase))
                                    {
                                        return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.InvalidArgument, $"remove-node cannot remove 'revision' attribute in '{normPath}'. The engine owns document root revision.", normPath) });
                                    }
                                    if (string.Equals(m.Name, "operation_id", StringComparison.OrdinalIgnoreCase))
                                    {
                                        return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.IdempotencyConflict, $"remove-node cannot remove an 'operation_id' receipt in '{normPath}'. Task update receipts are durable.", normPath) });
                                    }
                                }
                                else
                                {
                                    return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.InvalidArgument, $"remove-node selector '{select}' selected unsupported node type '{m.NodeType}'. remove-node targets non-root elements or attributes only.", normPath) });
                                }
                            }

                            foreach (var m in matchedItems)
                            {
                                if (m.NodeType == XPathNodeType.Attribute && m.UnderlyingObject is XAttribute attr)
                                {
                                    attr.Remove();
                                }
                                else if (m.NodeType == XPathNodeType.Element && m.UnderlyingObject is XElement elem)
                                {
                                    if (elem.Parent != null)
                                    {
                                        elem.Remove();
                                    }
                                }
                            }
                        }
                    }
                }
                else
                {
                    return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.InvalidArgument, $"Unknown transaction operation '<{opName}>'.", normPath) });
                }
            }
        }

        // 10. Check semantic changes and prepare replacement documents
        var changedOps = new List<TransactionDocumentOperation>();
        var changedTrees = new List<(string NormPath, XDocument OrigDoc, XDocument WorkingDoc)>();

        foreach (var item in loadedDocs)
        {
            var isChanged = !GenericAppender.AreElementsCanonicallyEqual(item.OriginalDoc.Root!, item.WorkingDoc.Root!);
            if (isChanged)
            {
                var newRev = item.ExpectedRevision + 1;
                item.WorkingDoc.Root!.SetAttributeValue("revision", newRev.ToString(CultureInfo.InvariantCulture));

                if (item.WorkingDoc.Root!.Attribute("updated_at") != null)
                {
                    item.WorkingDoc.Root!.SetAttributeValue("updated_at", clock.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture));
                }

                var replacementContent = ManagedDocumentSerializer.Serialize(item.WorkingDoc);

                changedOps.Add(new TransactionDocumentOperation(
                    item.NormPath,
                    replacementContent,
                    item.ExpectedRevision,
                    newRev));

                changedTrees.Add((item.NormPath, item.OriginalDoc, item.WorkingDoc));
            }
        }

        // 11. If no documents changed semantically, return already_applied success
        if (changedOps.Count == 0)
        {
            var envelope = new MutationEnvelope(
                "transaction apply",
                Array.Empty<MutatedDocument>(),
                alreadyApplied: true);
            return (true, envelope, Array.Empty<Diagnostic>());
        }

        // 12. Run resolved-state authority guard
        foreach (var (normPath, origDoc, workingDoc) in changedTrees)
        {
            var guardDiag = ProtectedStateGuard.CheckProtectedState(normPath, origDoc, workingDoc);
            if (guardDiag != null)
            {
                return (false, null, new[] { guardDiag });
            }
        }

        // 13. Prospective validation across full workspace
        var prospectiveDocs = changedOps.Select(op => new ProspectiveDocument(
            op.RelativePath,
            op.ReplacementContent,
            IsNew: false,
            ExpectedRevision: op.ExpectedRevision)).ToList();

        var prospectiveResult = SchemaValidator.ValidateProspective(workspaceRoot, prospectiveDocs, version);
        if (!prospectiveResult.IsValid)
        {
            return (false, null, prospectiveResult.Diagnostics);
        }

        // 14. Commit atomically via WorkspaceTransactionCommitter
        return WorkspaceTransactionCommitter.Commit(
            workspaceRoot,
            "transaction apply",
            changedOps,
            clock,
            faultInjector,
            version,
            correlationId: operationId);
    }

    private static bool ToEffectiveBooleanValue(object? evalResult)
    {
        if (evalResult is null) return false;
        if (evalResult is bool b) return b;
        if (evalResult is string s) return !string.IsNullOrEmpty(s);
        if (evalResult is double d) return d != 0.0 && !double.IsNaN(d);
        if (evalResult is int i) return i != 0;
        if (evalResult is XPathNodeIterator it)
        {
            var clone = it.Clone();
            return clone.MoveNext();
        }
        if (evalResult is XPathNavigator) return true;
        return false;
    }
}
