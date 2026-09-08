using System.Diagnostics;
using DogdouSpec.Core.Diagnostics;
using DogdouSpec.Core.Security;
using DogdouSpec.Core.Tasks;

namespace DogdouSpec.Core.Workspace;

public static class WorkspaceVcsStatus
{
    private static readonly char[] LineSeparators = { '\r', '\n' };
    private static readonly string[] RevParseArgs = { "rev-parse", "--is-inside-work-tree" };
    private static readonly string[] StatusPorcelainArgs = { "status", "--porcelain", "-uall", "--ignored=matching" };
    private static readonly string[] LsFilesArgs = { "ls-files" };

    public static (bool Success, WorkspaceVcsStatusResult? Result, IReadOnlyList<Diagnostic> Diagnostics) CheckStatus(
        string workspaceRoot)
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

        var repoRoot = GetRepositoryRoot(workspaceRoot);

        var diagnostics = new List<Diagnostic>();
        // Check if Git is available and this is a git repo
        var (gitAvail, exitCode, stdout, stderr, gitDiag) = TaskScopeVerifier.RunGit(repoRoot, RevParseArgs);
        bool isGit = gitAvail && exitCode == 0 && stdout.Trim().Equals("true", StringComparison.OrdinalIgnoreCase);

        if (!gitAvail)
        {
            diagnostics.Add(gitDiag ?? Diagnostic.Error(DiagnosticCodes.FilesystemError, "Git executable is unavailable or failed to execute."));
        }
        else if (exitCode != 0 && !stderr.Contains("not a git repository", StringComparison.OrdinalIgnoreCase))
        {
            var errorDetail = !string.IsNullOrWhiteSpace(stderr) ? stderr.Trim() : $"git rev-parse exited with code {exitCode}";
            diagnostics.Add(Diagnostic.Error(DiagnosticCodes.FilesystemError, $"Git inspection failed: {errorDetail}"));
        }

        var managedFiles = new List<WorkspaceVcsFileStatus>();
        var uncheckpointed = new List<string>();

        if (!Directory.Exists(workspaceRoot))
        {
            return (true, new WorkspaceVcsStatusResult(workspaceRoot, repoRoot, isGit, false, managedFiles, uncheckpointed), Array.Empty<Diagnostic>());
        }

        // Enumerate local files in workspaceRoot (excluding _tmp relative to workspaceRoot)
        var allLocalFiles = Directory.EnumerateFiles(workspaceRoot, "*", SearchOption.AllDirectories)
            .Where(f =>
            {
                var relFromWs = Path.GetRelativePath(workspaceRoot, f).Replace('\\', '/');
                var segments = relFromWs.Split('/', StringSplitOptions.RemoveEmptyEntries);
                return !segments.Any(s => string.Equals(s, "_tmp", StringComparison.OrdinalIgnoreCase));
            })
            .OrderBy(f => f.Replace('\\', '/'), StringComparer.Ordinal)
            .ToList();

        // Get git status for workspace directory
        bool gitStatusSucceeded = false;
        var gitStatusMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var ignoredDirectories = new List<string>();
        var untrackedDirectories = new List<string>();
        if (isGit)
        {
            var (statSuccess, statExit, statStdout, statStderr, statDiag) = TaskScopeVerifier.RunGit(
                repoRoot,
                StatusPorcelainArgs);

            if (statSuccess && statExit == 0)
            {
                gitStatusSucceeded = true;
                var lines = statStdout.Split(LineSeparators, StringSplitOptions.RemoveEmptyEntries);
                foreach (var line in lines)
                {
                    if (line.Length < 3) continue;
                    var statusCode = line[..2];
                    var path = line[3..].Trim().Trim('"');
                    if (path.Contains(" -> "))
                    {
                        path = path.Split(" -> ")[^1].Trim().Trim('"');
                    }
                    var normalizedPath = TaskScopeMatcher.NormalizePath(path);
                    bool isDir = path.EndsWith('/') || path.EndsWith('\\');
                    if (!isDir && !string.IsNullOrWhiteSpace(normalizedPath))
                    {
                        var fullPath = Path.Combine(repoRoot, normalizedPath);
                        isDir = Directory.Exists(fullPath);
                    }

                    if (isDir)
                    {
                        if (statusCode.StartsWith("!!", StringComparison.Ordinal))
                        {
                            ignoredDirectories.Add(normalizedPath);
                        }
                        else if (statusCode.StartsWith("??", StringComparison.Ordinal))
                        {
                            untrackedDirectories.Add(normalizedPath);
                        }
                    }
                    else
                    {
                        gitStatusMap[normalizedPath] = statusCode;
                    }
                }
            }
            else
            {
                var errorDetail = !string.IsNullOrWhiteSpace(statStderr)
                    ? statStderr.Trim()
                    : (statDiag?.Message ?? $"git status exited with code {statExit}");
                diagnostics.Add(Diagnostic.Error(
                    DiagnosticCodes.FilesystemError,
                    $"Git status execution failed: {errorDetail}"));
            }
        }

