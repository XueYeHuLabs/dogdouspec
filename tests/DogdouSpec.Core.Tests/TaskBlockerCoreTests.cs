using System.Globalization;
using System.Text;
using System.Xml.Linq;
using DogdouSpec.Core.Changes;
using DogdouSpec.Core.Diagnostics;
using DogdouSpec.Core.Iterations;
using DogdouSpec.Core.Tasks;
using DogdouSpec.Core.Time;

namespace DogdouSpec.Core.Tests;

[TestClass]
public sealed class TaskBlockerCoreTests
{
    private static string RepoRoot = null!;
    private string _tempDir = null!;
    private string _workspace = null!;

    private static readonly string[] DefaultFeatureCriteria = new[] { "Workflow integration verified." };

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
        _tempDir = Path.Combine(Path.GetTempPath(), "DogdouSpec_BlockerTests_" + Guid.NewGuid().ToString("N"));
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
            catch { }
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

    private void InitWorkspaceWithFeatureIteration(string iterId = "20260824-test-feature")
    {
        _workspace = CreateWorkspaceCopy();
        var clock = new TestClock(new DateTime(2026, 8, 24, 9, 0, 0, DateTimeKind.Utc));
        var (iSuccess, _, iDiags) = IterationCreator.Create(_workspace, iterId, "feature", clock, criteria: DefaultFeatureCriteria);
        Assert.IsTrue(iSuccess, $"Iteration create failed: {string.Join(", ", iDiags.Select(d => d.Message))}");
    }

    private string AddTask(string iterId, string taskId, string? dependsOn = null)
    {
        var deps = string.IsNullOrEmpty(dependsOn) ? Array.Empty<string>() : new[] { dependsOn };
        var input = new QuickTaskInput(
            Title: $"Task {taskId}",
            Scopes: new List<string> { "src/**" },
            DoneWhen: "Criterion verified",
            Why: "Task objective",
            Origins: Array.Empty<string>(),
            Dependencies: deps,
            Terms: new List<string> { "component=core" },
            IterationId: iterId,
            ExpectedRevision: null,
            Start: false,
            DryRun: false,
            TaskId: taskId,
            OperationId: $"20260824T091000Z-op-{Guid.NewGuid():N}");

        var (success, _, _, diags) = TaskQuick.Create(_workspace, input);
        Assert.IsTrue(success, string.Join(", ", diags.Select(d => d.Message)));
        return taskId;
    }

    private void StartTask(string iterId, string taskId, TestClock? clock = null)
    {
        clock ??= new TestClock(new DateTime(2026, 8, 24, 9, 30, 0, DateTimeKind.Utc));
        var nowUtc = clock.UtcNow;
        var isoTime = nowUtc.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
        var opId = $"{nowUtc:yyyyMMddTHHmmssZ}-start-{Guid.NewGuid():N}";
        var recId = $"{nowUtc:yyyyMMddTHHmmssZ}-rec-start-{Guid.NewGuid():N}";
        var xml = $"""
<?xml version="1.0" encoding="utf-8"?>
<task-update id="{opId}" transition="start" actor="codex" occurred_at="{isoTime}">
  <records>
    <record id="{recId}" kind="start" status="informational" created_at="{isoTime}" actor="codex" operation_id="{opId}">
      <summary>Start task {taskId}.</summary>
    </record>
  </records>
</task-update>
""";
        var tasksPath = Path.Combine(_workspace, iterId, "tasks.xml");
        var xdoc = XDocument.Load(tasksPath);
        var rev = int.Parse(xdoc.Root!.Attribute("revision")!.Value, CultureInfo.InvariantCulture);
        var (ok, _, diags) = TaskUpdater.Update(_workspace, iterId, taskId, rev, xml, clock: clock);
        Assert.IsTrue(ok, string.Join(", ", diags.Select(d => d.Message)));
    }

