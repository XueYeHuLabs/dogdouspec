using System.Xml.Linq;
using DogdouSpec.Cli.Commands;
using DogdouSpec.Core.Diagnostics;
using DogdouSpec.Core.Workspace;

namespace DogdouSpec.Cli.Tests;

[TestClass]
public sealed class HighLevelSemanticCommandTests
{
    private string _tempDir = null!;

    [TestInitialize]
    public void SetUp()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "DogdouSpec_PorcelainTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        WorkspaceInitializer.Initialize(_tempDir, _tempDir);
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
    public void IterationCreate_WithActivate_CreatesActiveIterationImmediately()
    {
        var iterId = "20260827-test-active-create";
        var exitCode = Program.Main(new[] { "iteration", "create", "--id", iterId, "--kind", "feature", "--activate", "--workspace-root", _tempDir });
        Assert.AreEqual(0, exitCode);

        var specPath = Path.Combine(_tempDir, ".dogdouspec", iterId, "spec.xml");
        Assert.IsTrue(File.Exists(specPath));
        var specDoc = XDocument.Load(specPath);
        Assert.AreEqual("active", (string?)specDoc.Root?.Attribute("status"));

        var req = specDoc.Descendants("requirement").FirstOrDefault();
        Assert.IsNotNull(req);
        Assert.AreEqual("approved", (string?)req.Attribute("status"));

        // Verify task quick --start can immediately execute without manual activation
        var taskExit = Program.Main(new[] {
            "task", "quick",
            "--iteration", iterId,
            "--title", "Immediate Task",
            "--scope", ".",
            "--done-when", "Immediately ready",
            "--why", "Verify active state",
            "--start",
            "--workspace-root", _tempDir
        });
        Assert.AreEqual(0, taskExit);

        var tasksPath = Path.Combine(_tempDir, ".dogdouspec", iterId, "tasks.xml");
        var tasksDoc = XDocument.Load(tasksPath);
        var task = tasksDoc.Descendants("task").FirstOrDefault();
        Assert.IsNotNull(task);
        Assert.AreEqual("in-progress", (string?)task.Attribute("status"));
    }

    [TestMethod]
    public void IterationActivate_AutoApprove_ActivatesDraftIteration()
    {
        var iterId = "20260827-test-draft-activate";
        var createExit = Program.Main(new[] { "iteration", "create", "--id", iterId, "--kind", "feature", "--workspace-root", _tempDir });
        Assert.AreEqual(0, createExit);

        var specPath = Path.Combine(_tempDir, ".dogdouspec", iterId, "spec.xml");
        var specDocBefore = XDocument.Load(specPath);
        Assert.AreEqual("draft", (string?)specDocBefore.Root?.Attribute("status"));

        var activateExit = Program.Main(new[] { "iteration", "activate", "--iteration", iterId, "--auto-approve", "--workspace-root", _tempDir });
        Assert.AreEqual(0, activateExit);

        var specDocAfter = XDocument.Load(specPath);
        Assert.AreEqual("active", (string?)specDocAfter.Root?.Attribute("status"));
        var req = specDocAfter.Descendants("requirement").FirstOrDefault();
        Assert.IsNotNull(req);
        Assert.AreEqual("approved", (string?)req.Attribute("status"));
    }

    [TestMethod]
    public void TaskStart_Verify_Finish_Progression()
    {
        var iterId = "20260827-test-task-progression";
        Program.Main(new[] { "iteration", "create", "--id", iterId, "--kind", "feature", "--activate", "--workspace-root", _tempDir });

        var quickExit = Program.Main(new[] {
            "task", "quick",
            "--iteration", iterId,
            "--title", "Step-by-step task",
            "--scope", ".",
            "--done-when", "Progression tested",
            "--why", "Verify start/verify/finish sequence",
            "--workspace-root", _tempDir
        });
        Assert.AreEqual(0, quickExit);

        var tasksPath = Path.Combine(_tempDir, ".dogdouspec", iterId, "tasks.xml");
        var doc1 = XDocument.Load(tasksPath);
        var task = doc1.Descendants("task").First();
        var taskId = (string)task.Attribute("id")!;
        Assert.AreEqual("pending", (string?)task.Attribute("status"));

        // 1. Task Start
        var startExit = Program.Main(new[] { "task", "start", "--iteration", iterId, "--task", taskId, "--workspace-root", _tempDir });
        Assert.AreEqual(0, startExit);
        var doc2 = XDocument.Load(tasksPath);
        Assert.AreEqual("in-progress", (string?)doc2.Descendants("task").First().Attribute("status"));

        // 2. Task Verify
        var verifyExit = Program.Main(new[] { "task", "verify", "--iteration", iterId, "--task", taskId, "--workspace-root", _tempDir });
        Assert.AreEqual(0, verifyExit);
        var doc3 = XDocument.Load(tasksPath);
        Assert.AreEqual("verification", (string?)doc3.Descendants("task").First().Attribute("status"));

        // 3. Task Finish
        var finishExit = Program.Main(new[] { "task", "finish", "--iteration", iterId, "--task", taskId, "--summary", "Step finished", "--workspace-root", _tempDir });
        Assert.AreEqual(0, finishExit);
        var doc4 = XDocument.Load(tasksPath);
        Assert.AreEqual("done", (string?)doc4.Descendants("task").First().Attribute("status"));
    }

    [TestMethod]
    public void TaskFinish_FromPending_DirectlyCompletesAndMarksCriteriaPassed()
    {
        var iterId = "20260827-test-task-direct-finish";
        Program.Main(new[] { "iteration", "create", "--id", iterId, "--kind", "feature", "--activate", "--workspace-root", _tempDir });

        Program.Main(new[] {
            "task", "quick",
            "--iteration", iterId,
            "--title", "Direct finish task",
            "--scope", ".",
            "--done-when", "Direct finish tested",
            "--why", "Verify atomic finish from pending",
            "--workspace-root", _tempDir
        });

        var tasksPath = Path.Combine(_tempDir, ".dogdouspec", iterId, "tasks.xml");
        var docBefore = XDocument.Load(tasksPath);
        var taskId = (string)docBefore.Descendants("task").First().Attribute("id")!;

        // Directly finish from pending
        var finishExit = Program.Main(new[] { "task", "finish", "--iteration", iterId, "--task", taskId, "--workspace-root", _tempDir });
        Assert.AreEqual(0, finishExit);

        var doc = XDocument.Load(tasksPath);
        var task = doc.Descendants("task").First();
        Assert.AreEqual("done", (string?)task.Attribute("status"));

        var crit = task.Descendants("criterion").First();
        Assert.AreEqual("passed", (string?)crit.Attribute("status"));

        // Now complete the iteration using iteration complete --accept-all
        var completeExit = Program.Main(new[] { "iteration", "complete", "--iteration", iterId, "--accept-all", "--workspace-root", _tempDir });
        Assert.AreEqual(0, completeExit);

        var specPath = Path.Combine(_tempDir, ".dogdouspec", iterId, "spec.xml");
        var specDoc = XDocument.Load(specPath);
        Assert.AreEqual("completed", (string?)specDoc.Root?.Attribute("status"));
    }
}