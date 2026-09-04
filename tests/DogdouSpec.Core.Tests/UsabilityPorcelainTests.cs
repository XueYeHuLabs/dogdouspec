using System.Xml.Linq;
using DogdouSpec.Core.Diagnostics;
using DogdouSpec.Core.Formatting;
using DogdouSpec.Core.Iterations;
using DogdouSpec.Core.Tasks;
using DogdouSpec.Core.Workspace;

namespace DogdouSpec.Core.Tests;

[TestClass]
public sealed class UsabilityPorcelainTests
{
    private static readonly string[] CoreScopes = { "src/Core" };
    private static readonly string[] CliScopes = { "src/Cli" };

    private static readonly string[] TestCriteria = new[] { "Substantive criterion for porcelain test." };
    private string _tempDir = null!;
    private string _workspaceRoot = null!;
    private const string TestIterationId = "20260902-porcelain-test";

    [TestInitialize]
    public void SetUp()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "DogdouSpec_PorcelainCore_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        WorkspaceInitializer.Initialize(_tempDir, _tempDir);
        _workspaceRoot = Path.Combine(_tempDir, ".dogdouspec");

        // Create an active iteration
        IterationCreator.Create(_workspaceRoot, TestIterationId, "feature", activate: true, criteria: TestCriteria);
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
    public void TaskList_And_TaskSummary_WorkCorrectly()
    {
        // Add two tasks: one started (in-progress), one pending
        var input1 = new QuickTaskInput(
            Title: "Task 1",
            Scopes: CoreScopes,
            DoneWhen: "Done 1",
            Why: "Why 1",
            Origins: Array.Empty<string>(),
            Dependencies: Array.Empty<string>(),
            Terms: Array.Empty<string>(),
            IterationId: TestIterationId,
            ExpectedRevision: null,
            Start: true,
            DryRun: false,
            TaskId: null,
            OperationId: null);

        var (q1Ok, _, _, _) = TaskQuick.Create(_workspaceRoot, input1);
        Assert.IsTrue(q1Ok);

        var input2 = new QuickTaskInput(
            Title: "Task 2",
            Scopes: CliScopes,
            DoneWhen: "Done 2",
            Why: "Why 2",
            Origins: Array.Empty<string>(),
            Dependencies: Array.Empty<string>(),
            Terms: Array.Empty<string>(),
            IterationId: TestIterationId,
            ExpectedRevision: null,
            Start: false,
            DryRun: false,
            TaskId: null,
            OperationId: null);

        var (q2Ok, _, _, _) = TaskQuick.Create(_workspaceRoot, input2);
        Assert.IsTrue(q2Ok);

        // Test TaskList
        var (listOk, listRes, listDiags) = TaskList.List(_workspaceRoot, TestIterationId);
        Assert.IsTrue(listOk);
        Assert.AreEqual(0, listDiags.Count);
        Assert.IsNotNull(listRes);
        Assert.AreEqual(2, listRes.Tasks.Count);

        var (inProgListOk, inProgRes, _) = TaskList.List(_workspaceRoot, TestIterationId, "in-progress");
        Assert.IsTrue(inProgListOk);
        Assert.IsNotNull(inProgRes);
        Assert.AreEqual(1, inProgRes.Tasks.Count);
        Assert.AreEqual("in-progress", inProgRes.Tasks[0].Status);

        // Test TaskSummary
        var (sumOk, sumRes, sumDiags) = TaskSummary.Summarize(_workspaceRoot, TestIterationId);
        Assert.IsTrue(sumOk);
        Assert.AreEqual(0, sumDiags.Count);
        Assert.IsNotNull(sumRes);
        Assert.AreEqual(2, sumRes.Total);
        Assert.AreEqual(1, sumRes.Pending);
        Assert.AreEqual(1, sumRes.InProgress);
        Assert.AreEqual(0, sumRes.Done);

        var xml = sumRes.ToXmlString();
        Assert.IsTrue(xml.Contains("total=\"2\""));
        var human = sumRes.ToHumanString();
        Assert.IsTrue(human.Contains("Total tasks:    2"));
    }

