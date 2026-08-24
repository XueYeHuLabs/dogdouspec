using DogdouSpec.Cli;
using DogdouSpec.Core.Diagnostics;

namespace DogdouSpec.Cli.Tests;

[TestClass]
public sealed class SemanticCliValidationTests
{
    private static string RepoRoot = null!;
    private string _tempDir = null!;

    [ClassInitialize]
    public static void ClassInit(TestContext context)
    {
        var current = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
        while (current != null)
        {
            if (File.Exists(Path.Combine(current.FullName, "DogdouSpec.slnx")) ||
                File.Exists(Path.Combine(current.FullName, "DogdouSpec.sln")))
            {
                RepoRoot = current.FullName;
                break;
            }
            current = current.Parent;
        }

        Assert.IsNotNull(RepoRoot, "Repository root could not be located.");
    }

    [TestInitialize]
    public void SetUp()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "DogdouSpec_CliSemanticTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    [TestCleanup]
    public void TearDown()
    {
        if (Directory.Exists(_tempDir))
        {
            try
            {
                Directory.Delete(_tempDir, true);
            }
            catch
            {
                // Ignore cleanup errors
            }
        }
    }

    private static (int ExitCode, string Stdout, string Stderr) RunCli(params string[] args)
    {
        var originalOut = Console.Out;
        var originalErr = Console.Error;
        var originalDir = Environment.CurrentDirectory;

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
            Environment.CurrentDirectory = originalDir;
        }
    }

    private string CreateWorkspaceCopy()
    {
        var srcDemo = Path.Combine(RepoRoot, "docs", "demos", "v1-core", ".dogdouspec");
        var destDir = Path.Combine(_tempDir, ".dogdouspec");
        CopyDirectory(srcDemo, destDir);
        return destDir;
    }

    private static void CopyDirectory(string sourceDir, string destinationDir)
    {
        Directory.CreateDirectory(destinationDir);
        foreach (var file in Directory.GetFiles(sourceDir))
        {
            File.Copy(file, Path.Combine(destinationDir, Path.GetFileName(file)), true);
        }
        foreach (var dir in Directory.GetDirectories(sourceDir))
        {
            CopyDirectory(dir, Path.Combine(destinationDir, Path.GetFileName(dir)));
        }
    }

    [TestMethod]
    public void CliValidate_DemoWorkspace_ReturnsExitCode0()
    {
        var workspace = CreateWorkspaceCopy();

        var (exitCode, stdout, stderr) = RunCli("validate", "--workspace-root", workspace, "--format", "xml");

        Assert.AreEqual(0, exitCode, $"Stderr: {stderr}");
        Assert.IsTrue(stdout.Contains("<validation valid=\"true\" scope=\"workspace\" schema=\"passed\" semantic=\"passed\" checked_documents=\"4\"", StringComparison.Ordinal));
        Assert.IsTrue(string.IsNullOrEmpty(stderr));
    }

    [TestMethod]
    public void CliValidate_DemoWorkspaceIterationScope_ReturnsExitCode0()
    {
        var workspace = CreateWorkspaceCopy();

        var (exitCode, stdout, stderr) = RunCli("validate", "--workspace-root", workspace, "--iteration", "20260823-xpath-core", "--format", "xml");

        Assert.AreEqual(0, exitCode, $"Stderr: {stderr}");
        Assert.IsTrue(stdout.Contains("<validation valid=\"true\" scope=\"iteration\" iteration=\"20260823-xpath-core\" schema=\"passed\" semantic=\"passed\" checked_documents=\"2\"", StringComparison.Ordinal));
        Assert.IsTrue(string.IsNullOrEmpty(stderr));
    }

    [TestMethod]
    public void CliValidate_DemoWorkspaceDocumentScope_ReturnsExitCode0()
    {
        var workspace = CreateWorkspaceCopy();

        var (exitCode, stdout, stderr) = RunCli("validate", "--workspace-root", workspace, "--document", "20260823-xpath-core/tasks.xml", "--format", "xml");

        Assert.AreEqual(0, exitCode, $"Stderr: {stderr}");
        Assert.IsTrue(stdout.Contains("<validation valid=\"true\" scope=\"document\" document=\"20260823-xpath-core/tasks.xml\" schema=\"passed\" semantic=\"passed\" checked_documents=\"1\"", StringComparison.Ordinal));
        Assert.IsTrue(string.IsNullOrEmpty(stderr));
    }

    [TestMethod]
    public void CliValidate_DuplicateIdSemanticError_ReturnsExitCode3AndXmlDiagnostics()
    {
        var workspace = CreateWorkspaceCopy();

        // Introduce duplicate ID in tasks.xml
        var tasksPath = Path.Combine(workspace, "20260823-xpath-core", "tasks.xml");
        var tasksContent = File.ReadAllText(tasksPath).Replace("\r\n", "\n");
        tasksContent = tasksContent.Replace("20260823-task-task-history", "20260823-task-xpath-projection");
        File.WriteAllText(tasksPath, tasksContent);

        var (exitCode, stdout, stderr) = RunCli("validate", "--workspace-root", workspace, "--format", "xml");

        Assert.AreEqual(3, exitCode, "Semantic validation failure must return exit code 3");
        Assert.IsTrue(string.IsNullOrEmpty(stdout), "Stdout must be empty on validation failure");
        Assert.IsTrue(stderr.Contains("<diagnostics command=\"validate\"", StringComparison.Ordinal));
        Assert.IsTrue(stderr.Contains($"code=\"{DiagnosticCodes.DuplicateId}\"", StringComparison.Ordinal));
    }

    [TestMethod]
    public void CliValidate_TaskCycleSemanticError_ReturnsExitCode3()
    {
        var workspace = CreateWorkspaceCopy();

        // Introduce dependency cycle
        var tasksPath = Path.Combine(workspace, "20260823-xpath-core", "tasks.xml");
        var tasksContent = File.ReadAllText(tasksPath).Replace("\r\n", "\n");
        tasksContent = tasksContent.Replace(
            "target=\"20260823-task-iteration-layout\"\n        relation=\"depends-on\"",
            "target=\"20260823-task-xpath-projection\"\n        relation=\"depends-on\"");
        File.WriteAllText(tasksPath, tasksContent);

        var (exitCode, stdout, stderr) = RunCli("validate", "--workspace-root", workspace, "--format", "xml");

        Assert.AreEqual(3, exitCode);
        Assert.IsTrue(stderr.Contains(DiagnosticCodes.DependencyCycle, StringComparison.Ordinal));
    }

    [TestMethod]
    public void CliValidate_DoneTaskMissingRecord_ReturnsExitCode3()
    {
        var workspace = CreateWorkspaceCopy();

        // In done task, change completion record to discussion
        var tasksPath = Path.Combine(workspace, "20260823-xpath-core", "tasks.xml");
        var tasksContent = File.ReadAllText(tasksPath).Replace("\r\n", "\n");
        tasksContent = tasksContent.Replace("kind=\"completion\"", "kind=\"discussion\"");
        File.WriteAllText(tasksPath, tasksContent);

        var (exitCode, stdout, stderr) = RunCli("validate", "--workspace-root", workspace, "--format", "xml");

        Assert.AreEqual(3, exitCode);
        Assert.IsTrue(stderr.Contains(DiagnosticCodes.TaskCompletionRecordMissing, StringComparison.Ordinal));
    }

    [TestMethod]
    public void CliValidate_ConfirmationTargetIntegrityError_ReturnsExitCode3()
    {
        var workspace = CreateWorkspaceCopy();

        // Introduce contradictory confirmation decision
        var specPath = Path.Combine(workspace, "20260823-xpath-core", "spec.xml");
        var specContent = File.ReadAllText(specPath).Replace("\r\n", "\n");
        var badConf = """
    <confirmation
      id="20260823T040000Z-conf-contradictory"
      action="continue"
      decision="accepted"
      actor="owner"
      decided_at="2026-08-23T04:00:00Z">
      <summary>Contradictory</summary>
      <acceptance>
        <criterion target="20260823-accept-directory-overview" decision="accepted"/>
        <criterion target="20260823-accept-directory-overview" decision="rejected"/>
      </acceptance>
    </confirmation>
""";
        specContent = specContent.Replace("</confirmations>", badConf + "\n  </confirmations>");
        File.WriteAllText(specPath, specContent);

        var (exitCode, stdout, stderr) = RunCli("validate", "--workspace-root", workspace, "--format", "xml");

        Assert.AreEqual(3, exitCode);
        Assert.IsTrue(stderr.Contains(DiagnosticCodes.ContradictoryConfirmationDecision, StringComparison.Ordinal));
    }

    [TestMethod]
    public void CliValidate_ScopedDocumentWithInvalidNonTarget_ReturnsExitCode3AndSemanticContextIncomplete()
    {
        var workspace = CreateWorkspaceCopy();

        // Corrupt backlog.xml with schema error
        var backlogPath = Path.Combine(workspace, "backlog.xml");
        File.WriteAllText(backlogPath, "<backlog id=\"20260823-backlog\"><invalid/></backlog>");

        // Validate document scope
        var (exitCode, stdout, stderr) = RunCli("validate", "--workspace-root", workspace, "--document", "20260823-xpath-core/tasks.xml", "--format", "xml");

        Assert.AreEqual(3, exitCode);
        Assert.IsTrue(stderr.Contains(DiagnosticCodes.SemanticContextIncomplete, StringComparison.Ordinal));
        Assert.IsTrue(stderr.Contains("20260823-xpath-core/tasks.xml", StringComparison.Ordinal));
    }

    [TestMethod]
    public void CliValidate_HumanFormat_ReturnsHumanOutput()
    {
        var workspace = CreateWorkspaceCopy();

        var (exitCode, stdout, stderr) = RunCli("validate", "--workspace-root", workspace, "--format", "human");

        Assert.AreEqual(0, exitCode, $"Stderr: {stderr}");
        Assert.IsTrue(stdout.Contains("Validation passed:", StringComparison.Ordinal));
    }
}
