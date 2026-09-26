using System.Diagnostics;
using System.Security.Cryptography;
using System.Xml.Linq;
using DogdouSpec.Core.Diagnostics;
using DogdouSpec.Core.Iterations;
using DogdouSpec.Core.Tasks;
using DogdouSpec.Core.Workspace;

namespace DogdouSpec.Core.Tests;

[TestClass]
public sealed class IterationReadinessVcsTests
{
    private static readonly string[] TestCriteria = ["Substantive criterion for VCS readiness testing."];
    private string _tempDir = null!;
    private string _wsRoot = null!;
    private const string TestIterationId = "20260908-vcs-readiness";

    [TestInitialize]
    public void SetUp()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "DogdouSpec_VcsTest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    [TestCleanup]
    public void TearDown()
    {
        if (Directory.Exists(_tempDir))
        {
            try { Directory.Delete(_tempDir, true); } catch { }
        }
    }

    private void InitGitRepo()
    {
        RunGit(_tempDir, "init");
        RunGit(_tempDir, "config", "user.name", "Tester");
        RunGit(_tempDir, "config", "user.email", "tester@example.com");
    }

    private static (int ExitCode, string Stdout, string Stderr) RunGit(string workingDir, params string[] args)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "git",
                WorkingDirectory = workingDir,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };

        foreach (var arg in args)
        {
            process.StartInfo.ArgumentList.Add(arg);
        }

        process.Start();
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        return (process.ExitCode, stdout, stderr);
    }

    [TestMethod]
    public void CleanGitWorkspace_ReturnsPassed_AgreesWithVcsStatusAndCheckpointPlan()
    {
        InitGitRepo();
        WorkspaceInitializer.Initialize(_tempDir, _tempDir);
        _wsRoot = Path.Combine(_tempDir, ".dogdouspec");
        IterationCreator.Create(_wsRoot, TestIterationId, "feature", activate: true, criteria: TestCriteria);

        RunGit(_tempDir, "add", ".");
        RunGit(_tempDir, "commit", "-m", "Initial commit");

        // 1. WorkspaceVcsStatus CheckStatus
        var (vcsOk, vcsRes, vcsDiags) = WorkspaceVcsStatus.CheckStatus(_wsRoot);
        Assert.IsTrue(vcsOk);
        Assert.AreEqual(0, vcsDiags.Count);
        Assert.IsNotNull(vcsRes);
        Assert.IsTrue(vcsRes.IsGitRepository);
        Assert.IsTrue(vcsRes.IsTransportReady);
        Assert.AreEqual(0, vcsRes.UncheckpointedFiles.Count);

        // 2. CreateCheckpointPlan
        var (planOk, planRes, _) = WorkspaceVcsStatus.CreateCheckpointPlan(_wsRoot);
        Assert.IsTrue(planOk);
        Assert.IsNotNull(planRes);
        Assert.IsTrue(planRes.IsSatisfied);
        Assert.AreEqual(0, planRes.UncheckpointedFiles.Count);

        // 3. IterationReadiness for completion
        var (readyOk, readyRes, readyDiags) = IterationReadiness.Assess(_wsRoot, TestIterationId, "completion");
        Assert.IsTrue(readyOk);
        Assert.AreEqual(0, readyDiags.Count);
        Assert.IsNotNull(readyRes);
        var vcsDim = readyRes.Dimensions.First(d => d.Name == "vcs_checkpoint");
        Assert.AreEqual("passed", vcsDim.Status);
        Assert.AreEqual("Authoritative documents are clean and checkpointed", vcsDim.Message);

        // 4. IterationReadiness for activation
        var (actOk, actRes, _) = IterationReadiness.Assess(_wsRoot, TestIterationId, "activation");
        Assert.IsTrue(actOk);
        Assert.IsNotNull(actRes);
        var actVcsDim = actRes.Dimensions.First(d => d.Name == "vcs_checkpoint");
        Assert.AreEqual("passed", actVcsDim.Status);
        Assert.AreEqual("Authoritative documents are clean and checkpointed", actVcsDim.Message);
    }

    [TestMethod]
    public void UntrackedAuthoritativeFile_ReturnsFailed_ListsExactPath()
    {
        InitGitRepo();
        WorkspaceInitializer.Initialize(_tempDir, _tempDir);
        _wsRoot = Path.Combine(_tempDir, ".dogdouspec");
        IterationCreator.Create(_wsRoot, TestIterationId, "feature", activate: true, criteria: TestCriteria);

        RunGit(_tempDir, "add", ".");
        RunGit(_tempDir, "commit", "-m", "Initial commit");

        // Create an untracked authoritative document by creating a second iteration without committing
        var secondIterId = "20260909-second-iter";
        var (createOk, _, createDiags) = IterationCreator.Create(_wsRoot, secondIterId, "feature", activate: false, criteria: TestCriteria);
        Assert.IsTrue(createOk, string.Join("; ", createDiags.Select(d => d.Message)));

        var (readyOk, readyRes, readyDiags) = IterationReadiness.Assess(_wsRoot, TestIterationId, "completion");
        Assert.IsTrue(readyOk, string.Join("; ", readyDiags.Select(d => d.Message)));
        Assert.IsNotNull(readyRes);
        var vcsDim = readyRes.Dimensions.First(d => d.Name == "vcs_checkpoint");
        Assert.AreEqual("failed", vcsDim.Status);
        Assert.IsTrue(vcsDim.Message.StartsWith("Uncheckpointed authoritative documents exist:", StringComparison.Ordinal));
        Assert.IsTrue(vcsDim.Message.Contains($".dogdouspec/{secondIterId}/spec.xml (untracked)"));

        var (planOk, planRes, _) = WorkspaceVcsStatus.CreateCheckpointPlan(_wsRoot);
        Assert.IsTrue(planOk);
        Assert.IsNotNull(planRes);
        Assert.IsFalse(planRes.IsSatisfied);
        Assert.IsTrue(planRes.UncheckpointedFiles.Contains($".dogdouspec/{secondIterId}/spec.xml"));
    }

    [TestMethod]
    public void ModifiedAuthoritativeFile_ReturnsFailed_ListsExactPath()
    {
        InitGitRepo();
        WorkspaceInitializer.Initialize(_tempDir, _tempDir);
        _wsRoot = Path.Combine(_tempDir, ".dogdouspec");
        IterationCreator.Create(_wsRoot, TestIterationId, "feature", activate: true, criteria: TestCriteria);

        RunGit(_tempDir, "add", ".");
        RunGit(_tempDir, "commit", "-m", "Initial commit");

        // Modify spec.xml
        var specPath = Path.Combine(_wsRoot, TestIterationId, "spec.xml");
        File.AppendAllText(specPath, "<!-- modification -->\n");

        var (readyOk, readyRes, _) = IterationReadiness.Assess(_wsRoot, TestIterationId, "completion");
        Assert.IsTrue(readyOk);
        Assert.IsNotNull(readyRes);
        var vcsDim = readyRes.Dimensions.First(d => d.Name == "vcs_checkpoint");
        Assert.AreEqual("failed", vcsDim.Status);
        Assert.IsTrue(vcsDim.Message.Contains($".dogdouspec/{TestIterationId}/spec.xml (modified)"));
    }

    [TestMethod]
    public void StagedAuthoritativeFile_ReturnsFailed_ListsExactPath()
    {
        InitGitRepo();
        WorkspaceInitializer.Initialize(_tempDir, _tempDir);
        _wsRoot = Path.Combine(_tempDir, ".dogdouspec");
        IterationCreator.Create(_wsRoot, TestIterationId, "feature", activate: true, criteria: TestCriteria);

        RunGit(_tempDir, "add", ".");
        RunGit(_tempDir, "commit", "-m", "Initial commit");

        // Modify and stage spec.xml
        var specPath = Path.Combine(_wsRoot, TestIterationId, "spec.xml");
        File.AppendAllText(specPath, "<!-- staged change -->\n");
        RunGit(_tempDir, "add", Path.Combine(".dogdouspec", TestIterationId, "spec.xml"));

        var (readyOk, readyRes, _) = IterationReadiness.Assess(_wsRoot, TestIterationId, "completion");
        Assert.IsTrue(readyOk);
        Assert.IsNotNull(readyRes);
        var vcsDim = readyRes.Dimensions.First(d => d.Name == "vcs_checkpoint");
        Assert.AreEqual("failed", vcsDim.Status);
        Assert.IsTrue(vcsDim.Message.Contains($".dogdouspec/{TestIterationId}/spec.xml (staged)"));
    }

    [TestMethod]
    public void IgnoredAuthoritativeFile_ReturnsFailed_ListsExactPath()
    {
        InitGitRepo();
        WorkspaceInitializer.Initialize(_tempDir, _tempDir);
        _wsRoot = Path.Combine(_tempDir, ".dogdouspec");
        IterationCreator.Create(_wsRoot, TestIterationId, "feature", activate: true, criteria: TestCriteria);

        // Ignore a second iteration directory
        var ignoredIterId = "20260909-ignored-iter";
        File.WriteAllText(Path.Combine(_tempDir, ".gitignore"), $".dogdouspec/{ignoredIterId}/**\n");
        RunGit(_tempDir, "add", ".");
        RunGit(_tempDir, "commit", "-m", "Initial commit");

        // Now create the ignored authoritative documents
        var (createOk, _, createDiags) = IterationCreator.Create(_wsRoot, ignoredIterId, "feature", activate: false, criteria: TestCriteria);
        Assert.IsTrue(createOk, string.Join("; ", createDiags.Select(d => d.Message)));

        var (readyOk, readyRes, readyDiags) = IterationReadiness.Assess(_wsRoot, TestIterationId, "completion");
        Assert.IsTrue(readyOk, string.Join("; ", readyDiags.Select(d => d.Message)));
        Assert.IsNotNull(readyRes);
        var vcsDim = readyRes.Dimensions.First(d => d.Name == "vcs_checkpoint");
        Assert.AreEqual("failed", vcsDim.Status);
        Assert.IsTrue(vcsDim.Message.Contains($".dogdouspec/{ignoredIterId}/spec.xml (ignored)"));
    }

    [TestMethod]
    public void NonGitWorkspace_ReturnsNotApplicable_AcrossActivationAndCompletion()
    {
        WorkspaceInitializer.Initialize(_tempDir, _tempDir);
        _wsRoot = Path.Combine(_tempDir, ".dogdouspec");
        IterationCreator.Create(_wsRoot, TestIterationId, "feature", activate: true, criteria: TestCriteria);

        // 1. Completion
        var (readyOk, readyRes, _) = IterationReadiness.Assess(_wsRoot, TestIterationId, "completion");
        Assert.IsTrue(readyOk);
        Assert.IsNotNull(readyRes);
        var vcsDim = readyRes.Dimensions.First(d => d.Name == "vcs_checkpoint");
        Assert.AreEqual("not-applicable", vcsDim.Status);
        Assert.AreEqual("Non-Git workspace: VCS checkpoint dimension not applicable.", vcsDim.Message);

        // 2. Activation
        var (actOk, actRes, _) = IterationReadiness.Assess(_wsRoot, TestIterationId, "activation");
        Assert.IsTrue(actOk);
        Assert.IsNotNull(actRes);
        var actVcsDim = actRes.Dimensions.First(d => d.Name == "vcs_checkpoint");
        Assert.AreEqual("not-applicable", actVcsDim.Status);
        Assert.AreEqual("Non-Git workspace: VCS checkpoint dimension not applicable.", actVcsDim.Message);
    }

    [TestMethod]
    public void Readiness_PreservesHeadIndexAndManagedFiles_NoMutations()
    {
        InitGitRepo();
        WorkspaceInitializer.Initialize(_tempDir, _tempDir);
        _wsRoot = Path.Combine(_tempDir, ".dogdouspec");
        IterationCreator.Create(_wsRoot, TestIterationId, "feature", activate: true, criteria: TestCriteria);

        RunGit(_tempDir, "add", ".");
        RunGit(_tempDir, "commit", "-m", "Initial commit");

        var (_, headBefore, _) = RunGit(_tempDir, "rev-parse", "HEAD");
        var (_, statusBefore, _) = RunGit(_tempDir, "status", "--porcelain", "-uall");
        var specPath = Path.Combine(_wsRoot, TestIterationId, "spec.xml");
        var specHashBefore = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(specPath)));

        // Run readiness check multiple times across phases
        IterationReadiness.Assess(_wsRoot, TestIterationId, "completion");
        IterationReadiness.Assess(_wsRoot, TestIterationId, "activation");

        var (_, headAfter, _) = RunGit(_tempDir, "rev-parse", "HEAD");
        var (_, statusAfter, _) = RunGit(_tempDir, "status", "--porcelain", "-uall");
        var specHashAfter = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(specPath)));

        Assert.AreEqual(headBefore, headAfter);
        Assert.AreEqual(statusBefore, statusAfter);
        Assert.AreEqual(specHashBefore, specHashAfter);
    }

    [TestMethod]
    public void Readiness_FailedVcsCheckpoint_DoesNotSilentlyBlockProductConfirmation()
    {
        InitGitRepo();
        WorkspaceInitializer.Initialize(_tempDir, _tempDir);
        _wsRoot = Path.Combine(_tempDir, ".dogdouspec");
        IterationCreator.Create(_wsRoot, TestIterationId, "feature", activate: true, criteria: TestCriteria);

        // Leave documents untracked (vcs_checkpoint will be failed)
        var (readyOk, readyRes, _) = IterationReadiness.Assess(_wsRoot, TestIterationId, "completion");
        Assert.IsTrue(readyOk);
        Assert.IsNotNull(readyRes);

        var vcsDim = readyRes.Dimensions.First(d => d.Name == "vcs_checkpoint");
        Assert.AreEqual("failed", vcsDim.Status);

        // Product confirmation dimension remains independent and reflects actual product decision count
        var prodDim = readyRes.Dimensions.First(d => d.Name == "product_confirmation");
        Assert.IsNotNull(prodDim);
        Assert.AreEqual(readyRes.ProductDecisions.Total > 0 ? "pending" : "passed", prodDim.Status);
    }

    [TestMethod]
    public void VcsStatus_WhenDogdouSpecDirIgnoredInGitignore_ClassifiedAsIgnoredAndNotClean()
    {
        InitGitRepo();
        File.WriteAllText(Path.Combine(_tempDir, ".gitignore"), "/.dogdouspec/\n");
        RunGit(_tempDir, "add", ".gitignore");
        RunGit(_tempDir, "commit", "-m", "Ignore .dogdouspec directory");

        WorkspaceInitializer.Initialize(_tempDir, _tempDir);
        _wsRoot = Path.Combine(_tempDir, ".dogdouspec");
        IterationCreator.Create(_wsRoot, TestIterationId, "feature", activate: true, criteria: TestCriteria);

        var (statusOk, statusRes, statusDiags) = WorkspaceVcsStatus.CheckStatus(_wsRoot);
        Assert.IsTrue(statusOk, string.Join("; ", statusDiags.Select(d => d.Message)));
        Assert.IsNotNull(statusRes);
        Assert.IsTrue(statusRes.IsGitRepository);
        Assert.IsFalse(statusRes.IsTransportReady);

        // Authoritative documents must be classified as 'ignored', never 'clean'
        var specFile = statusRes.ManagedFiles.FirstOrDefault(f => f.RelativePath.EndsWith("spec.xml", StringComparison.OrdinalIgnoreCase));
        Assert.IsNotNull(specFile);
        Assert.AreEqual("ignored", specFile.Status);

        var tasksFile = statusRes.ManagedFiles.FirstOrDefault(f => f.RelativePath.EndsWith("tasks.xml", StringComparison.OrdinalIgnoreCase));
        Assert.IsNotNull(tasksFile);
        Assert.AreEqual("ignored", tasksFile.Status);

        Assert.IsTrue(statusRes.UncheckpointedFiles.Count > 0);
    }
}
