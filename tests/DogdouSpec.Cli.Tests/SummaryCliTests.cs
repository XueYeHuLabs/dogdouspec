using DogdouSpec.Core.Iterations;
using DogdouSpec.Core.Tasks;
using DogdouSpec.Core.Workspace;

namespace DogdouSpec.Cli.Tests;

[TestClass]
public sealed class SummaryCliTests
{
    private string _tempDir = null!;
    private string _wsRoot = null!;

    private static readonly string[] MarkdownSummaryArgs = new[] { "summary", "--format", "markdown" };
    private static readonly string[] TestCriteria = new[] { "Summary criterion defined." };

    [TestInitialize]
    public void SetUp()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "DogdouSpec_SummaryCliTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        var (_, wsRoot, _) = WorkspaceInitializer.Initialize(_tempDir, _tempDir);
        _wsRoot = wsRoot;
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
    public void SummaryCommand_MarkdownFormat_OutputsProgressCard()
    {
        var iterId = "20260831-cli-test";
        IterationCreator.Create(_wsRoot, iterId, "feature", activate: true, criteria: TestCriteria);
        var input = new QuickTaskInput("CLI Task", new List<string> { "src/**" }, "Done", "Why",
            Array.Empty<string>(), Array.Empty<string>(), new List<string> { "component=core" }, iterId, 1, true, false,
            $"{iterId}-01", "20260831T120000Z-cli-task");
        TaskQuick.Create(_wsRoot, input);

        using var sw = new StringWriter();
        var originalOut = Console.Out;
        try
        {
            Console.SetOut(sw);
            var exitCode = Program.Main(new[] { "summary", "--workspace-root", _tempDir, "--format", "markdown" });
            Assert.AreEqual(0, exitCode);

            var output = sw.ToString();
            Assert.IsTrue(output.Contains("### 🚀 Iteration Progress: `20260831-cli-test`"));
            Assert.IsTrue(output.Contains("Progress: [░░░░░░░░░░] 0.0%"));
            Assert.IsTrue(output.Contains("CLI Task"));
        }
        finally
        {
            Console.SetOut(originalOut);
        }
    }

    [TestMethod]
    public void IterationSummaryCommand_JsonFormat_OutputsJsonStructure()
    {
        var iterId = "20260831-json-test";
        IterationCreator.Create(_wsRoot, iterId, "feature", activate: true, criteria: TestCriteria);

        using var sw = new StringWriter();
        var originalOut = Console.Out;
        try
        {
            Console.SetOut(sw);
            var exitCode = Program.Main(new[] { "iteration", "summary", "--iteration", iterId, "--workspace-root", _tempDir, "--format", "json" });
            Assert.AreEqual(0, exitCode);

            var output = sw.ToString();
            Assert.IsTrue(output.Contains("\"iteration\": \"20260831-json-test\""));
            Assert.IsTrue(output.Contains("\"progress\":"));
        }
        finally
        {
            Console.SetOut(originalOut);
        }
    }

    [TestMethod]
    public void SummaryCommand_WhenWarningsPresent_OutputsSummaryAndReturnsZero()
    {
        var iterId = "20260831-warn-test";
        IterationCreator.Create(_wsRoot, iterId, "feature", activate: true, criteria: TestCriteria);
        var input = new QuickTaskInput("Warning Task", new List<string> { "src/**" }, "Done", "Why",
            Array.Empty<string>(), Array.Empty<string>(), new List<string> { "component=core" }, iterId, 1, true, false,
            $"{iterId}-01", "20260831T120000Z-warn-task");
        TaskQuick.Create(_wsRoot, input);

        // Intentionally modify task status to non-standard status to trigger warning
        var tasksPath = Path.Combine(_wsRoot, iterId, "tasks.xml");
        var tasksDoc = System.Xml.Linq.XDocument.Load(tasksPath);
        tasksDoc.Root!.Element("task")!.SetAttributeValue("status", "mysterious_status");
        tasksDoc.Save(tasksPath);

        using var swOut = new StringWriter();
        using var swErr = new StringWriter();
        var originalOut = Console.Out;
        var originalErr = Console.Error;
        try
        {
            Console.SetOut(swOut);
            Console.SetError(swErr);
            var exitCode = Program.Main(new[] { "summary", "--iteration", iterId, "--workspace-root", _tempDir, "--format", "markdown" });
            Assert.AreEqual(0, exitCode);

            var stdout = swOut.ToString();
            Assert.IsTrue(stdout.Contains("### 🚀 Iteration Progress: `20260831-warn-test`"));
            Assert.IsTrue(stdout.Contains("Warning Task"));

            var stderr = swErr.ToString();
            Assert.IsTrue(stderr.Contains("has unrecognized status 'mysterious_status'"));
        }
        finally
        {
            Console.SetOut(originalOut);
            Console.SetError(originalErr);
        }
    }
}