    private void VerifyTask(string iterId, string taskId, TestClock? clock = null)
    {
        clock ??= new TestClock(new DateTime(2026, 8, 24, 9, 40, 0, DateTimeKind.Utc));
        var nowUtc = clock.UtcNow;
        var isoTime = nowUtc.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
        var opId = $"{nowUtc:yyyyMMddTHHmmssZ}-verify-{Guid.NewGuid():N}";
        var recId = $"{nowUtc:yyyyMMddTHHmmssZ}-rec-verify-{Guid.NewGuid():N}";
        var tasksPath = Path.Combine(_workspace, iterId, "tasks.xml");
        var xdoc = XDocument.Load(tasksPath);
        var taskElem = xdoc.Descendants("task").First(t => (string?)t.Attribute("id") == taskId);
        var critIds = taskElem.Descendants("criterion").Select(c => (string?)c.Attribute("id")).Where(id => !string.IsNullOrEmpty(id)).ToList();
        var coversXml = new StringBuilder();
        if (critIds.Count > 0)
        {
            coversXml.AppendLine("      <covers>");
            foreach (var cid in critIds)
            {
                coversXml.AppendLine(CultureInfo.InvariantCulture, $"        <ref scope=\"document\" target=\"{cid}\" relation=\"covers\"/>");
            }
            coversXml.AppendLine("      </covers>");
        }

        var xml = $"""
<?xml version="1.0" encoding="utf-8"?>
<task-update id="{opId}" transition="verify" actor="codex" occurred_at="{isoTime}">
  <records>
    <record id="{recId}" kind="verification" status="informational" created_at="{isoTime}" actor="codex" operation_id="{opId}">
      <summary>Verify task {taskId}.</summary>
{coversXml}    </record>
  </records>
</task-update>
""";
        var rev = int.Parse(xdoc.Root!.Attribute("revision")!.Value, CultureInfo.InvariantCulture);
        var (ok, _, diags) = TaskUpdater.Update(_workspace, iterId, taskId, rev, xml, clock: clock);
        Assert.IsTrue(ok, string.Join(", ", diags.Select(d => d.Message)));
    }

    [TestMethod]
    public void TaskBlock_InProgressTask_TransitionsToBlockedAndAppendsFindingRecord()
    {
        var iterId = "20260824-block-01";
        InitWorkspaceWithFeatureIteration(iterId);
        var taskId = AddTask(iterId, "20260824-task-b01");

        // Start task to in-progress
        StartTask(iterId, taskId);

        var clock = new TestClock(new DateTime(2026, 8, 24, 10, 0, 0, DateTimeKind.Utc));

        // Block the task
        var (blockOk, envelope, blockDiags) = TaskBlock.Block(
            _workspace,
            taskId,
            iterationId: iterId,
            actor: "agent-1",
            summary: "Waiting for external credential",
            blockerKind: "external",
            blockerOwner: "owner",
            blockerReviewAt: "20260824T120000Z",
            condition: "Owner provisions API token",
            nextAction: "Resume task and execute tests",
            clock: clock);

        Assert.IsTrue(blockOk, string.Join(", ", blockDiags.Select(d => d.Message)));
        Assert.IsNotNull(envelope);

        var tasksDoc = XDocument.Load(Path.Combine(_workspace, iterId, "tasks.xml"));
        var taskElem = tasksDoc.Root!.Elements("task").First(t => (string?)t.Attribute("id") == taskId);
        Assert.AreEqual("blocked", (string?)taskElem.Attribute("status"));

        var finding = taskElem.Element("records")!.Elements("record").FirstOrDefault(r => (string?)r.Attribute("kind") == "finding");
        Assert.IsNotNull(finding);
        Assert.AreEqual("active", (string?)finding.Attribute("status"));
        Assert.AreEqual("agent-1", (string?)finding.Attribute("actor"));
        Assert.AreEqual("Waiting for external credential", finding.Element("summary")?.Value);
        Assert.AreEqual("Owner provisions API token", finding.Element("context")?.Value);
        Assert.AreEqual("Resume task and execute tests", finding.Element("outcome")?.Value);

        var terms = finding.Element("index")!.Elements("term").ToDictionary(t => (string)t.Attribute("key")!, t => (string)t.Attribute("value")!);
        Assert.AreEqual("external", terms["blocker-kind"]);
        Assert.AreEqual("owner", terms["blocker-owner"]);
        Assert.AreEqual("20260824T120000Z", terms["blocker-review-at"]);
    }

