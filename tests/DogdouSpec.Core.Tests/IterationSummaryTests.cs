using DogdouSpec.Core.Diagnostics;
using DogdouSpec.Core.Formatting;
using DogdouSpec.Core.Iterations;
using DogdouSpec.Core.Reporting;
using DogdouSpec.Core.Tasks;
using DogdouSpec.Core.Workspace;

namespace DogdouSpec.Core.Tests;

[TestClass]
public sealed class IterationSummaryTests
{
    private string _tempDir = null!;
    private string _wsRoot = null!;

    [TestInitialize]
    public void SetUp()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "DogdouSpec_SummaryTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        var (_, wsRoot, _) = WorkspaceInitializer.Initialize(_tempDir, _tempDir);
        _wsRoot = wsRoot;
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
    public void IterationSummary_GeneratesAccurateProgressAndBreakdown()
    {
        var iterId = "20260831-feature-test";
        var (createSuccess, _, _) = IterationCreator.Create(_wsRoot, iterId, "feature", activate: true);
        Assert.IsTrue(createSuccess);

        // Add 2 tasks
        var input1 = new QuickTaskInput("Task One", new List<string> { "src/**" }, "Done When One", "Why One",
            Array.Empty<string>(), Array.Empty<string>(), new List<string> { "component=core" }, iterId, 1, true, false,
            $"{iterId}-01", "20260831T120000Z-task-one");
        var (t1Success, _, _, _) = TaskQuick.Create(_wsRoot, input1);
        Assert.IsTrue(t1Success);

        var input2 = new QuickTaskInput("Task Two", new List<string> { "src/**" }, "Done When Two", "Why Two",
            Array.Empty<string>(), Array.Empty<string>(), new List<string> { "component=core" }, iterId, 2, false, false,
            $"{iterId}-02", "20260831T120001Z-task-two");
        var (t2Success, _, _, _) = TaskQuick.Create(_wsRoot, input2);
        Assert.IsTrue(t2Success);

        var (summarySuccess, summaryResult, diags) = IterationSummaryGenerator.Generate(_wsRoot, iterId);
        Assert.IsTrue(summarySuccess);
        Assert.IsNotNull(summaryResult);
        Assert.AreEqual(0, diags.Count);

        var s = summaryResult.Summary;
        Assert.AreEqual(iterId, s.IterationId);
        Assert.AreEqual("active", s.Status);
        Assert.AreEqual(2, s.TotalTasks);
        Assert.AreEqual(1, s.InProgressTasks);
        Assert.AreEqual(1, s.PendingTasks);
        Assert.AreEqual(0, s.DoneTasks);
        Assert.AreEqual(0.0, s.ProgressPercentage);

        // Verify Markdown output contains progress bar and breakdown
        var md = summaryResult.ToMarkdownString();
        Assert.IsTrue(md.Contains("### 🚀 Iteration Progress: `20260831-feature-test` (feature)"));
        Assert.IsTrue(md.Contains("Progress: [░░░░░░░░░░] 0.0% (0/2 tasks completed)"));
        Assert.IsTrue(md.Contains("Task One"));
        Assert.IsTrue(md.Contains("Task Two"));

        // Verify JSON output
        var json = summaryResult.ToJsonString();
        Assert.IsTrue(json.Contains("\"iteration\": \"20260831-feature-test\""));
        Assert.IsTrue(json.Contains("\"total\": 2"));

        // Verify XML output
        var xml = summaryResult.ToXmlString();
        Assert.IsTrue(xml.Contains("<iteration-summary iteration=\"20260831-feature-test\""));
        Assert.IsTrue(xml.Contains("progress_percentage=\"0.0\""));

        // Verify Human output
        var human = summaryResult.ToHumanString();
        Assert.IsTrue(human.Contains("Iteration: 20260831-feature-test"));
        Assert.IsTrue(human.Contains("Progress:  [----------] 0.0%"));
    }

    [TestMethod]
    public void IterationSummary_PendingGates_OnlyIncludesExplicitPendingCriteria()
    {
        var iterId = "20260831-criteria-test";
        var (createSuccess, _, _) = IterationCreator.Create(_wsRoot, iterId, "feature", activate: true);
        Assert.IsTrue(createSuccess);

        // Modify spec.xml to add one criterion without decision, and one with decision="pending"
        var specPath = Path.Combine(_wsRoot, iterId, "spec.xml");
        var xDoc = System.Xml.Linq.XDocument.Load(specPath);
        var specElem = xDoc.Root!;
        var acceptanceElem = specElem.Element("acceptance") ?? specElem.Element("product")?.Element("acceptance");
        if (acceptanceElem == null)
        {
            acceptanceElem = new System.Xml.Linq.XElement("acceptance");
            specElem.Add(acceptanceElem);
        }
        else
        {
            acceptanceElem.RemoveAll();
        }

        acceptanceElem.Add(new System.Xml.Linq.XElement("criterion",
            new System.Xml.Linq.XAttribute("id", "20260831T120000Z-crit-plain"),
            new System.Xml.Linq.XElement("statement", "Plain criterion without decision attribute")));

        acceptanceElem.Add(new System.Xml.Linq.XElement("criterion",
            new System.Xml.Linq.XAttribute("id", "20260831T120001Z-crit-pending"),
            new System.Xml.Linq.XAttribute("decision", "pending"),
            new System.Xml.Linq.XElement("statement", "Pending criterion requiring decision")));

        xDoc.Save(specPath);

        var (summarySuccess, summaryResult, diags) = IterationSummaryGenerator.Generate(_wsRoot, iterId);
        Assert.IsTrue(summarySuccess);
        Assert.IsNotNull(summaryResult);

        var gates = summaryResult.Summary.PendingGates;
        // Only the one with decision="pending" should be in gates
        Assert.AreEqual(1, gates.Count(g => g.Kind == "acceptance"));
        Assert.AreEqual("20260831T120001Z-crit-pending", gates.First(g => g.Kind == "acceptance").Id);
    }

