using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Xml.Linq;
using DogdouSpec.Cli;
using DogdouSpec.Core.Diagnostics;

namespace DogdouSpec.Cli.Tests;

[TestClass]
public sealed class ProgressionCliTests
{
    private static readonly string[] TestCriteria = ["Criterion."];
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
        _tempDir = Path.Combine(Path.GetTempPath(), "DogdouSpec_ProgCliTests_" + Guid.NewGuid().ToString("N"));
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
    public void TaskNextCli_SupportsAgentFilteringAndTaskSelection()
    {
        var workspace = CreateWorkspaceCopy();
        var tasksPath = Path.Combine(workspace, "20260823-xpath-core", "tasks.xml");
        var tasksDoc = XDocument.Load(tasksPath);
        foreach (var taskEl in tasksDoc.Root!.Elements("task"))
        {
            taskEl.SetAttributeValue("agent", "codex");
        }
        tasksDoc.Save(tasksPath);

        // 1. Task next with matching agent
        var (exitCode1, stdout1, stderr1) = RunCli(
            "task", "next",
            "--iteration", "20260823-xpath-core",
            "--agent", "codex",
            "--workspace-root", workspace,
            "--format", "xml");

        Assert.AreEqual(0, exitCode1, $"Stderr: {stderr1}");
        Assert.IsTrue(stdout1.Contains("<task-next"), "Expected <task-next>");
        Assert.IsTrue(stdout1.Contains("20260823-task-xpath-projection"));

        // 2. Task next with non-matching agent (none assigned to other-agent)
        var (exitCode2, stdout2, stderr2) = RunCli(
            "task", "next",
            "--iteration", "20260823-xpath-core",
            "--agent", "nonexistent-agent",
            "--workspace-root", workspace,
            "--format", "xml");

        Assert.AreEqual(0, exitCode2, $"Stderr: {stderr2}");
        Assert.IsTrue(stdout2.Contains("found=\"false\""));

        // 3. Task next with explicit valid --task
        var (exitCode3, stdout3, stderr3) = RunCli(
            "task", "next",
            "--iteration", "20260823-xpath-core",
            "--task", "20260823-task-xpath-projection",
            "--workspace-root", workspace,
            "--format", "human");

        Assert.AreEqual(0, exitCode3, $"Stderr: {stderr3}");
        Assert.IsTrue(stdout3.Contains("20260823-task-xpath-projection"));

        // 4. Task next with non-existent --task fails with code 2
        var (exitCode4, stdout4, stderr4) = RunCli(
            "task", "next",
            "--iteration", "20260823-xpath-core",
            "--task", "nonexistent-task-id",
            "--workspace-root", workspace,
            "--format", "xml");

        Assert.AreEqual(2, exitCode4);
        Assert.IsTrue(stderr4.Contains(DiagnosticCodes.ResourceNotFound));
    }

    [TestMethod]
    public void TaskSummaryCli_XmlAndHuman_IncludesEligibleAndTerminalDispositions()
    {
        var workspace = CreateWorkspaceCopy();

        // Run task summary in XML
        var (exitCodeXml, stdoutXml, stderrXml) = RunCli(
            "task", "summary",
            "--iteration", "20260823-xpath-core",
            "--workspace-root", workspace,
            "--format", "xml");

        Assert.AreEqual(0, exitCodeXml, $"Stderr: {stderrXml}");
        Assert.IsTrue(stdoutXml.Contains("<task-summary"));
        Assert.IsTrue(stdoutXml.Contains("eligible=\""));
        Assert.IsTrue(stdoutXml.Contains("completion_percentage=\""));

        // Run task summary in Human
        var (exitCodeHuman, stdoutHuman, stderrHuman) = RunCli(
            "task", "summary",
            "--iteration", "20260823-xpath-core",
            "--workspace-root", workspace,
            "--format", "human");

        Assert.AreEqual(0, exitCodeHuman, $"Stderr: {stderrHuman}");
        Assert.IsTrue(stdoutHuman.Contains("Task Summary for iteration"));
        Assert.IsTrue(stdoutHuman.Contains("Eligible tasks:"));
        Assert.IsTrue(stdoutHuman.Contains("Completion:"));
    }

