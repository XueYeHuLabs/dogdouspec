using DogdouSpec.Core.Diagnostics;
using DogdouSpec.Core.XPath;

namespace DogdouSpec.Cli.Commands;

internal static class XPathInput
{
    public static (string? Expression, Diagnostic? Error) Read(string? expression, string? path)
    {
        if ((expression != null) == (path != null))
            return (null, Diagnostic.Error(DiagnosticCodes.InvalidArgument, "Specify exactly one of --xpath or --xpath-file."));
        if (path != null)
        {
            try
            {
                if (new FileInfo(path).Length > XPathQueryLimits.MaxDocumentBytes)
                    return (null, Diagnostic.Error(DiagnosticCodes.LimitExceeded, "XPath input file exceeds 16 MiB."));
                expression = File.ReadAllText(path);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                return (null, Diagnostic.Error(DiagnosticCodes.InvalidArgument, $"Cannot read XPath input file: {error.Message}"));
            }
        }
        return string.IsNullOrWhiteSpace(expression)
            ? (null, Diagnostic.Error(DiagnosticCodes.InvalidArgument, "XPath expression cannot be empty."))
            : (expression, null);
    }
}
