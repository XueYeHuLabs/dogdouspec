using System.Runtime.CompilerServices;
using System.Xml.Linq;
using DogdouSpec.Cli;
using DogdouSpec.Core.Diagnostics;

namespace DogdouSpec.Cli.Tests;

[TestClass]
public sealed class TaskContextCliTests
{
    private static string RepoRoot = null!;
    private string _tempDir = null!;

    [ClassInitialize]
    public static void ClassInit(TestContext context)
    {
        RepoRoot = FindRepositoryRootFromSource()
            ?? FindRepositoryRoot(Environment.CurrentDirectory)
            ?? FindRepositoryRoot(AppDomain.CurrentDomain.BaseDirectory)
            ?? string.Empty;
        Assert.IsFalse(string.IsNullOrEmpty(RepoRoot), "Repository root could not be located.");
    }

    private static string? FindRepositoryRoot(string startDirectory)
    {
        if (string.IsNullOrWhiteSpace(startDirectory)) return null;

        for (var current = new DirectoryInfo(Path.GetFullPath(startDirectory)); current != null; current = current.Parent)
        {
            if (File.Exists(Path.Combine(current.FullName, "DogdouSpec.slnx")) ||
                File.Exists(Path.Combine(current.FullName, "DogdouSpec.sln")))
            {
                return current.FullName;
            }
        }

        return null;
    }

    private static string? FindRepositoryRootFromSource([CallerFilePath] string sourceFile = "") =>
        FindRepositoryRoot(Path.GetDirectoryName(sourceFile) ?? string.Empty);

    [TestInitialize]
    public void SetUp()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "DogdouSpec_TaskContextCliTests_" + Guid.NewGuid().ToString("N"));
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

    private static (int ExitCode, string Stdout, string Stderr) RunCli(params string[] args)
    {
        var originalOut = Console.Out;
        var originalErr = Console.Error;
        var originalIn = Console.In;

        using var outSw = new StringWriter();
        using var errSw = new StringWriter();
        using var inSr = new StringReader(string.Empty);

        try
        {
            Console.SetOut(outSw);
            Console.SetError(errSw);
            Console.SetIn(inSr);

            var exitCode = Program.Main(args);
            return (exitCode, outSw.ToString(), errSw.ToString());
        }
        finally
        {
            Console.SetOut(originalOut);
            Console.SetError(originalErr);
            Console.SetIn(originalIn);
        }
    }

    [TestMethod]
    public void TaskContextCli_XmlFormat_ReturnsStructuredContext()
    {
        var workspace = CreateWorkspaceCopy();
        var iterId = "20260823-xpath-core";
        var taskId = "20260823-task-xpath-projection";

        var (exitCode, stdout, stderr) = RunCli(
            "task", "context",
            "--task", taskId,
            "--iteration", iterId,
            "--workspace-root", workspace,
            "--format", "xml");

        Assert.AreEqual(0, exitCode, $"Stderr: {stderr}");
        var doc = XDocument.Parse(stdout);
        Assert.IsNotNull(doc.Root);
        Assert.AreEqual("task-context", doc.Root.Name.LocalName);
        Assert.AreEqual(taskId, (string?)doc.Root.Attribute("task"));
        Assert.IsNotNull(doc.Root.Element("essential"));
        Assert.IsNotNull(doc.Root.Element("essential")?.Element("progression-facts"));
    }

    [TestMethod]
    public void TaskContextCli_HumanFormat_ReturnsReadableContext()
    {
        var workspace = CreateWorkspaceCopy();
        var iterId = "20260823-xpath-core";
        var taskId = "20260823-task-xpath-projection";

        var (exitCode, stdout, stderr) = RunCli(
            "task", "context",
            "--task", taskId,
            "--iteration", iterId,
            "--workspace-root", workspace,
            "--format", "human");

        Assert.AreEqual(0, exitCode, $"Stderr: {stderr}");
        StringAssert.Contains(stdout, $"Task Recovery Context: {taskId}");
        StringAssert.Contains(stdout, "Status: IN-PROGRESS");
        StringAssert.Contains(stdout, "=== Essential Context ===");
    }

    [TestMethod]
    public void TaskContextCli_EssentialExceedsMaxBytes_FailsClosedWithLimitExceeded()
    {
        var workspace = CreateWorkspaceCopy();
        var iterId = "20260823-xpath-core";
        var taskId = "20260823-task-xpath-projection";

        var (exitCode, stdout, stderr) = RunCli(
            "task", "context",
            "--task", taskId,
            "--iteration", iterId,
            "--workspace-root", workspace,
            "--max-bytes", "200",
            "--format", "xml");

        Assert.AreEqual(7, exitCode);
        var combined = stdout + stderr;
        StringAssert.Contains(combined, "LIMIT_EXCEEDED");
    }

    [TestMethod]
    public void TaskContextCli_TerminalTask_ReportsTerminalStateAndProhibitions()
    {
        var workspace = CreateWorkspaceCopy();
        var iterId = "20260823-xpath-core";
        var taskId = "20260823-task-iteration-layout"; // status: done

        var (exitCode, stdout, stderr) = RunCli(
            "task", "context",
            "--task", taskId,
            "--iteration", iterId,
            "--workspace-root", workspace,
            "--format", "human");

        Assert.AreEqual(0, exitCode, $"Stderr: {stderr}");
        StringAssert.Contains(stdout, $"Task Recovery Context: {taskId}");
        StringAssert.Contains(stdout, "Status: DONE");
        StringAssert.Contains(stdout, "terminal");
    }

    [TestMethod]
    public void TaskContextCli_NonExistentTask_ReturnsResourceNotFound()
    {
        var workspace = CreateWorkspaceCopy();
        var iterId = "20260823-xpath-core";
        var taskId = "20260823-task-nonexistent";

        var (exitCode, stdout, stderr) = RunCli(
            "task", "context",
            "--task", taskId,
            "--iteration", iterId,
            "--workspace-root", workspace,
            "--format", "xml");

        Assert.AreEqual(2, exitCode);
        StringAssert.Contains(stdout + stderr, "RESOURCE_NOT_FOUND");
    }
}