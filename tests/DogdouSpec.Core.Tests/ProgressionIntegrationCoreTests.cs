using System.Xml.Linq;
using DogdouSpec.Core.Diagnostics;
using DogdouSpec.Core.Formatting;
using DogdouSpec.Core.Iterations;
using DogdouSpec.Core.Progression;
using DogdouSpec.Core.Reporting;
using DogdouSpec.Core.Tasks;
using DogdouSpec.Core.Workspace;

namespace DogdouSpec.Core.Tests;

[TestClass]
public sealed class ProgressionIntegrationCoreTests
{
    private static readonly string[] TestCriteria = new[] { "Substantive test acceptance criterion." };
    private string _tempDir = null!;
    private string _wsRoot = null!;
    private const string IterationId = "20260908-progression-int";

    [TestInitialize]
    public void SetUp()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "DogdouSpec_ProgInt_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        WorkspaceInitializer.Initialize(_tempDir, _tempDir);
        _wsRoot = Path.Combine(_tempDir, ".dogdouspec");
        IterationCreator.Create(_wsRoot, IterationId, "feature", activate: true, criteria: TestCriteria);
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
    public void BlockedOnlySet_NeverReportsAllTerminalOrNoTasks_AcrossCommands()
    {
        // Add 2 tasks, both blocked
        var tasksPath = Path.Combine(_wsRoot, IterationId, "tasks.xml");
        var tasksDoc = XDocument.Load(tasksPath);
        tasksDoc.Root!.Add(
            CreateTaskElement("20260908-task-b1", "blocked", "Blocked Task 1"),
            CreateTaskElement("20260908-task-b2", "blocked", "Blocked Task 2"));
        tasksDoc.Save(tasksPath);

        // 1. TaskNext
        var (nextOk, nextRes, nextDiags) = TaskNext.SelectNext(_wsRoot, IterationId);
        Assert.IsTrue(nextOk);
        Assert.AreEqual(0, nextDiags.Count);
        Assert.IsNotNull(nextRes);
        Assert.IsFalse(nextRes.HasTask);
        Assert.AreEqual(ProgressionReasonCodes.TasksBlocked, nextRes.ReasonCode);
        Assert.AreEqual(ProgressionActionCategories.WaitExternal, nextRes.ActionCategory);
        Assert.AreNotEqual("All tasks in iteration are terminal", nextRes.Reason);
        Assert.IsTrue(nextRes.Reason.Contains("blocked"));

        // 2. TaskSummary
        var (sumOk, sumRes, sumDiags) = TaskSummary.Summarize(_wsRoot, IterationId);
        Assert.IsTrue(sumOk);
        Assert.AreEqual(0, sumDiags.Count);
        Assert.IsNotNull(sumRes);
        Assert.AreEqual(2, sumRes.Total);
        Assert.AreEqual(2, sumRes.Blocked);
        Assert.AreEqual(0, sumRes.Done);
        Assert.AreEqual(2, sumRes.Eligible);
        Assert.AreEqual(0.0, sumRes.CompletionPercentage);

        // 3. IterationSummary
        var (iterSumOk, iterSumRes, iterSumDiags) = IterationSummaryGenerator.Generate(_wsRoot, IterationId);
        Assert.IsTrue(iterSumOk);
        Assert.AreEqual(0, iterSumDiags.Count);
        Assert.IsNotNull(iterSumRes);
        Assert.IsFalse(iterSumRes.Summary.RecommendedNextAction.Contains("No tasks defined"));
        Assert.IsFalse(iterSumRes.Summary.RecommendedNextAction.Contains("All tasks completed"));
        Assert.IsTrue(iterSumRes.Summary.RecommendedNextAction.Contains("blocked"));
        Assert.AreEqual(ProgressionActionCategories.WaitExternal, iterSumRes.Summary.ActionCategory);
        Assert.AreEqual(ProgressionReasonCodes.TasksBlocked, iterSumRes.Summary.ReasonCode);

        // 4. IterationReadiness
        var (readyOk, readyRes, readyDiags) = IterationReadiness.Assess(_wsRoot, IterationId, "completion");
        Assert.IsTrue(readyOk);
        Assert.IsNotNull(readyRes);
        var termDim = readyRes.Dimensions.First(d => d.Name == "execution_terminality");
        Assert.AreEqual("failed", termDim.Status);
    }