        // Query tracked files via git ls-files
        bool gitLsFilesSucceeded = false;
        var trackedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (isGit)
        {
            var (lsSuccess, lsExit, lsStdout, lsStderr, lsDiag) = TaskScopeVerifier.RunGit(
                repoRoot,
                LsFilesArgs);

            if (lsSuccess && lsExit == 0)
            {
                gitLsFilesSucceeded = true;
                var lines = lsStdout.Split(LineSeparators, StringSplitOptions.RemoveEmptyEntries);
                foreach (var line in lines)
                {
                    var norm = TaskScopeMatcher.NormalizePath(line.Trim().Trim('"'));
                    if (!string.IsNullOrEmpty(norm))
                    {
                        trackedFiles.Add(norm);
                    }
                }
            }
            else
            {
                var errorDetail = !string.IsNullOrWhiteSpace(lsStderr)
                    ? lsStderr.Trim()
                    : (lsDiag?.Message ?? $"git ls-files exited with code {lsExit}");
                diagnostics.Add(Diagnostic.Error(
                    DiagnosticCodes.FilesystemError,
                    $"Git ls-files execution failed: {errorDetail}"));
            }
        }

        foreach (var file in allLocalFiles)
        {
            var relFromRepo = Path.GetRelativePath(repoRoot, file).Replace('\\', '/');
            var normRel = TaskScopeMatcher.NormalizePath(relFromRepo);

            // Determine if authoritative (spec.xml, tasks.xml, knowledge.xml, backlog.xml)
            var fileName = Path.GetFileName(file);
            bool isAuth = fileName is "spec.xml" or "tasks.xml" or "knowledge.xml" or "backlog.xml";

            string status = "unknown";
            if (isGit && gitStatusSucceeded && gitLsFilesSucceeded)
            {
                if (gitStatusMap.TryGetValue(normRel, out var code))
                {
                    if (code.StartsWith("??", StringComparison.Ordinal))
                    {
                        status = "untracked";
                        if (isAuth) uncheckpointed.Add(normRel);
                    }
                    else if (code.StartsWith("!!", StringComparison.Ordinal))
                    {
                        status = "ignored";
                        if (isAuth) uncheckpointed.Add(normRel);
                    }
                    else if (code[0] != ' ' && code[1] == ' ')
                    {
                        status = "staged";
                        if (isAuth) uncheckpointed.Add(normRel);
                    }
                    else if (code[0] == 'D' || code[1] == 'D')
                    {
                        status = "deleted";
                        if (isAuth) uncheckpointed.Add(normRel);
                    }
                    else
                    {
                        status = "modified";
                        if (isAuth) uncheckpointed.Add(normRel);
                    }
                }
                else if (ignoredDirectories.Any(dir => IsInDirectory(normRel, dir)))
                {
                    if (trackedFiles.Contains(normRel))
                    {
                        status = "clean";
                    }
                    else
                    {
                        status = "ignored";
                        if (isAuth) uncheckpointed.Add(normRel);
                    }
                }
                else if (untrackedDirectories.Any(dir => IsInDirectory(normRel, dir)))
                {
                    if (trackedFiles.Contains(normRel))
                    {
                        status = "clean";
                    }
                    else
                    {
                        status = "untracked";
                        if (isAuth) uncheckpointed.Add(normRel);
                    }
                }
                else if (trackedFiles.Contains(normRel))
                {
                    status = "clean";
                }
                else
                {
                    status = "untracked";
                    if (isAuth) uncheckpointed.Add(normRel);
                }
            }
            else
            {
                // Non-Git workspace OR Git status/ls-files failed (degraded)
                // Fail-closed: mark every authoritative document uncheckpointed/unknown
                if (isAuth)
                {
                    uncheckpointed.Add(normRel);
                }
            }

            managedFiles.Add(new WorkspaceVcsFileStatus(normRel, status, isAuth));
        }

        bool isTransportReady = isGit && gitStatusSucceeded && gitLsFilesSucceeded && uncheckpointed.Count == 0;

        var result = new WorkspaceVcsStatusResult(
            workspaceRoot,
            repoRoot,
            isGit,
            isTransportReady,
            managedFiles,
            uncheckpointed);

        bool success = !diagnostics.Any(d => d.Code == DiagnosticCodes.FilesystemError);
        return (success, result, diagnostics);
    }

    private static bool IsInDirectory(string filePath, string dirPath)
    {
        if (string.IsNullOrEmpty(dirPath)) return false;
        if (dirPath == ".") return true;
        return filePath.Equals(dirPath, StringComparison.OrdinalIgnoreCase) ||
               filePath.StartsWith(dirPath + "/", StringComparison.OrdinalIgnoreCase);
    }

    public static (bool Success, WorkspaceCheckpointPlanResult? Result, IReadOnlyList<Diagnostic> Diagnostics) CreateCheckpointPlan(
        string workspaceRoot)
    {
        var (success, statusResult, diagnostics) = CheckStatus(workspaceRoot);
        if (statusResult == null)
        {
            return (false, null, diagnostics);
        }

        var isSatisfied = statusResult.IsTransportReady;
        var uncheckpointed = statusResult.UncheckpointedFiles;

        var recommendedMsg = uncheckpointed.Count > 0
            ? $"Governance checkpoint: update {uncheckpointed.Count} authoritative .dogdouspec documents"
            : (isSatisfied
                ? "Governance checkpoint: workspace is clean"
                : "Governance checkpoint: transport evidence unavailable");

        var plan = new WorkspaceCheckpointPlanResult(
            workspaceRoot,
            statusResult.RepositoryRoot,
            statusResult.IsGitRepository,
            isSatisfied,
            uncheckpointed,
            recommendedMsg);

        return (success, plan, diagnostics);
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
