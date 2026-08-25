using System.Xml.Linq;

namespace DogdouSpec.Core.Tasks;

public sealed record DeclaredRepositoryScope(
    string BasePath,
    IReadOnlyList<string> Includes,
    IReadOnlyList<string> Excludes);

public static class TaskScopeMatcher
{
    public static IReadOnlyList<DeclaredRepositoryScope> ParseScopes(XElement? scopeElement)
    {
        if (scopeElement == null)
        {
            return Array.Empty<DeclaredRepositoryScope>();
        }

        return scopeElement.Elements("repository")
            .Select(repository => new DeclaredRepositoryScope(
                NormalizePath(repository.Attribute("path")?.Value ?? "."),
                repository.Elements("include")
                    .Select(include => include.Attribute("path")?.Value)
                    .Where(path => !string.IsNullOrWhiteSpace(path))
                    .Select(path => NormalizePath(path!))
                    .ToArray(),
                repository.Elements("exclude")
                    .Select(exclude => exclude.Attribute("path")?.Value)
                    .Where(path => !string.IsNullOrWhiteSpace(path))
                    .Select(path => NormalizePath(path!))
                    .ToArray()))
            .ToArray();
    }

    public static string NormalizePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return string.Empty;
        }

        var raw = path.Trim().Replace('\\', '/');
        var isAbsolute = raw.StartsWith('/');
        var segments = new List<string>();
        foreach (var segment in raw.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (!string.Equals(segment, ".", StringComparison.Ordinal))
            {
                segments.Add(segment);
            }
        }

        var normalized = string.Join("/", segments);
        if (normalized.Length == 0)
        {
            return isAbsolute ? "/" : ".";
        }

        return isAbsolute ? "/" + normalized : normalized;
    }

    public static bool IsPathInScope(
        string candidatePath,
        IReadOnlyList<DeclaredRepositoryScope> declaredScopes,
        bool? forceCaseInsensitive = null)
    {
        var normalizedPath = NormalizePath(candidatePath);
        if (normalizedPath.Length == 0 || normalizedPath == "." || normalizedPath.StartsWith('/'))
        {
            return false;
        }

        var ignoreCase = forceCaseInsensitive ?? OperatingSystem.IsWindows();
        var relativeScopes = new List<(DeclaredRepositoryScope Scope, string RelativePath)>();
        foreach (var scope in declaredScopes)
        {
            if (TryGetRelativePath(normalizedPath, scope.BasePath, ignoreCase, out var relativePath))
            {
                relativeScopes.Add((scope, relativePath));
            }
        }

        if (relativeScopes.Any(item => item.Scope.Excludes.Any(pattern =>
            MatchesGlob(item.RelativePath, pattern, ignoreCase))))
        {
            return false;
        }

        return relativeScopes.Any(item => item.Scope.Includes.Any(pattern =>
            MatchesGlob(item.RelativePath, pattern, ignoreCase)));
    }

    public static bool MatchesGlob(string relativePath, string pattern, bool ignoreCase)
    {
        var normalizedPath = NormalizePath(relativePath);
        var normalizedPattern = NormalizePath(pattern);
        if (normalizedPath.Length == 0 || normalizedPath.StartsWith('/') ||
            normalizedPattern.Length == 0 || normalizedPattern.StartsWith('/'))
        {
            return false;
        }

        if (normalizedPattern is "." or "**" or "**/*")
        {
            return normalizedPath != ".";
        }

        return MatchPathSegments(
            normalizedPattern.Split('/', StringSplitOptions.RemoveEmptyEntries),
            normalizedPath.Split('/', StringSplitOptions.RemoveEmptyEntries),
            0,
            0,
            ignoreCase,
            new Dictionary<(int PatternIndex, int PathIndex), bool>());
    }

    private static bool TryGetRelativePath(string candidatePath, string basePath, bool ignoreCase, out string relativePath)
    {
        relativePath = string.Empty;
        var normalizedBase = NormalizePath(basePath);
        if (normalizedBase.Length == 0 || normalizedBase.StartsWith('/') || HasParentSegment(normalizedBase))
        {
            return false;
        }

        if (normalizedBase == ".")
        {
            relativePath = candidatePath;
            return true;
        }

        var comparison = ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (string.Equals(candidatePath, normalizedBase, comparison))
        {
            relativePath = ".";
            return true;
        }

        var prefix = normalizedBase + "/";
        if (!candidatePath.StartsWith(prefix, comparison))
        {
            return false;
        }

        relativePath = candidatePath[prefix.Length..];
        return true;
    }

    private static bool MatchPathSegments(
        string[] patternSegments,
        string[] pathSegments,
        int patternIndex,
        int pathIndex,
        bool ignoreCase,
        Dictionary<(int PatternIndex, int PathIndex), bool> memo)
    {
        var key = (patternIndex, pathIndex);
        if (memo.TryGetValue(key, out var known))
        {
            return known;
        }

        bool result;
        if (patternIndex == patternSegments.Length)
        {
            result = pathIndex == pathSegments.Length;
        }
        else if (patternSegments[patternIndex] == "**")
        {
            result = MatchPathSegments(patternSegments, pathSegments, patternIndex + 1, pathIndex, ignoreCase, memo) ||
                (pathIndex < pathSegments.Length && MatchPathSegments(patternSegments, pathSegments, patternIndex, pathIndex + 1, ignoreCase, memo));
        }
        else
        {
            result = pathIndex < pathSegments.Length &&
                MatchSegment(
                    patternSegments[patternIndex],
                    pathSegments[pathIndex],
                    ignoreCase,
                    0,
                    0,
                    new Dictionary<(int PatternIndex, int PathIndex), bool>()) &&
                MatchPathSegments(patternSegments, pathSegments, patternIndex + 1, pathIndex + 1, ignoreCase, memo);
        }

        memo[key] = result;
        return result;
    }

    private static bool MatchSegment(
        string pattern,
        string value,
        bool ignoreCase,
        int patternIndex,
        int valueIndex,
        Dictionary<(int PatternIndex, int PathIndex), bool> memo)
    {
        var key = (patternIndex, valueIndex);
        if (memo.TryGetValue(key, out var known))
        {
            return known;
        }

        bool result;
        if (patternIndex == pattern.Length)
        {
            result = valueIndex == value.Length;
        }
        else if (pattern[patternIndex] == '*')
        {
            result = MatchSegment(pattern, value, ignoreCase, patternIndex + 1, valueIndex, memo) ||
                (valueIndex < value.Length && MatchSegment(pattern, value, ignoreCase, patternIndex, valueIndex + 1, memo));
        }
        else if (pattern[patternIndex] == '?')
        {
            result = valueIndex < value.Length &&
                MatchSegment(pattern, value, ignoreCase, patternIndex + 1, valueIndex + 1, memo);
        }
        else
        {
            result = valueIndex < value.Length &&
                (ignoreCase
                    ? char.ToUpperInvariant(pattern[patternIndex]) == char.ToUpperInvariant(value[valueIndex])
                    : pattern[patternIndex] == value[valueIndex]) &&
                MatchSegment(pattern, value, ignoreCase, patternIndex + 1, valueIndex + 1, memo);
        }

        memo[key] = result;
        return result;
    }

    private static bool HasParentSegment(string path) =>
        path.Split('/', StringSplitOptions.RemoveEmptyEntries).Any(segment => segment == "..");
}
