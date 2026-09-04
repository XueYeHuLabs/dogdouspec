using System.Diagnostics;
using DogdouSpec.Cli;
using DogdouSpec.Core.Iterations;
using DogdouSpec.Core.Workspace;

namespace DogdouSpec.Cli.Tests;

[TestClass]
public sealed class WorkspaceVcsStatusCliTests
{
    private string _tempDir = null!;
    private string _workspaceRoot = null!;
    private const string TestIterationId = "20260904-vcs-cli-test";

    [TestInitialize]
    public void SetUp()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "DogdouSpec_VcsCli_" + Guid.NewGuid().ToString("N"));
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
    public void WorkspaceVcsStatus_And_CheckpointPlan_NonGitWorkspace_ReturnsZero_AndReportsNotTransportReady()
    {
        var (vcsExit, vcsOut, vcsErr) = ExecuteCli(
            "workspace", "vcs-status",
            "--workspace-root", _tempDir,
            "--format", "human");

        Assert.AreEqual(0, vcsExit, $"vcs-status failed: {vcsErr}");
        Assert.IsTrue(vcsOut.Contains("Git Repository:    No"), $"Expected 'Git Repository: No' in: {vcsOut}");
        Assert.IsTrue(vcsOut.Contains("Transport Ready:   NOT TRANSPORT-READY (no Git repository — workspace is locally durable only)"), $"Expected fail-closed transport ready in: {vcsOut}");
        Assert.IsTrue(vcsOut.Contains("Uncheckpointed Files"), $"Expected uncheckpointed files section in: {vcsOut}");
        Assert.IsTrue(vcsOut.Contains("spec.xml"), $"Expected spec.xml in: {vcsOut}");
        Assert.IsTrue(vcsOut.Contains("tasks.xml"), $"Expected tasks.xml in: {vcsOut}");

        var (planExit, planOut, planErr) = ExecuteCli(
            "workspace", "checkpoint-plan",
            "--workspace-root", _tempDir,
            "--format", "human");

        Assert.AreEqual(0, planExit, $"checkpoint-plan failed: {planErr}");
        Assert.IsTrue(planOut.Contains("Status:            ACTION REQUIRED (No Git repository — workspace is locally durable only)"), $"Expected action required in: {planOut}");
        Assert.IsTrue(planOut.Contains("(Establish a Git repository before checkpointing authoritative files)"), $"Expected establishment guidance in: {planOut}");
        Assert.IsTrue(planOut.Contains("  git init"), $"Expected git init in: {planOut}");
        Assert.IsTrue(planOut.Contains("  git add"), $"Expected git add in: {planOut}");
        Assert.IsTrue(planOut.Contains("Uncheckpointed Files:"), $"Expected uncheckpointed list in: {planOut}");
        Assert.IsTrue(planOut.Contains("spec.xml"), $"Expected spec.xml in: {planOut}");
        Assert.IsTrue(planOut.Contains("tasks.xml"), $"Expected tasks.xml in: {planOut}");

        // XML format verification
        var (xmlVcsExit, xmlVcsOut, xmlVcsErr) = ExecuteCli(
            "workspace", "vcs-status",
            "--workspace-root", _tempDir,
            "--format", "xml");

        Assert.AreEqual(0, xmlVcsExit, $"xml vcs-status failed: {xmlVcsErr}");
        Assert.IsTrue(xmlVcsOut.Contains("is_git=\"false\""), $"Expected is_git false in: {xmlVcsOut}");
        Assert.IsTrue(xmlVcsOut.Contains("transport_ready=\"false\""), $"Expected transport_ready false in: {xmlVcsOut}");
        Assert.IsTrue(xmlVcsOut.Contains("spec.xml"), $"Expected spec.xml in: {xmlVcsOut}");

        var (xmlPlanExit, xmlPlanOut, xmlPlanErr) = ExecuteCli(
            "workspace", "checkpoint-plan",
            "--workspace-root", _tempDir,
            "--format", "xml");

        Assert.AreEqual(0, xmlPlanExit, $"xml checkpoint-plan failed: {xmlPlanErr}");
        Assert.IsTrue(xmlPlanOut.Contains("is_git=\"false\""), $"Expected is_git false in: {xmlPlanOut}");
        Assert.IsTrue(xmlPlanOut.Contains("satisfied=\"false\""), $"Expected satisfied false in: {xmlPlanOut}");
        Assert.IsTrue(xmlPlanOut.Contains("spec.xml"), $"Expected spec.xml in: {xmlPlanOut}");
    }

    [TestMethod]
    public void WorkspaceVcsStatus_And_CheckpointPlan_WhenGitStatusFails_EmitsDiagnostic_AndReturnsNonZero()
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

        var (vcsExit, vcsOut, vcsErr) = ExecuteCli(
            "workspace", "vcs-status",
            "--workspace-root", _tempDir,
            "--format", "human");

        Assert.AreNotEqual(0, vcsExit, "Expected non-zero exit code on git status failure");
        Assert.AreEqual(6, vcsExit);
        Assert.IsTrue(vcsErr.Contains("[ERROR]"), $"Expected [ERROR] in stderr: {vcsErr}");
        Assert.IsTrue(vcsErr.Contains("FILESYSTEM_ERROR"), $"Expected FILESYSTEM_ERROR in stderr: {vcsErr}");
        Assert.IsTrue(vcsErr.Contains("Git status execution failed"), $"Expected useful diagnostic in stderr: {vcsErr}");
        // Stdout must render fail-closed degraded status safely without claiming clean
        Assert.IsTrue(vcsOut.Contains("Git Repository:    Yes"), $"Expected Git Repository: Yes in: {vcsOut}");
        Assert.IsTrue(vcsOut.Contains("Transport Ready:   NO (Uncheckpointed authoritative files exist)"), $"Expected fail-closed transport ready in: {vcsOut}");
        Assert.IsTrue(vcsOut.Contains("spec.xml"), $"Expected spec.xml in: {vcsOut}");
        Assert.IsTrue(vcsOut.Contains("tasks.xml"), $"Expected tasks.xml in: {vcsOut}");
        Assert.IsFalse(vcsOut.Contains("clean", StringComparison.OrdinalIgnoreCase), $"Should not claim clean in: {vcsOut}");
        Assert.IsFalse(vcsOut.Contains("up to date", StringComparison.OrdinalIgnoreCase), $"Should not claim up to date in: {vcsOut}");

        var (planExit, planOut, planErr) = ExecuteCli(
            "workspace", "checkpoint-plan",
            "--workspace-root", _tempDir,
            "--format", "human");

        Assert.AreNotEqual(0, planExit, "Expected non-zero exit code on git status failure");
        Assert.AreEqual(6, planExit);
        Assert.IsTrue(planErr.Contains("[ERROR]"), $"Expected [ERROR] in stderr: {planErr}");
        Assert.IsTrue(planErr.Contains("FILESYSTEM_ERROR"), $"Expected FILESYSTEM_ERROR in stderr: {planErr}");
        Assert.IsTrue(planErr.Contains("Git status execution failed"), $"Expected useful diagnostic in stderr: {planErr}");
        // Stdout must render fail-closed degraded plan safely without claiming clean
        Assert.IsTrue(planOut.Contains("Status:            ACTION REQUIRED (Uncheckpointed files exist)"), $"Expected action required in: {planOut}");
        Assert.IsTrue(planOut.Contains("spec.xml"), $"Expected spec.xml in: {planOut}");
        Assert.IsTrue(planOut.Contains("tasks.xml"), $"Expected tasks.xml in: {planOut}");
        Assert.IsTrue(planOut.Contains("git add"), $"Expected git add in: {planOut}");
        Assert.IsTrue(planOut.Contains("git commit -m"), $"Expected git commit in: {planOut}");
        Assert.IsFalse(planOut.Contains("clean", StringComparison.OrdinalIgnoreCase), $"Should not claim clean in: {planOut}");
        Assert.IsFalse(planOut.Contains("up to date", StringComparison.OrdinalIgnoreCase), $"Should not claim up to date in: {planOut}");

        // XML format verification
        var (xmlVcsExit, xmlVcsOut, xmlVcsErr) = ExecuteCli(
            "workspace", "vcs-status",
            "--workspace-root", _tempDir,
            "--format", "xml");

        Assert.AreNotEqual(0, xmlVcsExit);
        Assert.IsTrue(xmlVcsErr.Contains("<diagnostic code=\"FILESYSTEM_ERROR\" severity=\"error\">"), $"Expected XML diagnostic in stderr: {xmlVcsErr}");
        Assert.IsTrue(xmlVcsErr.Contains("Git status execution failed"), $"Expected diagnostic text in stderr: {xmlVcsErr}");
        Assert.IsTrue(xmlVcsOut.Contains("is_git=\"true\""), $"Expected is_git true in: {xmlVcsOut}");
        Assert.IsTrue(xmlVcsOut.Contains("transport_ready=\"false\""), $"Expected transport_ready false in: {xmlVcsOut}");
        Assert.IsTrue(xmlVcsOut.Contains("spec.xml"), $"Expected spec.xml in: {xmlVcsOut}");

        var (xmlPlanExit, xmlPlanOut, xmlPlanErr) = ExecuteCli(
            "workspace", "checkpoint-plan",
            "--workspace-root", _tempDir,
            "--format", "xml");

        Assert.AreNotEqual(0, xmlPlanExit);
        Assert.IsTrue(xmlPlanErr.Contains("<diagnostic code=\"FILESYSTEM_ERROR\" severity=\"error\">"), $"Expected XML diagnostic in stderr: {xmlPlanErr}");
        Assert.IsTrue(xmlPlanOut.Contains("is_git=\"true\""), $"Expected is_git true in: {xmlPlanOut}");
        Assert.IsTrue(xmlPlanOut.Contains("satisfied=\"false\""), $"Expected satisfied false in: {xmlPlanOut}");
        Assert.IsTrue(xmlPlanOut.Contains("spec.xml"), $"Expected spec.xml in: {xmlPlanOut}");
    }

    [TestMethod]
    public void WorkspaceVcsStatus_And_CheckpointPlan_CleanGitRepo_Succeeds_AndReportsTransportReady()
    {
        if (!IsGitInstalled())
        {
            Assert.Inconclusive("Git is not installed or accessible in this environment.");
        }

        var repoRoot = _tempDir;
        InitGitAndCommitAll(repoRoot);

        var (vcsExit, vcsOut, vcsErr) = ExecuteCli(
            "workspace", "vcs-status",
            "--workspace-root", _tempDir,
            "--format", "human");

        Assert.AreEqual(0, vcsExit, $"vcs-status failed: {vcsErr}");
        Assert.IsTrue(vcsOut.Contains("Git Repository:    Yes"), $"Expected 'Git Repository: Yes' in: {vcsOut}");
        Assert.IsTrue(vcsOut.Contains("Transport Ready:   YES (All authoritative files checkpointed)"), $"Expected transport ready in: {vcsOut}");

        var (planExit, planOut, planErr) = ExecuteCli(
            "workspace", "checkpoint-plan",
            "--workspace-root", _tempDir,
            "--format", "human");

        Assert.AreEqual(0, planExit, $"checkpoint-plan failed: {planErr}");
        Assert.IsTrue(planOut.Contains("Status:            SATISFIED (Workspace is transport-ready)"), $"Expected satisfied in: {planOut}");
        Assert.IsTrue(planOut.Contains("No uncheckpointed managed documents. Governance state is up to date."), $"Expected clean message in: {planOut}");

        var (xmlVcsExit, xmlVcsOut, xmlVcsErr) = ExecuteCli(
            "workspace", "vcs-status",
            "--workspace-root", _tempDir,
            "--format", "xml");
        Assert.AreEqual(0, xmlVcsExit, $"xml vcs-status failed: {xmlVcsErr}");
        Assert.IsTrue(xmlVcsOut.Contains("transport_ready=\"true\""));

        var (xmlPlanExit, xmlPlanOut, xmlPlanErr) = ExecuteCli(
            "workspace", "checkpoint-plan",
            "--workspace-root", _tempDir,
            "--format", "xml");
        Assert.AreEqual(0, xmlPlanExit, $"xml checkpoint-plan failed: {xmlPlanErr}");
        Assert.IsTrue(xmlPlanOut.Contains("satisfied=\"true\""));
    }

    [TestMethod]
    public void WorkspaceVcsStatus_And_CheckpointPlan_DirtyGitRepo_ReportsActionRequired_AndListsUncheckpointedFiles()
    {
        if (!IsGitInstalled())
        {
            Assert.Inconclusive("Git is not installed or accessible in this environment.");
        }

        var repoRoot = _tempDir;
        InitGitAndCommitAll(repoRoot);

        // Modify spec.xml
        var specPath = Path.Combine(_workspaceRoot, TestIterationId, "spec.xml");
        File.AppendAllText(specPath, "<!-- uncommitted diff -->\n");

        var (vcsExit, vcsOut, vcsErr) = ExecuteCli(
            "workspace", "vcs-status",
            "--workspace-root", _tempDir,
            "--format", "human");

        Assert.AreEqual(0, vcsExit, $"vcs-status failed: {vcsErr}");
        Assert.IsTrue(vcsOut.Contains("Git Repository:    Yes"), $"Expected Git Repository: Yes in: {vcsOut}");
        Assert.IsTrue(vcsOut.Contains("Transport Ready:   NO (Uncheckpointed authoritative files exist)"), $"Expected not transport ready in: {vcsOut}");
        Assert.IsTrue(vcsOut.Contains("spec.xml"), $"Expected spec.xml in: {vcsOut}");
        Assert.IsFalse(vcsOut.Contains("! .dogdouspec/" + TestIterationId + "/tasks.xml"), $"tasks.xml should not be uncheckpointed in: {vcsOut}");

        var (planExit, planOut, planErr) = ExecuteCli(
            "workspace", "checkpoint-plan",
            "--workspace-root", _tempDir,
            "--format", "human");

        Assert.AreEqual(0, planExit, $"checkpoint-plan failed: {planErr}");
        Assert.IsTrue(planOut.Contains("Status:            ACTION REQUIRED (Uncheckpointed files exist)"), $"Expected action required in: {planOut}");
        Assert.IsTrue(planOut.Contains("spec.xml"), $"Expected spec.xml in: {planOut}");
        Assert.IsTrue(planOut.Contains("git add"), $"Expected git add in: {planOut}");
        Assert.IsTrue(planOut.Contains("git commit -m"), $"Expected git commit in: {planOut}");

        var (xmlPlanExit, xmlPlanOut, xmlPlanErr) = ExecuteCli(
            "workspace", "checkpoint-plan",
            "--workspace-root", _tempDir,
            "--format", "xml");

        Assert.AreEqual(0, xmlPlanExit, $"xml checkpoint-plan failed: {xmlPlanErr}");
        Assert.IsTrue(xmlPlanOut.Contains("satisfied=\"false\""), $"Expected satisfied false in: {xmlPlanOut}");
        Assert.IsTrue(xmlPlanOut.Contains("spec.xml"), $"Expected spec.xml in: {xmlPlanOut}");
    }

    private static (int ExitCode, string Stdout, string Stderr) ExecuteCli(params string[] args)
    {
        var originalOut = Console.Out;
        var originalErr = Console.Error;

        using var outSw = new StringWriter();
        using var errSw = new StringWriter();

        try
        {
            Console.SetOut(outSw);
            Console.SetError(errSw);

            var exitCode = Program.Main(args);
            return (exitCode, outSw.ToString(), errSw.ToString());
        }
        finally
        {
            Console.SetOut(originalOut);
            Console.SetError(originalErr);
        }
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
        RunGit(repoRoot, "config", "user.email", "transport-cli-tests@example.invalid");
        RunGit(repoRoot, "config", "user.name", "Transport CLI Tests");
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
