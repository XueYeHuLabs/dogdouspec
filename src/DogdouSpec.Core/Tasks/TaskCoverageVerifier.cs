using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security;
using System.Text;
using System.Xml.Linq;
using DogdouSpec.Core.Diagnostics;

namespace DogdouSpec.Core.Tasks;

/// <summary>
/// Verifies acceptance criteria coverage by task-local verification or completion records.
/// Enforces fail-fast coverage validation during the verify transition and generates
/// expected XML shapes for actionable diagnostics.
/// </summary>
public static class TaskCoverageVerifier
{
    /// <summary>
    /// Builds an expected &lt;covers&gt; XML fragment for the specified criterion target IDs.
    /// </summary>
    public static string BuildExpectedCoversFragment(IEnumerable<string> criteriaIds)
    {
        var sb = new StringBuilder();
        sb.AppendLine("<covers>");
        foreach (var critId in criteriaIds)
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"  <ref scope=\"document\" target=\"{SecurityElement.Escape(critId)}\" relation=\"covers\"/>");
        }
        sb.Append("</covers>");
        return sb.ToString();
    }

    /// <summary>
    /// Identifies all criteria on a task that are not covered by any existing or newly requested verification or completion records.
    /// </summary>
    public static (bool IsCovered, List<string> UncoveredCriteriaIds) GetUncoveredCriteria(
        XElement taskElem,
        IEnumerable<XElement>? additionalRecords = null)
    {
        var criteria = taskElem.Element("acceptance")?
            .Elements("criterion")
            .Select(c => (string?)c.Attribute("id"))
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id!)
            .ToList() ?? new List<string>();

        if (criteria.Count == 0)
        {
            return (true, new List<string>());
        }

        var allRecords = new List<XElement>();
        var existingRecords = taskElem.Element("records")?.Elements("record");
        if (existingRecords != null)
        {
            allRecords.AddRange(existingRecords);
        }
        if (additionalRecords != null)
        {
            allRecords.AddRange(additionalRecords);
        }

        var coveringRecords = allRecords.Where(r =>
        {
            var kind = (string?)r.Attribute("kind");
            return string.Equals(kind, "verification", StringComparison.Ordinal) ||
                   string.Equals(kind, "completion", StringComparison.Ordinal);
        }).ToList();

        var coveredTargetIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var rec in coveringRecords)
        {
            var coversElem = rec.Element("covers");
            if (coversElem != null)
            {
                foreach (var refElem in coversElem.Elements("ref"))
                {
                    var target = (string?)refElem.Attribute("target");
                    if (!string.IsNullOrWhiteSpace(target))
                    {
                        coveredTargetIds.Add(target);
                    }
                }
            }
        }

        var uncovered = criteria.Where(c => !coveredTargetIds.Contains(c)).ToList();
        return (uncovered.Count == 0, uncovered);
    }

    /// <summary>
    /// Validates criteria coverage for a task transitioning to 'verification'.
    /// Returns a diagnostic error embedding the expected &lt;covers&gt; fragment if any criteria are uncovered.
    /// </summary>
    public static (bool Success, Diagnostic? Diagnostic) ValidateVerifyTransitionCoverage(
        XElement taskElem,
        IEnumerable<XElement>? additionalRecords,
        string documentPath)
    {
        var taskId = (string?)taskElem.Attribute("id") ?? "unknown";
        var (isCovered, uncovered) = GetUncoveredCriteria(taskElem, additionalRecords);

        if (isCovered || uncovered.Count == 0)
        {
            return (true, null);
        }

        var expectedCoversXml = BuildExpectedCoversFragment(uncovered);
        string message;
        if (uncovered.Count == 1)
        {
            message = $"Task '{taskId}' cannot transition to 'verification': acceptance criterion '{uncovered[0]}' is not covered by any task-local verification or completion record. Expected covers fragment:\n{expectedCoversXml}";
        }
        else
        {
            message = $"Task '{taskId}' cannot transition to 'verification': acceptance criteria ({string.Join(", ", uncovered)}) are not covered by any task-local verification or completion record. Expected covers fragment:\n{expectedCoversXml}";
        }

        return (false, Diagnostic.Error(DiagnosticCodes.TaskCriterionNotCovered, message, documentPath));
    }
}
