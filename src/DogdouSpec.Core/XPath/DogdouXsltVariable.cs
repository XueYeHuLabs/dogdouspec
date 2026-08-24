using System.Xml.XPath;
using System.Xml.Xsl;

namespace DogdouSpec.Core.XPath;

/// <summary>
/// XPath 1.0 string variable binding for XsltContext.
/// </summary>
public sealed class DogdouXsltVariable : IXsltContextVariable
{
    private readonly string _value;

    public DogdouXsltVariable(string value)
    {
        _value = value;
    }

    public XPathResultType VariableType => XPathResultType.String;
    public bool IsLocal => false;
    public bool IsParam => false;
    public object Evaluate(XsltContext xsltContext) => _value;
}
