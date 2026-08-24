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

            if (string.IsNullOrWhiteSpace(memberRaw) || !MemberPattern.IsMatch(memberRaw) || memberRaw.Contains(':'))
            {
                throw new DogdouXPathException(
                    DiagnosticCodes.InvalidArgument,
                    $"Invalid member argument '{memberRaw}'. Members must be exactly '@attribute-name' or 'direct-child-name' without paths, predicates, axes, wildcards, or prefixes.");
            }

            if (memberRaw.StartsWith('@'))
            {
                var attrName = memberRaw.Substring(1);
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
            }
            else
            {
                try
                {
                    XmlConvert.VerifyNCName(memberRaw);
                }
                catch (XmlException)
                {
                    throw new DogdouXPathException(
                        DiagnosticCodes.InvalidArgument,
                        $"Child element member name '{memberRaw}' is not a valid XML NCName.");
                }
                targetChildElements.Add(memberRaw);
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

            var projectedElem = ProjectElement(sourceElem, targetAttributes, targetChildElements, _isFilterOut);
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
        HashSet<string> targetChildElements,
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
                if (!targetAttributes.Contains(attr.Name.LocalName))
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
                    if (!targetChildElements.Contains(child.Name.LocalName))
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
}