    [TestMethod]
    public void TaskShow_FindsTaskAndReturnsDetails()
    {
        var input = new QuickTaskInput(
            Title: "Task Show Target",
            Scopes: CoreScopes,
            DoneWhen: "Observable completion",
            Why: "Inspect details",
            Origins: Array.Empty<string>(),
            Dependencies: Array.Empty<string>(),
            Terms: Array.Empty<string>(),
            IterationId: TestIterationId,
            ExpectedRevision: null,
            Start: true,
            DryRun: false,
            TaskId: null,
            OperationId: null);

        var (q1Ok, _, _, _) = TaskQuick.Create(_workspaceRoot, input);
        Assert.IsTrue(q1Ok);

        var tasksDoc = XDocument.Load(Path.Combine(_workspaceRoot, TestIterationId, "tasks.xml"));
        var createdTask = tasksDoc.Descendants("task").First();
        var expectedTaskId = createdTask.Attribute("id")!.Value;

        var (showOk, showRes, showDiags) = TaskShow.Show(_workspaceRoot, expectedTaskId, TestIterationId);
        Assert.IsTrue(showOk);
        Assert.AreEqual(0, showDiags.Count);
        Assert.IsNotNull(showRes);
        Assert.AreEqual(expectedTaskId, showRes.TaskId);
        Assert.AreEqual("Task Show Target", showRes.TaskElement.Element("title")?.Value);
        Assert.AreEqual("in-progress", showRes.TaskElement.Attribute("status")?.Value);

        var xml = showRes.ToXmlString();
        Assert.IsTrue(xml.Contains($"id=\"{expectedTaskId}\""));
        var human = showRes.ToHumanString();
        Assert.IsTrue(human.Contains("Title: Task Show Target"));
    }

    [TestMethod]
    public void TaskScopeMatcher_ExplainPath_ProvidesRuleMetadata()
    {
        var scopeXml = XElement.Parse(@"
<scope>
  <repository path=""."">
    <include path=""src/DogdouSpec.Core/**"" />
    <exclude path=""src/DogdouSpec.Core/obj/**"" />
  </repository>
</scope>");
        var scopes = TaskScopeMatcher.ParseScopes(scopeXml);

        var exp1 = TaskScopeMatcher.ExplainPath("src/DogdouSpec.Core/Tasks/TaskList.cs", scopes);
        Assert.IsTrue(exp1.InScope);
        Assert.AreEqual("include", exp1.RuleKind);
        Assert.AreEqual("src/DogdouSpec.Core/**", exp1.MatchedRule);

        var exp2 = TaskScopeMatcher.ExplainPath("src/DogdouSpec.Core/obj/Debug/net10.0/test.dll", scopes);
        Assert.IsFalse(exp2.InScope);
        Assert.AreEqual("exclude", exp2.RuleKind);
        Assert.AreEqual("src/DogdouSpec.Core/obj/**", exp2.MatchedRule);

        var exp3 = TaskScopeMatcher.ExplainPath("src/DogdouSpec.Cli/Program.cs", scopes);
        Assert.IsFalse(exp3.InScope);
        Assert.AreEqual("no-include-match", exp3.RuleKind);
    }

    [TestMethod]
    public void WorkspaceVcsStatus_And_CheckpointPlan_WorkCorrectly()
    {
        var (statusOk, statusRes, statusDiags) = WorkspaceVcsStatus.CheckStatus(_workspaceRoot);
        Assert.IsTrue(statusOk);
        Assert.AreEqual(0, statusDiags.Count);
        Assert.IsNotNull(statusRes);
        Assert.IsFalse(statusRes.IsGitRepository);
        Assert.IsFalse(statusRes.IsTransportReady);
        Assert.IsTrue(statusRes.UncheckpointedFiles.Count > 0);

        var (planOk, planRes, planDiags) = WorkspaceVcsStatus.CreateCheckpointPlan(_workspaceRoot);
        Assert.IsTrue(planOk);
        Assert.AreEqual(0, planDiags.Count);
        Assert.IsNotNull(planRes);
        Assert.IsFalse(planRes.IsGitRepository);
        Assert.IsFalse(planRes.IsSatisfied);
        Assert.IsTrue(planRes.UncheckpointedFiles.Count > 0);

        var human = planRes.ToHumanString();
        Assert.IsTrue(human.Contains("Workspace Checkpoint Plan"));
    }

    [TestMethod]
    public void IterationReadiness_EvaluatesDimensions()
    {
        var (assessOk, assessRes, _) = IterationReadiness.Assess(_workspaceRoot, TestIterationId, phase: "activation");
        Assert.IsTrue(assessOk);
        Assert.IsNotNull(assessRes);
        Assert.IsTrue(assessRes.Dimensions.Count >= 5);

        var dimNames = assessRes.Dimensions.Select(d => d.Name).ToList();
        CollectionAssert.Contains(dimNames, "execution_terminality");
        CollectionAssert.Contains(dimNames, "verification_completeness");
        CollectionAssert.Contains(dimNames, "unresolved_findings");
        CollectionAssert.Contains(dimNames, "product_confirmation");
        CollectionAssert.Contains(dimNames, "vcs_checkpoint");
    }
}
