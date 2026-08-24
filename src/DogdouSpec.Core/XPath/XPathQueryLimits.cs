namespace DogdouSpec.Core.XPath;

/// <summary>
/// Authoritative limits for XPath read operations defined by V1 CLI contract.
/// </summary>
public static class XPathQueryLimits
{
    public const int MaxDocumentBytes = 16 * 1024 * 1024; // 16 MiB
    public const int MaxOutputBytes = 4 * 1024 * 1024; // 4 MiB
    public const int MaxResultNodes = 10_000;
    public const int MaxProjectedNodes = 50_000;
}
