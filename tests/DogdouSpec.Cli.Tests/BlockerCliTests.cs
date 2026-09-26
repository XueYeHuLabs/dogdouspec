using System.Runtime.CompilerServices;
using System.Xml.Linq;
using DogdouSpec.Cli;

namespace DogdouSpec.Cli.Tests;

[TestClass]
public sealed class BlockerCliTests
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
        _tempDir = Path.Combine(Path.GetTempPath(), "DogdouSpec_BlockerCliTests_" + Guid.NewGuid().ToString("N"));
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
    public void TaskBlockResumeAndBlockersCli_EndToEndWorkflow()
    {
        var workspace = CreateWorkspaceCopy();
        var iterId = "20260823-xpath-core";

        // Task in demo workspace is 20260823-task-xpath-projection which is in-progress
        var taskId = "20260823-task-xpath-projection";

        // 1. Run task block CLI
        var (blockExit, blockOut, blockErr) = RunCli(
            "task", "block",
            "--task", taskId,
            "--iteration", iterId,
            "--summary", "Waiting for external service access",
            "--blocker-kind", "external",
            "--blocker-owner", "infosec",
            "--blocker-review-at", "20260824T120000Z",
            "--condition", "Firewall exception approved",
            "--next-action", "Resume task execution",
            "--workspace-root", workspace,
            "--format", "xml");

        Assert.AreEqual(0, blockExit, $"Block stderr: {blockErr}");
        StringAssert.Contains(blockOut, "task update");

        // Verify task is now blocked in XML
        var tasksPath = Path.Combine(workspace, iterId, "tasks.xml");
        var tasksDoc = XDocument.Load(tasksPath);
        var taskElem = tasksDoc.Root!.Elements("task").First(t => (string?)t.Attribute("id") == taskId);
        Assert.AreEqual("blocked", (string?)taskElem.Attribute("status"));

        // 2. Query blockers CLI
        var (bListExit, bListOut, bListErr) = RunCli(
            "task", "blockers",
            "--iteration", iterId,
            "--workspace-root", workspace,
            "--format", "xml");

        Assert.AreEqual(0, bListExit, $"Blockers stderr: {bListErr}");
        StringAssert.Contains(bListOut, "<blockers");
        StringAssert.Contains(bListOut, $"task=\"{taskId}\"");
        StringAssert.Contains(bListOut, "owner=\"infosec\"");

        // Query blockers with filter
        var (bFilterExit, bFilterOut, _) = RunCli(
            "task", "blockers",
            "--iteration", iterId,
            "--owner", "infosec",
            "--workspace-root", workspace,
            "--format", "human");

        Assert.AreEqual(0, bFilterExit);
        StringAssert.Contains(bFilterOut, "infosec");

        // 3. Resume task CLI
        var (resumeExit, resumeOut, resumeErr) = RunCli(
            "task", "resume",
            "--task", taskId,
            "--iteration", iterId,
            "--summary", "Access granted",
            "--all",
            "--workspace-root", workspace,
            "--format", "xml");

        Assert.AreEqual(0, resumeExit, $"Resume stderr: {resumeErr}");

        // Verify task returned to in-progress
        tasksDoc = XDocument.Load(tasksPath);
        taskElem = tasksDoc.Root!.Elements("task").First(t => (string?)t.Attribute("id") == taskId);
        Assert.AreEqual("in-progress", (string?)taskElem.Attribute("status"));
    }

    [TestMethod]
    public void TaskResumeCli_EnforcesExplicitIntentAndSummary()
    {
        var workspace = CreateWorkspaceCopy();
        var iterId = "20260823-xpath-core";
        var taskId = "20260823-task-xpath-projection";

        // Block the task
        var (blockExit, _, _) = RunCli(
            "task", "block",
            "--task", taskId,
            "--iteration", iterId,
            "--summary", "Waiting on review",
            "--workspace-root", workspace,
            "--format", "xml");
        Assert.AreEqual(0, blockExit);

        // Resume without --all or --finding fails
        var (noIntentExit, _, noIntentErr) = RunCli(
            "task", "resume",
            "--task", taskId,
            "--iteration", iterId,
            "--summary", "Resuming",
            "--workspace-root", workspace,
            "--format", "xml");
        Assert.AreNotEqual(0, noIntentExit);
        StringAssert.Contains(noIntentErr, "INVALID_ARGUMENT");

        // Resume with --all but missing summary fails
        var (noSumExit, _, noSumErr) = RunCli(
            "task", "resume",
            "--task", taskId,
            "--iteration", iterId,
            "--all",
            "--workspace-root", workspace,
            "--format", "xml");
        Assert.AreNotEqual(0, noSumExit);
        StringAssert.Contains(noSumErr, "INVALID_ARGUMENT");

        // Resume with --all and --summary succeeds
        var (okExit, okOut, okErr) = RunCli(
            "task", "resume",
            "--task", taskId,
            "--iteration", iterId,
            "--all",
            "--summary", "Review complete and approved",
            "--workspace-root", workspace,
            "--format", "xml");
        Assert.AreEqual(0, okExit, $"Stderr: {okErr}");
        StringAssert.Contains(okOut, "task update");
    }

    [TestMethod]
    public void TaskBlockCli_RecordOnly_PreservesInProgressStatusAndBlockersListsBoth()
    {
        var workspace = CreateWorkspaceCopy();
        var iterId = "20260823-xpath-core";
        var taskId = "20260823-task-xpath-projection";

        // 1. Run task block with --record-only
        var (blockExit, blockOut, blockErr) = RunCli(
            "task", "block",
            "--task", taskId,
            "--iteration", iterId,
            "--summary", "Waiting on external dependency while work continues",
            "--blocker-kind", "environment",
            "--blocker-owner", "devops",
            "--blocker-review-at", "20260825T120000Z",
            "--condition", "Dev cluster restored",
            "--next-action", "Run integration tests",
            "--record-only",
            "--workspace-root", workspace,
            "--format", "xml");

        Assert.AreEqual(0, blockExit, $"Block stderr: {blockErr}");
        StringAssert.Contains(blockOut, "task update");

        // 2. Verify task status remains 'in-progress' in tasks.xml
        var tasksPath = Path.Combine(workspace, iterId, "tasks.xml");
        var tasksDoc = XDocument.Load(tasksPath);
        var taskElem = tasksDoc.Root!.Elements("task").First(t => (string?)t.Attribute("id") == taskId);
        Assert.AreEqual("in-progress", (string?)taskElem.Attribute("status"));

        // Verify active finding record exists
        var findingRec = taskElem.Element("records")!.Elements("record")
            .FirstOrDefault(r => (string?)r.Attribute("kind") == "finding" && (string?)r.Attribute("status") == "active");
        Assert.IsNotNull(findingRec);
        Assert.AreEqual("record-only", findingRec.Element("index")?.Elements("term")
            .FirstOrDefault(t => (string?)t.Attribute("key") == "blocker-mode")?.Attribute("value")?.Value);

        // 3. Query task blockers - verify record-only blocker is returned
        var (bListExit, bListOut, bListErr) = RunCli(
            "task", "blockers",
            "--iteration", iterId,
            "--workspace-root", workspace,
            "--format", "xml");

        Assert.AreEqual(0, bListExit, $"Blockers stderr: {bListErr}");
        StringAssert.Contains(bListOut, $"task=\"{taskId}\"");
        StringAssert.Contains(bListOut, "task_status=\"in-progress\"");
        StringAssert.Contains(bListOut, "record_only=\"true\"");

        // Query with human format
        var (bHumanExit, bHumanOut, _) = RunCli(
            "task", "blockers",
            "--iteration", iterId,
            "--workspace-root", workspace,
            "--format", "human");

        Assert.AreEqual(0, bHumanExit);
        StringAssert.Contains(bHumanOut, "in-progress");
        StringAssert.Contains(bHumanOut, "record-only");

        // Query with --mode record-only
        var (bModeRecExit, bModeRecOut, _) = RunCli(
            "task", "blockers",
            "--iteration", iterId,
            "--mode", "record-only",
            "--workspace-root", workspace,
            "--format", "xml");

        Assert.AreEqual(0, bModeRecExit);
        StringAssert.Contains(bModeRecOut, $"task=\"{taskId}\"");

        // Query with --mode blocking (should NOT return the record-only blocker)
        var (bModeBlkExit, bModeBlkOut, _) = RunCli(
            "task", "blockers",
            "--iteration", iterId,
            "--mode", "blocking",
            "--workspace-root", workspace,
            "--format", "xml");

        Assert.AreEqual(0, bModeBlkExit);
        Assert.IsFalse(bModeBlkOut.Contains($"task=\"{taskId}\"", StringComparison.Ordinal));

        // 4. Resolve/resume on in-progress task with active finding
        var (resumeExit, resumeOut, resumeErr) = RunCli(
            "task", "resume",
            "--task", taskId,
            "--iteration", iterId,
            "--all",
            "--summary", "Dev cluster restored successfully",
            "--workspace-root", workspace,
            "--format", "xml");

        Assert.AreEqual(0, resumeExit, $"Resume stderr: {resumeErr}");
        StringAssert.Contains(resumeOut, "task update");

        // Verify task is still in-progress and finding is resolved
        tasksDoc = XDocument.Load(tasksPath);
        taskElem = tasksDoc.Root!.Elements("task").First(t => (string?)t.Attribute("id") == taskId);
        Assert.AreEqual("in-progress", (string?)taskElem.Attribute("status"));
        var activeFindings = taskElem.Element("records")!.Elements("record")
            .Where(r => (string?)r.Attribute("kind") == "finding" && (string?)r.Attribute("status") == "active")
            .ToList();
        Assert.AreEqual(0, activeFindings.Count);
    }
}