    [TestMethod]
    public void TaskBlock_AlreadyBlockedTask_AppendsAdditionalFindingWithoutModifyingStatus()
    {
        var iterId = "20260824-block-02";
        InitWorkspaceWithFeatureIteration(iterId);
        var taskId = AddTask(iterId, "20260824-task-b02");
        StartTask(iterId, taskId);

        var clock = new TestClock(new DateTime(2026, 8, 24, 10, 0, 0, DateTimeKind.Utc));

        // First blocker
        var (ok1, _, _) = TaskBlock.Block(_workspace, taskId, iterId, summary: "Blocker 1", clock: clock);
        Assert.IsTrue(ok1);

        // Second blocker on already-blocked task
        var (ok2, _, diags2) = TaskBlock.Block(_workspace, taskId, iterId, summary: "Blocker 2", blockerKind: "environment", clock: clock);
        Assert.IsTrue(ok2, string.Join(", ", diags2.Select(d => d.Message)));

        var tasksDoc = XDocument.Load(Path.Combine(_workspace, iterId, "tasks.xml"));
        var taskElem = tasksDoc.Root!.Elements("task").First(t => (string?)t.Attribute("id") == taskId);
        Assert.AreEqual("blocked", (string?)taskElem.Attribute("status"));

        var activeFindings = taskElem.Element("records")!.Elements("record")
            .Where(r => (string?)r.Attribute("kind") == "finding" && (string?)r.Attribute("status") == "active")
            .ToList();
        Assert.AreEqual(2, activeFindings.Count);
    }

    [TestMethod]
    public void TaskBlock_VerificationTask_TransitionsToBlocked()
    {
        var iterId = "20260824-block-03";
        InitWorkspaceWithFeatureIteration(iterId);
        var taskId = AddTask(iterId, "20260824-task-b03");
        StartTask(iterId, taskId);
        VerifyTask(iterId, taskId);

        // Block from verification
        var (blockOk, _, blockDiags) = TaskBlock.Block(_workspace, taskId, iterId, summary: "Blocker during verification");
        Assert.IsTrue(blockOk, string.Join(", ", blockDiags.Select(d => d.Message)));

        var tasksDoc = XDocument.Load(Path.Combine(_workspace, iterId, "tasks.xml"));
        var taskElem = tasksDoc.Root!.Elements("task").First(t => (string?)t.Attribute("id") == taskId);
        Assert.AreEqual("blocked", (string?)taskElem.Attribute("status"));
    }

    [TestMethod]
    public void TaskBlock_PendingTask_FailsWithTaskTransitionConflict()
    {
        var iterId = "20260824-block-04";
        InitWorkspaceWithFeatureIteration(iterId);
        var taskId = AddTask(iterId, "20260824-task-b04"); // status: pending

        var (blockOk, _, blockDiags) = TaskBlock.Block(_workspace, taskId, iterId, summary: "Cannot block pending");
        Assert.IsFalse(blockOk);
        Assert.IsTrue(blockDiags.Any(d => d.Code == DiagnosticCodes.TaskTransitionConflict));
    }

    [TestMethod]
    public void TaskBlock_InvalidReviewAtOrTokens_FailsWithInvalidArgument()
    {
        var iterId = "20260824-block-05";
        InitWorkspaceWithFeatureIteration(iterId);
        var taskId = AddTask(iterId, "20260824-task-b05");
        StartTask(iterId, taskId);

        // Invalid review-at format (contains colons or dashes)
        var (b1, _, d1) = TaskBlock.Block(_workspace, taskId, iterId, summary: "Bad review at", blockerReviewAt: "2026-08-24T12:00:00Z");
        Assert.IsFalse(b1);
        Assert.IsTrue(d1.Any(d => d.Code == DiagnosticCodes.InvalidArgument));

        // Invalid blocker-kind token (spaces or symbols)
        var (b2, _, d2) = TaskBlock.Block(_workspace, taskId, iterId, summary: "Bad kind", blockerKind: "invalid kind!");
        Assert.IsFalse(b2);
        Assert.IsTrue(d2.Any(d => d.Code == DiagnosticCodes.InvalidArgument));
    }

