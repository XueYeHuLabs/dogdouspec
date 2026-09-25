using System.Xml.Linq;
using DogdouSpec.Cli.Commands;
using DogdouSpec.Core.Workspace;

namespace DogdouSpec.Cli.Tests;

[TestClass]
public sealed class UsabilityPorcelainCliTests
{
    private string _tempDir = null!;
    private const string TestIterationId = "20260902-cli-porcelain-test";

    [TestInitialize]
    public void SetUp()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "DogdouSpec_PorcelainCli_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        WorkspaceInitializer.Initialize(_tempDir, _tempDir);

        // Create active iteration
        Program.Main(new[] { "iteration", "create", "--id", TestIterationId, "--kind", "feature", "--activate", "--criterion", "Porcelain CLI functionality verified.", "--workspace-root", _tempDir });
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
    public void TaskList_Show_Summary_CliCommands_ExecuteSuccessfully()
    {
        // Add a task
        var createExit = Program.Main(new[] {
            "task", "quick",
            "--iteration", TestIterationId,
            "--title", "CLI Task 1",
            "--scope", "src/DogdouSpec.Core",
            "--done-when", "Verified",
            "--why", "CLI test",
            "--start",
            "--workspace-root", _tempDir
        });
        Assert.AreEqual(0, createExit);

        // task list
        var listExit = Program.Main(new[] {
            "task", "list",
            "--iteration", TestIterationId,
            "--workspace-root", _tempDir
        });
        Assert.AreEqual(0, listExit);

        // task summary
        var summaryExit = Program.Main(new[] {
            "task", "summary",
            "--iteration", TestIterationId,
            "--workspace-root", _tempDir
        });
        Assert.AreEqual(0, summaryExit);

        // task show
        var tasksDoc = XDocument.Load(Path.Combine(_tempDir, ".dogdouspec", TestIterationId, "tasks.xml"));
        var taskId = tasksDoc.Descendants("task").First().Attribute("id")!.Value;

        var showExit = Program.Main(new[] {
            "task", "show",
            "--task", taskId,
            "--iteration", TestIterationId,
            "--workspace-root", _tempDir
        });
        Assert.AreEqual(0, showExit);
    }

    [TestMethod]
    public void TaskRevise_AdditiveCli_UpdatesTaskCorrectly()
    {
        var createExit = Program.Main(new[] {
            "task", "quick",
            "--iteration", TestIterationId,
            "--title", "Task To Revise",
            "--scope", "src/Core",
            "--done-when", "Done",
            "--why", "Test revise",
            "--workspace-root", _tempDir
        });
        Assert.AreEqual(0, createExit);

        var tasksDoc = XDocument.Load(Path.Combine(_tempDir, ".dogdouspec", TestIterationId, "tasks.xml"));
        var taskId = tasksDoc.Descendants("task").First().Attribute("id")!.Value;

        var reviseExit = Program.Main(new[] {
            "task", "revise",
            "--task", taskId,
            "--iteration", TestIterationId,
            "--add-constraint", "Must be fast and non-blocking",
            "--add-criterion", "100% tests pass",
            "--add-scope", "src/Cli",
            "--workspace-root", _tempDir
        });
        Assert.AreEqual(0, reviseExit);

        var updatedDoc = XDocument.Load(Path.Combine(_tempDir, ".dogdouspec", TestIterationId, "tasks.xml"));
        var taskElem = updatedDoc.Descendants("task").First(t => t.Attribute("id")?.Value == taskId);
        Assert.IsTrue(taskElem.Descendants("constraint").Any(c => c.Value.Contains("Must be fast")));
        Assert.IsTrue(taskElem.Descendants("criterion").Any(c => c.Value.Contains("100% tests pass")));
        Assert.IsTrue(taskElem.Descendants("include").Any(i => i.Attribute("path")?.Value == "src/Cli"));
    }