    [TestMethod]
    public void IterationSummary_UnrecognizedTaskStatus_EmitsWarningDiagnostic_AndTreatsAsActive()
    {
        var iterId = "20260831-unknown-status";
        var (createSuccess, _, _) = IterationCreator.Create(_wsRoot, iterId, "feature", activate: true);
        Assert.IsTrue(createSuccess);

        var input = new QuickTaskInput("Task One", new List<string> { "src/**" }, "Done", "Why",
            Array.Empty<string>(), Array.Empty<string>(), new List<string> { "component=core" }, iterId, 1, true, false,
            $"{iterId}-01", "20260831T120000Z-task-one");
        TaskQuick.Create(_wsRoot, input);

        // Manually alter task status to an unknown status
        var tasksPath = Path.Combine(_wsRoot, iterId, "tasks.xml");
        var xDoc = System.Xml.Linq.XDocument.Load(tasksPath);
        var taskElem = xDoc.Root!.Element("task")!;
        taskElem.SetAttributeValue("status", "unknown_typo_status");
        xDoc.Save(tasksPath);

        var (summarySuccess, summaryResult, diags) = IterationSummaryGenerator.Generate(_wsRoot, iterId);
        Assert.IsTrue(summarySuccess);
        Assert.IsNotNull(summaryResult);
        Assert.AreEqual(1, diags.Count);
        Assert.AreEqual(DiagnosticCodes.SchemaValidationError, diags[0].Code);
        Assert.AreEqual("warning", diags[0].Severity);

        // Inactive tasks should be 0 (not silently absorbed)
        Assert.AreEqual(0, summaryResult.Summary.InactiveTasks);
        Assert.AreEqual(1, summaryResult.Summary.PendingTasks);
    }

    [TestMethod]
    public void IterationSummary_Markdown_RendersInactiveAndUnknownTasks_Consistently()
    {
        var iterId = "20260831-md-status";
        var (createSuccess, _, _) = IterationCreator.Create(_wsRoot, iterId, "feature", activate: true);
        Assert.IsTrue(createSuccess);

        var input1 = new QuickTaskInput("Task Cancelled", new List<string> { "src/**" }, "Done", "Why",
            Array.Empty<string>(), Array.Empty<string>(), new List<string> { "component=core" }, iterId, 1, true, false,
            $"{iterId}-01", "20260831T120000Z-task-one");
        TaskQuick.Create(_wsRoot, input1);

        var input2 = new QuickTaskInput("Task Unknown", new List<string> { "src/**" }, "Done", "Why",
            Array.Empty<string>(), Array.Empty<string>(), new List<string> { "component=core" }, iterId, 2, true, false,
            $"{iterId}-02", "20260831T120001Z-task-two");
        TaskQuick.Create(_wsRoot, input2);

        var tasksPath = Path.Combine(_wsRoot, iterId, "tasks.xml");
        var xDoc = System.Xml.Linq.XDocument.Load(tasksPath);
        var taskElems = xDoc.Root!.Elements("task").ToList();
        taskElems[0].SetAttributeValue("status", "cancelled");
        taskElems[1].SetAttributeValue("status", "mysterious_status");
        xDoc.Save(tasksPath);

        var (summarySuccess, summaryResult, _) = IterationSummaryGenerator.Generate(_wsRoot, iterId);
        Assert.IsTrue(summarySuccess);
        Assert.IsNotNull(summaryResult);

        var md = summaryResult.ToMarkdownString();
        Assert.IsTrue(md.Contains("Inactive / Disposed (1)", StringComparison.Ordinal));
        Assert.IsTrue(md.Contains("`20260831-md-status-01` (`cancelled`)", StringComparison.Ordinal));
        Assert.IsTrue(md.Contains("Pending / Next (1)", StringComparison.Ordinal));
        Assert.IsTrue(md.Contains("`20260831-md-status-02`", StringComparison.Ordinal));
    }
}