    [TestMethod]
    public void TaskResume_MultipleBlockers_ResolvingOneLeavesTaskBlocked_ResolvingLastUnblocks()
    {
        var iterId = "20260824-resume-01";
        InitWorkspaceWithFeatureIteration(iterId);
        var taskId = AddTask(iterId, "20260824-task-r01");
        StartTask(iterId, taskId);

        var clock = new TestClock(new DateTime(2026, 8, 24, 10, 0, 0, DateTimeKind.Utc));

        TaskBlock.Block(_workspace, taskId, iterId, summary: "Blocker 1", clock: clock);
        TaskBlock.Block(_workspace, taskId, iterId, summary: "Blocker 2", clock: clock);

        var tasksDoc = XDocument.Load(Path.Combine(_workspace, iterId, "tasks.xml"));
        var taskElem = tasksDoc.Root!.Elements("task").First(t => (string?)t.Attribute("id") == taskId);
        var findings = taskElem.Element("records")!.Elements("record")
            .Where(r => (string?)r.Attribute("kind") == "finding" && (string?)r.Attribute("status") == "active")
            .ToList();
        Assert.AreEqual(2, findings.Count);
        var f1Id = (string)findings[0].Attribute("id")!;
        var f2Id = (string)findings[1].Attribute("id")!;

        // Resolve finding 1
        var (res1Ok, _, res1Diags) = TaskResume.Resume(_workspace, taskId, iterId, findingId: f1Id, summary: "Resolved finding 1", clock: clock);
        Assert.IsTrue(res1Ok, string.Join(", ", res1Diags.Select(d => d.Message)));

        tasksDoc = XDocument.Load(Path.Combine(_workspace, iterId, "tasks.xml"));
        taskElem = tasksDoc.Root!.Elements("task").First(t => (string?)t.Attribute("id") == taskId);
        // Task remains blocked because f2 is still active!
        Assert.AreEqual("blocked", (string?)taskElem.Attribute("status"));

        var f1 = taskElem.Element("records")!.Elements("record").First(r => (string?)r.Attribute("id") == f1Id);
        Assert.AreEqual("resolved", (string?)f1.Attribute("status"));

        // Resolve finding 2 (final blocker)
        var (res2Ok, _, res2Diags) = TaskResume.Resume(_workspace, taskId, iterId, findingId: f2Id, summary: "Resolved finding 2", clock: clock);
        Assert.IsTrue(res2Ok, string.Join(", ", res2Diags.Select(d => d.Message)));

        tasksDoc = XDocument.Load(Path.Combine(_workspace, iterId, "tasks.xml"));
        taskElem = tasksDoc.Root!.Elements("task").First(t => (string?)t.Attribute("id") == taskId);
        // Task is now in-progress!
        Assert.AreEqual("in-progress", (string?)taskElem.Attribute("status"));
    }

    [TestMethod]
    public void TaskResume_BlockedVerificationTask_ResumesToInProgressRequiringVerificationAgain()
    {
        var iterId = "20260824-resume-02";
        InitWorkspaceWithFeatureIteration(iterId);
        var taskId = AddTask(iterId, "20260824-task-r02");
        var clock = new TestClock(new DateTime(2026, 8, 24, 10, 0, 0, DateTimeKind.Utc));

        StartTask(iterId, taskId, clock);
        clock.Advance(TimeSpan.FromMinutes(1));
        VerifyTask(iterId, taskId, clock);

        // Block from verification
        clock.Advance(TimeSpan.FromMinutes(1));
        var (blockOk, _, blockDiags) = TaskBlock.Block(_workspace, taskId, iterId, summary: "Flaky test in verification", clock: clock);
        Assert.IsTrue(blockOk, string.Join(", ", blockDiags.Select(d => d.Message)));

        // Resume task (Criterion 05-4)
        clock.Advance(TimeSpan.FromMinutes(1));
        var (resOk, _, resDiags) = TaskResume.Resume(_workspace, taskId, iterId, summary: "Flaky test fixed", all: true, clock: clock);
        Assert.IsTrue(resOk, string.Join(", ", resDiags.Select(d => d.Message)));

        var tasksDoc = XDocument.Load(Path.Combine(_workspace, iterId, "tasks.xml"));
        var taskElem = tasksDoc.Root!.Elements("task").First(t => (string?)t.Attribute("id") == taskId);
        // Resumes to in-progress under existing semantics
        Assert.AreEqual("in-progress", (string?)taskElem.Attribute("status"));

        // Verify again before completion
        clock.Advance(TimeSpan.FromMinutes(1));
        VerifyTask(iterId, taskId, clock);

        tasksDoc = XDocument.Load(Path.Combine(_workspace, iterId, "tasks.xml"));
        taskElem = tasksDoc.Root!.Elements("task").First(t => (string?)t.Attribute("id") == taskId);
        Assert.AreEqual("verification", (string?)taskElem.Attribute("status"));
    }

