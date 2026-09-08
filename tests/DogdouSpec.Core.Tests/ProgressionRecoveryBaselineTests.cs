using System.Xml.Linq;
using DogdouSpec.Core.Diagnostics;
using DogdouSpec.Core.Iterations;
using DogdouSpec.Core.Tasks;
using DogdouSpec.Core.Workspace;

namespace DogdouSpec.Core.Tests;

[TestClass]
public sealed class ProgressionRecoveryBaselineTests
{
    private static readonly string[] TestCriteria = new[] { "Substantive criterion for progression recovery baseline test." };
    private string _tempDir = null!;
    private string _workspaceRoot = null!;
    private const string TestIterationId = "20260908-progression-baseline";

    [TestInitialize]
    public void SetUp()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "DogdouSpec_Baseline_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        WorkspaceInitializer.Initialize(_tempDir, _tempDir);
        _workspaceRoot = Path.Combine(_tempDir, ".dogdouspec");
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
    public void Baseline_Reproduce_BlockedTask_FalselyReportedAsAllTasksTerminal()
    {
        var tasksPath = Path.Combine(_workspaceRoot, TestIterationId, "tasks.xml");
        var tasksDoc = XDocument.Load(tasksPath);
        var taskXml = XElement.Parse($@"
<task id=""20260908-task-baseline-blocked"" status=""blocked"" created_at=""2026-09-08T00:00:00Z"" updated_at=""2026-09-08T00:00:00Z"">
  <index>
    <summary>Sample blocked task</summary>
    <term key=""kind"" value=""task""/>
    <term key=""status"" value=""blocked""/>
  </index>
  <title>Sample blocked task</title>
  <objective>Reproduce baseline defect where blocked task is treated as terminal</objective>
  <rationale>Testing progression assessment</rationale>
  <scope>
    <repository path=""."">
      <include path=""*"" />
    </repository>
  </scope>
  <origin>
    <ref scope=""iteration"" target=""20260908-req-01"" relation=""implements""/>
  </origin>
  <constraints/>
  <acceptance>
    <criterion id=""20260908-crit-blocked"" status=""pending"">Acceptance criterion</criterion>
  </acceptance>
  <context>
    <summary>Blocked on external dependency</summary>
  </context>
  <records/>
</task>");
        tasksDoc.Root!.Add(taskXml);
        tasksDoc.Save(tasksPath);

        var (nextOk, nextRes, nextDiags) = TaskNext.SelectNext(_workspaceRoot, TestIterationId);

        Assert.IsTrue(nextOk);
        Assert.AreEqual(0, nextDiags.Count);
        Assert.IsNotNull(nextRes);
        // Verified fix: Blocked task is NO LONGER falsely reported as terminal!
        Assert.IsFalse(nextRes.HasTask);
        Assert.AreNotEqual("All tasks in iteration are terminal", nextRes.Reason);
        Assert.AreEqual("All non-terminal tasks are currently blocked.", nextRes.Reason);
        Assert.AreEqual(DogdouSpec.Core.Progression.ProgressionReasonCodes.TasksBlocked, nextRes.ReasonCode);
        Assert.AreEqual(DogdouSpec.Core.Progression.ProgressionActionCategories.WaitExternal, nextRes.ActionCategory);
    }

    [TestMethod]
    public void Baseline_Reproduce_FixedVcsPassed_InNonGitWorkspace()
    {
        var (vcsOk, vcsRes, vcsDiags) = WorkspaceVcsStatus.CheckStatus(_workspaceRoot);
        Assert.IsTrue(vcsOk);
        Assert.IsNotNull(vcsRes);
        Assert.IsFalse(vcsRes.IsGitRepository);
        Assert.IsFalse(vcsRes.IsTransportReady);

        var (readinessOk, readinessRes, readinessDiags) = IterationReadiness.Assess(
            _workspaceRoot,
            TestIterationId,
            "completion");

        Assert.IsTrue(readinessOk);
        Assert.IsNotNull(readinessRes);
        var vcsDim = readinessRes.Dimensions.FirstOrDefault(d => d.Name == "vcs_checkpoint");
        Assert.IsNotNull(vcsDim);
        // Verified fix: IterationReadiness reports "not-applicable" for non-Git workspace!
        Assert.AreEqual("not-applicable", vcsDim.Status);
        Assert.IsTrue(vcsDim.Message.Contains("Non-Git workspace"));
    }
}
