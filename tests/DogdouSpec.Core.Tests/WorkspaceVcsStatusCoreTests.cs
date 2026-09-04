using System.Diagnostics;
using DogdouSpec.Core.Diagnostics;
using DogdouSpec.Core.Iterations;
using DogdouSpec.Core.Workspace;

namespace DogdouSpec.Core.Tests;

[TestClass]
public sealed class WorkspaceVcsStatusCoreTests
{
    private string _tempDir = null!;
    private string _workspaceRoot = null!;
    private const string TestIterationId = "20260904-vcs-core-test";

    [TestInitialize]
    public void SetUp()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "DogdouSpec_VcsCore_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        WorkspaceInitializer.Initialize(_tempDir, _tempDir);
        _workspaceRoot = Path.Combine(_tempDir, ".dogdouspec");
        IterationCreator.Create(_workspaceRoot, TestIterationId, "feature", activate: true);
    }

    [TestCleanup]
    public void TearDown()
    {
        if (Directory.Exists(_tempDir))
        {
            try { Directory.Delete(_tempDir, true); } catch { }
        }
    }

    [TestMethod]
    public void NonGitWorkspace_IsLocallyDurable_NotTransportReady_AndListsAuthoritativeFilesAsUncheckpointed()
    {
        var (statusOk, statusRes, statusDiags) = WorkspaceVcsStatus.CheckStatus(_workspaceRoot);

        Assert.IsTrue(statusOk);
        Assert.AreEqual(0, statusDiags.Count);
        Assert.IsNotNull(statusRes);
        Assert.IsFalse(statusRes.IsGitRepository);
        Assert.IsFalse(statusRes.IsTransportReady);
        Assert.IsTrue(statusRes.UncheckpointedFiles.Count > 0);

        // Authoritative files must be listed as uncheckpointed
        var uncheckpointed = statusRes.UncheckpointedFiles.ToList();
        Assert.IsTrue(uncheckpointed.Any(f => f.EndsWith("backlog.xml", StringComparison.OrdinalIgnoreCase)));
        Assert.IsTrue(uncheckpointed.Any(f => f.EndsWith("knowledge.xml", StringComparison.OrdinalIgnoreCase)));
        Assert.IsTrue(uncheckpointed.Any(f => f.EndsWith("spec.xml", StringComparison.OrdinalIgnoreCase)));
        Assert.IsTrue(uncheckpointed.Any(f => f.EndsWith("tasks.xml", StringComparison.OrdinalIgnoreCase)));

        // Non-authoritative files like schemas and gitignore must NOT be in uncheckpointed
        Assert.IsFalse(uncheckpointed.Any(f => f.EndsWith(".xsd", StringComparison.OrdinalIgnoreCase)));
        Assert.IsFalse(uncheckpointed.Any(f => f.EndsWith(".gitignore", StringComparison.OrdinalIgnoreCase)));

        var humanStatus = statusRes.ToHumanString();
        Assert.IsTrue(humanStatus.Contains("Git Repository:    No"));
        Assert.IsTrue(humanStatus.Contains("Transport Ready:   NOT TRANSPORT-READY (no Git repository — workspace is locally durable only)"));

        var (planOk, planRes, planDiags) = WorkspaceVcsStatus.CreateCheckpointPlan(_workspaceRoot);

        Assert.IsTrue(planOk);
        Assert.AreEqual(0, planDiags.Count);
        Assert.IsNotNull(planRes);
        Assert.IsFalse(planRes.IsGitRepository);
        Assert.IsFalse(planRes.IsSatisfied);
        Assert.IsTrue(planRes.UncheckpointedFiles.Count > 0);

        var humanPlan = planRes.ToHumanString();
        Assert.IsTrue(humanPlan.Contains("Status:            ACTION REQUIRED (No Git repository — workspace is locally durable only)"));
        Assert.IsTrue(humanPlan.Contains("(Establish a Git repository before checkpointing authoritative files)"));
        Assert.IsTrue(humanPlan.Contains("  git init"));
        Assert.IsTrue(humanPlan.Contains("  git add"));
        Assert.IsTrue(humanPlan.Contains("  git commit -m"));

        var xmlPlan = planRes.ToXmlString();
        Assert.IsTrue(xmlPlan.Contains("is_git=\"false\""));
        Assert.IsTrue(xmlPlan.Contains("satisfied=\"false\""));
        Assert.IsTrue(xmlPlan.Contains("spec.xml"));
        Assert.IsTrue(xmlPlan.Contains("tasks.xml"));

        var xmlStatus = statusRes.ToXmlString();
        Assert.IsTrue(xmlStatus.Contains("is_git=\"false\""));
        Assert.IsTrue(xmlStatus.Contains("transport_ready=\"false\""));
    }

    [TestMethod]
    public void GitRepository_WhenStatusFails_IsNotTransportReady_AndEmitsUsefulDiagnostic()
    {
        if (!IsGitInstalled())
        {
            Assert.Inconclusive("Git is not installed or accessible in this environment.");
        }

        var repoRoot = _tempDir;
        InitGitAndCommitAll(repoRoot);

        // Corrupt .git/index so git status fails
        var indexPath = Path.Combine(repoRoot, ".git", "index");
        File.WriteAllText(indexPath, "CORRUPT_INDEX_CONTENT_TRIGGERING_STATUS_FAILURE");

        var (statusOk, statusRes, statusDiags) = WorkspaceVcsStatus.CheckStatus(_workspaceRoot);

        Assert.IsFalse(statusOk);
        Assert.IsNotNull(statusRes);
        Assert.IsTrue(statusRes.IsGitRepository);
        Assert.IsFalse(statusRes.IsTransportReady);
        Assert.IsTrue(statusDiags.Count > 0);
        Assert.AreEqual(DiagnosticCodes.FilesystemError, statusDiags[0].Code);
        Assert.IsTrue(statusDiags[0].Message.Contains("Git status execution failed"));
        Assert.IsTrue(statusRes.UncheckpointedFiles.Count > 0);
        Assert.IsTrue(statusRes.UncheckpointedFiles.Any(f => f.EndsWith("spec.xml", StringComparison.OrdinalIgnoreCase)));
        Assert.IsTrue(statusRes.UncheckpointedFiles.Any(f => f.EndsWith("tasks.xml", StringComparison.OrdinalIgnoreCase)));
        Assert.IsTrue(statusRes.UncheckpointedFiles.Any(f => f.EndsWith("backlog.xml", StringComparison.OrdinalIgnoreCase)));
        Assert.IsTrue(statusRes.UncheckpointedFiles.Any(f => f.EndsWith("knowledge.xml", StringComparison.OrdinalIgnoreCase)));
        Assert.IsTrue(statusRes.ManagedFiles.Where(f => f.IsAuthoritative).All(f => f.Status == "unknown"));

        var humanStatus = statusRes.ToHumanString();
        Assert.IsTrue(humanStatus.Contains("Git Repository:    Yes"));
        Assert.IsTrue(humanStatus.Contains("Transport Ready:   NO (Uncheckpointed authoritative files exist)"));
        Assert.IsFalse(humanStatus.Contains("clean", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(humanStatus.Contains("up to date", StringComparison.OrdinalIgnoreCase));

        var (planOk, planRes, planDiags) = WorkspaceVcsStatus.CreateCheckpointPlan(_workspaceRoot);

        Assert.IsFalse(planOk);
        Assert.IsNotNull(planRes);
        Assert.IsTrue(planRes.IsGitRepository);
        Assert.IsFalse(planRes.IsSatisfied);
        Assert.IsTrue(planRes.UncheckpointedFiles.Count > 0);
        Assert.IsTrue(planRes.UncheckpointedFiles.Any(f => f.EndsWith("spec.xml", StringComparison.OrdinalIgnoreCase)));
        Assert.IsTrue(planRes.UncheckpointedFiles.Any(f => f.EndsWith("tasks.xml", StringComparison.OrdinalIgnoreCase)));
        Assert.IsTrue(planDiags.Count > 0);
        Assert.AreEqual(DiagnosticCodes.FilesystemError, planDiags[0].Code);
        Assert.IsTrue(planDiags[0].Message.Contains("Git status execution failed"));

        var humanPlan = planRes.ToHumanString();
        Assert.IsTrue(humanPlan.Contains("Status:            ACTION REQUIRED (Uncheckpointed files exist)"));
        Assert.IsTrue(humanPlan.Contains("Uncheckpointed Files:"));
        Assert.IsTrue(humanPlan.Contains("spec.xml"));
        Assert.IsTrue(humanPlan.Contains("tasks.xml"));
        Assert.IsTrue(humanPlan.Contains("git add"));
        Assert.IsTrue(humanPlan.Contains("git commit -m"));
        Assert.IsFalse(humanPlan.Contains("clean", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(humanPlan.Contains("up to date", StringComparison.OrdinalIgnoreCase));

        var xmlPlan = planRes.ToXmlString();
        Assert.IsTrue(xmlPlan.Contains("is_git=\"true\""));
        Assert.IsTrue(xmlPlan.Contains("satisfied=\"false\""));
        Assert.IsTrue(xmlPlan.Contains("spec.xml"));
    }

    [TestMethod]
    public void CleanCheckpointedGitRepository_RemainsTransportReady()
    {
        if (!IsGitInstalled())
        {
            Assert.Inconclusive("Git is not installed or accessible in this environment.");
        }

        var repoRoot = _tempDir;
        InitGitAndCommitAll(repoRoot);

        var (statusOk, statusRes, statusDiags) = WorkspaceVcsStatus.CheckStatus(_workspaceRoot);

        Assert.IsTrue(statusOk);
        Assert.AreEqual(0, statusDiags.Count);
        Assert.IsNotNull(statusRes);
        Assert.IsTrue(statusRes.IsGitRepository);
        Assert.IsTrue(statusRes.IsTransportReady);
        Assert.AreEqual(0, statusRes.UncheckpointedFiles.Count);

        var humanStatus = statusRes.ToHumanString();
        Assert.IsTrue(humanStatus.Contains("Git Repository:    Yes"));
        Assert.IsTrue(humanStatus.Contains("Transport Ready:   YES (All authoritative files checkpointed)"));

        var xmlStatus = statusRes.ToXmlString();
        Assert.IsTrue(xmlStatus.Contains("transport_ready=\"true\""));

        var (planOk, planRes, planDiags) = WorkspaceVcsStatus.CreateCheckpointPlan(_workspaceRoot);

        Assert.IsTrue(planOk);
        Assert.AreEqual(0, planDiags.Count);
        Assert.IsNotNull(planRes);
        Assert.IsTrue(planRes.IsGitRepository);
        Assert.IsTrue(planRes.IsSatisfied);
        Assert.AreEqual(0, planRes.UncheckpointedFiles.Count);

        var humanPlan = planRes.ToHumanString();
        Assert.IsTrue(humanPlan.Contains("Status:            SATISFIED (Workspace is transport-ready)"));
        Assert.IsTrue(humanPlan.Contains("No uncheckpointed managed documents. Governance state is up to date."));

        var xmlPlan = planRes.ToXmlString();
        Assert.IsTrue(xmlPlan.Contains("satisfied=\"true\""));
    }

    [TestMethod]
    public void GitRepository_WithModifiedAuthoritativeFile_FailsClosed()
    {
        if (!IsGitInstalled())
        {
            Assert.Inconclusive("Git is not installed or accessible in this environment.");
        }

        var repoRoot = _tempDir;
        InitGitAndCommitAll(repoRoot);

        // Modify an authoritative file
        var specPath = Path.Combine(_workspaceRoot, TestIterationId, "spec.xml");
        File.AppendAllText(specPath, "<!-- modification -->\n");

        var (statusOk, statusRes, statusDiags) = WorkspaceVcsStatus.CheckStatus(_workspaceRoot);

        Assert.IsTrue(statusOk);
        Assert.AreEqual(0, statusDiags.Count);
        Assert.IsNotNull(statusRes);
        Assert.IsTrue(statusRes.IsGitRepository);
        Assert.IsFalse(statusRes.IsTransportReady);
        Assert.IsTrue(statusRes.UncheckpointedFiles.Count > 0);
        Assert.IsTrue(statusRes.UncheckpointedFiles.Any(f => f.EndsWith("spec.xml", StringComparison.OrdinalIgnoreCase)));
        Assert.IsFalse(statusRes.UncheckpointedFiles.Any(f => f.EndsWith("tasks.xml", StringComparison.OrdinalIgnoreCase)));

        var (planOk, planRes, planDiags) = WorkspaceVcsStatus.CreateCheckpointPlan(_workspaceRoot);

        Assert.IsTrue(planOk);
        Assert.AreEqual(0, planDiags.Count);
        Assert.IsNotNull(planRes);
        Assert.IsFalse(planRes.IsSatisfied);
        Assert.IsTrue(planRes.UncheckpointedFiles.Count > 0);

        var humanPlan = planRes.ToHumanString();
        Assert.IsTrue(humanPlan.Contains("Status:            ACTION REQUIRED (Uncheckpointed files exist)"));
        Assert.IsTrue(humanPlan.Contains("spec.xml"));
        Assert.IsTrue(humanPlan.Contains("git add"));
        Assert.IsTrue(humanPlan.Contains("git commit -m"));

        var xmlPlan = planRes.ToXmlString();
        Assert.IsTrue(xmlPlan.Contains("is_git=\"true\""));
        Assert.IsTrue(xmlPlan.Contains("satisfied=\"false\""));
        Assert.IsTrue(xmlPlan.Contains("spec.xml"));
    }

    [TestMethod]
    public void WorkspaceWithAncestorDirectoryNamedTmp_DiscoversFilesAndExcludesInternalTmp()
    {
        var ancestorWithTmp = Path.Combine(_tempDir, "_tmp_ancestor_dir", "subproj");
        Directory.CreateDirectory(ancestorWithTmp);
        WorkspaceInitializer.Initialize(ancestorWithTmp, ancestorWithTmp);
        var wsRoot = Path.Combine(ancestorWithTmp, ".dogdouspec");
        IterationCreator.Create(wsRoot, "20260904-ancestor-test", "feature", activate: true);

        // Add a file in wsRoot/_tmp to verify internal _tmp exclusion
        var internalTmpDir = Path.Combine(wsRoot, "_tmp");
        Directory.CreateDirectory(internalTmpDir);
        File.WriteAllText(Path.Combine(internalTmpDir, "scratch.txt"), "transient scratch");

        var (statusOk, statusRes, statusDiags) = WorkspaceVcsStatus.CheckStatus(wsRoot);
        Assert.IsTrue(statusOk);
        Assert.AreEqual(0, statusDiags.Count);
        Assert.IsNotNull(statusRes);

        // Authoritative documents in wsRoot MUST be discovered despite _tmp in ancestor directory path
        Assert.IsTrue(statusRes.ManagedFiles.Any(f => f.RelativePath.EndsWith("spec.xml", StringComparison.OrdinalIgnoreCase)));
        Assert.IsTrue(statusRes.ManagedFiles.Any(f => f.RelativePath.EndsWith("tasks.xml", StringComparison.OrdinalIgnoreCase)));
        Assert.IsTrue(statusRes.UncheckpointedFiles.Count > 0);

        // Files inside internal _tmp/ MUST be excluded
        Assert.IsFalse(statusRes.ManagedFiles.Any(f => f.RelativePath.Contains("scratch.txt", StringComparison.OrdinalIgnoreCase)));
    }

    private static bool IsGitInstalled()
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "git",
                Arguments = "--version",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            });
            if (process == null) return false;
            process.WaitForExit(5000);
            return process.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    private static void InitGitAndCommitAll(string repoRoot)
    {
        RunGit(repoRoot, "init");
        RunGit(repoRoot, "config", "user.email", "transport-tests@example.invalid");
        RunGit(repoRoot, "config", "user.name", "Transport Tests");
        RunGit(repoRoot, "add", "--", ".");
        RunGit(repoRoot, "commit", "-m", "Initial governance checkpoint");
    }

    private static void RunGit(string repoRoot, params string[] arguments)
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

        using var process = Process.Start(startInfo);
        Assert.IsNotNull(process);
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        Assert.IsTrue(process.WaitForExit(30_000), $"Git command timed out: {string.Join(' ', arguments)}");
        Assert.AreEqual(0, process.ExitCode, $"Git command failed: {string.Join(' ', arguments)}\n{stdout}\n{stderr}");
    }
}