    [TestMethod]
    public void TaskBlockers_QueueSortingAndFiltering_CoversDueUpcomingUndatedAndDerived()
    {
        var iterId = "20260824-queue-01";
        InitWorkspaceWithFeatureIteration(iterId);

        var taskA = AddTask(iterId, "20260824-task-qa");
        var taskB = AddTask(iterId, "20260824-task-qb");
        var taskC = AddTask(iterId, "20260824-task-qc");
        var taskD = AddTask(iterId, "20260824-task-qd", dependsOn: taskA);

        StartTask(iterId, taskA);
        StartTask(iterId, taskB);
        StartTask(iterId, taskC);

        var nowTime = new DateTime(2026, 8, 24, 10, 0, 0, DateTimeKind.Utc);
        var clock = new TestClock(nowTime);

        // Task A: Overdue blocker (review_at = 09:00:00Z <= now 10:00:00Z)
        TaskBlock.Block(_workspace, taskA, iterId, summary: "Overdue blocker", blockerKind: "external", blockerOwner: "owner", blockerReviewAt: "20260824T090000Z", clock: clock);

        // Task B: Upcoming blocker (review_at = 12:00:00Z > now 10:00:00Z)
        TaskBlock.Block(_workspace, taskB, iterId, summary: "Upcoming blocker", blockerKind: "environment", blockerOwner: "devops", blockerReviewAt: "20260824T120000Z", clock: clock);

        // Task C: Undated blocker (no review_at)
        TaskBlock.Block(_workspace, taskC, iterId, summary: "Undated blocker", blockerKind: "internal", blockerOwner: "agent", blockerReviewAt: null, clock: clock);

        // Task D is pending, waiting on Task A (unmet upstream dependency).

        // Query all blockers
        var (qOk, qResult, qDiags) = TaskBlockers.Query(_workspace, iterId, clock: clock);
        Assert.IsTrue(qOk, string.Join(", ", qDiags.Select(d => d.Message)));
        Assert.IsNotNull(qResult);

        // Total blockers: 4 (A: overdue, B: upcoming, C: undated, D: derived dependency)
        Assert.AreEqual(4, qResult.TotalCount);
        Assert.AreEqual(1, qResult.DueCount);

        // Deterministic sort order: Overdue first (A), Upcoming second (B), Undated third (C, D in document order)
        Assert.AreEqual(taskA, qResult.Blockers[0].TaskId);
        Assert.AreEqual("overdue", qResult.Blockers[0].DueStatus);
        Assert.IsTrue(qResult.Blockers[0].IsDue);

        Assert.AreEqual(taskB, qResult.Blockers[1].TaskId);
        Assert.AreEqual("upcoming", qResult.Blockers[1].DueStatus);
        Assert.IsFalse(qResult.Blockers[1].IsDue);

        Assert.AreEqual(taskC, qResult.Blockers[2].TaskId);
        Assert.AreEqual("undated", qResult.Blockers[2].DueStatus);
        Assert.IsFalse(qResult.Blockers[2].IsDerived);

        Assert.AreEqual(taskD, qResult.Blockers[3].TaskId);
        Assert.AreEqual("undated", qResult.Blockers[3].DueStatus);
        Assert.IsTrue(qResult.Blockers[3].IsDerived);

        // Filter: --due-only
        var (dueOk, dueResult, _) = TaskBlockers.Query(_workspace, iterId, dueOnly: true, clock: clock);
        Assert.IsTrue(dueOk);
        Assert.AreEqual(1, dueResult!.TotalCount);
        Assert.AreEqual(taskA, dueResult.Blockers[0].TaskId);

        // Filter: --owner devops
        var (ownerOk, ownerResult, _) = TaskBlockers.Query(_workspace, iterId, owner: "devops", clock: clock);
        Assert.IsTrue(ownerOk);
        Assert.AreEqual(1, ownerResult!.TotalCount);
        Assert.AreEqual(taskB, ownerResult.Blockers[0].TaskId);

        // Filter: --kind external
        var (kindOk, kindResult, _) = TaskBlockers.Query(_workspace, iterId, kind: "external", clock: clock);
        Assert.IsTrue(kindOk);
        Assert.AreEqual(1, kindResult!.TotalCount);
        Assert.AreEqual(taskA, kindResult.Blockers[0].TaskId);

        // Filter: --task
        var (taskOk, taskResult, _) = TaskBlockers.Query(_workspace, iterId, taskId: taskD, clock: clock);
        Assert.IsTrue(taskOk);
        Assert.AreEqual(1, taskResult!.TotalCount);
        Assert.AreEqual(taskD, taskResult.Blockers[0].TaskId);

        // XML and Human output check
        var xmlOutput = qResult.ToXmlString();
        StringAssert.Contains(xmlOutput, "total=\"4\"");
        StringAssert.Contains(xmlOutput, "due_count=\"1\"");

        var humanOutput = qResult.ToHumanString();
        StringAssert.Contains(humanOutput, "[OVERDUE]");
        StringAssert.Contains(humanOutput, "[UPCOMING]");
        StringAssert.Contains(humanOutput, "[UNDATED]");
    }

