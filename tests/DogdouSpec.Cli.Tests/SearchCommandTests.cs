using DogdouSpec.Cli;
using DogdouSpec.Core.Diagnostics;

namespace DogdouSpec.Cli.Tests;

[TestClass]
public sealed class SearchCommandTests
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
        _tempDir = Path.Combine(Path.GetTempPath(), "DogdouSpec_SearchTests_" + Guid.NewGuid().ToString("N"));
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

    [TestMethod]
    public void Search_ProjectScope_ReturnsMatchingDocumentsWithExitCode0()
    {
        var demoWorkspace = Path.Combine(RepoRoot, "docs", "demos", "v1-core", ".dogdouspec");

        var (exitCode, stdout, stderr) = RunCli(
            "search",
            "--workspace-root", demoWorkspace,
            "--scope", "project",
            "--var", "topic=xpath-extension",
            "--xpath", "//*[@id and index/term[@key='topic' and @value=$topic]]",
            "--format", "xml");

        Assert.AreEqual(0, exitCode, $"Stderr: {stderr}");
        Assert.IsTrue(stdout.Contains("<search scope=\"project\" derived=\"false\">", StringComparison.Ordinal));
        Assert.IsTrue(stdout.Contains("<document path=\"20260823-xpath-core/tasks.xml\"", StringComparison.Ordinal));
        Assert.IsTrue(stdout.Contains("<task id=\"20260823-task-xpath-projection\"", StringComparison.Ordinal));
    }

    [TestMethod]
    public void Search_ProjectScopeWithProjection_SetsDerivedTrue()
    {
        var demoWorkspace = Path.Combine(RepoRoot, "docs", "demos", "v1-core", ".dogdouspec");

        var (exitCode, stdout, stderr) = RunCli(
            "search",
            "--workspace-root", demoWorkspace,
            "--scope", "project",
            "--var", "topic=xpath-extension",
            "--xpath", "ds:filter(//*[@id and index/term[@key='topic' and @value=$topic]], '@id', 'index')",
            "--format", "xml");

        Assert.AreEqual(0, exitCode, $"Stderr: {stderr}");
        Assert.IsTrue(stdout.Contains("<search scope=\"project\" derived=\"true\">", StringComparison.Ordinal));
        Assert.IsTrue(stdout.Contains("<task id=\"20260823-task-xpath-projection\">", StringComparison.Ordinal));
        Assert.IsFalse(stdout.Contains("<context>", StringComparison.Ordinal));
    }

    [TestMethod]
    public void Search_IterationScope_RestrictsSearchToTargetIteration()
    {
        var demoWorkspace = Path.Combine(RepoRoot, "docs", "demos", "v1-core", ".dogdouspec");

        var (exitCode, stdout, stderr) = RunCli(
            "search",
            "--workspace-root", demoWorkspace,
            "--scope", "iteration",
            "--iteration", "20260823-xpath-core",
            "--xpath", "//task[@status='in-progress']",
            "--format", "xml");

        Assert.AreEqual(0, exitCode, $"Stderr: {stderr}");
        Assert.IsTrue(stdout.Contains("<search scope=\"iteration\" iteration=\"20260823-xpath-core\"", StringComparison.Ordinal));
        Assert.IsTrue(stdout.Contains("<document path=\"20260823-xpath-core/tasks.xml\"", StringComparison.Ordinal));
        Assert.IsFalse(stdout.Contains("knowledge.xml", StringComparison.Ordinal));
    }

    [TestMethod]
    public void Search_DeterministicDocumentOrder_AscendingRelativePath()
    {
        var demoWorkspace = Path.Combine(RepoRoot, "docs", "demos", "v1-core", ".dogdouspec");

        var (exitCode, stdout, stderr) = RunCli(
            "search",
            "--workspace-root", demoWorkspace,
            "--scope", "project",
            "--xpath", "/*[@id]",
            "--format", "xml");

        Assert.AreEqual(0, exitCode, $"Stderr: {stderr}");

        var specIdx = stdout.IndexOf("20260823-xpath-core/spec.xml", StringComparison.Ordinal);
        var tasksIdx = stdout.IndexOf("20260823-xpath-core/tasks.xml", StringComparison.Ordinal);
        var backlogIdx = stdout.IndexOf("backlog.xml", StringComparison.Ordinal);
        var knowledgeIdx = stdout.IndexOf("knowledge.xml", StringComparison.Ordinal);

        Assert.IsTrue(specIdx >= 0, "spec.xml must be in search results");
        Assert.IsTrue(tasksIdx >= 0, "tasks.xml must be in search results");
        Assert.IsTrue(backlogIdx >= 0, "backlog.xml must be in search results");
        Assert.IsTrue(knowledgeIdx >= 0, "knowledge.xml must be in search results");

        Assert.IsTrue(specIdx < tasksIdx, "spec.xml must precede tasks.xml");
        Assert.IsTrue(tasksIdx < backlogIdx, "tasks.xml must precede backlog.xml");
        Assert.IsTrue(backlogIdx < knowledgeIdx, "backlog.xml must precede knowledge.xml");
    }

    [TestMethod]
    public void Search_OmitsEmptyDocumentWrappers()
    {
        var demoWorkspace = Path.Combine(RepoRoot, "docs", "demos", "v1-core", ".dogdouspec");

        var (exitCode, stdout, stderr) = RunCli(
            "search",
            "--workspace-root", demoWorkspace,
            "--scope", "project",
            "--xpath", "//entry",
            "--format", "xml");

        Assert.AreEqual(0, exitCode, $"Stderr: {stderr}");
        Assert.IsTrue(stdout.Contains("knowledge.xml", StringComparison.Ordinal));
        Assert.IsFalse(stdout.Contains("tasks.xml", StringComparison.Ordinal));
        Assert.IsFalse(stdout.Contains("spec.xml", StringComparison.Ordinal));
        Assert.IsFalse(stdout.Contains("backlog.xml", StringComparison.Ordinal));
    }

    [TestMethod]
    public void Search_ScalarBoolean_IncludesOnlyTrueDocuments()
    {
        var demoWorkspace = Path.Combine(RepoRoot, "docs", "demos", "v1-core", ".dogdouspec");

        var (exitCode, stdout, stderr) = RunCli(
            "search",
            "--workspace-root", demoWorkspace,
            "--scope", "project",
            "--xpath", "count(//task) > 0",
            "--format", "xml");

        Assert.AreEqual(0, exitCode, $"Stderr: {stderr}");
        Assert.IsTrue(stdout.Contains("20260823-xpath-core/tasks.xml", StringComparison.Ordinal));
        Assert.IsFalse(stdout.Contains("knowledge.xml", StringComparison.Ordinal));
        Assert.IsFalse(stdout.Contains("backlog.xml", StringComparison.Ordinal));
    }

    [TestMethod]
    public void Search_HumanFormat_ReturnsFormattedHumanSummary()
    {
        var demoWorkspace = Path.Combine(RepoRoot, "docs", "demos", "v1-core", ".dogdouspec");

        var (exitCode, stdout, stderr) = RunCli(
            "search",
            "--workspace-root", demoWorkspace,
            "--scope", "project",
            "--var", "topic=xpath-extension",
            "--xpath", "//*[@id and index/term[@key='topic' and @value=$topic]]",
            "--format", "human");

        Assert.AreEqual(0, exitCode, $"Stderr: {stderr}");
        Assert.IsTrue(stdout.Contains("Search Scope: project", StringComparison.Ordinal));
        Assert.IsTrue(stdout.Contains("[20260823-xpath-core/tasks.xml]", StringComparison.Ordinal));
        Assert.IsTrue(stdout.Contains("Found results in 1 document.", StringComparison.Ordinal));
    }

    [TestMethod]
    public void Search_MissingScope_ReturnsExitCode2()
    {
        var (exitCode, stdout, stderr) = RunCli("search", "--xpath", "//task");

        Assert.AreEqual(2, exitCode);
        Assert.IsTrue(stderr.Contains(DiagnosticCodes.InvalidArgument, StringComparison.Ordinal));
        Assert.IsTrue(string.IsNullOrEmpty(stdout));
    }

    [TestMethod]
    public void Search_IterationScopeWithoutIterationOption_ReturnsExitCode2()
    {
        var (exitCode, stdout, stderr) = RunCli("search", "--scope", "iteration", "--xpath", "//task");

        Assert.AreEqual(2, exitCode);
        Assert.IsTrue(stderr.Contains(DiagnosticCodes.InvalidArgument, StringComparison.Ordinal));
        Assert.IsTrue(string.IsNullOrEmpty(stdout));
    }

    [TestMethod]
    public void Search_ProjectScopeWithIterationOption_ReturnsExitCode2()
    {
        var (exitCode, stdout, stderr) = RunCli(
            "search",
            "--scope", "project",
            "--iteration", "20260823-xpath-core",
            "--xpath", "//task");

        Assert.AreEqual(2, exitCode);
        Assert.IsTrue(stderr.Contains(DiagnosticCodes.InvalidArgument, StringComparison.Ordinal));
        Assert.IsTrue(string.IsNullOrEmpty(stdout));
    }

    [TestMethod]
    public void Search_InvalidIterationId_ReturnsExitCode2()
    {
        var (exitCode, stdout, stderr) = RunCli(
            "search",
            "--scope", "iteration",
            "--iteration", "invalid-iter-name",
            "--xpath", "//task");

        Assert.AreEqual(2, exitCode);
        Assert.IsTrue(stderr.Contains(DiagnosticCodes.InvalidArgument, StringComparison.Ordinal));
        Assert.IsTrue(string.IsNullOrEmpty(stdout));
    }

    [TestMethod]
    public void Search_LimitExceeded_ReturnsExitCode7AndDiagnosticsOnStderr()
    {
        var dogdouDir = Path.Combine(_tempDir, ".dogdouspec");
        Directory.CreateDirectory(dogdouDir);

        var sb = new System.Text.StringBuilder();
        sb.AppendLine("<?xml version=\"1.0\" encoding=\"utf-8\"?>");
        sb.AppendLine("<tasks id=\"t\" revision=\"1\">");
        for (var i = 0; i < 10_005; i++)
        {
            sb.AppendLine(System.Globalization.CultureInfo.InvariantCulture, $"  <task id=\"T{i}\" status=\"pending\"/>");
        }
        sb.AppendLine("</tasks>");

        File.WriteAllText(Path.Combine(dogdouDir, "backlog.xml"), sb.ToString());
        File.WriteAllText(Path.Combine(dogdouDir, "knowledge.xml"), "<?xml version=\"1.0\" encoding=\"utf-8\"?><knowledge id=\"k\" revision=\"1\"/>");

        var (exitCode, stdout, stderr) = RunCli(
            "search",
            "--workspace-root", dogdouDir,
            "--scope", "project",
            "--xpath", "//task",
            "--format", "xml");

        Assert.AreEqual(7, exitCode, "Limit exceeded during search must return exit code 7");
        Assert.IsTrue(stderr.Contains("<diagnostics command=\"search\"", StringComparison.Ordinal));
        Assert.IsTrue(stderr.Contains(DiagnosticCodes.LimitExceeded, StringComparison.Ordinal));
        Assert.IsTrue(string.IsNullOrEmpty(stdout), "No partial stdout output on search limit failure");
    }

    [TestMethod]
    public void Search_ProjectionWithNodeSetMember_ReturnsExitCode2AndEmptyStdout()
    {
        var demoWorkspace = Path.Combine(RepoRoot, "docs", "demos", "v1-core", ".dogdouspec");

        var (exitCode, stdout, stderr) = RunCli(
            "search",
            "--workspace-root", demoWorkspace,
            "--scope", "project",
            "--xpath", "ds:filter(//task, @id)",
            "--format", "xml");

        Assert.AreEqual(2, exitCode, "Node-set member in search must fail with exit code 2");
        Assert.IsTrue(stderr.Contains(DiagnosticCodes.InvalidArgument, StringComparison.Ordinal));
        Assert.IsTrue(string.IsNullOrEmpty(stdout), "Stdout must be empty on search member failure");
    }
}