    [TestMethod]
    public void SummaryCli_AllFormats_MarkdownJsonXmlHuman()
    {
        var workspace = CreateWorkspaceCopy();

        // 1. Markdown
        var (exitMd, stdoutMd, errMd) = RunCli(
            "summary",
            "--iteration", "20260823-xpath-core",
            "--workspace-root", workspace,
            "--format", "markdown");
        Assert.AreEqual(0, exitMd, $"Stderr: {errMd}");
        Assert.IsTrue(stdoutMd.Contains("Iteration Progress: `20260823-xpath-core`"));
        Assert.IsTrue(stdoutMd.Contains("Next Action:"));

        // 2. JSON
        var (exitJson, stdoutJson, errJson) = RunCli(
            "summary",
            "--iteration", "20260823-xpath-core",
            "--workspace-root", workspace,
            "--format", "json");
        Assert.AreEqual(0, exitJson, $"Stderr: {errJson}");
        Assert.IsTrue(stdoutJson.Contains("\"iteration\": \"20260823-xpath-core\""));
        Assert.IsTrue(stdoutJson.Contains("\"recommended_next_action\":"));

        // 3. XML
        var (exitXml, stdoutXml, errXml) = RunCli(
            "summary",
            "--iteration", "20260823-xpath-core",
            "--workspace-root", workspace,
            "--format", "xml");
        Assert.AreEqual(0, exitXml, $"Stderr: {errXml}");
        Assert.IsTrue(stdoutXml.Contains("<iteration-summary"));
        Assert.IsTrue(stdoutXml.Contains("<next-action"));

        // 4. Human
        var (exitHuman, stdoutHuman, errHuman) = RunCli(
            "summary",
            "--iteration", "20260823-xpath-core",
            "--workspace-root", workspace,
            "--format", "human");
        Assert.AreEqual(0, exitHuman, $"Stderr: {errHuman}");
        Assert.IsTrue(stdoutHuman.Contains("Iteration: 20260823-xpath-core"));
        Assert.IsTrue(stdoutHuman.Contains("Next Action:"));
    }

    [TestMethod]
    public void MultipleActiveIterations_AllPorcelainCommandsFailWithExitCode2()
    {
        var wsRoot = Path.Combine(_tempDir, "multi-active-ws");
        Directory.CreateDirectory(wsRoot);
        DogdouSpec.Core.Workspace.WorkspaceInitializer.Initialize(wsRoot, wsRoot);
        var workspace = Path.Combine(wsRoot, ".dogdouspec");
        DogdouSpec.Core.Iterations.IterationCreator.Create(workspace, "20260908-iter-a", "feature", activate: true, criteria: TestCriteria);
        DogdouSpec.Core.Iterations.IterationCreator.Create(workspace, "20260909-iter-b", "feature", activate: true, criteria: TestCriteria);

        // 1. task next without --iteration
        var (exitNext, _, errNext) = RunCli("task", "next", "--workspace-root", workspace, "--format", "xml");
        Assert.AreEqual(2, exitNext);
        Assert.IsTrue(errNext.Contains("Multiple active iterations"));

        // 2. task summary without --iteration
        var (exitTaskSum, _, errTaskSum) = RunCli("task", "summary", "--workspace-root", workspace, "--format", "xml");
        Assert.AreEqual(2, exitTaskSum);
        Assert.IsTrue(errTaskSum.Contains("Multiple active iterations"));

        // 3. summary without --iteration
        var (exitSum, _, errSum) = RunCli("summary", "--workspace-root", workspace, "--format", "xml");
        Assert.AreEqual(2, exitSum);
        Assert.IsTrue(errSum.Contains("Multiple active iterations"));

        // 4. task list without --iteration
        var (exitList, _, errList) = RunCli("task", "list", "--workspace-root", workspace, "--format", "xml");
        Assert.AreEqual(2, exitList);
        Assert.IsTrue(errList.Contains("Multiple active iterations"));
    }
}