    [TestMethod]
    public void TaskResume_RequiresExplicitResolutionIntentAndSummary_WhenActiveFindingsExist()
    {
        var iterId = "20260824-resume-intent";
        InitWorkspaceWithFeatureIteration(iterId);
        var taskId = AddTask(iterId, "20260824-task-res-intent");
        var clock = new TestClock(new DateTime(2026, 8, 24, 10, 0, 0, DateTimeKind.Utc));

        StartTask(iterId, taskId, clock);
        clock.Advance(TimeSpan.FromMinutes(1));
        var (blockOk, _, _) = TaskBlock.Block(_workspace, taskId, iterId, summary: "Blocked on external access", clock: clock);
        Assert.IsTrue(blockOk);

        // 1. Omitted intent (no --all and no --finding) -> fails with InvalidArgument
        clock.Advance(TimeSpan.FromMinutes(1));
        var (noIntentOk, _, noIntentDiags) = TaskResume.Resume(_workspace, taskId, iterId, summary: "Fixed it", clock: clock);
        Assert.IsFalse(noIntentOk);
        Assert.IsTrue(noIntentDiags.Any(d => d.Code == DiagnosticCodes.InvalidArgument && d.Message.Contains("--finding") && d.Message.Contains("--all")));

        // 2. Both --all and --finding provided -> fails with InvalidArgument
        var tasksDoc = XDocument.Load(Path.Combine(_workspace, iterId, "tasks.xml"));
        var taskElem = tasksDoc.Root!.Elements("task").First(t => (string?)t.Attribute("id") == taskId);
        var findingId = (string)taskElem.Element("records")!.Elements("record").First(r => (string?)r.Attribute("kind") == "finding").Attribute("id")!;

        var (bothOk, _, bothDiags) = TaskResume.Resume(_workspace, taskId, iterId, summary: "Fixed it", findingId: findingId, all: true, clock: clock);
        Assert.IsFalse(bothOk);
        Assert.IsTrue(bothDiags.Any(d => d.Code == DiagnosticCodes.InvalidArgument && d.Message.Contains("Cannot specify both")));

        // 3. Explicit intent (--all), but missing or whitespace summary -> fails with InvalidArgument
        var (noSumOk, _, noSumDiags) = TaskResume.Resume(_workspace, taskId, iterId, summary: "   ", all: true, clock: clock);
        Assert.IsFalse(noSumOk);
        Assert.IsTrue(noSumDiags.Any(d => d.Code == DiagnosticCodes.InvalidArgument && d.Message.Contains("summary")));

        // 4. Valid explicit intent (--all) and non-empty summary -> succeeds and unblocks
        var (resOk, _, resDiags) = TaskResume.Resume(_workspace, taskId, iterId, summary: "Access restored by ops", all: true, clock: clock);
        Assert.IsTrue(resOk, string.Join(", ", resDiags.Select(d => d.Message)));

        tasksDoc = XDocument.Load(Path.Combine(_workspace, iterId, "tasks.xml"));
        taskElem = tasksDoc.Root!.Elements("task").First(t => (string?)t.Attribute("id") == taskId);
        Assert.AreEqual("in-progress", (string?)taskElem.Attribute("status"));
    }

