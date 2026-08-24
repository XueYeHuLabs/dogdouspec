namespace DogdouSpec.Core.XPath;

/// <summary>
/// Result of evaluating an XPath expression across multiple documents in a search scope.
/// </summary>
public sealed class XPathSearchResult
{
    public string Scope { get; }
    public string? IterationId { get; }
    public string XPath { get; }
    public bool Derived { get; }
    public IReadOnlyList<XPathQueryResult> DocumentResults { get; }

    public XPathSearchResult(
        string scope,
        string? iterationId,
        string xpath,
        bool derived,
        IReadOnlyList<XPathQueryResult> documentResults)
    {
        Scope = scope;
        IterationId = iterationId;
        XPath = xpath;
        Derived = derived;
        DocumentResults = documentResults ?? Array.Empty<XPathQueryResult>();
    }
}
