using System.Globalization;
using System.Xml;
using System.Xml.Linq;
using DogdouSpec.Core.Diagnostics;
using DogdouSpec.Core.Resources;
using DogdouSpec.Core.Security;

namespace DogdouSpec.Core.Revisions;

/// <summary>
/// Shared revision resolution primitive for DogdouSpec.
/// Fails closed on missing or malformed revision attributes (never falls back to 1).
/// Explicit revisions remain unchanged and are diagnosed through normal mutation handling.
/// </summary>
public static class DocumentRevisionResolver
{
    public static (bool Success, int Revision, Diagnostic? Error) ResolveExpectedRevision(
        string workspaceRoot,
        string relativeDocumentPath,
        int? explicitRevision)
    {
        if (explicitRevision.HasValue)
        {
            if (explicitRevision.Value <= 0)
            {
                return (false, 0, Diagnostic.Error(
                    DiagnosticCodes.InvalidArgument,
                    "--expected-revision must be positive."));
            }
            return (true, explicitRevision.Value, null);
        }

        return ReadDocumentRevision(workspaceRoot, relativeDocumentPath);
    }

    public static (bool Success, int Revision, Diagnostic? Error) ReadDocumentRevision(
        string workspaceRoot,
        string relativeDocumentPath)
    {
        if (string.IsNullOrWhiteSpace(workspaceRoot))
        {
            return (false, 0, Diagnostic.Error(
                DiagnosticCodes.InvalidArgument,
                "Workspace root must be specified."));
        }

        var (isValid, normRelPath, pathErr) = PathSecurity.ValidateRelativeDocumentPath(relativeDocumentPath);
        if (!isValid || pathErr != null)
        {
            return (false, 0, pathErr ?? Diagnostic.Error(
                DiagnosticCodes.InvalidArgument,
                $"Invalid document path '{relativeDocumentPath}'."));
        }

        var fullPath = Path.Combine(workspaceRoot, normRelPath.Replace('/', Path.DirectorySeparatorChar));
        var (isSafe, safeErr) = PathSecurity.CheckContainmentAndReparsePoints(workspaceRoot, fullPath);
        if (!isSafe || safeErr != null)
        {
            return (false, 0, safeErr ?? Diagnostic.Error(
                DiagnosticCodes.PathEscapeDetected,
                $"Target path escapes workspace: '{normRelPath}'."));
        }

        if (!File.Exists(fullPath))
        {
            return (false, 0, Diagnostic.Error(
                DiagnosticCodes.DocumentNotFound,
                $"Document '{normRelPath}' does not exist in workspace.",
                normRelPath));
        }

        try
        {
            using var stream = File.OpenRead(fullPath);
            using var reader = SecureXmlReaderFactory.CreateReader(stream);
            var doc = XDocument.Load(reader);
            var revStr = doc.Root?.Attribute("revision")?.Value;
            if (string.IsNullOrWhiteSpace(revStr) ||
                !int.TryParse(revStr, NumberStyles.None, CultureInfo.InvariantCulture, out var rev) ||
                rev <= 0)
            {
                return (false, 0, Diagnostic.Error(
                    DiagnosticCodes.XmlParseError,
                    $"Document '{normRelPath}' root revision attribute is missing, non-positive, or malformed.",
                    normRelPath));
            }
            return (true, rev, null);
        }
        catch (XmlException xmlEx)
        {
            return (false, 0, Diagnostic.Error(
                DiagnosticCodes.XmlParseError,
                $"Failed to parse XML document '{normRelPath}': {xmlEx.Message}",
                normRelPath));
        }
        catch (Exception ex)
        {
            return (false, 0, Diagnostic.Error(
                DiagnosticCodes.XmlParseError,
                $"Failed to read '{normRelPath}': {ex.Message}",
                normRelPath));
        }
    }
}