    [TestMethod]
    public void TaskBlockers_CrossIterationAndTerminalStatuses_DependenciesEvaluatedAccurately()
    {
        var clock = new TestClock(new DateTime(2026, 8, 24, 9, 0, 0, DateTimeKind.Utc));
        _workspace = CreateWorkspaceCopy();

        var upstreamIter = "20260824-up-iter";
        var downstreamIter = "20260825-down-iter";

        var (uOk, _, _) = IterationCreator.Create(_workspace, upstreamIter, "feature", clock, activate: true, criteria: DefaultFeatureCriteria);
        Assert.IsTrue(uOk);
        var (dOk, _, _) = IterationCreator.Create(_workspace, downstreamIter, "feature", clock, activate: true, criteria: DefaultFeatureCriteria);
        Assert.IsTrue(dOk);

        var taskUpCancelled = AddTask(upstreamIter, "20260824-task-up-canc");
        var taskUpActive = AddTask(upstreamIter, "20260824-task-up-act");

        // Transition taskUpCancelled to cancelled
        var xmlCancel = $"""
<?xml version="1.0" encoding="utf-8"?>
<task-update id="20260824T092000Z-canc" transition="cancel" actor="codex" occurred_at="2026-08-24T09:20:00Z">
  <records>
    <record id="20260824T092000Z-rec-canc" kind="decision" status="resolved" created_at="2026-08-24T09:20:00Z" actor="codex" operation_id="20260824T092000Z-canc">
      <summary>Cancelled due to requirement scope change.</summary>
    </record>
  </records>
</task-update>
""";
        var (cancOk, _, cancDiags) = TaskUpdater.Update(_workspace, upstreamIter, taskUpCancelled, 3, xmlCancel, clock: clock);
        Assert.IsTrue(cancOk, string.Join(", ", cancDiags.Select(d => d.Message)));

        // Add taskDownSatisfied in downstreamIter with scope="project" depending on cancelled upstream task
        var xmlAddSatisfied = $"""
<?xml version="1.0" encoding="utf-8"?>
<task-add id="20260825T090000Z-op-add-sat" actor="codex" occurred_at="2026-08-25T09:00:00Z">
  <task id="20260825-task-down-sat" status="pending" created_at="2026-08-25T09:00:00Z" updated_at="2026-08-25T09:00:00Z">
    <index>
      <summary>Downstream Satisfied</summary>
    </index>
    <title>Downstream Satisfied</title>
    <objective>Depends on cancelled task</objective>
    <rationale>Testing cross iteration terminal</rationale>
    <scope><repository path="."/></scope>
    <origin><ref scope="iteration" target="20260825-req-down-iter" relation="implements"/></origin>
    <dependencies>
      <ref scope="project" target="{taskUpCancelled}" relation="depends-on"/>
    </dependencies>
    <constraints/>
    <acceptance><criterion id="20260825-crit-task-down-sat" status="pending">Crit</criterion></acceptance>
    <context><summary>Context</summary></context>
    <records>
      <record id="20260825T090000Z-rec-sat" kind="discussion" status="informational" created_at="2026-08-25T09:00:00Z" actor="codex">
        <summary>Created.</summary>
      </record>
    </records>
  </task>
</task-add>
""";
        var (addSatOk, _, addSatDiags) = TaskAdder.Add(_workspace, downstreamIter, 1, xmlAddSatisfied);
        Assert.IsTrue(addSatOk, string.Join(", ", addSatDiags.Select(d => d.Message)));

        // Add taskDownBlocked in downstreamIter with scope="project" depending on active upstream task
        var xmlAddBlocked = $"""
<?xml version="1.0" encoding="utf-8"?>
<task-add id="20260825T090100Z-op-add-blk" actor="codex" occurred_at="2026-08-25T09:01:00Z">
  <task id="20260825-task-down-blk" status="pending" created_at="2026-08-25T09:01:00Z" updated_at="2026-08-25T09:01:00Z">
    <index>
      <summary>Downstream Blocked</summary>
    </index>
    <title>Downstream Blocked</title>
    <objective>Depends on pending task</objective>
    <rationale>Testing cross iteration blocked</rationale>
    <scope><repository path="."/></scope>
    <origin><ref scope="iteration" target="20260825-req-down-iter" relation="implements"/></origin>
    <dependencies>
      <ref scope="project" target="{taskUpActive}" relation="depends-on"/>
    </dependencies>
    <constraints/>
    <acceptance><criterion id="20260825-crit-task-down-blk" status="pending">Crit</criterion></acceptance>
    <context><summary>Context</summary></context>
    <records>
      <record id="20260825T090100Z-rec-blk" kind="discussion" status="informational" created_at="2026-08-25T09:01:00Z" actor="codex">
        <summary>Created.</summary>
      </record>
    </records>
  </task>
</task-add>
""";
        var (addBlkOk, _, addBlkDiags) = TaskAdder.Add(_workspace, downstreamIter, 2, xmlAddBlocked);
        Assert.IsTrue(addBlkOk, string.Join(", ", addBlkDiags.Select(d => d.Message)));

        // Query blockers for downstream iteration
        var (qOk, qResult, qDiags) = TaskBlockers.Query(_workspace, downstreamIter, clock: clock);
        Assert.IsTrue(qOk, string.Join(", ", qDiags.Select(d => d.Message)));
        Assert.IsNotNull(qResult);

        // taskDownSatisfied must NOT appear as blocked (cancelled upstream satisfies dependency)
        Assert.IsFalse(qResult.Blockers.Any(b => b.TaskId == "20260825-task-down-sat"));

        // taskDownBlocked MUST appear as blocked by taskUpActive
        var blk = qResult.Blockers.FirstOrDefault(b => b.TaskId == "20260825-task-down-blk");
        Assert.IsNotNull(blk);
        Assert.IsTrue(blk.IsDerived);
        Assert.AreEqual("dependency", blk.Kind);
        StringAssert.Contains(blk.Summary, taskUpActive);
    }