    [TestMethod]
    public void TaskInVerificationWithReviewRequired_RecommendsReview_NeverBypassesWithFinish()
    {
        var tasksPath = Path.Combine(_wsRoot, IterationId, "tasks.xml");
        var tasksDoc = XDocument.Load(tasksPath);
        var task = CreateTaskElement("20260908-task-rev", "verification", "Review Task");
        task.Add(new XElement("review", new XAttribute("required", "true")));
        tasksDoc.Root!.Add(task);
        tasksDoc.Save(tasksPath);

        // 1. TaskNext
        var (nextOk, nextRes, _) = TaskNext.SelectNext(_wsRoot, IterationId);
        Assert.IsTrue(nextOk);
        Assert.IsNotNull(nextRes);
        Assert.IsTrue(nextRes.HasTask);
        Assert.AreEqual(ProgressionActionCategories.ReviewRequired, nextRes.ActionCategory);
        Assert.AreEqual(ProgressionReasonCodes.ActiveTaskReviewRequired, nextRes.ReasonCode);

        // 2. IterationSummary
        var (iterSumOk, iterSumRes, _) = IterationSummaryGenerator.Generate(_wsRoot, IterationId);
        Assert.IsTrue(iterSumOk);
        Assert.IsNotNull(iterSumRes);
        Assert.AreEqual(ProgressionActionCategories.ReviewRequired, iterSumRes.Summary.ActionCategory);
        Assert.AreEqual(ProgressionReasonCodes.ActiveTaskReviewRequired, iterSumRes.Summary.ReasonCode);
        Assert.IsTrue(iterSumRes.Summary.RecommendedNextAction.Contains("review"));
        Assert.IsFalse(iterSumRes.Summary.RecommendedNextAction.Contains("task finish"));
    }

    [TestMethod]
    public void BothSummaries_UseSameEligibleDenominator_AndDistinguishTerminalDispositions()
    {
        // 5 tasks: 2 done, 1 cancelled, 1 transferred, 1 superseded
        var tasksPath = Path.Combine(_wsRoot, IterationId, "tasks.xml");
        var tasksDoc = XDocument.Load(tasksPath);
        tasksDoc.Root!.Add(
            CreateTaskElement("20260908-task-d1", "done", "Done 1"),
            CreateTaskElement("20260908-task-d2", "done", "Done 2"),
            CreateTaskElement("20260908-task-c1", "cancelled", "Cancelled 1"),
            CreateTaskElement("20260908-task-t1", "transferred", "Transferred 1"),
            CreateTaskElement("20260908-task-s1", "superseded", "Superseded 1"));
        tasksDoc.Save(tasksPath);

        // 1. TaskSummary
        var (sumOk, sumRes, _) = TaskSummary.Summarize(_wsRoot, IterationId);
        Assert.IsTrue(sumOk);
        Assert.IsNotNull(sumRes);
        Assert.AreEqual(5, sumRes.Total);
        Assert.AreEqual(2, sumRes.Done);
        Assert.AreEqual(3, sumRes.Inactive);
        Assert.AreEqual(2, sumRes.Eligible);
        Assert.AreEqual(100.0, sumRes.CompletionPercentage);

        var sumHuman = sumRes.ToHumanString();
        Assert.IsTrue(sumHuman.Contains("Transferred:  1 (terminal disposition)"));
        Assert.IsTrue(sumHuman.Contains("Superseded:   1 (terminal disposition)"));
        Assert.IsTrue(sumHuman.Contains("Cancelled:    1 (terminal disposition)"));
        Assert.IsTrue(sumHuman.Contains("Eligible tasks: 2"));
        Assert.IsTrue(sumHuman.Contains("Completion:     100.0%"));

        // 2. IterationSummary
        var (iterSumOk, iterSumRes, _) = IterationSummaryGenerator.Generate(_wsRoot, IterationId);
        Assert.IsTrue(iterSumOk);
        Assert.IsNotNull(iterSumRes);
        var isum = iterSumRes.Summary;
        Assert.AreEqual(5, isum.TotalTasks);
        Assert.AreEqual(2, isum.DoneTasks);
        Assert.AreEqual(3, isum.InactiveTasks);
        Assert.AreEqual(100.0, isum.ProgressPercentage);

        // Denominators match!
        Assert.AreEqual(sumRes.Eligible, isum.TotalTasks - isum.InactiveTasks);

        // Progress markdown shows 2/2 tasks completed
        var md = iterSumRes.ToMarkdownString();
        Assert.IsTrue(md.Contains("(2/2 tasks completed)"));
    }

    [TestMethod]
    public void TaskNext_AgentFiltering_SelectsAppropriateCandidates()
    {
        var tasksPath = Path.Combine(_wsRoot, IterationId, "tasks.xml");
        var tasksDoc = XDocument.Load(tasksPath);
        tasksDoc.Root!.Add(
            CreateTaskElement("20260908-task-alpha", "pending", "Alpha Task", agent: "agent-alpha"),
            CreateTaskElement("20260908-task-beta", "pending", "Beta Task", agent: "agent-beta"),
            CreateTaskElement("20260908-task-unassigned", "pending", "Unassigned Task", agent: null));
        tasksDoc.Save(tasksPath);

        // Filter for agent-alpha
        var (nextAlphaOk, nextAlphaRes, _) = TaskNext.SelectNext(_wsRoot, IterationId, agentFilter: "agent-alpha");
        Assert.IsTrue(nextAlphaOk);
        Assert.IsNotNull(nextAlphaRes?.Task);
        Assert.AreEqual("20260908-task-alpha", nextAlphaRes.Task.Id);

        // Filter for agent-beta
        var (nextBetaOk, nextBetaRes, _) = TaskNext.SelectNext(_wsRoot, IterationId, agentFilter: "agent-beta");
        Assert.IsTrue(nextBetaOk);
        Assert.IsNotNull(nextBetaRes?.Task);
        Assert.AreEqual("20260908-task-beta", nextBetaRes.Task.Id);

        // Filter for agent-gamma (only unassigned matches)
        var (nextGammaOk, nextGammaRes, _) = TaskNext.SelectNext(_wsRoot, IterationId, agentFilter: "agent-gamma");
        Assert.IsTrue(nextGammaOk);
        Assert.IsNotNull(nextGammaRes?.Task);
        Assert.AreEqual("20260908-task-unassigned", nextGammaRes.Task.Id);
    }

