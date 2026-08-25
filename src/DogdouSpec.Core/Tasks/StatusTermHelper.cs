using System.Xml.Linq;

namespace DogdouSpec.Core.Tasks;

/// <summary>
/// Shared helper for status-term synchronization across task and iteration transitions.
/// </summary>
public static class StatusTermHelper
{
    /// <summary>
    /// Synchronizes index/term[@key='status'] with the new status on an indexed element.
    /// If exactly one status term exists, its value attribute is updated to newStatus.
    /// If zero status terms exist, no status term is added (does not invent metadata).
    /// If multiple status terms exist, does not modify them (leaving ambiguity for validator rejection).
    /// </summary>
    public static void SynchronizeStatusTerm(XElement? element, string newStatus)
    {
        if (element == null)
        {
            return;
        }

        var indexElem = element.Element("index");
        if (indexElem == null)
        {
            return;
        }

        var statusTerms = indexElem.Elements("term")
            .Where(t => string.Equals(t.Attribute("key")?.Value, "status", StringComparison.Ordinal))
            .ToList();

        if (statusTerms.Count == 1)
        {
            statusTerms[0].SetAttributeValue("value", newStatus);
        }
    }
}
