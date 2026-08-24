using DogdouSpec.Cli;
using DogdouSpec.Core.Diagnostics;

namespace DogdouSpec.Cli.Tests;

[TestClass]
public sealed class QueryCommandTests
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
        _tempDir = Path.Combine(Path.GetTempPath(), "DogdouSpec_QueryTests_" + Guid.NewGuid().ToString("N"));
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
    public void Query_AllElementNodeSet_ReturnsCompactXmlWrapperWithExitCode0()
    {
        var demoWorkspace = Path.Combine(RepoRoot, "docs", "demos", "v1-core", ".dogdouspec");

        var (exitCode, stdout, stderr) = RunCli(
            "query",
            "--workspace-root", demoWorkspace,
            "--document", "20260823-xpath-core/tasks.xml",
            "--xpath", "//task[@status='in-progress']",
            "--format", "xml");

        Assert.AreEqual(0, exitCode, $"Stderr: {stderr}");
        Assert.IsTrue(stdout.Contains("<results document=\"20260823-xpath-core/tasks.xml\"", StringComparison.Ordinal));
        Assert.IsTrue(stdout.Contains("revision=\"9\"", StringComparison.Ordinal));
        Assert.IsTrue(stdout.Contains("type=\"node-set\"", StringComparison.Ordinal));
        Assert.IsTrue(stdout.Contains("derived=\"false\"", StringComparison.Ordinal));
        Assert.IsTrue(stdout.Contains("<task id=\"20260823-task-xpath-projection\"", StringComparison.Ordinal));
    }

    [TestMethod]
    public void Query_Projection_SetsDerivedTrueAndFiltersMembers()
    {
        var demoWorkspace = Path.Combine(RepoRoot, "docs", "demos", "v1-core", ".dogdouspec");

        var (exitCode, stdout, stderr) = RunCli(
            "query",
            "--workspace-root", demoWorkspace,
            "--document", "20260823-xpath-core/tasks.xml",
            "--xpath", "ds:filter(//task[@status='in-progress'], '@id', '@status', 'index')",
            "--format", "xml");

        Assert.AreEqual(0, exitCode, $"Stderr: {stderr}");
        Assert.IsTrue(stdout.Contains("derived=\"true\"", StringComparison.Ordinal));
        Assert.IsTrue(stdout.Contains("<task id=\"20260823-task-xpath-projection\" status=\"in-progress\">", StringComparison.Ordinal));
        Assert.IsTrue(stdout.Contains("<index>", StringComparison.Ordinal));
        Assert.IsFalse(stdout.Contains("<context>", StringComparison.Ordinal));
        Assert.IsFalse(stdout.Contains("<records>", StringComparison.Ordinal));
    }

    [TestMethod]
    public void Query_WithVariable_BindsAndEvaluatesSuccessfully()
    {
        var demoWorkspace = Path.Combine(RepoRoot, "docs", "demos", "v1-core", ".dogdouspec");

        var (exitCode, stdout, stderr) = RunCli(
            "query",
            "--workspace-root", demoWorkspace,
            "--document", "20260823-xpath-core/tasks.xml",
            "--var", "task_id=20260823-task-xpath-projection",
            "--xpath", "//task[@id=$task_id]",
            "--format", "xml");

        Assert.AreEqual(0, exitCode, $"Stderr: {stderr}");
        Assert.IsTrue(stdout.Contains("<task id=\"20260823-task-xpath-projection\"", StringComparison.Ordinal));
    }

    [TestMethod]
    public void Query_ScalarBoolean_ReturnsSingleResultElement()
    {
        var demoWorkspace = Path.Combine(RepoRoot, "docs", "demos", "v1-core", ".dogdouspec");

        var (exitCode, stdout, stderr) = RunCli(
            "query",
            "--workspace-root", demoWorkspace,
            "--document", "20260823-xpath-core/tasks.xml",
            "--xpath", "count(//task[@status='in-progress']) > 0",
            "--format", "xml");

        Assert.AreEqual(0, exitCode, $"Stderr: {stderr}");
        Assert.IsTrue(stdout.Contains("<result", StringComparison.Ordinal));
        Assert.IsTrue(stdout.Contains("type=\"boolean\"", StringComparison.Ordinal));
        Assert.IsTrue(stdout.Contains(">true</result>", StringComparison.Ordinal));
    }

    [TestMethod]
    public void Query_ScalarNumber_ReturnsSingleResultElement()
    {
        var demoWorkspace = Path.Combine(RepoRoot, "docs", "demos", "v1-core", ".dogdouspec");

        var (exitCode, stdout, stderr) = RunCli(
            "query",
            "--workspace-root", demoWorkspace,
            "--document", "20260823-xpath-core/tasks.xml",
            "--xpath", "count(//task)",
            "--format", "xml");

        Assert.AreEqual(0, exitCode, $"Stderr: {stderr}");
        Assert.IsTrue(stdout.Contains("type=\"number\"", StringComparison.Ordinal));
        Assert.IsTrue(stdout.Contains(">4</result>", StringComparison.Ordinal));
    }

    [TestMethod]
    public void Query_ScalarString_ReturnsSingleResultElement()
    {
        var demoWorkspace = Path.Combine(RepoRoot, "docs", "demos", "v1-core", ".dogdouspec");

        var (exitCode, stdout, stderr) = RunCli(
            "query",
            "--workspace-root", demoWorkspace,
            "--document", "20260823-xpath-core/tasks.xml",
            "--xpath", "string(//task[@id='20260823-task-xpath-projection']/@status)",
            "--format", "xml");

        Assert.AreEqual(0, exitCode, $"Stderr: {stderr}");
        Assert.IsTrue(stdout.Contains("type=\"string\"", StringComparison.Ordinal));
        Assert.IsTrue(stdout.Contains(">in-progress</result>", StringComparison.Ordinal));
    }

    [TestMethod]
    public void Query_AttributeNodeSet_UsesMinimalTypedItemWrappers()
    {
        var demoWorkspace = Path.Combine(RepoRoot, "docs", "demos", "v1-core", ".dogdouspec");

        var (exitCode, stdout, stderr) = RunCli(
            "query",
            "--workspace-root", demoWorkspace,
            "--document", "20260823-xpath-core/tasks.xml",
            "--xpath", "//task/@id",
            "--format", "xml");

        Assert.AreEqual(0, exitCode, $"Stderr: {stderr}");
        Assert.IsTrue(stdout.Contains("<item type=\"attribute\" name=\"id\" value=\"20260823-task-xpath-projection\"", StringComparison.Ordinal));
    }

    [TestMethod]
    public void Query_TextNodeSet_UsesMinimalTypedItemWrappers()
    {
        var demoWorkspace = Path.Combine(RepoRoot, "docs", "demos", "v1-core", ".dogdouspec");

        var (exitCode, stdout, stderr) = RunCli(
            "query",
            "--workspace-root", demoWorkspace,
            "--document", "knowledge.xml",
            "--xpath", "//entry/statement/text()",
            "--format", "xml");

        Assert.AreEqual(0, exitCode, $"Stderr: {stderr}");
        Assert.IsTrue(stdout.Contains("<item type=\"text\">", StringComparison.Ordinal));
    }

    [TestMethod]
    public void Query_HumanFormat_ReturnsFormattedHumanText()
    {
        var demoWorkspace = Path.Combine(RepoRoot, "docs", "demos", "v1-core", ".dogdouspec");

        var (exitCode, stdout, stderr) = RunCli(
            "query",
            "--workspace-root", demoWorkspace,
            "--document", "20260823-xpath-core/tasks.xml",
            "--xpath", "//task[@status='in-progress']",
            "--format", "human");

        Assert.AreEqual(0, exitCode, $"Stderr: {stderr}");
        Assert.IsTrue(stdout.Contains("Document: 20260823-xpath-core/tasks.xml (revision: 9", StringComparison.Ordinal));
        Assert.IsTrue(stdout.Contains("Result: 1 node", StringComparison.Ordinal));
        Assert.IsTrue(stdout.Contains("<task id=\"20260823-task-xpath-projection\"", StringComparison.Ordinal));
    }

    [TestMethod]
    public void Query_MissingDocumentOption_ReturnsExitCode2()
    {
        var (exitCode, stdout, stderr) = RunCli("query", "--xpath", "//task");

        Assert.AreEqual(2, exitCode);
        Assert.IsTrue(stderr.Contains(DiagnosticCodes.InvalidArgument, StringComparison.Ordinal));
        Assert.IsTrue(string.IsNullOrEmpty(stdout));
    }

    [TestMethod]
    public void Query_DocumentNotFound_ReturnsExitCode2AndDocumentNotFoundCode()
    {
        var demoWorkspace = Path.Combine(RepoRoot, "docs", "demos", "v1-core", ".dogdouspec");

        var (exitCode, stdout, stderr) = RunCli(
            "query",
            "--workspace-root", demoWorkspace,
            "--document", "20260823-nonexistent/tasks.xml",
            "--xpath", "//task");

        Assert.AreEqual(2, exitCode);
        Assert.IsTrue(stderr.Contains(DiagnosticCodes.DocumentNotFound, StringComparison.Ordinal));
        Assert.IsTrue(string.IsNullOrEmpty(stdout));
    }

    [TestMethod]
    public void Query_UnboundVariable_ReturnsExitCode2()
    {
        var demoWorkspace = Path.Combine(RepoRoot, "docs", "demos", "v1-core", ".dogdouspec");

        var (exitCode, stdout, stderr) = RunCli(
            "query",
            "--workspace-root", demoWorkspace,
            "--document", "20260823-xpath-core/tasks.xml",
            "--xpath", "//task[@id=$missing_var]");

        Assert.AreEqual(2, exitCode);
        Assert.IsTrue(stderr.Contains(DiagnosticCodes.InvalidArgument, StringComparison.Ordinal));
        Assert.IsTrue(string.IsNullOrEmpty(stdout));
    }

    [TestMethod]
    public void Query_DuplicateVariable_ReturnsExitCode2()
    {
        var demoWorkspace = Path.Combine(RepoRoot, "docs", "demos", "v1-core", ".dogdouspec");

        var (exitCode, stdout, stderr) = RunCli(
            "query",
            "--workspace-root", demoWorkspace,
            "--document", "20260823-xpath-core/tasks.xml",
            "--var", "id=1",
            "--var", "id=2",
            "--xpath", "//task[@id=$id]");

        Assert.AreEqual(2, exitCode);
        Assert.IsTrue(stderr.Contains(DiagnosticCodes.InvalidArgument, StringComparison.Ordinal));
        Assert.IsTrue(string.IsNullOrEmpty(stdout));
    }

    [TestMethod]
    public void Query_MixedNodeSet_UsesTypedItemWrappers()
    {
        var demoWorkspace = Path.Combine(RepoRoot, "docs", "demos", "v1-core", ".dogdouspec");

        var (exitCode, stdout, stderr) = RunCli(
            "query",
            "--workspace-root", demoWorkspace,
            "--document", "20260823-xpath-core/tasks.xml",
            "--xpath", "//task[1] | //task[1]/@id",
            "--format", "xml");

        Assert.AreEqual(0, exitCode, $"Stderr: {stderr}");
        Assert.IsTrue(stdout.Contains("<item type=\"element\">", StringComparison.Ordinal));
        Assert.IsTrue(stdout.Contains("<item type=\"attribute\" name=\"id\"", StringComparison.Ordinal));
    }

    [TestMethod]
    public void Query_LimitExceeded_ReturnsExitCode7AndDiagnosticsOnStderr()
    {
        // Create an oversized document in temp workspace
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

        var (exitCode, stdout, stderr) = RunCli(
            "query",
            "--workspace-root", dogdouDir,
            "--document", "backlog.xml",
            "--xpath", "//task",
            "--format", "xml");

        Assert.AreEqual(7, exitCode, "Limit exceeded must return exit code 7");
        Assert.IsTrue(stderr.Contains("<diagnostics command=\"query\"", StringComparison.Ordinal));
        Assert.IsTrue(stderr.Contains(DiagnosticCodes.LimitExceeded, StringComparison.Ordinal));
        Assert.IsTrue(string.IsNullOrEmpty(stdout), "No partial stdout output on limit failure");
    }

    [TestMethod]
    public void Query_ProjectionWithNodeSetMember_ReturnsExitCode2AndEmptyStdout()
    {
        var demoWorkspace = Path.Combine(RepoRoot, "docs", "demos", "v1-core", ".dogdouspec");

        var (exitCode, stdout, stderr) = RunCli(
            "query",
            "--workspace-root", demoWorkspace,
            "--document", "20260823-xpath-core/tasks.xml",
            "--xpath", "ds:filter(//task, @id)",
            "--format", "xml");

        Assert.AreEqual(2, exitCode, "Node-set member argument must fail with exit code 2");
        Assert.IsTrue(stderr.Contains(DiagnosticCodes.InvalidArgument, StringComparison.Ordinal));
        Assert.IsTrue(string.IsNullOrEmpty(stdout), "Stdout must be empty on invalid member argument failure");
    }

    [TestMethod]
    public void Query_ProjectionWithBooleanMember_ReturnsExitCode2AndEmptyStdout()
    {
        var demoWorkspace = Path.Combine(RepoRoot, "docs", "demos", "v1-core", ".dogdouspec");

        var (exitCode, stdout, stderr) = RunCli(
            "query",
            "--workspace-root", demoWorkspace,
            "--document", "20260823-xpath-core/tasks.xml",
            "--xpath", "ds:filter(//task, true())",
            "--format", "xml");

        Assert.AreEqual(2, exitCode, "Boolean member argument must fail with exit code 2");
        Assert.IsTrue(stderr.Contains(DiagnosticCodes.InvalidArgument, StringComparison.Ordinal));
        Assert.IsTrue(string.IsNullOrEmpty(stdout), "Stdout must be empty on boolean member failure");
    }

    [TestMethod]
    public void Query_ProjectionWithNumberMember_ReturnsExitCode2AndEmptyStdout()
    {
        var demoWorkspace = Path.Combine(RepoRoot, "docs", "demos", "v1-core", ".dogdouspec");

        var (exitCode, stdout, stderr) = RunCli(
            "query",
            "--workspace-root", demoWorkspace,
            "--document", "20260823-xpath-core/tasks.xml",
            "--xpath", "ds:filter(//task, 42)",
            "--format", "xml");

        Assert.AreEqual(2, exitCode, "Number member argument must fail with exit code 2");
        Assert.IsTrue(stderr.Contains(DiagnosticCodes.InvalidArgument, StringComparison.Ordinal));
        Assert.IsTrue(string.IsNullOrEmpty(stdout), "Stdout must be empty on number member failure");
    }

    [TestMethod]
    public void Query_ProjectionWithBoundStringVariableMember_ReturnsSuccess()
    {
        var demoWorkspace = Path.Combine(RepoRoot, "docs", "demos", "v1-core", ".dogdouspec");

        var (exitCode, stdout, stderr) = RunCli(
            "query",
            "--workspace-root", demoWorkspace,
            "--document", "20260823-xpath-core/tasks.xml",
            "--var", "m1=@id",
            "--var", "m2=index",
            "--xpath", "ds:filter(//task[@status='in-progress'], $m1, $m2)",
            "--format", "xml");

        Assert.AreEqual(0, exitCode, $"Stderr: {stderr}");
        Assert.IsTrue(stdout.Contains("derived=\"true\"", StringComparison.Ordinal));
        Assert.IsTrue(stdout.Contains("<task id=\"20260823-task-xpath-projection\"", StringComparison.Ordinal));
        Assert.IsTrue(stdout.Contains("<index>", StringComparison.Ordinal));
        Assert.IsFalse(stdout.Contains("<context>", StringComparison.Ordinal));
    }
}
