using System.Text.RegularExpressions;
using DogdouSpec.Core.Diagnostics;

namespace DogdouSpec.Core.XPath;

/// <summary>
/// Parser and validator for XPath variables supplied via --var name=value.
/// Exact variable grammar: [a-z][a-z0-9_]* (lowercase ASCII, case-sensitive).
/// Duplicate, invalid, and unbound variables are errors.
/// </summary>
public static class XPathVariables
{
    private static readonly Regex VariableNameRegex = new(@"^[a-z][a-z0-9_]*$", RegexOptions.Compiled);

    public static Dictionary<string, string> Parse(IEnumerable<string>? rawVariables)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (rawVariables == null) return result;

        foreach (var raw in rawVariables)
        {
            if (string.IsNullOrEmpty(raw))
            {
                throw new DogdouXPathException(
                    DiagnosticCodes.InvalidArgument,
                    "Variable argument cannot be empty. Expected format: name=value.");
            }

            var eqIndex = raw.IndexOf('=');
            if (eqIndex < 0)
            {
                throw new DogdouXPathException(
                    DiagnosticCodes.InvalidArgument,
                    $"Variable specification '{raw}' is invalid. Missing '=' separator. Expected format: name=value.");
            }

            var name = raw.Substring(0, eqIndex);
            var value = raw.Substring(eqIndex + 1);

            if (!VariableNameRegex.IsMatch(name))
            {
                throw new DogdouXPathException(
                    DiagnosticCodes.InvalidArgument,
                    $"Variable name '{name}' is invalid. Variable names must match [a-z][a-z0-9_]* (lowercase ASCII starting with a letter).");
            }

            if (result.ContainsKey(name))
            {
                throw new DogdouXPathException(
                    DiagnosticCodes.InvalidArgument,
                    $"Duplicate variable name '{name}'. Each variable may only be bound once.");
            }

            result[name] = value;
        }

        return result;
    }
}
