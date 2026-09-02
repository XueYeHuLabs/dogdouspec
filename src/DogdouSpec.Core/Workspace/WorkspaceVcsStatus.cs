using System.Diagnostics;
using DogdouSpec.Core.Diagnostics;
using DogdouSpec.Core.Security;
using DogdouSpec.Core.Tasks;

namespace DogdouSpec.Core.Workspace;

public static class WorkspaceVcsStatus
{
    private static readonly char[] LineSeparators = { '\r', '\n' };
    private static readonly string[] RevParseArgs = { "rev-parse", "--is-inside-work-tree" };
    private static readonly string[] StatusPorcelainArgs = { "status", "--porcelain", "-uall" };

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

        // Check if Git is available and this is a git repo
        var (gitAvail, exitCode, stdout, _, _) = TaskScopeVerifier.RunGit(repoRoot, RevParseArgs);
        bool isGit = gitAvail && exitCode == 0 && stdout.Trim().Equals("true", StringComparison.OrdinalIgnoreCase);

        var managedFiles = new List<WorkspaceVcsFileStatus>();
        var uncheckpointed = new List<string>();

        if (!Directory.Exists(workspaceRoot))
        {
            return (true, new WorkspaceVcsStatusResult(workspaceRoot, repoRoot, isGit, true, managedFiles, uncheckpointed), Array.Empty<Diagnostic>());
        }

        // Enumerate local files in workspaceRoot (excluding _tmp)
        var allLocalFiles = Directory.EnumerateFiles(workspaceRoot, "*", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}_tmp{Path.DirectorySeparatorChar}") &&
                        !f.EndsWith($"{Path.DirectorySeparatorChar}_tmp", StringComparison.OrdinalIgnoreCase))
            .ToList();

        // Get git status for workspace directory
        var gitStatusMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (isGit)
        {
            var (statSuccess, statExit, statStdout, _, _) = TaskScopeVerifier.RunGit(
                repoRoot,
                StatusPorcelainArgs);

            if (statSuccess && statExit == 0)
            {
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
                    gitStatusMap[normalizedPath] = statusCode;
                }
            }
        }

        foreach (var file in allLocalFiles)
        {
            var relFromRepo = Path.GetRelativePath(repoRoot, file).Replace('\\', '/');
            var normRel = TaskScopeMatcher.NormalizePath(relFromRepo);

            // Determine if authoritative (spec.xml, tasks.xml, knowledge.xml, backlog.xml)
            var fileName = Path.GetFileName(file);
            bool isAuth = fileName is "spec.xml" or "tasks.xml" or "knowledge.xml" or "backlog.xml";

            string status = "clean";
            if (isGit)
            {
                if (gitStatusMap.TryGetValue(normRel, out var code))
                {
                    if (code.StartsWith("??", StringComparison.Ordinal))
                    {
                        status = "untracked";
                        if (isAuth) uncheckpointed.Add(normRel);
                    }
                    else if (code.Contains('M') || code.Contains('A') || code.Contains('D'))
                    {
                        status = "modified";
                        if (isAuth) uncheckpointed.Add(normRel);
                    }
                }
            }

            managedFiles.Add(new WorkspaceVcsFileStatus(normRel, status, isAuth));
        }

        bool isTransportReady = uncheckpointed.Count == 0;

        var result = new WorkspaceVcsStatusResult(
            workspaceRoot,
            repoRoot,
            isGit,
            isTransportReady,
            managedFiles,
            uncheckpointed);

        return (true, result, Array.Empty<Diagnostic>());
    }

    public static (bool Success, WorkspaceCheckpointPlanResult? Result, IReadOnlyList<Diagnostic> Diagnostics) CreateCheckpointPlan(
        string workspaceRoot)
    {
        var (success, statusResult, diagnostics) = CheckStatus(workspaceRoot);
        if (!success || statusResult == null)
        {
            return (false, null, diagnostics);
        }

        var isSatisfied = statusResult.IsTransportReady;
        var uncheckpointed = statusResult.UncheckpointedFiles;

        var recommendedMsg = uncheckpointed.Count > 0
            ? $"Governance checkpoint: update {uncheckpointed.Count} authoritative .dogdouspec documents"
            : "Governance checkpoint: workspace is clean";

        var plan = new WorkspaceCheckpointPlanResult(
            workspaceRoot,
            statusResult.RepositoryRoot,
            statusResult.IsGitRepository,
            isSatisfied,
            uncheckpointed,
            recommendedMsg);

        return (true, plan, Array.Empty<Diagnostic>());
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
