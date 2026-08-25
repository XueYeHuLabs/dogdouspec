using System.Buffers;
using System.Diagnostics;
using System.Xml.Linq;
using DogdouSpec.Core.Diagnostics;
using DogdouSpec.Core.Security;
using DogdouSpec.Core.Validation;
using DogdouSpec.Core.Workspace;

namespace DogdouSpec.Core.Tasks;

/// <summary>
/// Public read-only verifier that compares changed repository paths against a task's declared scope.
/// Never mutates Git or workspace state.
/// </summary>
public static class TaskScopeVerifier
{
    private static readonly HashSet<string> ReservedDeviceNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
    };

    private const int GitTimeoutMilliseconds = 30_000;
    private static readonly char[] GitLineSeparators = { '\r', '\n' };
    private static readonly SearchValues<char> InvalidConcretePathCharacters = SearchValues.Create("<>\"|?*");

    public static (bool Success, TaskScopeResult? Result, IReadOnlyList<Diagnostic> Diagnostics) VerifyScope(
        string workspaceRoot,
        string taskId,
        string? iterationId = null,
        IReadOnlyList<string>? explicitPaths = null,
        string? gitRef = null,
        string? gitRange = null,
        bool? forceCaseInsensitive = null)
    {
        if (string.IsNullOrWhiteSpace(workspaceRoot))
        {
            return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.InvalidArgument, "Workspace root cannot be empty.") });
        }

        var (isWsSafe, wsErr) = PathSecurity.VerifyWorkspaceDirectorySecurity(workspaceRoot);
        if (!isWsSafe || wsErr != null)
        {
            return (false, null, new[] { wsErr ?? Diagnostic.Error(DiagnosticCodes.PathEscapeDetected, "Workspace directory security verification failed.") });
        }

        if (string.IsNullOrWhiteSpace(taskId))
        {
            return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.InvalidArgument, "Task ID cannot be empty.") });
        }

        if (!ProjectSemanticIndex.IsValidTimeFirstId(taskId))
        {
            return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.InvalidIdGrammar, $"Task ID '{taskId}' does not conform to the time-first ID grammar.") });
        }

        // Validate mutually exclusive input modes
        var hasExplicitPaths = explicitPaths is { Count: > 0 };
        var hasGitRef = !string.IsNullOrWhiteSpace(gitRef);
        var hasGitRange = !string.IsNullOrWhiteSpace(gitRange);

        var modeCount = (hasExplicitPaths ? 1 : 0) + (hasGitRef ? 1 : 0) + (hasGitRange ? 1 : 0);
        if (modeCount == 0)
        {
            return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.InvalidArgument, "Must specify either explicit --path arguments, --git-ref, or --git-range.") });
        }

        if (modeCount > 1)
        {
            return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.InvalidArgument, "Specify either explicit --path arguments, --git-ref, or --git-range, not multiple sources.") });
        }

        // Derive repository root (parent of .dogdouspec or workspace root itself)
        var repoRoot = GetRepositoryRoot(workspaceRoot);

        // Collect changed paths
        var (pathsOk, candidatePaths, pathsDiags) = CollectChangedPaths(repoRoot, explicitPaths, gitRef, gitRange);
        if (!pathsOk || pathsDiags.Count > 0)
        {
            return (false, null, pathsDiags);
        }

        // Load project index to resolve task and declared scope
        var (enumSuccess, allDocs, enumDiags) = WorkspaceDiscovery.EnumerateDocuments(workspaceRoot, iterationId);
        if (!enumSuccess || enumDiags.Count > 0)
        {
            return (false, null, enumDiags);
        }

        var parsedDocs = new List<(ManagedDocument Document, XDocument XDoc)>();
        foreach (var doc in allDocs)
        {
            try
            {
                using var stream = File.OpenRead(doc.FullPath);
                using var reader = SecureXmlReaderFactory.CreateReader(stream);
                var xDoc = XDocument.Load(reader, LoadOptions.SetLineInfo);
                parsedDocs.Add((doc, xDoc));
            }
            catch (Exception ex)
            {
                return (false, null, new[] { Diagnostic.Error(
                    DiagnosticCodes.XmlParseError,
                    $"Failed to parse XML document '{doc.RelativePath}' during scope verification: {ex.Message}",
                    doc.RelativePath) });
            }
        }

        var index = ProjectSemanticIndex.Build(parsedDocs);

        if (!index.ObjectsById.TryGetValue(taskId, out var matchingObjects) || matchingObjects.Count == 0)
        {
            return (false, null, new[] { Diagnostic.Error(
                DiagnosticCodes.DocumentNotFound,
                $"Task '{taskId}' was not found in workspace.",
                iterationId != null ? $"{iterationId}/tasks.xml" : null) });
        }

        if (matchingObjects.Count > 1)
        {
            return (false, null, new[] { Diagnostic.Error(
                DiagnosticCodes.AmbiguousReference,
                $"Task '{taskId}' is ambiguous ({matchingObjects.Count} elements found in workspace).") });
        }

        var taskObj = matchingObjects[0];
        if (!string.Equals(taskObj.ElementName, "task", StringComparison.Ordinal))
        {
            return (false, null, new[] { Diagnostic.Error(
                DiagnosticCodes.InvalidReferenceTargetType,
                $"Target '{taskId}' is a <{taskObj.ElementName}>, not a <task>.") });
        }

        var taskElem = taskObj.Element;
        var scopeElem = taskElem.Element("scope");
        var resolvedIterationId = taskObj.Document.IterationId ?? iterationId ?? string.Empty;

        var declaredScopes = TaskScopeMatcher.ParseScopes(scopeElem);

        // Partition candidate paths into InScope and OutOfScope
        var inScope = new List<string>();
        var outOfScope = new List<string>();

        foreach (var p in candidatePaths)
        {
            if (TaskScopeMatcher.IsPathInScope(p, declaredScopes, forceCaseInsensitive))
            {
                inScope.Add(p);
            }
            else
            {
                outOfScope.Add(p);
            }
        }

        // Stable deterministic ordering
        inScope.Sort(StringComparer.Ordinal);
        outOfScope.Sort(StringComparer.Ordinal);

        var result = new TaskScopeResult(
            taskId,
            resolvedIterationId,
            scopeElem,
            inScope,
            outOfScope);

        return (true, result, Array.Empty<Diagnostic>());
    }

    private static (bool Success, IReadOnlyList<string> Paths, IReadOnlyList<Diagnostic> Diagnostics) CollectChangedPaths(
        string repoRoot,
        IReadOnlyList<string>? explicitPaths,
        string? gitRef,
        string? gitRange)
    {
        var diagnostics = new List<Diagnostic>();
        var normalizedPaths = new HashSet<string>(StringComparer.Ordinal);
        if (explicitPaths is { Count: > 0 })
        {
            foreach (var rawPath in explicitPaths)
            {
                var (isValid, normalizedPath, diagnostic) = ValidateAndNormalizeInputPath(repoRoot, rawPath);
                if (!isValid || diagnostic != null)
                {
                    diagnostics.Add(diagnostic!);
                    continue;
                }

                normalizedPaths.Add(normalizedPath);
            }

            return diagnostics.Count == 0
                ? (true, normalizedPaths.OrderBy(path => path, StringComparer.Ordinal).ToArray(), Array.Empty<Diagnostic>())
                : (false, Array.Empty<string>(), diagnostics);
        }

        string diffArgument;
        if (!string.IsNullOrWhiteSpace(gitRef))
        {
            var (resolved, commit, diagnostic) = ResolveGitCommit(repoRoot, gitRef, "--git-ref");
            if (!resolved || diagnostic != null)
            {
                return (false, Array.Empty<string>(), new[] { diagnostic! });
            }

            diffArgument = commit!;
        }
        else
        {
            var (parsed, left, separator, right, parseDiagnostic) = ParseGitRange(gitRange!);
            if (!parsed || parseDiagnostic != null)
            {
                return (false, Array.Empty<string>(), new[] { parseDiagnostic! });
            }

            var (leftResolved, leftCommit, leftDiagnostic) = ResolveGitCommit(repoRoot, left!, "--git-range left revision");
            if (!leftResolved || leftDiagnostic != null)
            {
                return (false, Array.Empty<string>(), new[] { leftDiagnostic! });
            }

            var (rightResolved, rightCommit, rightDiagnostic) = ResolveGitCommit(repoRoot, right!, "--git-range right revision");
            if (!rightResolved || rightDiagnostic != null)
            {
                return (false, Array.Empty<string>(), new[] { rightDiagnostic! });
            }

            diffArgument = leftCommit + separator + rightCommit;
        }

        var run = RunGit(repoRoot, new[]
        {
            "diff", "--name-only", "-z", "--no-renames", "--no-ext-diff", "--no-textconv", diffArgument, "--"
        });
        if (!run.Success || run.Diagnostic != null)
        {
            return (false, Array.Empty<string>(), new[] { run.Diagnostic! });
        }

        if (run.ExitCode != 0)
        {
            var detail = string.IsNullOrWhiteSpace(run.StandardError)
                ? $"git diff exited with code {run.ExitCode}"
                : run.StandardError.Trim();
            return (false, Array.Empty<string>(), new[]
            {
                Diagnostic.Error(DiagnosticCodes.InvalidArgument, $"Failed to retrieve Git diff: {detail}")
            });
        }

        foreach (var rawPath in run.StandardOutput.Split('\0', StringSplitOptions.RemoveEmptyEntries))
        {
            var (isValid, normalizedPath, diagnostic) = ValidateAndNormalizeInputPath(repoRoot, rawPath);
            if (!isValid || diagnostic != null)
            {
                diagnostics.Add(diagnostic!);
                continue;
            }

            normalizedPaths.Add(normalizedPath);
        }

        return diagnostics.Count == 0
            ? (true, normalizedPaths.OrderBy(path => path, StringComparer.Ordinal).ToArray(), Array.Empty<Diagnostic>())
            : (false, Array.Empty<string>(), diagnostics);
    }

    private static (bool Success, string? Commit, Diagnostic? Diagnostic) ResolveGitCommit(
        string repoRoot,
        string revision,
        string argumentName)
    {
        if (string.IsNullOrWhiteSpace(revision) ||
            revision.Length > 512 ||
            revision.StartsWith('-') ||
            revision.Any(char.IsControl) ||
            revision.Any(char.IsWhiteSpace) ||
            revision.Contains("..", StringComparison.Ordinal))
        {
            return (false, null, Diagnostic.Error(
                DiagnosticCodes.InvalidArgument,
                $"{argumentName} must be one non-option Git revision without whitespace, control characters, or range syntax."));
        }

        var run = RunGit(repoRoot, new[]
        {
            "rev-parse", "--verify", "--quiet", "--end-of-options", revision + "^{commit}"
        });
        if (!run.Success || run.Diagnostic != null)
        {
            return (false, null, run.Diagnostic);
        }

        var values = run.StandardOutput.Split(GitLineSeparators, StringSplitOptions.RemoveEmptyEntries);
        if (run.ExitCode != 0 || values.Length != 1 || values[0].Length is < 40 or > 64 || !values[0].All(Uri.IsHexDigit))
        {
            return (false, null, Diagnostic.Error(
                DiagnosticCodes.InvalidArgument,
                $"{argumentName} does not resolve to exactly one commit."));
        }

        return (true, values[0].ToLowerInvariant(), null);
    }

    private static (bool Success, string? Left, string? Separator, string? Right, Diagnostic? Diagnostic) ParseGitRange(string range)
    {
        if (string.IsNullOrWhiteSpace(range) ||
            range.Length > 1024 ||
            range.Any(char.IsControl) ||
            range.Any(char.IsWhiteSpace))
        {
            return (false, null, null, null, Diagnostic.Error(
                DiagnosticCodes.InvalidArgument,
                "--git-range must be one whitespace-free A..B or A...B expression."));
        }

        var separator = range.Contains("...", StringComparison.Ordinal) ? "..." : "..";
        var parts = range.Split(new[] { separator }, StringSplitOptions.None);
        if (parts.Length != 2 ||
            string.IsNullOrEmpty(parts[0]) ||
            string.IsNullOrEmpty(parts[1]) ||
            parts[0].Contains("..", StringComparison.Ordinal) ||
            parts[1].Contains("..", StringComparison.Ordinal))
        {
            return (false, null, null, null, Diagnostic.Error(
                DiagnosticCodes.InvalidArgument,
                "--git-range must contain exactly one A..B or A...B separator."));
        }

        return (true, parts[0], separator, parts[1], null);
    }

    private static (bool Success, int ExitCode, string StandardOutput, string StandardError, Diagnostic? Diagnostic) RunGit(
        string repoRoot,
        IReadOnlyList<string> arguments)
    {
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "git",
                WorkingDirectory = repoRoot,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            foreach (var argument in arguments)
            {
                startInfo.ArgumentList.Add(argument);
            }

            using var process = new Process { StartInfo = startInfo };
            process.Start();
            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(GitTimeoutMilliseconds))
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (InvalidOperationException)
                {
                }

                process.WaitForExit();
                Task.WaitAll(stdoutTask, stderrTask);
                return (false, -1, stdoutTask.Result, stderrTask.Result, Diagnostic.Error(
                    DiagnosticCodes.FilesystemError,
                    $"Git command exceeded the {GitTimeoutMilliseconds / 1000}-second read-only scope-verification limit."));
            }

            Task.WaitAll(stdoutTask, stderrTask);
            return (true, process.ExitCode, stdoutTask.Result, stderrTask.Result, null);
        }
        catch (Exception ex)
        {
            return (false, -1, string.Empty, string.Empty, Diagnostic.Error(
                DiagnosticCodes.FilesystemError,
                $"Failed to execute Git for read-only scope verification: {ex.Message}"));
        }
    }

    /// <summary>
    /// Validates and normalizes an input changed path.
    /// Rejects traversal, absolute, device, alternate data stream, and workspace/repo escaping paths.
    /// </summary>
    public static (bool IsValid, string NormalizedPath, Diagnostic? Error) ValidateAndNormalizeInputPath(
        string repoRoot,
        string rawPath)
    {
        if (string.IsNullOrWhiteSpace(rawPath))
        {
            return (false, string.Empty, Diagnostic.Error(DiagnosticCodes.InvalidArgument, "Path cannot be empty."));
        }

        var trimmed = rawPath.Trim();
        if (!string.Equals(rawPath, trimmed, StringComparison.Ordinal) || trimmed.Any(char.IsControl))
        {
            return (false, string.Empty, Diagnostic.Error(
                DiagnosticCodes.InvalidPath,
                $"Path '{rawPath}' contains leading or trailing whitespace or control characters."));
        }

        // Check for device namespaces, UNC, or drive roots
        if (trimmed.StartsWith(@"\\", StringComparison.Ordinal) ||
            trimmed.StartsWith("//", StringComparison.Ordinal) ||
            (trimmed.Length >= 2 && trimmed[1] == ':'))
        {
            return (false, string.Empty, Diagnostic.Error(DiagnosticCodes.PathEscapeDetected, $"Absolute or rooted path '{rawPath}' is rejected. Inputs must be repository-relative canonical paths."));
        }

        // Check leading slash
        if (trimmed.StartsWith('/') || trimmed.StartsWith('\\'))
        {
            return (false, string.Empty, Diagnostic.Error(DiagnosticCodes.PathEscapeDetected, $"Absolute path starting with slash '{rawPath}' is rejected. Inputs must be repository-relative canonical paths."));
        }

        // Traversal checks on raw input
        if (trimmed == ".." || trimmed.StartsWith("../", StringComparison.Ordinal) || trimmed.StartsWith(@"..\", StringComparison.Ordinal) || trimmed.Contains("/../") || trimmed.Contains(@"\..\") || trimmed.EndsWith("/..", StringComparison.Ordinal) || trimmed.EndsWith(@"\..", StringComparison.Ordinal))
        {
            return (false, string.Empty, Diagnostic.Error(DiagnosticCodes.PathTraversalDetected, $"Path '{rawPath}' contains path traversal and is rejected."));
        }

        // Alternate data stream syntax
        if (trimmed.Contains(':'))
        {
            return (false, string.Empty, Diagnostic.Error(DiagnosticCodes.InvalidPath, $"Alternate data stream syntax in path '{rawPath}' is rejected."));
        }

        // Invalid characters
        if (trimmed.IndexOfAny(Path.GetInvalidPathChars()) >= 0)
        {
            return (false, string.Empty, Diagnostic.Error(DiagnosticCodes.InvalidPath, $"Path '{rawPath}' contains invalid characters."));
        }

        var normalized = TaskScopeMatcher.NormalizePath(trimmed);
        if (normalized is "" or "." || normalized.StartsWith('/'))
        {
            return (false, string.Empty, Diagnostic.Error(
                DiagnosticCodes.InvalidPath,
                $"Path '{rawPath}' does not identify a concrete repository-relative path."));
        }

        // Segment-level validation
        var segments = normalized.Split('/');
        foreach (var seg in segments)
        {
            if (seg == "..")
            {
                return (false, string.Empty, Diagnostic.Error(DiagnosticCodes.PathTraversalDetected, $"Path '{rawPath}' contains traversal segment '..' and is rejected."));
            }

            if (seg.AsSpan().IndexOfAny(InvalidConcretePathCharacters) >= 0)
            {
                return (false, string.Empty, Diagnostic.Error(
                    DiagnosticCodes.InvalidPath,
                    $"Path '{rawPath}' contains wildcard or Windows-unsafe characters."));
            }

            if (seg.Length > 1 && (seg.EndsWith(' ') || seg.EndsWith('.')))
            {
                return (false, string.Empty, Diagnostic.Error(
                    DiagnosticCodes.InvalidPath,
                    $"Path '{rawPath}' contains a segment ending in a space or period, which is not Windows-safe."));
            }

            var baseName = Path.GetFileNameWithoutExtension(seg);
            if (ReservedDeviceNames.Contains(baseName) || ReservedDeviceNames.Contains(seg))
            {
                return (false, string.Empty, Diagnostic.Error(DiagnosticCodes.InvalidPath, $"Reserved device name '{seg}' in path '{rawPath}' is rejected."));
            }
        }

        // Physical containment and reparse check if path exists
        var fullPath = Path.Combine(repoRoot, normalized.Replace('/', Path.DirectorySeparatorChar));
        var (isSafe, contErr) = PathSecurity.CheckContainmentAndReparsePoints(repoRoot, fullPath);
        if (!isSafe || contErr != null)
        {
            return (false, string.Empty, contErr ?? Diagnostic.Error(DiagnosticCodes.PathEscapeDetected, $"Path '{rawPath}' escapes repository root."));
        }

        return (true, normalized, null);
    }

    private static string GetRepositoryRoot(string workspaceRoot)
    {
        var normalized = Path.GetFullPath(workspaceRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (Path.GetFileName(normalized) == ".dogdouspec")
        {
            var parent = Directory.GetParent(normalized);
            if (parent != null)
            {
                return parent.FullName;
            }
        }

        return normalized;
    }
}
