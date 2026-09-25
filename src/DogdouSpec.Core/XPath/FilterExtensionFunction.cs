using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using System.Xml.XPath;
using System.Xml.Xsl;
using DogdouSpec.Core.Diagnostics;

namespace DogdouSpec.Core.XPath;

/// <summary>
/// Implements ds:filter and ds:filter-out extension functions for XPath 1.0.
/// </summary>
public sealed class FilterExtensionFunction : IXsltContextFunction
{
    private static readonly Regex MemberPattern = new(@"^(@[a-zA-Z_][a-zA-Z0-9_.-]*|[a-zA-Z_][a-zA-Z0-9_.-]*)$", RegexOptions.Compiled);

    private readonly bool _isFilterOut;
    private readonly XPathEvaluationContext _context;

    public FilterExtensionFunction(bool isFilterOut, XPathEvaluationContext context)
    {
        _isFilterOut = isFilterOut;
        _context = context;
    }

    public int Minargs => 2;
    public int Maxargs => int.MaxValue;
    public XPathResultType ReturnType => XPathResultType.NodeSet;
    public XPathResultType[] ArgTypes => Array.Empty<XPathResultType>();

    public object Invoke(XsltContext xsltContext, object[] args, XPathNavigator docContext)
    {
        _context.Derived = true;

        if (args == null || args.Length < 2)
        {
            var funcName = _isFilterOut ? "ds:filter-out" : "ds:filter";
            throw new DogdouXPathException(
                DiagnosticCodes.InvalidArgument,
                $"Function '{funcName}' requires at least two arguments: a node-set and at least one member name.");
        }

        // Validate member arguments
        var targetAttributes = new HashSet<string>(StringComparer.Ordinal);
        var targetChildElements = new HashSet<string>(StringComparer.Ordinal);
        var targetAttributePredicates = new Dictionary<string, List<XPathExpression>>(StringComparer.Ordinal);
        var targetChildPredicates = new Dictionary<string, List<XPathExpression>>(StringComparer.Ordinal);

