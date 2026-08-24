using DogdouSpec.Core.Security;
using DogdouSpec.Core.Workspace;

namespace DogdouSpec.Core.Validation;

/// <summary>
/// Deterministically maps managed documents to their corresponding XSD schema name.
/// </summary>
public static class DocumentSchemaMapper
{
    public static string? GetSchemaNameForDocument(ManagedDocument document)
    {
        if (document == null)
        {
            return null;
        }

        var relPath = document.RelativePath?.Replace('\\', '/').Trim();
        if (!string.IsNullOrEmpty(relPath))
        {
            if (string.Equals(relPath, "knowledge.xml", StringComparison.Ordinal))
            {
                return "knowledge";
            }

            if (string.Equals(relPath, "backlog.xml", StringComparison.Ordinal))
            {
                return "backlog";
            }

            var segments = relPath.Split('/');
            if (segments.Length == 2 && PathSecurity.IterationIdRegex.IsMatch(segments[0]))
            {
                if (string.Equals(segments[1], "spec.xml", StringComparison.Ordinal))
                {
                    return "spec";
                }

                if (string.Equals(segments[1], "tasks.xml", StringComparison.Ordinal))
                {
                    return "tasks";
                }
            }

            return null;
        }

        if (!string.IsNullOrWhiteSpace(document.IterationId) && PathSecurity.IterationIdRegex.IsMatch(document.IterationId))
        {
            var fileName = Path.GetFileName(document.FullPath);
            if (string.Equals(fileName, "spec.xml", StringComparison.Ordinal)) return "spec";
            if (string.Equals(fileName, "tasks.xml", StringComparison.Ordinal)) return "tasks";
            return null;
        }

        var rootFileName = Path.GetFileName(document.FullPath);
        if (string.Equals(rootFileName, "knowledge.xml", StringComparison.Ordinal)) return "knowledge";
        if (string.Equals(rootFileName, "backlog.xml", StringComparison.Ordinal)) return "backlog";

        return null;
    }
}
