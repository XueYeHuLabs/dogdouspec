using DogdouSpec.Cli;
using DogdouSpec.Core.Workspace;

namespace DogdouSpec.Cli.Tests;

[TestClass]
public sealed class UpgradeWorkflowCliTests
{
    private string _tempDir = null!;
    private string _workspaceRoot = null!;
    private string _skillDir = null!;

    [TestInitialize]
    public void SetUp()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "DogdouSpec_UpgradeWorkflow_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        var (success, workspaceRoot, error) = WorkspaceInitializer.Initialize(_tempDir, _tempDir);
        Assert.IsTrue(success, error?.Message);
        _workspaceRoot = workspaceRoot;
        _skillDir = Path.Combine(_tempDir, ".agents", "skills", "dogdouspec");
    }

    [TestCleanup]
    public void TearDown()
    {
        if (Directory.Exists(_tempDir))
        {
            try { Directory.Delete(_tempDir, recursive: true); } catch { }
        }
    }

    [TestMethod]
    public void PreviousVersionRepository_FollowsGuideFirstExplicitUpgradeWithoutChangingRepoGuidance()
    {
        var agentsPath = Path.Combine(_tempDir, "AGENTS.md");
        const string repositoryGuidance = "# Repository-owned agent guidance";
        File.WriteAllText(agentsPath, repositoryGuidance);

        File.WriteAllText(Path.Combine(_skillDir, "SKILL.md"), "# Previous binary Skill");
        File.Delete(Path.Combine(_skillDir, "references", "upgrade.md"));
        File.WriteAllText(Path.Combine(_workspaceRoot, "_schema", "spec.xsd"), "previous readable schema copy");

        var (helpExit, helpOut, helpErr) = RunCli("--help");
        Assert.AreEqual(0, helpExit, helpErr);
        Assert.IsTrue(helpOut.Contains("dogdouspec skill guide --all", StringComparison.Ordinal));
        Assert.IsTrue(helpOut.IndexOf("dogdouspec skill guide --all", StringComparison.Ordinal) <
                      helpOut.IndexOf("dogdouspec skill status", StringComparison.Ordinal));

        var (guideExit, guideOut, guideErr) = RunCli("skill", "guide", "--all", "--format", "markdown");
        Assert.AreEqual(0, guideExit, guideErr);
        Assert.IsTrue(guideOut.Contains("# DogdouSpec Upgrade Contract", StringComparison.Ordinal));
        Assert.IsTrue(guideOut.Contains("The calling agent owns repository analysis and judgment", StringComparison.Ordinal));

        var (skillStatusExit, skillStatusOut, skillStatusErr) = RunCli(
            "skill", "status", "--output-dir", _skillDir, "--format", "xml");
        Assert.AreEqual(1, skillStatusExit, skillStatusErr);
        Assert.IsTrue(skillStatusOut.Contains("path=\"SKILL.md\" state=\"modified\"", StringComparison.Ordinal));
        Assert.IsTrue(skillStatusOut.Contains("path=\"references/upgrade.md\" state=\"missing\"", StringComparison.Ordinal));

        var (schemaStatusExit, schemaStatusOut, schemaStatusErr) = RunCli(
            "schema", "status", "--workspace-root", _workspaceRoot, "--format", "xml");
        Assert.AreEqual(1, schemaStatusExit, schemaStatusErr);
        Assert.IsTrue(schemaStatusOut.Contains("path=\"_schema/spec.xsd\" state=\"modified\"", StringComparison.Ordinal));

        var (schemaSyncExit, _, schemaSyncErr) = RunCli(
            "schema", "sync", "--expected-version", "1.0", "--workspace-root", _workspaceRoot, "--format", "xml");
        Assert.AreEqual(0, schemaSyncExit, schemaSyncErr);
        var (skillSyncExit, _, skillSyncErr) = RunCli(
            "skill", "sync", "--force", "--output-dir", _skillDir, "--format", "xml");
        Assert.AreEqual(0, skillSyncExit, skillSyncErr);

        Assert.AreEqual(0, RunCli("schema", "status", "--workspace-root", _workspaceRoot, "--format", "xml").ExitCode);
        Assert.AreEqual(0, RunCli("skill", "status", "--output-dir", _skillDir, "--format", "xml").ExitCode);
        Assert.AreEqual(0, RunCli("validate", "--workspace-root", _workspaceRoot, "--format", "xml").ExitCode);
        Assert.AreEqual(repositoryGuidance, File.ReadAllText(agentsPath));
    }

    private static (int ExitCode, string Stdout, string Stderr) RunCli(params string[] args)
    {
        var originalOut = Console.Out;
        var originalErr = Console.Error;
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        try
        {
            Console.SetOut(stdout);
            Console.SetError(stderr);
            return (Program.Main(args), stdout.ToString(), stderr.ToString());
        }
        finally
        {
            Console.SetOut(originalOut);
            Console.SetError(originalErr);
        }
    }
}
