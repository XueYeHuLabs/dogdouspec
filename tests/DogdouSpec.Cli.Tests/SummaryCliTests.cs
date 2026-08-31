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
        IterationCreator.Create(_wsRoot, iterId, "feature", activate: true);
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
        IterationCreator.Create(_wsRoot, iterId, "feature", activate: true);

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
}