    [TestMethod]
    public void TaskNext_TargetTaskSelection_ReturnsTaskNotFound_WhenTaskDoesNotExist()
    {
        var (ok, res, diags) = TaskNext.SelectNext(_wsRoot, IterationId, requestedTaskId: "20260908-task-nonexistent");
        Assert.IsFalse(ok);
        Assert.IsNull(res);
        Assert.AreEqual(1, diags.Count);
        Assert.AreEqual(DiagnosticCodes.ResourceNotFound, diags[0].Code);
    }

    [TestMethod]
    public void MultipleActiveIterations_ReturnsDiagnosticAcrossAllCommands()
    {
        // Create second active iteration
        var secondIterId = "20260909-second-active";
        var (createOk, _, createDiags) = IterationCreator.Create(_wsRoot, secondIterId, "feature", activate: true, criteria: TestCriteria);
        Assert.IsTrue(createOk, $"Failed to create second iteration: {string.Join("; ", createDiags.Select(d => d.Message))}");

        // 1. TaskNext without --iteration
        var (nextOk, _, nextDiags) = TaskNext.SelectNext(_wsRoot, null);
        Assert.IsFalse(nextOk);
        Assert.IsTrue(nextDiags.Any(d => d.Code == DiagnosticCodes.InvalidArgument && d.Message.Contains("Multiple active iterations")));

        // 2. TaskSummary without --iteration
        var (sumOk, _, sumDiags) = TaskSummary.Summarize(_wsRoot, null);
        Assert.IsFalse(sumOk);
        Assert.IsTrue(sumDiags.Any(d => d.Code == DiagnosticCodes.InvalidArgument && d.Message.Contains("Multiple active iterations")));

        // 3. IterationSummary without --iteration
        var (iterSumOk, _, iterSumDiags) = IterationSummaryGenerator.Generate(_wsRoot, null);
        Assert.IsFalse(iterSumOk);
        Assert.IsTrue(iterSumDiags.Any(d => d.Code == DiagnosticCodes.InvalidArgument && d.Message.Contains("Multiple active iterations")));

        // 4. Specifying --iteration explicitly succeeds on all commands
        var (explicitNextOk, explicitNextRes, _) = TaskNext.SelectNext(_wsRoot, IterationId);
        Assert.IsTrue(explicitNextOk);
        Assert.IsNotNull(explicitNextRes);

        var (explicitSumOk, explicitSumRes, _) = TaskSummary.Summarize(_wsRoot, IterationId);
        Assert.IsTrue(explicitSumOk);
        Assert.IsNotNull(explicitSumRes);

        var (explicitIterSumOk, explicitIterSumRes, _) = IterationSummaryGenerator.Generate(_wsRoot, IterationId);
        Assert.IsTrue(explicitIterSumOk);
        Assert.IsNotNull(explicitIterSumRes);
    }

    private static XElement CreateTaskElement(string id, string status, string title, string? agent = "codex")
    {
        var task = new XElement("task",
            new XAttribute("id", id),
            new XAttribute("status", status),
            new XAttribute("created_at", "2026-09-08T00:00:00Z"),
            new XAttribute("updated_at", "2026-09-08T00:00:00Z"));

        if (!string.IsNullOrEmpty(agent))
        {
            task.Add(new XAttribute("agent", agent));
        }

        task.Add(
            new XElement("index",
                new XElement("summary", title),
                new XElement("term", new XAttribute("key", "kind"), new XAttribute("value", "task")),
                new XElement("term", new XAttribute("key", "status"), new XAttribute("value", status))),
            new XElement("title", title),
            new XElement("objective", $"Objective for {title}"),
            new XElement("rationale", $"Rationale for {title}"),
            new XElement("scope",
                new XElement("repository", new XAttribute("path", "."),
                    new XElement("include", new XAttribute("path", "*")))),
            new XElement("origin",
                new XElement("ref", new XAttribute("scope", "iteration"), new XAttribute("target", "20260908-req-progression-int"), new XAttribute("relation", "implements"))),
            new XElement("constraints"),
            new XElement("acceptance",
                new XElement("criterion", new XAttribute("id", $"{id}-crit"), new XAttribute("status", "pending"), "Substantive criterion")),
            new XElement("context",
                new XElement("summary", "Task context")),
            new XElement("records"));

        return task;
    }
}
