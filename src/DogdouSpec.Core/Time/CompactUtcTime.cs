using System.Globalization;

namespace DogdouSpec.Core.Time;

/// <summary>
/// Utility for formatting and validating compact UTC ISO timestamps (yyyyMMddTHHmmssZ)
/// without punctuation, conforming to the TokenValueType pattern.
/// </summary>
public static class CompactUtcTime
{
    public const string FormatString = "yyyyMMddTHHmmssZ";

    public static string Format(DateTime dateTime)
    {
        return dateTime.ToUniversalTime().ToString(FormatString, CultureInfo.InvariantCulture);
    }

    public static string Format(DateTimeOffset dateTimeOffset)
    {
        return dateTimeOffset.UtcDateTime.ToString(FormatString, CultureInfo.InvariantCulture);
    }

    public static bool TryParse(string? input, out DateTime utcTime)
    {
        utcTime = default;
        if (string.IsNullOrWhiteSpace(input)) return false;

        return DateTime.TryParseExact(
            input.Trim(),
            FormatString,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal,
            out utcTime);
    }

    public static bool IsValid(string? input) => TryParse(input, out _);
}