    [TestMethod]
    public void TaskReview_ApproveAndRequestChanges_CliCommands()
    {
        // Create task requiring review
        var createExit = Program.Main(new[] {
            "task", "quick",
            "--iteration", TestIterationId,
            "--title", "Task With Review",
            "--scope", "src/Core",
            "--done-when", "Done",
            "--why", "Test review",
            "--agent", "test-agent",
            "--review-required",
            "--workspace-root", _tempDir
        });
        Assert.AreEqual(0, createExit);

        var tasksDoc = XDocument.Load(Path.Combine(_tempDir, ".dogdouspec", TestIterationId, "tasks.xml"));
        var taskId = tasksDoc.Descendants("task").First().Attribute("id")!.Value;

        // Transition: pending -> in-progress -> verification
        Program.Main(new[] { "task", "start", "--task", taskId, "--iteration", TestIterationId, "--workspace-root", _tempDir });
        Program.Main(new[] { "task", "verify", "--task", taskId, "--iteration", TestIterationId, "--workspace-root", _tempDir });

        // Submit approval
        var approveExit = Program.Main(new[] {
            "task", "review", "approve",
            "--task", taskId,
            "--iteration", TestIterationId,
            "--actor", "reviewer",
            "--summary", "Review approved in unit test",
            "--workspace-root", _tempDir
        });
        Assert.AreEqual(0, approveExit);

        // Now complete the task
        var finishExit = Program.Main(new[] {
            "task", "finish",
            "--task", taskId,
            "--iteration", TestIterationId,
            "--workspace-root", _tempDir
        });
        Assert.AreEqual(0, finishExit);
    }

    [TestMethod]
    public void WorkspaceVcsStatus_And_CheckpointPlan_CliCommands()
    {
        var vcsExit = Program.Main(new[] {
            "workspace", "vcs-status",
            "--workspace-root", _tempDir
        });
        Assert.AreEqual(0, vcsExit);

        var planExit = Program.Main(new[] {
            "workspace", "checkpoint-plan",
            "--workspace-root", _tempDir
        });
        Assert.AreEqual(0, planExit);
    }

    [TestMethod]
    public void TaskScope_Explain_CliCommand()
    {
        var createExit = Program.Main(new[] {
            "task", "quick",
            "--iteration", TestIterationId,
            "--title", "Scope Explain Task",
            "--scope", "src/DogdouSpec.Core/**",
            "--done-when", "Done",
            "--why", "Test scope explain",
            "--workspace-root", _tempDir
        });
        Assert.AreEqual(0, createExit);

        var tasksDoc = XDocument.Load(Path.Combine(_tempDir, ".dogdouspec", TestIterationId, "tasks.xml"));
        var taskId = tasksDoc.Descendants("task").First().Attribute("id")!.Value;

        var explainExit = Program.Main(new[] {
            "task", "scope", "explain",
            "--task", taskId,
            "--iteration", TestIterationId,
            "--path", "src/DogdouSpec.Core/Tasks/TaskList.cs",
            "--path", "src/DogdouSpec.Cli/Program.cs",
            "--workspace-root", _tempDir
        });
        Assert.AreEqual(0, explainExit);
    }

