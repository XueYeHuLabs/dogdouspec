using System.Runtime.CompilerServices;
using System.Xml.Linq;
using DogdouSpec.Cli;
using DogdouSpec.Core.Diagnostics;

namespace DogdouSpec.Cli.Tests;

[TestClass]
public sealed class ProgressionRecoveryEndToEndTests
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
        _tempDir = Path.Combine(Path.GetTempPath(), "DogdouSpec_EndToEndTests_" + Guid.NewGuid().ToString("N"));
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
    public void EndToEnd_ProgressionBlockerRecoveryAndHandoffWorkflow()
    {
        // 1. Workspace Init
        var (initExit, initOut, initErr) = RunCli("workspace", "init", "--workspace-root", _tempDir, "--format", "xml");
        Assert.AreEqual(0, initExit, $"Init error: {initErr}");

        var iterId = "20260908-e2e-iteration";

        // 2. Create Feature Iteration (AC01, AC09)
        var (createExit, createOut, createErr) = RunCli("iteration", "create", "--id", iterId, "--kind", "feature", "--workspace-root", _tempDir, "--format", "xml");
        Assert.AreEqual(0, createExit, $"Create error: {createErr}");

        // 2b. Define acceptance criterion so iteration can be activated
        var (defExit, defOut, defErr) = RunCli("iteration", "criterion", "define", "--iteration", iterId, "--text", "End to end feature verified.", "--workspace-root", _tempDir, "--format", "xml");
        Assert.AreEqual(0, defExit, $"Criterion define error: {defErr}");

        // 3. Activate Iteration with owner authorization (AC02, AC04)
        var (actExit, actOut, actErr) = RunCli("iteration", "activate", "--iteration", iterId, "--auto-approve", "--summary", "Pre-authorized activation", "--workspace-root", _tempDir, "--format", "xml");
        Assert.AreEqual(0, actExit, $"Activate error: {actErr}");

        // 4. In an empty active iteration, Task Next returns null and reports no-tasks (AC01, AC02)
        var (nextEmptyExit, nextEmptyOut, nextEmptyErr) = RunCli("task", "next", "--iteration", iterId, "--workspace-root", _tempDir, "--format", "xml");
        Assert.AreEqual(0, nextEmptyExit, $"Next error: {nextEmptyErr}");
        StringAssert.Contains(nextEmptyOut, "found=\"false\"");
        StringAssert.Contains(nextEmptyOut, "action_category=\"no-tasks\"");
        StringAssert.Contains(nextEmptyOut, "reason_code=\"NO_TASKS\"");

        // 5. Add two tasks: Task 1 and Task 2 (Task 2 depends on Task 1) (AC03)
        var nowUtc = DateTimeOffset.UtcNow;
        var opId1 = $"{nowUtc:yyyyMMddTHHmmssZ}-op-e2e-01";
        var opId2 = $"{nowUtc.AddSeconds(1):yyyyMMddTHHmmssZ}-op-e2e-02";
        var reviewAt = $"{nowUtc.AddDays(1):yyyyMMddTHHmmssZ}";

        var taskId1 = "20260908-task-e2e-01";
        var (t1Exit, t1Out, t1Err) = RunCli("task", "quick",
            "--id", taskId1,
            "--operation-id", opId1,
            "--title", "First execution task",
            "--scope", "src/**",
            "--done-when", "Implementation complete",
            "--why", "Primary feature requirement",
            "--agent", "codex",
            "--review-required",
            "--iteration", iterId,
            "--workspace-root", _tempDir,
            "--format", "xml");
        Assert.AreEqual(0, t1Exit, $"Task 1 add error: {t1Err}");

        var taskId2 = "20260908-task-e2e-02";
        var (t2Exit, t2Out, t2Err) = RunCli("task", "quick",
            "--id", taskId2,
            "--operation-id", opId2,
            "--title", "Second dependent task",
            "--scope", "src/**",
            "--done-when", "Dependent feature complete",
            "--why", "Secondary requirement",
            "--depends-on", taskId1,
            "--iteration", iterId,
            "--workspace-root", _tempDir,
            "--format", "xml");
        Assert.AreEqual(0, t2Exit, $"Task 2 add error: {t2Err}");

        // 6. Task Next returns Task 1 as actionable (Task 2 is blocked by dependency) (AC01, AC03)
        var (next1Exit, next1Out, next1Err) = RunCli("task", "next", "--iteration", iterId, "--workspace-root", _tempDir, "--format", "xml");
        Assert.AreEqual(0, next1Exit, $"Next error: {next1Err}");
        StringAssert.Contains(next1Out, taskId1);
        StringAssert.Contains(next1Out, "action_category=\"start-work\"");

        // 7. Start Task 1 (transitions to in-progress)
        var (startExit, startOut, startErr) = RunCli("task", "start", "--task", taskId1, "--iteration", iterId, "--summary", "Started Task 1", "--workspace-root", _tempDir, "--format", "xml");
        Assert.AreEqual(0, startExit, $"Start error: {startErr}");

        // 8. Block Task 1 with structured blocker metadata (AC05, AC06)
        var (blockExit, blockOut, blockErr) = RunCli("task", "block",
            "--task", taskId1,
            "--iteration", iterId,
            "--summary", "Waiting on external database provisioning",
            "--blocker-kind", "environment",
            "--blocker-owner", "devops",
            "--blocker-review-at", reviewAt,
            "--condition", "Database provisioned and credentials supplied",
            "--next-action", "Resume task execution and run migrations",
            "--workspace-root", _tempDir,
            "--format", "xml");
        Assert.AreEqual(0, blockExit, $"Block error: {blockErr}");

        // Verify task status is blocked in XML
        var tasksXmlPath = Path.Combine(_tempDir, ".dogdouspec", iterId, "tasks.xml");
        var tasksDoc = XDocument.Load(tasksXmlPath);
        var t1Elem = tasksDoc.Root!.Elements("task").First(t => (string?)t.Attribute("id") == taskId1);
        Assert.AreEqual("blocked", (string?)t1Elem.Attribute("status"));

        // 9. Query Blocker Queue (AC06)
        var (blockersExit, blockersOut, blockersErr) = RunCli("task", "blockers", "--iteration", iterId, "--workspace-root", _tempDir, "--format", "xml");
        Assert.AreEqual(0, blockersExit, $"Blockers error: {blockersErr}");
        StringAssert.Contains(blockersOut, taskId1);
        StringAssert.Contains(blockersOut, "kind=\"environment\"");
        StringAssert.Contains(blockersOut, "owner=\"devops\"");

        // 10. Query Bounded Task Context (AC07)
        var (ctxExit, ctxOut, ctxErr) = RunCli("task", "context", "--task", taskId1, "--iteration", iterId, "--workspace-root", _tempDir, "--format", "xml");
        Assert.AreEqual(0, ctxExit, $"Context error: {ctxErr}");
        StringAssert.Contains(ctxOut, "<task-context");
        StringAssert.Contains(ctxOut, "<action-category>wait-external</action-category>");
        StringAssert.Contains(ctxOut, "<reason-code>TASKS_BLOCKED</reason-code>");
        StringAssert.Contains(ctxOut, "<blockers total=\"1\"");

        // 11. Resume Task 1 (resolves blocker findings and transitions to in-progress) (AC05, AC06)
        var (resumeExit, resumeOut, resumeErr) = RunCli("task", "resume", "--task", taskId1, "--iteration", iterId, "--summary", "Database credentials provisioned in vault", "--all", "--workspace-root", _tempDir, "--format", "xml");
        Assert.AreEqual(0, resumeExit, $"Resume error: {resumeErr}");

        tasksDoc = XDocument.Load(tasksXmlPath);
        t1Elem = tasksDoc.Root!.Elements("task").First(t => (string?)t.Attribute("id") == taskId1);
        Assert.AreEqual("in-progress", (string?)t1Elem.Attribute("status"));

        // 12. Verify Task 1 (transitions to verification)
        var (verifyExit, verifyOut, verifyErr) = RunCli("task", "verify", "--task", taskId1, "--iteration", iterId, "--summary", "Task 1 verification complete", "--workspace-root", _tempDir, "--format", "xml");
        Assert.AreEqual(0, verifyExit, $"Verify error: {verifyErr}");

        // 13. Submit Independent Review Approval (AC03, AC09)
        var (reviewExit, reviewOut, reviewErr) = RunCli("task", "review", "approve", "--task", taskId1, "--iteration", iterId, "--actor", "reviewer-bot", "--summary", "Independent review passed", "--workspace-root", _tempDir, "--format", "xml");
        Assert.AreEqual(0, reviewExit, $"Review error: {reviewErr}");

        // 14. Complete Task 1 (transitions to done)
        var (finishExit, finishOut, finishErr) = RunCli("task", "finish", "--task", taskId1, "--iteration", iterId, "--summary", "Task 1 completed", "--workspace-root", _tempDir, "--format", "xml");
        Assert.AreEqual(0, finishExit, $"Finish error: {finishErr}");

        // 15. Task Next now unblocks Task 2 because Task 1 is satisfied (AC01, AC03)
        var (next2Exit, next2Out, next2Err) = RunCli("task", "next", "--iteration", iterId, "--workspace-root", _tempDir, "--format", "xml");
        Assert.AreEqual(0, next2Exit, $"Next 2 error: {next2Err}");
        StringAssert.Contains(next2Out, taskId2);
        StringAssert.Contains(next2Out, "action_category=\"start-work\"");

        // 16. Fast-finish Task 2
        var (finish2Exit, finish2Out, finish2Err) = RunCli("task", "finish", "--task", taskId2, "--iteration", iterId, "--summary", "Task 2 completed", "--workspace-root", _tempDir, "--format", "xml");
        Assert.AreEqual(0, finish2Exit, $"Finish 2 error: {finish2Err}");

        // 17. Iteration Summary reflects complete task breakdown and progression facts (AC01, AC02)
        var (sumExit, sumOut, sumErr) = RunCli("summary", "--iteration", iterId, "--workspace-root", _tempDir, "--format", "xml");
        Assert.AreEqual(0, sumExit, $"Summary error: {sumErr}");
        StringAssert.Contains(sumOut, "total=\"2\"");
        StringAssert.Contains(sumOut, "done=\"2\"");

        // 18. Iteration Readiness evaluation for completion (AC04, AC09)
        var (readyExit, readyOut, readyErr) = RunCli("iteration", "readiness", "--iteration", iterId, "--phase", "completion", "--workspace-root", _tempDir, "--format", "xml");
        Assert.AreEqual(0, readyExit, $"Readiness error: {readyErr}");
        StringAssert.Contains(readyOut, "phase=\"completion\"");
        StringAssert.Contains(readyOut, "technically_ready=\"true\"");

        // 19. Task Next when all tasks are done returns action_category="execution-terminal" (AC01, AC02)
        var (nextAllDoneExit, nextAllDoneOut, _) = RunCli("task", "next", "--iteration", iterId, "--workspace-root", _tempDir, "--format", "xml");
        Assert.AreEqual(0, nextAllDoneExit);
        StringAssert.Contains(nextAllDoneOut, "action_category=\"execution-terminal\"");
        StringAssert.Contains(nextAllDoneOut, "reason_code=\"ALL_TASKS_DONE\"");

        // 20. Product acceptance criteria remain pending in spec.xml (AC09)
        var specDoc = XDocument.Load(Path.Combine(_tempDir, ".dogdouspec", iterId, "spec.xml"));
        var pendingCriteria = specDoc.Descendants("criterion").Where(c => (string?)c.Attribute("decision") == "pending").ToList();
        Assert.IsTrue(pendingCriteria.Count > 0, "Product acceptance criteria must remain pending until human owner confirms.");
    }
}