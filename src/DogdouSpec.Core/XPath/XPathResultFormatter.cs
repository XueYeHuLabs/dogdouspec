using System.Globalization;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using System.Xml.XPath;
using DogdouSpec.Core.Diagnostics;

namespace DogdouSpec.Core.XPath;

/// <summary>
/// Formatter for XPath query and search results in XML and Human formats.
/// Enforces deterministic formatting, minimal typed item wrappers, and the 4 MiB output byte limit.
/// </summary>
public static class XPathResultFormatter
{
    private static readonly XmlWriterSettings StandardXmlWriterSettings = new()
    {
        Indent = true,
        IndentChars = "  ",
        OmitXmlDeclaration = false,
        Encoding = new UTF8Encoding(false),
        NewLineHandling = NewLineHandling.Replace,
        NewLineChars = "\n"
    };

    /// <summary>
    /// Deterministic non-empty rule for search results:
    /// - NodeSet: node count > 0
    /// - Boolean: true
    /// - Number: non-zero and not NaN
    /// - String: non-empty string (length > 0)
    /// </summary>
    public static bool IsNonEmpty(XPathQueryResult result) => result.ResultType switch
    {
        XPathResultKind.NodeSet => result.Nodes.Count > 0,
        XPathResultKind.Boolean => result.BooleanValue == true,
        XPathResultKind.Number => result.NumberValue.HasValue && !double.IsNaN(result.NumberValue.Value) && result.NumberValue.Value != 0.0,
        XPathResultKind.String => !string.IsNullOrEmpty(result.StringValue),
        _ => false
    };

    public static string FormatQueryXml(XPathQueryResult result)
    {
        using var ms = new MemoryStream();
        using (var writer = XmlWriter.Create(ms, StandardXmlWriterSettings))
        {
            writer.WriteStartDocument();

            if (result.ResultType == XPathResultKind.NodeSet)
            {
                writer.WriteStartElement("results");
                writer.WriteAttributeString("document", result.DocumentPath);
                writer.WriteAttributeString("revision", result.Revision);
                writer.WriteAttributeString("type", "node-set");
                writer.WriteAttributeString("derived", result.Derived ? "true" : "false");

                var allElements = result.Nodes.Count == 0 || result.Nodes.All(n => n.NodeType == XPathNodeType.Element);
                if (allElements)
                {
                    foreach (var nav in result.Nodes)
                    {
                        WriteNavigatorNode(writer, nav);
                    }
                }
                else
                {
                    foreach (var nav in result.Nodes)
                    {
                        WriteTypedItemWrapper(writer, nav);
                    }
                }

                writer.WriteEndElement();
            }
            else
            {
                writer.WriteStartElement("result");
                writer.WriteAttributeString("document", result.DocumentPath);
                writer.WriteAttributeString("revision", result.Revision);
                writer.WriteAttributeString("type", result.ResultType.ToTypeString());
                writer.WriteAttributeString("derived", result.Derived ? "true" : "false");
                writer.WriteString(result.ScalarValueString);
                writer.WriteEndElement();
            }

            writer.WriteEndDocument();
        }

        var bytes = ms.ToArray();
        if (bytes.Length > XPathQueryLimits.MaxOutputBytes)
        {
            throw new DogdouXPathException(
                DiagnosticCodes.LimitExceeded,
                $"Serialized output size {bytes.Length} bytes exceeded the limit of {XPathQueryLimits.MaxOutputBytes} bytes (4 MiB). Use a narrower XPath expression or structural projection.",
                exitCode: 7);
        }

        return Encoding.UTF8.GetString(bytes) + "\n";
    }

    public static string FormatSearchXml(XPathSearchResult searchResult)
    {
        using var ms = new MemoryStream();
        using (var writer = XmlWriter.Create(ms, StandardXmlWriterSettings))
        {
            writer.WriteStartDocument();
            writer.WriteStartElement("search");
            writer.WriteAttributeString("scope", searchResult.Scope);

            if (!string.IsNullOrEmpty(searchResult.IterationId))
            {
                writer.WriteAttributeString("iteration", searchResult.IterationId);
            }

            writer.WriteAttributeString("derived", searchResult.Derived ? "true" : "false");

            foreach (var docResult in searchResult.DocumentResults)
            {
                writer.WriteStartElement("document");
                writer.WriteAttributeString("path", docResult.DocumentPath);
                writer.WriteAttributeString("revision", docResult.Revision);
                writer.WriteAttributeString("type", docResult.ResultType.ToTypeString());

                if (docResult.ResultType == XPathResultKind.NodeSet)
                {
                    var allElements = docResult.Nodes.Count == 0 || docResult.Nodes.All(n => n.NodeType == XPathNodeType.Element);
                    if (allElements)
                    {
                        foreach (var nav in docResult.Nodes)
                        {
                            WriteNavigatorNode(writer, nav);
                        }
                    }
                    else
                    {
                        foreach (var nav in docResult.Nodes)
                        {
                            WriteTypedItemWrapper(writer, nav);
                        }
                    }
                }
                else
                {
                    writer.WriteString(docResult.ScalarValueString);
                }

                writer.WriteEndElement();
            }

            writer.WriteEndElement();
            writer.WriteEndDocument();
        }

        var bytes = ms.ToArray();
        if (bytes.Length > XPathQueryLimits.MaxOutputBytes)
        {
            throw new DogdouXPathException(
                DiagnosticCodes.LimitExceeded,
                $"Serialized output size {bytes.Length} bytes exceeded the limit of {XPathQueryLimits.MaxOutputBytes} bytes (4 MiB). Use a narrower XPath expression or structural projection.",
                exitCode: 7);
        }

        return Encoding.UTF8.GetString(bytes) + "\n";
    }

