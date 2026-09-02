using System.Buffers;
using System.Diagnostics;
using System.Xml.Linq;
using DogdouSpec.Core.Diagnostics;
using DogdouSpec.Core.Security;
using DogdouSpec.Core.Validation;
using DogdouSpec.Core.Workspace;

namespace DogdouSpec.Core.Tasks;

/// <summary>
/// Public read-only verifier that compares changed repository paths against a task declared repository scope.
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
    private static readonly string[] GitStatusPorcelainArgs = { "status", "--porcelain", "-uall" };
    private static readonly SearchValues<char> InvalidConcretePathCharacters = SearchValues.Create("<>\"|?*");

    public static (bool Success, TaskScopeResult? Result, IReadOnlyList<Diagnostic> Diagnostics) VerifyScope(
        string workspaceRoot,
        string taskId,
        string? iterationId = null,
        IReadOnlyList<string>? explicitPaths = null,
        string? gitRef = null,
        string? gitRange = null,
        bool worktree = false,
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

        var modeCount = (hasExplicitPaths ? 1 : 0) + (hasGitRef ? 1 : 0) + (hasGitRange ? 1 : 0) + (worktree ? 1 : 0);
        if (modeCount == 0)
        {
            return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.InvalidArgument, "Must specify either explicit --path arguments, --worktree, --git-ref, or --git-range.") });
        }

        if (modeCount > 1)
        {
            return (false, null, new[] { Diagnostic.Error(DiagnosticCodes.InvalidArgument, "Specify either explicit --path arguments, --worktree, --git-ref, or --git-range, not multiple sources.") });
        }

        // Derive repository root (parent of .dogdouspec or workspace root itself)
        var repoRoot = GetRepositoryRoot(workspaceRoot);

        // Collect changed paths
        var (pathsOk, candidatePaths, pathsDiags) = CollectChangedPaths(repoRoot, explicitPaths, gitRef, gitRange, worktree);
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

        // Partition candidate paths into InScope and OutOfScope and record explanations
        var inScope = new List<string>();
        var outOfScope = new List<string>();
        var explanations = new List<ScopePathExplanation>();

        foreach (var p in candidatePaths)
        {
            var exp = TaskScopeMatcher.ExplainPath(p, declaredScopes, forceCaseInsensitive);
            explanations.Add(exp);
            if (exp.InScope)
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
            outOfScope,
            explanations);

        return (true, result, Array.Empty<Diagnostic>());
    }

    private static (bool Success, IReadOnlyList<string> Paths, IReadOnlyList<Diagnostic> Diagnostics) CollectChangedPaths(
        string repoRoot,
        IReadOnlyList<string>? explicitPaths,
        string? gitRef,
        string? gitRange,
        bool worktree)
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

        if (worktree)
        {
            var (gitSuccess, exitCode, stdout, stderr, runDiag) = RunGit(repoRoot, GitStatusPorcelainArgs);
            if (!gitSuccess || runDiag != null)
            {
                return (false, Array.Empty<string>(), new[] { runDiag ?? Diagnostic.Error(DiagnosticCodes.FilesystemError, "Failed to run git status for worktree scope verification.") });
            }

            if (exitCode != 0)
            {
                return (false, Array.Empty<string>(), new[] { Diagnostic.Error(
                    DiagnosticCodes.FilesystemError,
                    $"Git status returned exit code {exitCode}: {stderr.Trim()}") });
            }

            var lines = stdout.Split(GitLineSeparators, StringSplitOptions.RemoveEmptyEntries);
            foreach (var line in lines)
            {
                if (line.Length < 3) continue;
                var raw = line[3..].Trim();
                if (raw.Contains(" -> "))
                {
                    var parts = raw.Split(" -> ");
                    raw = parts[^1].Trim();
                }
                raw = raw.Trim('"');
                if (string.IsNullOrWhiteSpace(raw)) continue;

                var (isValid, normalizedPath, diagnostic) = ValidateAndNormalizeInputPath(repoRoot, raw);
                if (isValid && diagnostic == null)
                {
                    normalizedPaths.Add(normalizedPath);
                }
                else if (diagnostic != null)
                {
                    diagnostics.Add(diagnostic);
                }
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

            diffArgument = $"{leftCommit}{separator}{rightCommit}";
        }

        var (diffSuccess, diffExitCode, diffStdout, diffStderr, diffRunDiagnostic) = RunGit(
            repoRoot,
            new[] { "diff", "--name-only", "-z", "--no-renames", diffArgument });
        if (!diffSuccess || diffRunDiagnostic != null)
        {
            return (false, Array.Empty<string>(), new[] { diffRunDiagnostic ?? Diagnostic.Error(
                DiagnosticCodes.FilesystemError,
                "Failed to run Git for read-only scope verification.") });
        }

        if (diffExitCode != 0)
        {
            return (false, Array.Empty<string>(), new[] { Diagnostic.Error(
                DiagnosticCodes.FilesystemError,
                $"Git diff failed with exit code {diffExitCode}: {diffStderr.Trim()}") });
        }

        var rawPaths = diffStdout.Split('\0', StringSplitOptions.RemoveEmptyEntries);
        foreach (var rawPath in rawPaths)
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

    private static (bool Resolved, string? CommitSha, Diagnostic? Diagnostic) ResolveGitCommit(
        string repoRoot,
        string revision,
        string optionName)
    {
        if (string.IsNullOrWhiteSpace(revision))
        {
            return (false, null, Diagnostic.Error(
                DiagnosticCodes.InvalidArgument,
                $"{optionName} requires a non-empty Git revision."));
        }

        var (success, exitCode, stdout, stderr, runDiagnostic) = RunGit(
            repoRoot,
            new[] { "rev-parse", "--verify", "--end-of-options", $"{revision}^{{commit}}" });
        if (!success || runDiagnostic != null)
        {
            return (false, null, runDiagnostic ?? Diagnostic.Error(
                DiagnosticCodes.FilesystemError,
                $"Failed to resolve Git revision '{revision}' for {optionName}."));
        }

        if (exitCode != 0)
        {
            return (false, null, Diagnostic.Error(
                DiagnosticCodes.InvalidArgument,
                $"Git could not resolve revision '{revision}' for {optionName}: {stderr.Trim()}"));
        }

        var commit = stdout.Trim();
        if (commit.Length != 40 || !commit.All(char.IsAsciiHexDigitLower))
        {
            return (false, null, Diagnostic.Error(
                DiagnosticCodes.InvalidArgument,
                $"Git revision '{revision}' for {optionName} did not resolve to a canonical commit SHA."));
        }

        return (true, commit, null);
    }

    private static (bool Parsed, string? Left, string? Separator, string? Right, Diagnostic? Diagnostic) ParseGitRange(string gitRange)
    {
        if (string.IsNullOrWhiteSpace(gitRange))
        {
            return (false, null, null, null, Diagnostic.Error(
                DiagnosticCodes.InvalidArgument,
                "--git-range requires a non-empty two-reference range expression."));
        }

        var tripleIndex = gitRange.IndexOf("...", StringComparison.Ordinal);
        if (tripleIndex >= 0)
        {
            var left = gitRange[..tripleIndex].Trim();
            var right = gitRange[(tripleIndex + 3)..].Trim();
            if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right) ||
                gitRange.IndexOf("...", tripleIndex + 3, StringComparison.Ordinal) >= 0)
            {
                return (false, null, null, null, Diagnostic.Error(
                    DiagnosticCodes.InvalidArgument,
                    $"Invalid symmetric difference range '{gitRange}'. Must follow '<left>...<right>'."));
            }

            return (true, left, "...", right, null);
        }

        var doubleIndex = gitRange.IndexOf("..", StringComparison.Ordinal);
        if (doubleIndex >= 0)
        {
            var left = gitRange[..doubleIndex].Trim();
            var right = gitRange[(doubleIndex + 2)..].Trim();
            if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right) ||
                gitRange.IndexOf("..", doubleIndex + 2, StringComparison.Ordinal) >= 0)
            {
                return (false, null, null, null, Diagnostic.Error(
                    DiagnosticCodes.InvalidArgument,
                    $"Invalid two-reference range '{gitRange}'. Must follow '<left>..<right>'."));
            }

            return (true, left, "..", right, null);
        }

        return (false, null, null, null, Diagnostic.Error(
            DiagnosticCodes.InvalidArgument,
            $"Invalid --git-range '{gitRange}'. Expected '<left>..<right>' or '<left>...<right>'."));
    }

    public static (bool Success, int ExitCode, string Stdout, string Stderr, Diagnostic? Diagnostic) RunGit(
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

        if (trimmed.StartsWith(@"\\", StringComparison.Ordinal) ||
            trimmed.StartsWith("//", StringComparison.Ordinal) ||
            (trimmed.Length >= 2 && trimmed[1] == ':'))
        {
            return (false, string.Empty, Diagnostic.Error(DiagnosticCodes.PathEscapeDetected, $"Absolute or rooted path '{rawPath}' is rejected. Inputs must be repository-relative canonical paths."));
        }

        if (trimmed.StartsWith('/') || trimmed.StartsWith('\\'))
        {
            return (false, string.Empty, Diagnostic.Error(DiagnosticCodes.PathEscapeDetected, $"Absolute path starting with slash '{rawPath}' is rejected. Inputs must be repository-relative canonical paths."));
        }

        var normalized = TaskScopeMatcher.NormalizePath(trimmed);
        if (normalized.Length == 0 || normalized == "." || normalized.StartsWith('/'))
        {
            return (false, string.Empty, Diagnostic.Error(DiagnosticCodes.InvalidPath, $"Path '{rawPath}' normalizes to empty or invalid path."));
        }

        var segments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Any(s => s == ".."))
        {
            return (false, string.Empty, Diagnostic.Error(DiagnosticCodes.PathTraversalDetected, $"Path '{rawPath}' contains forbidden '..' traversal."));
        }

        if (segments.Any(s => ReservedDeviceNames.Contains(s)))
        {
            return (false, string.Empty, Diagnostic.Error(DiagnosticCodes.InvalidPath, $"Path '{rawPath}' contains a reserved device name."));
        }

        if (segments.Any(s => s.EndsWith('.') || s.EndsWith(' ')))
        {
            return (false, string.Empty, Diagnostic.Error(DiagnosticCodes.InvalidPath, $"Path '{rawPath}' contains segment with trailing dot or space."));
        }

        if (segments.Any(s => s.AsSpan().IndexOfAny(InvalidConcretePathCharacters) >= 0 || s.Contains(':')))
        {
            return (false, string.Empty, Diagnostic.Error(DiagnosticCodes.InvalidPath, $"Path '{rawPath}' contains invalid characters or stream specifiers."));
        }

        return (true, normalized, null);
    }

    private static string GetRepositoryRoot(string workspaceRoot)
    {
        var dir = new DirectoryInfo(workspaceRoot);
        if (string.Equals(dir.Name, ".dogdouspec", StringComparison.OrdinalIgnoreCase) && dir.Parent != null)
        {
            return dir.Parent.FullName;
        }

        return workspaceRoot;
    }
}
