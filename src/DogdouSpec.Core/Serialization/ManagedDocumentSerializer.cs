using System.Text;
using System.Xml;
using System.Xml.Linq;
using DogdouSpec.Core.Security;

namespace DogdouSpec.Core.Serialization;

/// <summary>
/// Canonical serializer for all managed XML documents in DogdouSpec.
/// Enforces:
/// - UTF-8 XML declaration (no BOM)
/// - Stable two-space structural indentation
/// - LF newlines (\n)
/// - Exactly one trailing LF byte at end of document
/// - No newline accumulation after repeated mutations
/// - Byte-identical no-op round trips once canonical
/// - Preservation of prose text values, mixed content, hashes, IDs, and XPath semantics
/// </summary>
public static class ManagedDocumentSerializer
{
    private static readonly UTF8Encoding Utf8NoBom = new(false);

    private static readonly XmlWriterSettings CanonicalSettings = new()
    {
        Indent = false,
        OmitXmlDeclaration = false,
        Encoding = Utf8NoBom,
        NewLineHandling = NewLineHandling.Entitize,
        NewLineChars = "\n"
    };

    /// <summary>
    /// Serializes an <see cref="XDocument"/> to its canonical managed document XML string.
    /// </summary>
    public static string Serialize(XDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);

        var clone = new XDocument(document);
        clone.Declaration = new XDeclaration("1.0", "utf-8", null);

        FormatDocument(clone);

        using var ms = new MemoryStream();
        using (var writer = XmlWriter.Create(ms, CanonicalSettings))
        {
            clone.Save(writer);
        }

        var xml = Utf8NoBom.GetString(ms.ToArray()).TrimEnd('\r', '\n') + "\n";
        return xml;
    }

    /// <summary>
    /// Serializes an <see cref="XElement"/> to its canonical managed document XML string.
    /// </summary>
    public static string Serialize(XElement element)
    {
        ArgumentNullException.ThrowIfNull(element);

        var doc = new XDocument(new XDeclaration("1.0", "utf-8", null), new XElement(element));
        return Serialize(doc);
    }

    /// <summary>
    /// Serializes an <see cref="XDocument"/> to canonical UTF-8 bytes (no BOM, trailing LF).
    /// </summary>
    public static byte[] SerializeToBytes(XDocument document) =>
        Utf8NoBom.GetBytes(Serialize(document));

    /// <summary>
    /// Serializes an <see cref="XElement"/> to canonical UTF-8 bytes (no BOM, trailing LF).
    /// </summary>
    public static byte[] SerializeToBytes(XElement element) =>
        Utf8NoBom.GetBytes(Serialize(element));

    /// <summary>
    /// Serializes an <see cref="XDocument"/> to the specified stream using canonical formatting.
    /// </summary>
    public static void Serialize(XDocument document, Stream stream)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(stream);

        var bytes = SerializeToBytes(document);
        stream.Write(bytes, 0, bytes.Length);
    }

    /// <summary>
    /// Serializes an <see cref="XElement"/> to the specified stream using canonical formatting.
    /// </summary>
    public static void Serialize(XElement element, Stream stream)
    {
        ArgumentNullException.ThrowIfNull(element);
        ArgumentNullException.ThrowIfNull(stream);

        var bytes = SerializeToBytes(element);
        stream.Write(bytes, 0, bytes.Length);
    }

    /// <summary>
    /// Parses the provided XML content and normalizes it to canonical managed document format.
    /// </summary>
    public static string Normalize(string xmlContent)
    {
        ArgumentNullException.ThrowIfNull(xmlContent);

        using var sr = new StringReader(xmlContent);
        using var reader = SecureXmlReaderFactory.CreateReader(sr);
        var doc = XDocument.Load(reader, LoadOptions.PreserveWhitespace | LoadOptions.SetLineInfo);
        return Serialize(doc);
    }

    private static void FormatDocument(XDocument doc)
    {
        var docTextNodes = doc.Nodes().OfType<XText>().ToList();
        foreach (var text in docTextNodes)
        {
            text.Remove();
        }

        var topLevelNodes = doc.Nodes().ToList();
        foreach (var node in topLevelNodes)
        {
            node.AddBeforeSelf(new XText("\n"));
            if (node is XElement el)
            {
                FormatContainer(el, 0);
            }
        }

        if (topLevelNodes.Count > 0)
        {
            doc.Add(new XText("\n"));
        }
    }

    private static void FormatContainer(XElement element, int depth)
    {
        var hasElementChildren = element.Elements().Any();
        if (!hasElementChildren)
        {
            var nodes = element.Nodes().ToList();
            if (nodes.Count > 0 && nodes.All(n => n is XText t && IsStructuralNewlineWhitespace(t)))
            {
                element.RemoveNodes();
            }
            return;
        }

        var isMixed = element.Nodes().OfType<XText>().Any(IsMeaningfulText);
        if (isMixed)
        {
            foreach (var childEl in element.Elements())
            {
                FormatContainer(childEl, depth + 1);
            }
            return;
        }

        var textNodes = element.Nodes().OfType<XText>().ToList();
        foreach (var t in textNodes)
        {
            t.Remove();
        }

        var childNodes = element.Nodes().ToList();
        if (childNodes.Count == 0)
        {
            return;
        }

        var childIndent = "\n" + new string(' ', (depth + 1) * 2);
        var closingIndent = "\n" + new string(' ', depth * 2);

        foreach (var child in childNodes)
        {
            child.AddBeforeSelf(new XText(childIndent));
        }
        element.Add(new XText(closingIndent));

        foreach (var childEl in childNodes.OfType<XElement>())
        {
            FormatContainer(childEl, depth + 1);
        }
    }

    private static bool IsStructuralNewlineWhitespace(XText text)
    {
        if (text is XCData)
        {
            return false;
        }

        var val = text.Value;
        return string.IsNullOrWhiteSpace(val) && (val.Contains('\n') || val.Contains('\r'));
    }

    private static bool IsMeaningfulText(XText text)
    {
        return !IsStructuralNewlineWhitespace(text);
    }
}

/// <summary>
/// Canonical alias for <see cref="ManagedDocumentSerializer"/>.
/// </summary>
public static class CanonicalXmlSerializer
{
    public static string Serialize(XDocument document) => ManagedDocumentSerializer.Serialize(document);
    public static string Serialize(XElement element) => ManagedDocumentSerializer.Serialize(element);
    public static byte[] SerializeToBytes(XDocument document) => ManagedDocumentSerializer.SerializeToBytes(document);
    public static byte[] SerializeToBytes(XElement element) => ManagedDocumentSerializer.SerializeToBytes(element);
    public static void Serialize(XDocument document, Stream stream) => ManagedDocumentSerializer.Serialize(document, stream);
    public static void Serialize(XElement element, Stream stream) => ManagedDocumentSerializer.Serialize(element, stream);
    public static string Normalize(string xmlContent) => ManagedDocumentSerializer.Normalize(xmlContent);
}