        for (var i = 1; i < args.Length; i++)
        {
            var arg = args[i];
            if (arg is not string memberRaw)
            {
                var funcName = _isFilterOut ? "ds:filter-out" : "ds:filter";
                var typeName = arg switch
                {
                    XPathNodeIterator => "node-set",
                    bool => "boolean",
                    double => "number",
                    null => "null",
                    _ => arg.GetType().Name
                };

                throw new DogdouXPathException(
                    DiagnosticCodes.InvalidArgument,
                    $"Member argument at position {i + 1} to {funcName} must be an XPath string (literal or bound string variable), but received {typeName}.");
            }

            if (string.IsNullOrWhiteSpace(memberRaw))
            {
                throw new DogdouXPathException(
                    DiagnosticCodes.InvalidArgument,
                    $"Invalid member argument '{memberRaw}'. Members must be exactly '@attribute-name' or 'direct-child-name' without paths, predicates, axes, wildcards, or prefixes.");
            }

            // Support common inspection idioms: name() and namespace-uri()
            if (string.Equals(memberRaw, "name()", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(memberRaw, "name", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(memberRaw, "namespace-uri()", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(memberRaw, "namespace-uri", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string baseName;
            string? predicateText = null;

            var bracketIdx = memberRaw.IndexOf('[');
            if (bracketIdx >= 0)
            {
                if (!memberRaw.EndsWith(']'))
                {
                    throw new DogdouXPathException(
                        DiagnosticCodes.InvalidArgument,
                        $"Invalid member argument '{memberRaw}'. Unmatched predicate brackets.");
                }
                baseName = memberRaw.Substring(0, bracketIdx);
                predicateText = memberRaw.Substring(bracketIdx + 1, memberRaw.Length - bracketIdx - 2);

                if (!predicateText.Contains('@'))
                {
                    throw new DogdouXPathException(
                        DiagnosticCodes.InvalidArgument,
                        $"Invalid member argument '{memberRaw}'. Positional and non-attribute predicates are not supported.");
                }
            }
            else
            {
                baseName = memberRaw;
            }

            if (baseName.Contains(':'))
            {
                throw new DogdouXPathException(
                    DiagnosticCodes.InvalidArgument,
                    $"Invalid member argument '{memberRaw}'. Namespaces and prefixes are not allowed.");
            }

            if (baseName.StartsWith('@'))
            {
                var attrName = baseName.Substring(1);
                try
                {
                    XmlConvert.VerifyNCName(attrName);
                }
                catch (XmlException)
                {
                    throw new DogdouXPathException(
                        DiagnosticCodes.InvalidArgument,
                        $"Attribute member name '{memberRaw}' is not a valid XML NCName.");
                }
                targetAttributes.Add(attrName);

                if (!string.IsNullOrWhiteSpace(predicateText))
                {
                    try
                    {
                        var expr = XPathExpression.Compile(predicateText);
                        if (xsltContext != null) expr.SetContext(xsltContext);
                        if (!targetAttributePredicates.TryGetValue(attrName, out var list))
                        {
                            list = new List<XPathExpression>();
                            targetAttributePredicates[attrName] = list;
                        }
                        list.Add(expr);
                    }
                    catch (Exception ex)
                    {
                        throw new DogdouXPathException(
                            DiagnosticCodes.InvalidArgument,
                            $"Invalid XPath predicate '{predicateText}' in member '{memberRaw}': {ex.Message}",
                            innerException: ex);
                    }
                }
            }
            else
            {
                try
                {
                    XmlConvert.VerifyNCName(baseName);
                }
                catch (XmlException)
                {
                    throw new DogdouXPathException(
                        DiagnosticCodes.InvalidArgument,
                        $"Child element member name '{memberRaw}' is not a valid XML NCName.");
                }
                targetChildElements.Add(baseName);

                if (!string.IsNullOrWhiteSpace(predicateText))
                {
                    try
                    {
                        var expr = XPathExpression.Compile(predicateText);
                        if (xsltContext != null) expr.SetContext(xsltContext);
                        if (!targetChildPredicates.TryGetValue(baseName, out var list))
                        {
                            list = new List<XPathExpression>();
                            targetChildPredicates[baseName] = list;
                        }
                        list.Add(expr);
                    }
                    catch (Exception ex)
                    {
                        throw new DogdouXPathException(
                            DiagnosticCodes.InvalidArgument,
                            $"Invalid XPath predicate '{predicateText}' in member '{memberRaw}': {ex.Message}",
                            innerException: ex);
                    }
                }
            }
        }

        // Validate first argument
        if (args[0] is not XPathNodeIterator iterator)
        {
            throw new DogdouXPathException(
                DiagnosticCodes.InvalidArgument,
                "First argument to ds:filter/ds:filter-out must be an element node-set.");
        }

        var it = iterator.Clone();
        var projectedNavigators = new List<XPathNavigator>();

        while (it.MoveNext())
        {
            var current = it.Current;
            if (current == null || current.NodeType != XPathNodeType.Element)
            {
                throw new DogdouXPathException(
                    DiagnosticCodes.InvalidArgument,
                    "First argument node-set to ds:filter/ds:filter-out must contain only element nodes.");
            }

            XElement sourceElem;
            if (current.UnderlyingObject is XElement directElem)
            {
                sourceElem = directElem;
            }
            else
            {
                try
                {
                    using var reader = current.ReadSubtree();
                    sourceElem = XElement.Load(reader, LoadOptions.PreserveWhitespace);
                }
                catch (Exception ex)
                {
                    throw new DogdouXPathException(
                        DiagnosticCodes.InvalidArgument,
                        $"Failed to load source element for projection: {ex.Message}",
                        innerException: ex);
                }
            }

            var projectedElem = ProjectElement(sourceElem, targetAttributes, targetAttributePredicates, targetChildElements, targetChildPredicates, _isFilterOut);
            var docUri = $"dogdou://projected/{_context.ProjectedDocSequence++}";
            using var textReader = new StringReader(projectedElem.ToString(SaveOptions.DisableFormatting));
            using var projReader = XmlReader.Create(textReader, (XmlReaderSettings?)null, docUri);
            var projDoc = XDocument.Load(projReader, LoadOptions.PreserveWhitespace | LoadOptions.SetBaseUri);
            projectedNavigators.Add(projDoc.Root!.CreateNavigator());
        }

        return new SequenceXPathNodeIterator(projectedNavigators);
    }

    private XElement ProjectElement(
        XElement sourceElem,
        HashSet<string> targetAttributes,
        Dictionary<string, List<XPathExpression>> targetAttributePredicates,
        HashSet<string> targetChildElements,
        Dictionary<string, List<XPathExpression>> targetChildPredicates,
        bool isFilterOut)
    {
        var proj = new XElement(sourceElem.Name);
        var nodeCount = 1; // Root element

        if (!isFilterOut)
        {
            // ds:filter
            // Retain named direct attributes
            foreach (var attr in sourceElem.Attributes())
            {
                if (targetAttributes.Contains(attr.Name.LocalName))
                {
                    if (targetAttributePredicates.TryGetValue(attr.Name.LocalName, out var aPreds) && aPreds.Count > 0)
                    {
                        var sourceNav = sourceElem.CreateNavigator();
                        if (!aPreds.All(p => EvaluatePredicate(sourceNav, p)))
                        {
                            continue;
                        }
                    }
                    proj.Add(new XAttribute(attr.Name, attr.Value));
                    nodeCount++;
                }
            }

            // Retain direct root text and named direct child element subtrees
            foreach (var node in sourceElem.Nodes())
            {
                if (node is XText text)
                {
                    if (!string.IsNullOrWhiteSpace(text.Value))
                    {
                        proj.Add(new XText(text));
                        nodeCount++;
                    }
                }
                else if (node is XElement child)
                {
                    if (targetChildElements.Contains(child.Name.LocalName))
                    {
                        if (targetChildPredicates.TryGetValue(child.Name.LocalName, out var cPreds) && cPreds.Count > 0)
                        {
                            var childNav = child.CreateNavigator();
                            if (!cPreds.All(p => EvaluatePredicate(childNav, p)))
                            {
                                continue;
                            }
                        }
                        var childClone = new XElement(child);
                        proj.Add(childClone);
                        nodeCount += CountSubtreeNodes(childClone);
                    }
                }
            }
        }
        else
        {
            // ds:filter-out
            // Retain direct attributes not excluded
            foreach (var attr in sourceElem.Attributes())
            {
                var isExcluded = targetAttributes.Contains(attr.Name.LocalName);
                if (isExcluded && targetAttributePredicates.TryGetValue(attr.Name.LocalName, out var aPreds) && aPreds.Count > 0)
                {
                    var sourceNav = sourceElem.CreateNavigator();
                    isExcluded = aPreds.All(p => EvaluatePredicate(sourceNav, p));
                }

                if (!isExcluded)
                {
                    proj.Add(new XAttribute(attr.Name, attr.Value));
                    nodeCount++;
                }
            }

            // Retain direct child nodes not excluded
            foreach (var node in sourceElem.Nodes())
            {
                if (node is XElement child)
                {
                    var isExcluded = targetChildElements.Contains(child.Name.LocalName);
                    if (isExcluded && targetChildPredicates.TryGetValue(child.Name.LocalName, out var cPreds) && cPreds.Count > 0)
                    {
                        var childNav = child.CreateNavigator();
                        isExcluded = cPreds.All(p => EvaluatePredicate(childNav, p));
                    }

                    if (!isExcluded)
                    {
                        var childClone = new XElement(child);
                        proj.Add(childClone);
                        nodeCount += CountSubtreeNodes(childClone);
                    }
                }
                else if (node is XText text)
                {
                    if (!string.IsNullOrWhiteSpace(text.Value))
                    {
                        proj.Add(new XText(text));
                        nodeCount++;
                    }
                }
                else if (node is XComment comment)
                {
                    proj.Add(new XComment(comment));
                    nodeCount++;
                }
                else if (node is XProcessingInstruction pi)
                {
                    proj.Add(new XProcessingInstruction(pi));
                    nodeCount++;
                }
            }
        }

        _context.TrackProjectedNodes(nodeCount);
        return proj;
    }

    private static int CountSubtreeNodes(XElement elem)
    {
        var count = 1; // The element itself
        count += elem.Attributes().Count();
        foreach (var node in elem.Nodes())
        {
            if (node is XElement child)
            {
                count += CountSubtreeNodes(child);
            }
            else
            {
                count++;
            }
        }
        return count;
    }

    private static bool EvaluatePredicate(XPathNavigator nav, XPathExpression expr)
    {
        try
        {
            var res = nav.Evaluate(expr);
            if (res is bool b) return b;
            if (res is XPathNodeIterator it) return it.Count > 0;
            if (res is double d) return d != 0 && !double.IsNaN(d);
            if (res is string s) return !string.IsNullOrEmpty(s);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
