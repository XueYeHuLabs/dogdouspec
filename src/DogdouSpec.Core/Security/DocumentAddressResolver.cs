using DogdouSpec.Core.Diagnostics;

namespace DogdouSpec.Core.Security;

/// <summary>
/// Shared document address resolver for DogdouSpec CLI and Core.
/// Unifies legacy full document paths (<iteration>/spec.xml|tasks.xml) with
/// unambiguous shorthand (--iteration <id> --document spec.xml|tasks.xml),
/// while rejecting conflicts, traversals, and ambiguous forms.
/// </summary>
public static class DocumentAddressResolver
{
    public static (bool IsValid, string? ResolvedRelativePath, string? IterationId, Diagnostic? Error) Resolve(
        string? iterationId,
        string? documentPath,
        bool requireDocument = false)
    {
        var hasIteration = !string.IsNullOrWhiteSpace(iterationId);
        var hasDocument = !string.IsNullOrWhiteSpace(documentPath);

        string? normIterationId = null;
        if (hasIteration)
        {
            var (isIterValid, normIter, iterError) = PathSecurity.ValidateIterationId(iterationId);
            if (!isIterValid || iterError != null)
            {
                return (false, null, null, iterError ?? Diagnostic.Error(
                    DiagnosticCodes.InvalidArgument,
                    $"Iteration ID '{iterationId}' is invalid."));
            }
            normIterationId = normIter;
        }

        if (!hasDocument)
        {
            if (requireDocument)
            {
                return (false, null, null, Diagnostic.Error(
                    DiagnosticCodes.InvalidArgument,
                    "--document option is required."));
            }

            return (true, null, normIterationId, null);
        }

        var trimmedDoc = documentPath!.Trim();

        // Traversal checks on raw document input
        if (trimmedDoc == "." || trimmedDoc == ".." || trimmedDoc.Contains(".."))
        {
            return (false, null, null, Diagnostic.Error(
                DiagnosticCodes.PathTraversalDetected,
                $"Relative path '{documentPath}' contains path traversal and is rejected."));
        }

        // Absolute / rooted / ADS / invalid chars check
        if (trimmedDoc.StartsWith(@"\\", StringComparison.Ordinal) ||
            trimmedDoc.StartsWith("//", StringComparison.Ordinal) ||
            (trimmedDoc.Length >= 2 && trimmedDoc[1] == ':'))
        {
            return (false, null, null, Diagnostic.Error(
                DiagnosticCodes.PathEscapeDetected,
                $"Absolute or rooted document path '{documentPath}' is rejected."));
        }

        if (trimmedDoc.Contains(':'))
        {
            return (false, null, null, Diagnostic.Error(
                DiagnosticCodes.InvalidPath,
                $"Alternate data stream syntax in path '{documentPath}' is rejected."));
        }

        var normalizedDoc = trimmedDoc.Replace('\\', '/');
        if (normalizedDoc.StartsWith('/'))
        {
            return (false, null, null, Diagnostic.Error(
                DiagnosticCodes.PathEscapeDetected,
                $"Document path cannot start with a slash: '{documentPath}'."));
        }

        if (hasIteration)
        {
            // Reject root documents paired with an iteration
            if (string.Equals(normalizedDoc, "knowledge.xml", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(normalizedDoc, "backlog.xml", StringComparison.OrdinalIgnoreCase))
            {
                return (false, null, null, Diagnostic.Error(
                    DiagnosticCodes.InvalidArgument,
                    $"--iteration cannot be specified with root document '{documentPath}'."));
            }

            // Shorthand: spec.xml or tasks.xml
            if (string.Equals(normalizedDoc, "spec.xml", StringComparison.OrdinalIgnoreCase))
            {
                return (true, $"{normIterationId}/spec.xml", normIterationId, null);
            }
            if (string.Equals(normalizedDoc, "tasks.xml", StringComparison.OrdinalIgnoreCase))
            {
                return (true, $"{normIterationId}/tasks.xml", normIterationId, null);
            }

            // Full path: <iteration>/spec.xml or <iteration>/tasks.xml
            if (normalizedDoc.Contains('/'))
            {
                var segments = normalizedDoc.Split('/', StringSplitOptions.None);
                if (segments.Length == 2 &&
                    (string.Equals(segments[1], "spec.xml", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(segments[1], "tasks.xml", StringComparison.OrdinalIgnoreCase)))
                {
                    var (isPathIterValid, normPathIter, _) = PathSecurity.ValidateIterationId(segments[0]);
                    if (isPathIterValid && string.Equals(normPathIter, normIterationId, StringComparison.Ordinal))
                    {
                        var canonicalDocName = string.Equals(segments[1], "spec.xml", StringComparison.OrdinalIgnoreCase)
                            ? "spec.xml"
                            : "tasks.xml";
                        return (true, $"{normIterationId}/{canonicalDocName}", normIterationId, null);
                    }

                    return (false, null, null, Diagnostic.Error(
                        DiagnosticCodes.InvalidArgument,
                        $"Document path '{documentPath}' conflicts with specified --iteration '{iterationId}'."));
                }

                return (false, null, null, Diagnostic.Error(
                    DiagnosticCodes.InvalidArgument,
                    $"Document path '{documentPath}' conflicts with specified --iteration '{iterationId}'."));
            }

            return (false, null, null, Diagnostic.Error(
                DiagnosticCodes.InvalidArgument,
                $"Document reference '{documentPath}' is not a recognized managed iteration document for iteration '{normIterationId}'. Acceptable iteration documents are 'spec.xml' and 'tasks.xml'."));
        }
        else
        {
            // No iteration supplied: documentPath must be a valid relative document path
            var (isValid, canonicalPath, docError) = PathSecurity.ValidateRelativeDocumentPath(normalizedDoc);
            if (!isValid || docError != null)
            {
                return (false, null, null, docError ?? Diagnostic.Error(
                    DiagnosticCodes.InvalidArgument,
                    $"Invalid document reference '{documentPath}'."));
            }

            string? docIterId = null;
            var segments = canonicalPath.Split('/');
            if (segments.Length == 2)
            {
                docIterId = segments[0];
            }

            return (true, canonicalPath, docIterId, null);
        }
    }
}