    [TestMethod]
    public void Block_RecordOnly_KeepsStatusInProgressAndSurfacesInBlockers()
    {
        _workspace = CreateWorkspaceCopy();
        var iterId = "20260823-xpath-core";
        var taskId = "20260823-task-xpath-projection";

        // Task in demo is in-progress
        var (ok, envelope, diags) = TaskBlock.Block(
            _workspace,
            taskId,
            iterationId: iterId,
            summary: "Environment outage while continuing execution",
            blockerKind: "environment",
            blockerOwner: "infra",
            recordOnly: true);

        Assert.IsTrue(ok, string.Join(", ", diags.Select(d => d.Message)));
        Assert.IsNotNull(envelope);

        // Verify task status remains in-progress
        var tasksDoc = XDocument.Load(Path.Combine(_workspace, iterId, "tasks.xml"));
        var taskElem = tasksDoc.Root!.Elements("task").First(t => (string?)t.Attribute("id") == taskId);
        Assert.AreEqual("in-progress", (string?)taskElem.Attribute("status"));

        // Query blockers
        var (qOk, qRes, qDiags) = TaskBlockers.Query(_workspace, iterId);
        Assert.IsTrue(qOk, string.Join(", ", qDiags.Select(d => d.Message)));
        Assert.IsNotNull(qRes);

        var blocker = qRes.Blockers.FirstOrDefault(b => b.TaskId == taskId && b.Kind == "environment");
        Assert.IsNotNull(blocker);
        Assert.AreEqual("in-progress", blocker.TaskStatus);
        Assert.IsTrue(blocker.IsRecordOnly);

        // Query with mode: blocking -> should NOT include this blocker
        var (blkOk, blkRes, _) = TaskBlockers.Query(_workspace, iterId, mode: "blocking");
        Assert.IsTrue(blkOk);
        Assert.IsFalse(blkRes!.Blockers.Any(b => b.TaskId == taskId && b.Kind == "environment"));

        // Query with mode: record-only -> MUST include this blocker
        var (recOk, recRes, _) = TaskBlockers.Query(_workspace, iterId, mode: "record-only");
        Assert.IsTrue(recOk);
        Assert.IsTrue(recRes!.Blockers.Any(b => b.TaskId == taskId && b.Kind == "environment"));
    }
}
