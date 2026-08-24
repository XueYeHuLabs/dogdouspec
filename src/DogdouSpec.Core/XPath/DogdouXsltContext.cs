using System.Xml.XPath;
using System.Xml.Xsl;
using DogdouSpec.Core.Diagnostics;

namespace DogdouSpec.Core.XPath;

/// <summary>
/// Custom XsltContext providing pre-bound ds: namespace, DogdouSpec string variables,
/// and structural projection extension functions (ds:filter, ds:filter-out).
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1010:Collections should implement generic interface", Justification = "Inherits from Framework XsltContext")]
public sealed class DogdouXsltContext : XsltContext
{
    public const string DogdouFunctionsNamespace = "urn:dogdouspec:xpath:functions:1";

    private readonly IReadOnlyDictionary<string, string> _variables;
    private readonly XPathEvaluationContext _context;
    private readonly FilterExtensionFunction _filterFunction;
    private readonly FilterExtensionFunction _filterOutFunction;

    public DogdouXsltContext(
        IReadOnlyDictionary<string, string>? variables,
        XPathEvaluationContext context)
        : base(new System.Xml.NameTable())
    {
        _variables = variables ?? new Dictionary<string, string>();
        _context = context;
        _filterFunction = new FilterExtensionFunction(isFilterOut: false, _context);
        _filterOutFunction = new FilterExtensionFunction(isFilterOut: true, _context);

        AddNamespace("ds", DogdouFunctionsNamespace);
    }

    public override IXsltContextVariable ResolveVariable(string prefix, string name)
    {
        if (!string.IsNullOrEmpty(prefix))
        {
            throw new DogdouXPathException(
                DiagnosticCodes.InvalidArgument,
                $"Prefixed variable '${prefix}:{name}' is not supported. DogdouSpec variables must be unprefixed.");
        }

        if (_variables.TryGetValue(name, out var value))
        {
            return new DogdouXsltVariable(value);
        }

        throw new DogdouXPathException(
            DiagnosticCodes.InvalidArgument,
            $"Unbound variable '${name}' referenced in XPath expression.");
    }

    public override IXsltContextFunction ResolveFunction(string prefix, string name, XPathResultType[] ArgTypes)
    {
        var ns = LookupNamespace(prefix);
        if (string.Equals(ns, DogdouFunctionsNamespace, StringComparison.Ordinal) ||
            string.Equals(prefix, "ds", StringComparison.Ordinal))
        {
            if (string.Equals(name, "filter", StringComparison.Ordinal))
            {
                return _filterFunction;
            }

            if (string.Equals(name, "filter-out", StringComparison.Ordinal))
            {
                return _filterOutFunction;
            }

            throw new DogdouXPathException(
                DiagnosticCodes.InvalidArgument,
                $"Unknown extension function 'ds:{name}'. Supported functions are ds:filter and ds:filter-out.");
        }

        return null!;
    }

    public override bool Whitespace => true;
    public override bool PreserveWhitespace(XPathNavigator node) => true;
    public override int CompareDocument(string baseUri, string nextbaseUri)
    {
        return Math.Sign(string.Compare(baseUri, nextbaseUri, StringComparison.Ordinal));
    }
}
