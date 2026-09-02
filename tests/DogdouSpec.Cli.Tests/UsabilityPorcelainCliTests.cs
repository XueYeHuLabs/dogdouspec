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
        Program.Main(new[] { "iteration", "create", "--id", TestIterationId, "--kind", "feature", "--activate", "--workspace-root", _tempDir });
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
}