    public static string FormatQueryHuman(XPathQueryResult result)
    {
        var sb = new StringBuilder();
        sb.AppendLine(CultureInfo.InvariantCulture, $"Document: {result.DocumentPath} (revision: {result.Revision}, derived: {result.Derived.ToString().ToLowerInvariant()})");

        if (result.ResultType == XPathResultKind.NodeSet)
        {
            var count = result.Nodes.Count;
            sb.AppendLine(CultureInfo.InvariantCulture, $"Result: {count} {(count == 1 ? "node" : "nodes")}");

            if (count > 0)
            {
                sb.AppendLine();
                foreach (var nav in result.Nodes)
                {
                    sb.AppendLine(FormatNodeHuman(nav));
                }
            }
        }
        else
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"Result ({result.ResultType.ToTypeString()}): {result.ScalarValueString}");
        }

        var text = sb.ToString();
        var byteCount = Encoding.UTF8.GetByteCount(text);
        if (byteCount > XPathQueryLimits.MaxOutputBytes)
        {
            throw new DogdouXPathException(
                DiagnosticCodes.LimitExceeded,
                $"Serialized output size {byteCount} bytes exceeded the limit of {XPathQueryLimits.MaxOutputBytes} bytes (4 MiB). Use a narrower XPath expression or structural projection.",
                exitCode: 7);
        }

        return text;
    }

    public static string FormatSearchHuman(XPathSearchResult searchResult)
    {
        var sb = new StringBuilder();
        var iterSuffix = !string.IsNullOrEmpty(searchResult.IterationId) ? $" (iteration: {searchResult.IterationId})" : string.Empty;
        sb.AppendLine(CultureInfo.InvariantCulture, $"Search Scope: {searchResult.Scope}{iterSuffix} (derived: {searchResult.Derived.ToString().ToLowerInvariant()})");

        if (searchResult.DocumentResults.Count == 0)
        {
            sb.AppendLine("No matching results found.");
        }
        else
        {
            sb.AppendLine();
            foreach (var docResult in searchResult.DocumentResults)
            {
                sb.AppendLine(CultureInfo.InvariantCulture, $"[{docResult.DocumentPath}] (revision: {docResult.Revision}, type: {docResult.ResultType.ToTypeString()})");
                if (docResult.ResultType == XPathResultKind.NodeSet)
                {
                    foreach (var nav in docResult.Nodes)
                    {
                        sb.AppendLine(FormatNodeHuman(nav));
                    }
                }
                else
                {
                    sb.AppendLine(CultureInfo.InvariantCulture, $"Result: {docResult.ScalarValueString}");
                }
                sb.AppendLine();
            }

            var docCount = searchResult.DocumentResults.Count;
            sb.AppendLine(CultureInfo.InvariantCulture, $"Found results in {docCount} {(docCount == 1 ? "document" : "documents")}.");
        }

        var text = sb.ToString();
        var byteCount = Encoding.UTF8.GetByteCount(text);
        if (byteCount > XPathQueryLimits.MaxOutputBytes)
        {
            throw new DogdouXPathException(
                DiagnosticCodes.LimitExceeded,
                $"Serialized output size {byteCount} bytes exceeded the limit of {XPathQueryLimits.MaxOutputBytes} bytes (4 MiB). Use a narrower XPath expression or structural projection.",
                exitCode: 7);
        }

        return text;
    }

    private static void WriteNavigatorNode(XmlWriter writer, XPathNavigator nav)
    {
        if (nav.UnderlyingObject is XElement directElem)
        {
            directElem.WriteTo(writer);
        }
        else
        {
            using var subtreeReader = nav.ReadSubtree();
            writer.WriteNode(subtreeReader, false);
        }
    }

    private static void WriteTypedItemWrapper(XmlWriter writer, XPathNavigator nav)
    {
        writer.WriteStartElement("item");

        switch (nav.NodeType)
        {
            case XPathNodeType.Element:
                writer.WriteAttributeString("type", "element");
                WriteNavigatorNode(writer, nav);
                break;

            case XPathNodeType.Attribute:
                writer.WriteAttributeString("type", "attribute");
                writer.WriteAttributeString("name", nav.LocalName);
                writer.WriteAttributeString("value", nav.Value);
                break;

            case XPathNodeType.Text:
            case XPathNodeType.SignificantWhitespace:
            case XPathNodeType.Whitespace:
                writer.WriteAttributeString("type", "text");
                writer.WriteString(nav.Value);
                break;

            case XPathNodeType.Comment:
                writer.WriteAttributeString("type", "comment");
                writer.WriteString(nav.Value);
                break;

            case XPathNodeType.ProcessingInstruction:
                writer.WriteAttributeString("type", "processing-instruction");
                writer.WriteAttributeString("name", nav.LocalName);
                writer.WriteString(nav.Value);
                break;

            case XPathNodeType.Root:
                writer.WriteAttributeString("type", "root");
                writer.WriteString(nav.Value);
                break;

            default:
                writer.WriteAttributeString("type", nav.NodeType.ToString().ToLowerInvariant());
                writer.WriteString(nav.Value);
                break;
        }

        writer.WriteEndElement();
    }

    private static string FormatNodeHuman(XPathNavigator nav)
    {
        return nav.NodeType switch
        {
            XPathNodeType.Element => nav.OuterXml,
            XPathNodeType.Attribute => $"@{nav.LocalName}=\"{nav.Value}\"",
            XPathNodeType.Text => nav.Value,
            XPathNodeType.Comment => $"<!--{nav.Value}-->",
            XPathNodeType.ProcessingInstruction => $"<?{nav.LocalName} {nav.Value}?>",
            _ => nav.Value
        };
    }
}