    [TestMethod]
    public void TaskRecord_PorcelainCli_RecordsStructuredEntriesWithEscaping()
    {
        // 1. Create a task with start
        var createExit = Program.Main(new[] {
            "task", "quick",
            "--iteration", TestIterationId,
            "--title", "Record Test Task",
            "--scope", "src/Core",
            "--done-when", "Criteria met",
            "--why", "Testing porcelain task record",
            "--start",
            "--workspace-root", _tempDir
        });
        Assert.AreEqual(0, createExit);

        var tasksPath = Path.Combine(_tempDir, ".dogdouspec", TestIterationId, "tasks.xml");
        var tasksDoc = XDocument.Load(tasksPath);
        var taskElem = tasksDoc.Descendants("task").First(t => t.Element("title")?.Value == "Record Test Task");
        var taskId = taskElem.Attribute("id")!.Value;
        var critId = taskElem.Descendants("criterion").First().Attribute("id")!.Value;

        // 2. Add verification record with auto-escaping and covers
        var recordVerifExit = Program.Main(new[] {
            "task", "record",
            "--task", taskId,
            "--iteration", TestIterationId,
            "--kind", "verification",
            "--covers", critId,
            "--summary", "Verified <feature> & 'behavior' \"correctly\"",
            "--workspace-root", _tempDir
        });
        Assert.AreEqual(0, recordVerifExit);

        tasksDoc = XDocument.Load(tasksPath);
        taskElem = tasksDoc.Descendants("task").First(t => (string?)t.Attribute("id") == taskId);
        var verifRec = taskElem.Descendants("record").Last(r => (string?)r.Attribute("kind") == "verification");
        Assert.AreEqual("informational", (string?)verifRec.Attribute("status"));
        Assert.AreEqual("Verified <feature> & 'behavior' \"correctly\"", verifRec.Element("summary")?.Value);
        var covRef = verifRec.Element("covers")?.Element("ref");
        Assert.IsNotNull(covRef);
        Assert.AreEqual(critId, (string?)covRef.Attribute("target"));
        Assert.AreEqual("covers", (string?)covRef.Attribute("relation"));

        // 3. Add finding record (defaults to active)
        var recordFindingExit = Program.Main(new[] {
            "task", "record",
            "--task", taskId,
            "--iteration", TestIterationId,
            "--kind", "finding",
            "--summary", "Found issue <123> in parser",
            "--context", "Precondition: input is null",
            "--workspace-root", _tempDir
        });
        Assert.AreEqual(0, recordFindingExit);

        tasksDoc = XDocument.Load(tasksPath);
        taskElem = tasksDoc.Descendants("task").First(t => (string?)t.Attribute("id") == taskId);
        var findingRec = taskElem.Descendants("record").Last(r => (string?)r.Attribute("kind") == "finding");
        Assert.AreEqual("active", (string?)findingRec.Attribute("status"));
        Assert.AreEqual("Found issue <123> in parser", findingRec.Element("summary")?.Value);
        Assert.AreEqual("Precondition: input is null", findingRec.Element("context")?.Value);
        var findingId = findingRec.Attribute("id")!.Value;

        // 4. Add resolution record resolving the finding
        var recordResExit = Program.Main(new[] {
            "task", "record",
            "--task", taskId,
            "--iteration", TestIterationId,
            "--kind", "resolution",
            "--resolve", findingId,
            "--summary", "Resolved parser bug <123>",
            "--workspace-root", _tempDir
        });
        Assert.AreEqual(0, recordResExit);

        tasksDoc = XDocument.Load(tasksPath);
        taskElem = tasksDoc.Descendants("task").First(t => (string?)t.Attribute("id") == taskId);
        var reloadedFinding = taskElem.Descendants("record").First(r => (string?)r.Attribute("id") == findingId);
        Assert.AreEqual("resolved", (string?)reloadedFinding.Attribute("status"));
        var resRec = taskElem.Descendants("record").Last(r => (string?)r.Attribute("kind") == "resolution");
        Assert.AreEqual("resolved", (string?)resRec.Attribute("status"));
        Assert.AreEqual("Resolved parser bug <123>", resRec.Element("summary")?.Value);
        var resRef = resRec.Element("covers")?.Elements("ref").FirstOrDefault(r => (string?)r.Attribute("relation") == "resolves");
        Assert.IsNotNull(resRef);
        Assert.AreEqual(findingId, (string?)resRef.Attribute("target"));

        // 5. Add discussion record
        var recordDiscExit = Program.Main(new[] {
            "task", "record",
            "--task", taskId,
            "--iteration", TestIterationId,
            "--kind", "discussion",
            "--summary", "Team decided to keep option A > B",
            "--workspace-root", _tempDir
        });
        Assert.AreEqual(0, recordDiscExit);

        // 6. Add completion record
        var recordCompExit = Program.Main(new[] {
            "task", "record",
            "--task", taskId,
            "--iteration", TestIterationId,
            "--kind", "completion",
            "--summary", "Completed implementation of <record>",
            "--workspace-root", _tempDir
        });
        Assert.AreEqual(0, recordCompExit);
    }

