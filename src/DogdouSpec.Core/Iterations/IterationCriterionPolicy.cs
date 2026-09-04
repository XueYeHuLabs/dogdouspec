using System.Diagnostics.CodeAnalysis;
using DogdouSpec.Core.Diagnostics;

namespace DogdouSpec.Core.Iterations;

/// <summary>
/// Authoritative core policy for iteration acceptance criterion content.
/// Rejects null, blank, whitespace-only content and known seeded placeholder literals
/// without fuzzy rejection of legitimate prose.
/// </summary>
public static class IterationCriterionPolicy
{
    public const string SeededFeatureCriterion = "Product criterion pending definition.";
    public const string SeededResearchCriterion = "Research criterion pending definition.";

    /// <summary>
    /// Evaluates whether criterion text contains defined content.
    /// Returns false if text is null, whitespace, or matches either known seeded placeholder literal.
    /// </summary>
    public static bool IsDefined([NotNullWhen(true)] string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var trimmed = text.Trim();
        if (string.Equals(trimmed, SeededFeatureCriterion, StringComparison.Ordinal) ||
            string.Equals(trimmed, SeededResearchCriterion, StringComparison.Ordinal))
        {
            return false;
        }

        return true;
    }

    /// <summary>
    /// Validates criterion text content and returns an actionable failure reason if invalid.
    /// </summary>
    public static (bool IsValid, string? FailureReason) Validate(string? text, string? criterionId = null)
    {
        var idPrefix = string.IsNullOrWhiteSpace(criterionId)
            ? "Acceptance criterion"
            : $"Acceptance criterion '{criterionId}'";

        if (string.IsNullOrWhiteSpace(text))
        {
            return (false, $"{idPrefix} text cannot be blank or whitespace.");
        }

        var trimmed = text.Trim();
        if (string.Equals(trimmed, SeededFeatureCriterion, StringComparison.Ordinal) ||
            string.Equals(trimmed, SeededResearchCriterion, StringComparison.Ordinal))
        {
            return (false, $"{idPrefix} carries seeded placeholder literal '{trimmed}'. A defined criterion is required.");
        }

        return (true, null);
    }
}
