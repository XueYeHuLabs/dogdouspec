using System.Text.RegularExpressions;
using DogdouSpec.Core.Diagnostics;

namespace DogdouSpec.Core.Security;

/// <summary>
/// Secure path validation and normalization.
/// Rejects path traversal, alternate data streams, device names, and workspace escapes.
/// </summary>
public static class PathSecurity
{
    private static readonly HashSet<string> ReservedDeviceNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
    };

    /// <summary>
    /// Validates a relative managed document reference inside .dogdouspec workspace.
    /// Accepts only: knowledge.xml, backlog.xml, <iteration-id>/spec.xml, <iteration-id>/tasks.xml.
    /// Rejects path traversal, absolute paths, ADS, device names, _schema/**, _skill/**,
    /// arbitrary directories, extra segments, and other non-managed files.
    /// Returns normalized relative path with '/' separators.
    /// </summary>
    public static (bool IsValid, string NormalizedPath, Diagnostic? Error) ValidateRelativeDocumentPath(string? relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            return (false, string.Empty, Diagnostic.Error(DiagnosticCodes.InvalidArgument, "Document path cannot be empty."));
        }

        var trimmed = relativePath.Trim();

        // Traversal checks on raw input
        if (trimmed == "." || trimmed == ".." || trimmed.Contains(".."))
        {
            return (false, string.Empty, Diagnostic.Error(DiagnosticCodes.PathTraversalDetected, $"Relative path '{relativePath}' contains path traversal and is rejected."));
        }

        // Check for device namespaces or drive root
        if (trimmed.StartsWith(@"\\", StringComparison.Ordinal) ||
            trimmed.StartsWith("//", StringComparison.Ordinal) ||
            (trimmed.Length >= 2 && trimmed[1] == ':'))
        {
            return (false, string.Empty, Diagnostic.Error(DiagnosticCodes.PathEscapeDetected, $"Absolute or rooted document path '{relativePath}' is rejected."));
        }

        // Check for alternate data streams
        if (trimmed.Contains(':'))
        {
            return (false, string.Empty, Diagnostic.Error(DiagnosticCodes.InvalidPath, $"Alternate data stream syntax in path '{relativePath}' is rejected."));
        }

        // Check for null characters or illegal characters
        if (trimmed.IndexOfAny(Path.GetInvalidPathChars()) >= 0)
        {
            return (false, string.Empty, Diagnostic.Error(DiagnosticCodes.InvalidPath, $"Path '{relativePath}' contains invalid characters."));
        }

        // Normalize separators
        var normalized = trimmed.Replace('\\', '/');

        // Check leading slash
        if (normalized.StartsWith('/'))
        {
            return (false, string.Empty, Diagnostic.Error(DiagnosticCodes.PathEscapeDetected, $"Document path cannot start with a slash: '{relativePath}'."));
        }

        // Split segments and check each segment
        var segments = normalized.Split('/', StringSplitOptions.None);
        foreach (var seg in segments)
        {
            if (string.IsNullOrEmpty(seg))
            {
                return (false, string.Empty, Diagnostic.Error(DiagnosticCodes.InvalidPath, $"Empty path segment in '{relativePath}' is rejected."));
            }

            if (seg == "." || seg == "..")
            {
                return (false, string.Empty, Diagnostic.Error(DiagnosticCodes.PathTraversalDetected, $"Relative path segment '{seg}' in '{relativePath}' is rejected."));
            }

            var baseName = Path.GetFileNameWithoutExtension(seg);
            if (ReservedDeviceNames.Contains(baseName) || ReservedDeviceNames.Contains(seg))
            {
                return (false, string.Empty, Diagnostic.Error(DiagnosticCodes.InvalidPath, $"Reserved device name '{seg}' in path is rejected."));
            }
        }

        // Enforce managed document reference grammar
        if (segments.Length == 1)
        {
            var single = segments[0];
            if (string.Equals(single, "knowledge.xml", StringComparison.Ordinal) ||
                string.Equals(single, "backlog.xml", StringComparison.Ordinal))
            {
                return (true, single, null);
            }

            return (false, string.Empty, Diagnostic.Error(
                DiagnosticCodes.InvalidArgument,
                $"Document reference '{relativePath}' is not a recognized managed document. Acceptable root documents are 'knowledge.xml' and 'backlog.xml'."));
        }

        if (segments.Length == 2)
        {
            var iterSeg = segments[0];
            var fileSeg = segments[1];

            var (isIterValid, normalizedIter, iterErr) = ValidateIterationId(iterSeg);
            if (!isIterValid || iterErr != null)
            {
                return (false, string.Empty, iterErr ?? Diagnostic.Error(
                    DiagnosticCodes.InvalidArgument,
                    $"Iteration directory '{iterSeg}' in document reference '{relativePath}' is not a valid iteration identifier."));
            }

            if (string.Equals(fileSeg, "spec.xml", StringComparison.Ordinal) ||
                string.Equals(fileSeg, "tasks.xml", StringComparison.Ordinal))
            {
                return (true, $"{normalizedIter}/{fileSeg}", null);
            }

            return (false, string.Empty, Diagnostic.Error(
                DiagnosticCodes.InvalidArgument,
                $"Document reference '{relativePath}' is not a recognized managed iteration document. Acceptable iteration documents are 'spec.xml' and 'tasks.xml'."));
        }

        return (false, string.Empty, Diagnostic.Error(
            DiagnosticCodes.InvalidArgument,
            $"Document reference '{relativePath}' contains nested extra segments and is rejected."));
    }

    /// <summary>
    /// Reusable Core helper to validate a managed document reference.
    /// </summary>
    public static (bool IsValid, string NormalizedReference, Diagnostic? Error) ValidateManagedDocumentReference(string? reference) =>
        ValidateRelativeDocumentPath(reference);

    /// <summary>
    /// Validates an explicit workspace root path.
    /// </summary>
    public static (bool IsValid, string FullNormalizedPath, Diagnostic? Error) ValidateWorkspaceRootPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return (false, string.Empty, Diagnostic.Error(DiagnosticCodes.InvalidArgument, "Workspace root path cannot be empty."));
        }

        try
        {
            var trimmed = path.Trim();
            if (trimmed.Contains(".."))
            {
                // Check if traversal is safe by getting full path
                var full = Path.GetFullPath(trimmed);
                return (true, NormalizeSeparators(full), null);
            }

            var fullPath = Path.GetFullPath(trimmed);
            return (true, NormalizeSeparators(fullPath), null);
        }
        catch (Exception ex)
        {
            return (false, string.Empty, Diagnostic.Error(DiagnosticCodes.InvalidPath, $"Invalid workspace root path '{path}': {ex.Message}"));
        }
    }

    /// <summary>
    /// Verifies that a .dogdouspec directory is not an unverifiable or escaping reparse point.
    /// If parentProjectDirectory is null, it is derived from the parent of dogdouDirectoryFullPath.
    /// </summary>
    public static (bool IsSafe, Diagnostic? Error) VerifyWorkspaceDirectorySecurity(
        string dogdouDirectoryFullPath,
        string? parentProjectDirectory = null)
    {
        var fullDogdou = Path.GetFullPath(dogdouDirectoryFullPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (!Directory.Exists(fullDogdou))
        {
            return (true, null);
        }

        var attr = File.GetAttributes(fullDogdou);
        if (attr.HasFlag(FileAttributes.ReparsePoint))
        {
            var parentDir = parentProjectDirectory != null
                ? Path.GetFullPath(parentProjectDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                : Directory.GetParent(fullDogdou)?.FullName.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            try
            {
                var dirInfo = new DirectoryInfo(fullDogdou);
                var resolved = dirInfo.ResolveLinkTarget(returnFinalTarget: true);
                if (resolved == null)
                {
                    return (false, Diagnostic.Error(
                        DiagnosticCodes.PathEscapeDetected,
                        $"Workspace root '{fullDogdou}' is an unverifiable reparse point."));
                }

                if (parentDir != null && !IsContainedWithin(parentDir, resolved.FullName))
                {
                    return (false, Diagnostic.Error(
                        DiagnosticCodes.PathEscapeDetected,
                        $"Workspace root '{fullDogdou}' is a reparse point resolving outside its parent project '{parentDir}'."));
                }
            }
            catch (Exception ex)
            {
                return (false, Diagnostic.Error(
                    DiagnosticCodes.PathEscapeDetected,
                    $"Failed to verify workspace root reparse point '{fullDogdou}': {ex.Message}"));
            }
        }

        return (true, null);
    }

    public static readonly Regex IterationIdRegex = new(@"^[0-9]{8}(T[0-9]{6}Z)?-[a-z0-9]([a-z0-9-]*[a-z0-9])?$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// Validates an iteration identifier against the TimeFirstIdType grammar (YYYYMMDD-name or YYYYMMDDTHHmmssZ-name).
    /// Rejects traversal, separators, absolute paths, ADS/device syntax, invalid casing/characters.
    /// </summary>
    public static (bool IsValid, string NormalizedId, Diagnostic? Error) ValidateIterationId(string? iterationId)
    {
        if (string.IsNullOrWhiteSpace(iterationId))
        {
            return (false, string.Empty, Diagnostic.Error(DiagnosticCodes.InvalidArgument, "Iteration identifier cannot be empty."));
        }

        var trimmed = iterationId.Trim();

        // Traversal checks
        if (trimmed == "." || trimmed == ".." || trimmed.Contains(".."))
        {
            return (false, string.Empty, Diagnostic.Error(DiagnosticCodes.PathTraversalDetected, $"Iteration identifier '{iterationId}' contains path traversal and is rejected."));
        }

        // Separators, drive roots, absolute paths, or alternate data streams
        if (trimmed.StartsWith(@"\\", StringComparison.Ordinal) ||
            trimmed.StartsWith("//", StringComparison.Ordinal) ||
            (trimmed.Length >= 2 && trimmed[1] == ':') ||
            trimmed.Contains('/') ||
            trimmed.Contains('\\') ||
            trimmed.Contains(':'))
        {
            return (false, string.Empty, Diagnostic.Error(DiagnosticCodes.PathEscapeDetected, $"Iteration identifier '{iterationId}' contains path separators or alternate data streams and is rejected."));
        }

        // Invalid characters
        if (trimmed.IndexOfAny(Path.GetInvalidPathChars()) >= 0)
        {
            return (false, string.Empty, Diagnostic.Error(DiagnosticCodes.InvalidPath, $"Iteration identifier '{iterationId}' contains invalid characters."));
        }

        // Device names
        if (ReservedDeviceNames.Contains(trimmed))
        {
            return (false, string.Empty, Diagnostic.Error(DiagnosticCodes.InvalidPath, $"Reserved device name '{iterationId}' is rejected."));
        }

        // Grammar check matching TimeFirstIdType (YYYYMMDD-name or YYYYMMDDTHHmmssZ-name in lowercase)
        if (!IterationIdRegex.IsMatch(trimmed))
        {
            return (false, string.Empty, Diagnostic.Error(DiagnosticCodes.InvalidArgument, $"Iteration identifier '{iterationId}' does not match required pattern YYYYMMDD-name or YYYYMMDDTHHmmssZ-name (e.g. 20260823-feature or 20260823T143000Z-feature)."));
        }

        return (true, trimmed, null);
    }

    /// <summary>
    /// Verifies that a target full path is strictly contained within the workspace root full path.
    /// </summary>
    public static bool IsContainedWithin(string rootFullPath, string targetFullPath)
    {
        var normalizedRoot = Path.GetFullPath(rootFullPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var normalizedTarget = Path.GetFullPath(targetFullPath);

        if (string.Equals(normalizedRoot, normalizedTarget, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var prefix = normalizedRoot + Path.DirectorySeparatorChar;
        return normalizedTarget.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Checks both lexical containment and verifies that no intermediate or target reparse points escape the workspace root.
    /// </summary>
    public static (bool IsSafe, Diagnostic? Error) CheckContainmentAndReparsePoints(string workspaceRoot, string targetFullPath)
    {
        var normalizedRoot = Path.GetFullPath(workspaceRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var normalizedTarget = Path.GetFullPath(targetFullPath);

        if (!IsContainedWithin(normalizedRoot, normalizedTarget))
        {
            return (false, Diagnostic.Error(DiagnosticCodes.PathEscapeDetected, $"Path '{targetFullPath}' escapes workspace root."));
        }

        var rel = Path.GetRelativePath(normalizedRoot, normalizedTarget);
        if (rel != "." && !rel.StartsWith("..", StringComparison.Ordinal))
        {
            var current = normalizedRoot;
            var segments = rel.Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries);

            foreach (var segment in segments)
            {
                current = Path.Combine(current, segment);
                if (File.Exists(current) || Directory.Exists(current))
                {
                    var attr = File.GetAttributes(current);
                    if (attr.HasFlag(FileAttributes.ReparsePoint))
                    {
                        try
                        {
                            FileSystemInfo fsi = Directory.Exists(current)
                                ? new DirectoryInfo(current)
                                : new FileInfo(current);

                            var resolved = fsi.ResolveLinkTarget(returnFinalTarget: true);
                            if (resolved == null || !IsContainedWithin(normalizedRoot, resolved.FullName))
                            {
                                return (false, Diagnostic.Error(
                                    DiagnosticCodes.PathEscapeDetected,
                                    $"Reparse point at '{current}' escapes workspace root."));
                            }
                        }
                        catch (Exception ex)
                        {
                            return (false, Diagnostic.Error(
                                DiagnosticCodes.PathEscapeDetected,
                                $"Failed to verify reparse point at '{current}': {ex.Message}"));
                        }
                    }
                }
            }
        }

        return (true, null);
    }

    public static string NormalizeSeparators(string path) =>
        path.Replace('\\', '/');

    private static readonly HashSet<string> AllowedTempFileNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "writer.lock",
        "marker.xml",
        "recovery.xml",
        "spec.xml",
        "tasks.xml"
    };

    private static readonly string[] AllowedTempPrefixes = new[]
    {
        "create_",
        "staging_",
        "tx_",
        "temp_",
        "backup_",
        "schema_sync_",
        "lock_",
        "recovery_"
    };

    /// <summary>
    /// Verifies that a path is a strictly contained, non-escaping, CLI-owned temporary child inside the workspace _tmp directory.
    /// </summary>
    public static bool IsSafeCliTempChild(string workspaceRoot, string candidatePath)
    {
        if (string.IsNullOrWhiteSpace(workspaceRoot) || string.IsNullOrWhiteSpace(candidatePath))
        {
            return false;
        }

        try
        {
            var normalizedRoot = Path.GetFullPath(workspaceRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var tmpDir = Path.Combine(normalizedRoot, "_tmp");
            var normalizedCandidate = Path.GetFullPath(candidatePath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            // Must be strictly inside _tmp (not _tmp itself, not root, not outside)
            if (!IsContainedWithin(tmpDir, normalizedCandidate) || string.Equals(tmpDir, normalizedCandidate, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            // Verify no reparse points escape
            var (isSafe, error) = CheckContainmentAndReparsePoints(tmpDir, normalizedCandidate);
            if (!isSafe || error != null)
            {
                return false;
            }

            // Check relative path from _tmp
            var relFromTmp = Path.GetRelativePath(tmpDir, normalizedCandidate);
            if (relFromTmp == "." || relFromTmp.StartsWith("..", StringComparison.Ordinal))
            {
                return false;
            }

            var segments = relFromTmp.Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length == 0)
            {
                return false;
            }

            var topSegment = segments[0];

            // Top-level segment must match CLI-owned patterns
            var isAllowedTop = AllowedTempFileNames.Contains(topSegment) ||
                               topSegment.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase) ||
                               AllowedTempPrefixes.Any(prefix => topSegment.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));

            return isAllowedTop;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Safely deletes a verified CLI-owned child inside the workspace _tmp directory.
    /// Returns false and refuses to delete if the path is not a verified safe CLI-owned temp child.
    /// </summary>
    public static bool SafeDeleteCliTempEntry(string workspaceRoot, string candidatePath)
    {
        if (!IsSafeCliTempChild(workspaceRoot, candidatePath))
        {
            return false;
        }

        try
        {
            if (File.Exists(candidatePath))
            {
                File.Delete(candidatePath);
                return true;
            }

            if (Directory.Exists(candidatePath))
            {
                Directory.Delete(candidatePath, true);
                return true;
            }

            return true;
        }
        catch
        {
            return false;
        }
    }
}