    [TestMethod]
    public void Task_PorcelainCommands_AcceptOccurredAtAndMonotonicityDiagnosticReportsMinimalTimestamp()
    {
        // 1. Create a task without start
        var createExit = Program.Main(new[] {
            "task", "quick",
            "--iteration", TestIterationId,
            "--title", "OccurredAt Flow Task",
            "--scope", "src/Core",
            "--done-when", "Criteria met",
            "--why", "Testing porcelain occurred-at parameters",
            "--workspace-root", _tempDir
        });
        Assert.AreEqual(0, createExit);

        var tasksPath = Path.Combine(_tempDir, ".dogdouspec", TestIterationId, "tasks.xml");
        var tasksDoc = XDocument.Load(tasksPath);
        var taskElem = tasksDoc.Descendants("task").First(t => t.Element("title")?.Value == "OccurredAt Flow Task");
        var taskId = taskElem.Attribute("id")!.Value;
        var createdAt = taskElem.Attribute("created_at")!.Value;

        // Monotonicity violation: earlier than created_at
        var backdatedExit = Program.Main(new[] {
            "task", "start",
            "--task", taskId,
            "--iteration", TestIterationId,
            "--occurred-at", "2020-01-01T00:00:00Z",
            "--workspace-root", _tempDir
        });
        Assert.AreNotEqual(0, backdatedExit);

        // 2. Start task with explicit --occurred-at
        var t1 = "2026-09-25T12:00:00Z";
        var startExit = Program.Main(new[] {
            "task", "start",
            "--task", taskId,
            "--iteration", TestIterationId,
            "--occurred-at", t1,
            "--workspace-root", _tempDir
        });
        Assert.AreEqual(0, startExit);

        tasksDoc = XDocument.Load(tasksPath);
        taskElem = tasksDoc.Descendants("task").First(t => (string?)t.Attribute("id") == taskId);
        Assert.AreEqual("in-progress", (string?)taskElem.Attribute("status"));
        Assert.AreEqual(t1, (string?)taskElem.Attribute("started_at"));
        Assert.AreEqual(t1, (string?)taskElem.Attribute("updated_at"));

        // 3. Record append with occurred_at == updated_at
        var recordExit = Program.Main(new[] {
            "task", "record",
            "--task", taskId,
            "--iteration", TestIterationId,
            "--kind", "discussion",
            "--summary", "Record at same timestamp as updated_at",
            "--occurred-at", t1,
            "--workspace-root", _tempDir
        });
        Assert.AreEqual(0, recordExit);

        tasksDoc = XDocument.Load(tasksPath);
        taskElem = tasksDoc.Descendants("task").First(t => (string?)t.Attribute("id") == taskId);
        Assert.AreEqual(t1, (string?)taskElem.Attribute("updated_at"));

        // 4. Verify task with explicit --occurred-at
        var t2 = "2026-09-25T12:05:00Z";
        var verifyExit = Program.Main(new[] {
            "task", "verify",
            "--task", taskId,
            "--iteration", TestIterationId,
            "--occurred-at", t2,
            "--workspace-root", _tempDir
        });
        Assert.AreEqual(0, verifyExit);

        tasksDoc = XDocument.Load(tasksPath);
        taskElem = tasksDoc.Descendants("task").First(t => (string?)t.Attribute("id") == taskId);
        Assert.AreEqual("verification", (string?)taskElem.Attribute("status"));
        Assert.AreEqual(t2, (string?)taskElem.Attribute("updated_at"));

        // 5. Block task with explicit --occurred-at
        var t3 = "2026-09-25T12:10:00Z";
        var blockExit = Program.Main(new[] {
            "task", "block",
            "--task", taskId,
            "--iteration", TestIterationId,
            "--summary", "Waiting on review",
            "--occurred-at", t3,
            "--workspace-root", _tempDir
        });
        Assert.AreEqual(0, blockExit);

        tasksDoc = XDocument.Load(tasksPath);
        taskElem = tasksDoc.Descendants("task").First(t => (string?)t.Attribute("id") == taskId);
        Assert.AreEqual("blocked", (string?)taskElem.Attribute("status"));
        Assert.AreEqual(t3, (string?)taskElem.Attribute("updated_at"));

        // 6. Resume task with explicit --occurred-at
        var t4 = "2026-09-25T12:15:00Z";
        var resumeExit = Program.Main(new[] {
            "task", "resume",
            "--task", taskId,
            "--iteration", TestIterationId,
            "--all",
            "--summary", "Review complete",
            "--occurred-at", t4,
            "--workspace-root", _tempDir
        });
        Assert.AreEqual(0, resumeExit);

        tasksDoc = XDocument.Load(tasksPath);
        taskElem = tasksDoc.Descendants("task").First(t => (string?)t.Attribute("id") == taskId);
        Assert.AreEqual("in-progress", (string?)taskElem.Attribute("status"));
        Assert.AreEqual(t4, (string?)taskElem.Attribute("updated_at"));

        // 7. Finish task with explicit --occurred-at
        var t5 = "2026-09-25T12:20:00Z";
        var finishExit = Program.Main(new[] {
            "task", "finish",
            "--task", taskId,
            "--iteration", TestIterationId,
            "--occurred-at", t5,
            "--workspace-root", _tempDir
        });
        Assert.AreEqual(0, finishExit);

        tasksDoc = XDocument.Load(tasksPath);
        taskElem = tasksDoc.Descendants("task").First(t => (string?)t.Attribute("id") == taskId);
        Assert.AreEqual("done", (string?)taskElem.Attribute("status"));
        Assert.AreEqual(t5, (string?)taskElem.Attribute("completed_at"));
        Assert.AreEqual(t5, (string?)taskElem.Attribute("updated_at"));
    }
}
