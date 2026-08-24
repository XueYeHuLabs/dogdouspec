using DogdouSpec.Core.Diagnostics;

namespace DogdouSpec.Core.XPath;

/// <summary>
/// Tracks execution state and intermediate projection budgets across XPath evaluation.
/// </summary>
public sealed class XPathEvaluationContext
{
    public int ProjectedNodeCount { get; private set; }
    public bool Derived { get; set; }
    public int ProjectedDocSequence { get; set; } = 1;

    public void TrackProjectedNodes(int count)
    {
        ProjectedNodeCount += count;
        if (ProjectedNodeCount > XPathQueryLimits.MaxProjectedNodes)
        {
            throw new DogdouXPathException(
                DiagnosticCodes.LimitExceeded,
                $"Projected node budget exceeded the limit of {XPathQueryLimits.MaxProjectedNodes} nodes. Use a narrower XPath expression or structural projection.",
                exitCode: 7);
        }
    }
}
