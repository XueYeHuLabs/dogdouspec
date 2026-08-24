namespace DogdouSpec.Core.Diagnostics;

/// <summary>
/// Structured diagnostic representing an error, warning, or informational message.
/// </summary>
public sealed record Diagnostic(
    string Code,
    string Severity,
    string Message,
    string? Document = null,
    int? Line = null,
    int? Column = null,
    int? ExpectedRevision = null,
    int? ActualRevision = null)
{
    public static Diagnostic Error(string code, string message, string? document = null, int? line = null, int? column = null) =>
        new(code, "error", message, document, line, column);

    public static Diagnostic Warning(string code, string message, string? document = null, int? line = null, int? column = null) =>
        new(code, "warning", message, document, line, column);

    public static Diagnostic Info(string code, string message, string? document = null, int? line = null, int? column = null) =>
        new(code, "info", message, document, line, column);
}
