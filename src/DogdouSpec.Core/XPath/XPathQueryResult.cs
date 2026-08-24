using System.Globalization;
using System.Xml.XPath;

namespace DogdouSpec.Core.XPath;

[System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1720:Identifier contains type name", Justification = "Maps directly to XPath 1.0 types")]
public enum XPathResultKind
{
    NodeSet,
    Boolean,
    Number,
    String
}

public static class XPathResultKindExtensions
{
    public static string ToTypeString(this XPathResultKind kind) => kind switch
    {
        XPathResultKind.NodeSet => "node-set",
        XPathResultKind.Boolean => "boolean",
        XPathResultKind.Number => "number",
        XPathResultKind.String => "string",
        _ => "unknown"
    };
}

/// <summary>
/// Result of evaluating an XPath expression against a single managed document.
/// </summary>
public sealed class XPathQueryResult
{
    public string DocumentPath { get; }
    public string Revision { get; }
    public XPathResultKind ResultType { get; }
    public bool Derived { get; }
    public IReadOnlyList<XPathNavigator> Nodes { get; }
    public bool? BooleanValue { get; }
    public double? NumberValue { get; }
    public string? StringValue { get; }

    private XPathQueryResult(
        string documentPath,
        string revision,
        XPathResultKind resultType,
        bool derived,
        IReadOnlyList<XPathNavigator>? nodes = null,
        bool? booleanValue = null,
        double? numberValue = null,
        string? stringValue = null)
    {
        DocumentPath = documentPath;
        Revision = revision;
        ResultType = resultType;
        Derived = derived;
        Nodes = nodes ?? Array.Empty<XPathNavigator>();
        BooleanValue = booleanValue;
        NumberValue = numberValue;
        StringValue = stringValue;
    }

    public static XPathQueryResult ForNodeSet(string documentPath, string revision, IReadOnlyList<XPathNavigator> nodes, bool derived) =>
        new(documentPath, revision, XPathResultKind.NodeSet, derived, nodes: nodes);

    public static XPathQueryResult ForBoolean(string documentPath, string revision, bool value, bool derived) =>
        new(documentPath, revision, XPathResultKind.Boolean, derived, booleanValue: value);

    public static XPathQueryResult ForNumber(string documentPath, string revision, double value, bool derived) =>
        new(documentPath, revision, XPathResultKind.Number, derived, numberValue: value);

    public static XPathQueryResult ForString(string documentPath, string revision, string value, bool derived) =>
        new(documentPath, revision, XPathResultKind.String, derived, stringValue: value);

    public string ScalarValueString => ResultType switch
    {
        XPathResultKind.Boolean => BooleanValue == true ? "true" : "false",
        XPathResultKind.Number => NumberValue.HasValue ? NumberValue.Value.ToString(CultureInfo.InvariantCulture) : "0",
        XPathResultKind.String => StringValue ?? string.Empty,
        _ => string.Empty
    };
}
