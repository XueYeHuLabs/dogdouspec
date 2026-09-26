using System.Globalization;
using System.Xml.Linq;
using DogdouSpec.Core.Changes;
using DogdouSpec.Core.Diagnostics;
using DogdouSpec.Core.Formatting;
using DogdouSpec.Core.Iterations;
using DogdouSpec.Core.Tasks;
using DogdouSpec.Core.Time;

namespace DogdouSpec.Core.Tests;

[TestClass]
public sealed class TaskContextCoreTests
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
        _tempDir = Path.Combine(Path.GetTempPath(), "DogdouSpec_ContextTests_" + Guid.NewGuid().ToString("N"));
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

    private string AddTaskWithHistory(string iterId, string taskId, string? dependsOn = null)
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

        // Add history records
        var tasksPath = Path.Combine(_workspace, iterId, "tasks.xml");
        var xdoc = XDocument.Load(tasksPath);
        var rev = int.Parse(xdoc.Root!.Attribute("revision")!.Value, CultureInfo.InvariantCulture);

        for (int i = 1; i <= 5; i++)
        {
            var minute = 20 + i;
            var opId = $"20260824T09{minute:D2}00Z-disc-{Guid.NewGuid():N}";
            var recId = $"20260824T09{minute:D2}00Z-rec-{Guid.NewGuid():N}";
            var xml = $"""
<?xml version="1.0" encoding="utf-8"?>
<task-update id="{opId}" actor="codex" occurred_at="2026-08-24T09:{minute:D2}:00Z">
  <records>
    <record id="{recId}" kind="discussion" status="informational" created_at="2026-08-24T09:{minute:D2}:00Z" actor="codex" operation_id="{opId}">
      <summary>Historical discussion record {i} for task {taskId}.</summary>
    </record>
  </records>
</task-update>
""";
            var (uOk, _, uDiags) = TaskUpdater.Update(_workspace, iterId, taskId, rev, xml);
            Assert.IsTrue(uOk, string.Join(", ", uDiags.Select(d => d.Message)));
            rev++;
        }

        return taskId;
    }

    [TestMethod]
    public void TaskContext_DemoTask_ProducesCompleteTraceableEssentialContext()
    {
        _workspace = CreateWorkspaceCopy();
        var iterId = "20260823-xpath-core";
        var taskId = "20260823-task-xpath-projection";

        var (ok, result, diags) = TaskContextQuery.Query(_workspace, taskId, iterId);
        Assert.IsTrue(ok, string.Join(", ", diags.Select(d => d.Message)));
        Assert.IsNotNull(result);

        // Core identifiers
        Assert.AreEqual(iterId, result.IterationId);
        Assert.AreEqual(taskId, result.TaskId);
        Assert.AreEqual("in-progress", result.TaskStatus);
        Assert.AreEqual("codex", result.Agent);

        // Essential context elements
        Assert.IsFalse(string.IsNullOrWhiteSpace(result.Title));
        Assert.IsFalse(string.IsNullOrWhiteSpace(result.Objective));
        Assert.IsFalse(string.IsNullOrWhiteSpace(result.Rationale));
        Assert.IsNotNull(result.ScopeElement);
        Assert.IsTrue(result.Constraints.Count > 0);
        Assert.IsTrue(result.OriginRequirements.Count > 0);
        Assert.IsTrue(result.AcceptanceCriteria.Count > 0);
        Assert.IsTrue(result.Dependencies.Count > 0);

        // Progression facts
        Assert.IsFalse(string.IsNullOrWhiteSpace(result.ActionCategory));
        Assert.IsFalse(string.IsNullOrWhiteSpace(result.ReasonCode));
        Assert.IsFalse(string.IsNullOrWhiteSpace(result.RecommendedAction));
        Assert.IsTrue(result.PermittedActions.Count > 0);
        Assert.IsTrue(result.ProhibitedActions.Count > 0);
        Assert.IsTrue(result.AuthorityBoundaries.Count > 0);

        // Traceability
        Assert.IsTrue(result.TasksRevision > 0);
        Assert.IsTrue(result.SpecRevision > 0);
        Assert.AreEqual(2, result.SourceDocuments.Count);

        // Output formatting
        var xml = result.ToXmlString();
        StringAssert.Contains(xml, "<essential>");
        StringAssert.Contains(xml, "<progression-facts>");
        StringAssert.Contains(xml, "<sources>");

        var human = result.ToHumanString();
        StringAssert.Contains(human, "=== Essential Context ===");
        StringAssert.Contains(human, "Progression Assessment:");
    }

    [TestMethod]
    public void TaskContext_BudgetTruncation_TruncatesSupplementalHistoryWithOmittedCountAndLocator()
    {
        var iterId = "20260824-ctx-trunc";
        InitWorkspaceWithFeatureIteration(iterId);
        var taskId = AddTaskWithHistory(iterId, "20260824-task-trunc");

        // First query with unlimited budget to measure total size
        var (fullOk, fullResult, _) = TaskContextQuery.Query(_workspace, taskId, iterId, maxBytes: 65536);
        Assert.IsTrue(fullOk);
        Assert.IsFalse(fullResult!.Truncated);
        Assert.AreEqual(0, fullResult.OmittedRecords);
        Assert.IsTrue(fullResult.IncludedSupplementalRecords.Count >= 5);

        // Query with a tight budget that only fits essential plus 1-2 records
        // Total full size is around ~3500 bytes, essential is ~1800 bytes
        var tightBudget = 2600;
        var (truncOk, truncResult, truncDiags) = TaskContextQuery.Query(_workspace, taskId, iterId, maxBytes: tightBudget);
        Assert.IsTrue(truncOk, string.Join(", ", truncDiags.Select(d => d.Message)));
        Assert.IsNotNull(truncResult);

        // Supplemental history must be truncated with metadata
        Assert.IsTrue(truncResult.Truncated);
        Assert.IsTrue(truncResult.OmittedRecords > 0);
        Assert.IsTrue(truncResult.TotalBytes <= tightBudget);
        Assert.IsFalse(string.IsNullOrEmpty(truncResult.QueryLocator));
        StringAssert.Contains(truncResult.QueryLocator, "dogdouspec query");
        StringAssert.Contains(truncResult.QueryLocator, taskId);

        // Essential context remains 100% complete and intact
        Assert.IsFalse(string.IsNullOrWhiteSpace(truncResult.Title));
        Assert.IsFalse(string.IsNullOrWhiteSpace(truncResult.Objective));
    }

    [TestMethod]
    public void TaskContext_EssentialExceedsLimit_FailsClosedWithLimitExceeded()
    {
        _workspace = CreateWorkspaceCopy();
        var iterId = "20260823-xpath-core";
        var taskId = "20260823-task-xpath-projection";

        // Extremely low byte bound (e.g. 200 bytes) which cannot fit essential context
        var (ok, result, diags) = TaskContextQuery.Query(_workspace, taskId, iterId, maxBytes: 200);
        Assert.IsFalse(ok);
        Assert.IsNull(result);
        Assert.IsTrue(diags.Any(d => d.Code == DiagnosticCodes.LimitExceeded));
    }

    [TestMethod]
    public void TaskContext_MissingTaskOrReferences_FailsWithResourceNotFound()
    {
        _workspace = CreateWorkspaceCopy();
        var iterId = "20260823-xpath-core";

        // Non-existent task
        var (tOk, _, tDiags) = TaskContextQuery.Query(_workspace, "20260823-task-nonexistent", iterId);
        Assert.IsFalse(tOk);
        Assert.IsTrue(tDiags.Any(d => d.Code == DiagnosticCodes.ResourceNotFound));

        // Invalid max-bytes
        var (mOk, _, mDiags) = TaskContextQuery.Query(_workspace, "20260823-task-xpath-projection", iterId, maxBytes: -1);
        Assert.IsFalse(mOk);
        Assert.IsTrue(mDiags.Any(d => d.Code == DiagnosticCodes.InvalidArgument));
    }

    [TestMethod]
    public void TaskContext_TerminalTask_ReportsTerminalStateAndProhibitions()
    {
        _workspace = CreateWorkspaceCopy();
        var iterId = "20260823-xpath-core";
        var taskId = "20260823-task-iteration-layout"; // status: done

        var (ok, result, diags) = TaskContextQuery.Query(_workspace, taskId, iterId);
        Assert.IsTrue(ok, string.Join(", ", diags.Select(d => d.Message)));
        Assert.IsNotNull(result);

        Assert.AreEqual("done", result.TaskStatus);
        Assert.IsTrue(result.ProhibitedActions.Any(a => a.Contains("terminal", StringComparison.OrdinalIgnoreCase) || a.Contains("immutable", StringComparison.OrdinalIgnoreCase)));
    }

    [TestMethod]
    public void TaskContext_ScopeHumanRendering_IncludesRepositoryAndIncludesAndExcludes()
    {
        _workspace = CreateWorkspaceCopy();
        var iterId = "20260823-xpath-core";
        var taskId = "20260823-task-xpath-projection";

        var (ok, result, diags) = TaskContextQuery.Query(_workspace, taskId, iterId);
        Assert.IsTrue(ok, string.Join(", ", diags.Select(d => d.Message)));
        Assert.IsNotNull(result);

        var human = result.ToHumanString();
        StringAssert.Contains(human, "Scope:");
        StringAssert.Contains(human, "Repository: .");
        StringAssert.Contains(human, "Includes:");
        StringAssert.Contains(human, "+ src/DogdouSpec.Core/**");

        var xml = result.ToXmlString();
        StringAssert.Contains(xml, "<scope>");
        StringAssert.Contains(xml, "<repository path=\".\">");
        StringAssert.Contains(xml, "<include path=\"src/DogdouSpec.Core/**\"");
    }

    private string AddActiveTask(string iterId, string taskId)
    {
        var input = new QuickTaskInput(
            Title: $"Task {taskId}",
            Scopes: new List<string> { "src/**" },
            DoneWhen: "Criterion verified",
            Why: "Task objective",
            Origins: Array.Empty<string>(),
            Dependencies: Array.Empty<string>(),
            Terms: new List<string> { "component=core" },
            IterationId: iterId,
            ExpectedRevision: null,
            Start: true,
            DryRun: false,
            TaskId: taskId,
            OperationId: $"20260824T091000Z-op-{Guid.NewGuid():N}");

        var (success, _, _, diags) = TaskQuick.Create(_workspace, input);
        Assert.IsTrue(success, string.Join(", ", diags.Select(d => d.Message)));
        return taskId;
    }

    [TestMethod]
    public void TaskContext_BlockedTask_DerivesPermittedAndProhibitedActionsFromGates()
    {
        var iterId = "20260824-ctx-gates";
        _workspace = CreateWorkspaceCopy();
        var clock = new TestClock(new DateTime(2026, 8, 24, 9, 0, 0, DateTimeKind.Utc));
        var (iSuccess, _, iDiags) = IterationCreator.Create(_workspace, iterId, "feature", clock, activate: true, criteria: DefaultFeatureCriteria);
        Assert.IsTrue(iSuccess, string.Join(", ", iDiags.Select(d => d.Message)));

        var taskId = AddActiveTask(iterId, "20260824-task-gates");
        clock.Advance(TimeSpan.FromMinutes(10));

        // Block the task
        var (bOk, _, bDiags) = TaskBlock.Block(_workspace, taskId, iterId, summary: "Waiting on external dependency", clock: clock);
        Assert.IsTrue(bOk, string.Join(", ", bDiags.Select(d => d.Message)));

        var (ok, result, diags) = TaskContextQuery.Query(_workspace, taskId, iterId);
        Assert.IsTrue(ok, string.Join(", ", diags.Select(d => d.Message)));
        Assert.IsNotNull(result);

        Assert.AreEqual("blocked", result.TaskStatus);
        // Permitted actions must include task resume
        Assert.IsTrue(result.PermittedActions.Any(a => a.Contains("task resume", StringComparison.OrdinalIgnoreCase)));
        // Prohibited actions must prohibit transition without resolving blockers
        Assert.IsTrue(result.ProhibitedActions.Any(a => a.Contains("blocked", StringComparison.OrdinalIgnoreCase) || a.Contains("blocker", StringComparison.OrdinalIgnoreCase)));
        // Progression action category must reflect blocked
        Assert.AreEqual("wait-external", result.ActionCategory);
        Assert.AreEqual("TASKS_BLOCKED", result.ReasonCode);
    }
}
