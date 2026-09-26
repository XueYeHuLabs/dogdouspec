using System.Xml.Linq;
using DogdouSpec.Core.Changes;
using DogdouSpec.Core.Diagnostics;
using DogdouSpec.Core.Iterations;
using DogdouSpec.Core.Requirements;
using DogdouSpec.Core.Tasks;
using DogdouSpec.Core.Time;
using DogdouSpec.Core.Transactions;
using DogdouSpec.Core.Validation;
using DogdouSpec.Core.XPath;

namespace DogdouSpec.Core.Tests;

[TestClass]
public sealed class TaskChangeWorkflowTests
{
    private static string RepoRoot = null!;
    private string _tempDir = null!;
    private string _workspace = null!;

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
        _tempDir = Path.Combine(Path.GetTempPath(), "DogdouSpec_WorkflowTests_" + Guid.NewGuid().ToString("N"));
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

    private static readonly string[] DefaultFeatureCriteria = new[] { "Workflow integration verified." };

    private void InitWorkspaceWithFeatureIteration(string iterId = "20260824-test-feature", IEnumerable<string>? criteria = null)
    {
        _workspace = CreateWorkspaceCopy();
        var clock = new TestClock(new DateTime(2026, 8, 24, 9, 0, 0, DateTimeKind.Utc));
        criteria ??= DefaultFeatureCriteria;
        var (iSuccess, _, iDiags) = IterationCreator.Create(_workspace, iterId, "feature", clock, criteria: criteria);
        Assert.IsTrue(iSuccess, $"Iteration create failed: {string.Join(", ", iDiags.Select(d => d.Message))}");
    }

    [TestMethod]
    public void TaskQuick_OperationalPendingAndDryRun_UseNormalTaskRepresentation()
    {
        var iterId = "20260824-quick-feature";
        InitWorkspaceWithFeatureIteration(iterId);
        var input = new QuickTaskInput("Quick maintenance", new List<string> { "src/**" }, "maintenance is verified", "bounded operational work",
            Array.Empty<string>(), Array.Empty<string>(), new List<string> { "component=core" }, iterId, 1, false, false,
            "20260824-task-quick-maintenance", "20260824T120000Z-quick-maintenance");
        var (success, result, envelope, diagnostics) = TaskQuick.Create(_workspace, input);
        Assert.IsTrue(success, string.Join(", ", diagnostics.Select(d => d.Message)));
        Assert.IsNotNull(envelope);
        Assert.AreEqual(2, envelope.Documents.Single().Revision);
        var task = XDocument.Load(Path.Combine(_workspace, iterId, "tasks.xml")).Root!.Elements("task").Single();
        Assert.AreEqual("pending", (string?)task.Attribute("status"));
        Assert.AreEqual(iterId, (string?)task.Element("origin")?.Element("ref")?.Attribute("target"));
        Assert.AreEqual("supports", (string?)task.Element("origin")?.Element("ref")?.Attribute("relation"));

        var before = File.ReadAllBytes(Path.Combine(_workspace, iterId, "tasks.xml"));
        var dry = input with { DryRun = true, ExpectedRevision = null, TaskId = "20260824-task-quick-preview", OperationId = "20260824T120001Z-quick-preview" };
        var (drySuccess, dryResult, dryEnvelope, dryDiags) = TaskQuick.Create(_workspace, dry);
        Assert.IsTrue(drySuccess, string.Join(", ", dryDiags.Select(d => d.Message)));
        Assert.IsNotNull(dryEnvelope);
        Assert.IsNotNull(dryResult);
        CollectionAssert.AreEqual(before, File.ReadAllBytes(Path.Combine(_workspace, iterId, "tasks.xml")));

        var badOperation = input with { DryRun = true, TaskId = "20260824-task-quick-bad-op", OperationId = "20260824-quick-bad-op" };
        var (badSuccess, _, _, badDiags) = TaskQuick.Create(_workspace, badOperation);
        Assert.IsFalse(badSuccess);
        Assert.IsTrue(badDiags.Any(d => d.Code == DiagnosticCodes.InvalidArgument));
    }

    [TestMethod]
    public void TaskQuick_DryRunDuplicateTaskMatchesWriteAndLeavesWorkspaceUntouched()
    {
        var iterId = "20260824-quick-duplicate";
        InitWorkspaceWithFeatureIteration(iterId);
        var first = new QuickTaskInput("First quick", new List<string> { "src/**" }, "done", "reason", Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>(), iterId, 1, false, false, "20260824-task-quick-duplicate", "20260824T140000Z-quick-first");
        Assert.IsTrue(TaskQuick.Create(_workspace, first).Success);
        var tasksPath = Path.Combine(_workspace, iterId, "tasks.xml");
        var before = File.ReadAllBytes(tasksPath);
        var tempPath = Path.Combine(_workspace, "_tmp");
        var tempBefore = Directory.Exists(tempPath) ? Directory.GetFileSystemEntries(tempPath, "*", SearchOption.AllDirectories).Order().ToArray() : Array.Empty<string>();
        var conflicting = first with { Title = "Conflicting quick", ExpectedRevision = null, OperationId = "20260824T140001Z-quick-conflict", DryRun = true };
        var preview = TaskQuick.Create(_workspace, conflicting);
        Assert.IsFalse(preview.Success);
        Assert.AreEqual(DiagnosticCodes.DuplicateId, preview.Diagnostics.Single().Code);
        CollectionAssert.AreEqual(before, File.ReadAllBytes(tasksPath));
        var tempAfterPreview = Directory.Exists(tempPath) ? Directory.GetFileSystemEntries(tempPath, "*", SearchOption.AllDirectories).Order().ToArray() : Array.Empty<string>();
        CollectionAssert.AreEqual(tempBefore, tempAfterPreview);
        var write = TaskQuick.Create(_workspace, conflicting with { DryRun = false });
        Assert.IsFalse(write.Success);
        Assert.AreEqual(preview.Diagnostics.Single().Code, write.Diagnostics.Single().Code);
        CollectionAssert.AreEqual(before, File.ReadAllBytes(tasksPath));
    }

    [TestMethod]
    public void ProtectedStateGuard_LowLevelNewInProgressTaskIsRejected()
    {
        var before = XDocument.Parse("<tasks id='20260824-guard' iteration='20260824-guard' schema_version='1.0' revision='1'><index><summary>x</summary></index></tasks>");
        var after = new XDocument(before);
        after.Root!.Add(new XElement("task", new XAttribute("id", "20260824-task-guard"), new XAttribute("status", "in-progress")));
        var diagnostic = ProtectedStateGuard.CheckProtectedState("20260824-guard/tasks.xml", before, after);
        Assert.IsNotNull(diagnostic);
        Assert.AreEqual(DiagnosticCodes.OwnerDecisionRequired, diagnostic.Code);
        after.Root!.Element("task")!.SetAttributeValue("status", "pending");
        Assert.IsNull(ProtectedStateGuard.CheckProtectedState("20260824-guard/tasks.xml", before, after));

        var existingBefore = new XDocument(after);
        after.Root!.Element("task")!.SetAttributeValue("status", "in-progress");
        var transition = ProtectedStateGuard.CheckProtectedState("20260824-guard/tasks.xml", existingBefore, after);
        Assert.IsNotNull(transition);
        Assert.AreEqual(DiagnosticCodes.TaskTransitionConflict, transition.Code);
        after.Root!.Element("task")!.SetAttributeValue("status", "pending");
        after.Root!.Element("task")!.Add(new XElement("records", new XElement("record", new XAttribute("id", "20260824-record-guard"))));
        Assert.IsNull(ProtectedStateGuard.CheckProtectedState("20260824-guard/tasks.xml", existingBefore, after));
        var removed = new XDocument(existingBefore);
        removed.Root!.Element("task")!.Remove();
        var removal = ProtectedStateGuard.CheckProtectedState("20260824-guard/tasks.xml", existingBefore, removed);
        Assert.IsNotNull(removal);
        Assert.AreEqual(DiagnosticCodes.TaskTransitionConflict, removal.Code);
    }

    [TestMethod]
    public void NewHelpers_RejectOversizedInputAndBackdatedOrUnstampedTasks()
    {
        var iterId = "20260824-test-feature";
        InitWorkspaceWithFeatureIteration(iterId);

        var oversized = new string('x', XPathQueryLimits.MaxDocumentBytes + 1);
        var oversizedResult = TaskAdder.Add(_workspace, iterId, 1, oversized);
        Assert.IsFalse(oversizedResult.Success);
        Assert.IsTrue(oversizedResult.Diagnostics.Any(d => d.Code == DiagnosticCodes.LimitExceeded));

        var badStamp = """
<task-add id="20260824T085900Z-taskadd-bad-stamp" actor="codex" occurred_at="2026-08-24T08:59:00Z"><task id="20260824-task-bad-stamp" status="pending" created_at="2026-08-24T08:58:59Z" updated_at="2026-08-24T08:59:00Z"><index><summary>Bad stamp.</summary></index><title>Bad stamp</title><objective>Reject timestamp mismatch.</objective><rationale>Test.</rationale><scope><repository path="src/test.cs"/></scope><origin><ref scope="iteration" target="20260824-req-test-feature" relation="implements"/></origin><constraints/><acceptance><criterion id="20260824-crit-bad-stamp" status="pending">No.</criterion></acceptance><context><summary>Test.</summary></context><records/></task></task-add>
""";
        var badStampResult = TaskAdder.Add(_workspace, iterId, 1, badStamp);
        Assert.IsFalse(badStampResult.Success);
        Assert.IsTrue(badStampResult.Diagnostics.Any(d => d.Code == DiagnosticCodes.InvalidArgument));

        var validStamp = badStamp.Replace("2026-08-24T08:58:59Z", "2026-08-24T08:59:00Z", StringComparison.Ordinal);
        var added = TaskAdder.Add(_workspace, iterId, 1, validStamp);
        Assert.IsTrue(added.Success, string.Join(", ", added.Diagnostics.Select(d => d.Message)));

        var addedTask = XDocument.Load(Path.Combine(_workspace, iterId, "tasks.xml"))
            .Root!
            .Elements("task")
            .Single(t => (string?)t.Attribute("id") == "20260824-task-bad-stamp");
        Assert.AreEqual("2026-08-24T08:59:00Z", (string?)addedTask.Attribute("created_at"));
        Assert.AreEqual("2026-08-24T08:59:00Z", (string?)addedTask.Attribute("updated_at"));
        Assert.IsNull(addedTask.Attribute("started_at"));
        Assert.IsNull(addedTask.Attribute("completed_at"));

        var backdatedRevise = """
<task-revise id="20260824T085859Z-taskrevise-backdated" actor="codex" occurred_at="2026-08-24T08:58:59Z"><records><record id="20260824T085859Z-rec-backdated" kind="discussion" status="informational" created_at="2026-08-24T08:58:59Z" actor="codex"><summary>Backdated.</summary></record></records></task-revise>
""";
        var backdatedResult = TaskReviser.Revise(_workspace, iterId, "20260824-task-bad-stamp", 2, backdatedRevise);
        Assert.IsFalse(backdatedResult.Success);
        Assert.IsTrue(backdatedResult.Diagnostics.Any(d => d.Code == DiagnosticCodes.InvalidArgument));

        var tasksPath = Path.Combine(_workspace, iterId, "tasks.xml");
        File.AppendAllText(tasksPath, new string(' ', XPathQueryLimits.MaxDocumentBytes + 1));
        var revise = """
<task-revise id="20260824T090000Z-taskrevise-limit" actor="codex" occurred_at="2026-08-24T09:00:00Z"><records><record id="20260824T090000Z-rec-limit" kind="discussion" status="informational" created_at="2026-08-24T09:00:00Z" actor="codex"><summary>Limit.</summary></record></records></task-revise>
""";
        var oversizedDocumentResult = TaskReviser.Revise(_workspace, iterId, "20260824-task-missing", 1, revise);
        Assert.IsFalse(oversizedDocumentResult.Success);
        Assert.IsTrue(oversizedDocumentResult.Diagnostics.Any(d => d.Code == DiagnosticCodes.LimitExceeded));
    }

    [TestMethod]
    public void TaskAdd_HappyPath_AddsPendingTaskAndIncrementsRevision()
    {
        var iterId = "20260824-test-feature";
        InitWorkspaceWithFeatureIteration(iterId);

        var requestXml = $"""
<task-add
  id="20260824T090000Z-taskadd-01"
  actor="codex"
  occurred_at="2026-08-24T09:00:00Z">
  <task
    id="20260824-task-feature-impl"
    status="pending"
    created_at="2026-08-24T09:00:00Z"
    updated_at="2026-08-24T09:00:00Z">
    <index>
      <summary>Feature Implementation Task Summary.</summary>
    </index>
    <title>Feature Implementation Task</title>
    <objective>Implement feature completely.</objective>
    <rationale>Implements feature req.</rationale>
    <scope>
      <repository path="src/main.cs"/>
    </scope>
    <origin>
      <ref scope="iteration" target="20260824-req-test-feature" relation="implements"/>
    </origin>
    <constraints/>
    <acceptance>
      <criterion id="20260824-crit-impl" status="pending">
        Implementation passes tests.
      </criterion>
    </acceptance>
    <context>
      <summary>Initial technical context.</summary>
    </context>
    <records>
      <record
        id="20260824T090000Z-rec-add-initial"
        kind="discussion"
        status="informational"
        created_at="2026-08-24T09:00:00Z"
        actor="codex">
        <summary>Task created.</summary>
      </record>
    </records>
  </task>
</task-add>
""";

        var (success, env, diags) = TaskAdder.Add(_workspace, iterId, 1, requestXml);
        Assert.IsTrue(success, $"Add failed: {string.Join(", ", diags.Select(d => d.Message))}");
        Assert.IsNotNull(env);
        Assert.IsFalse(env.AlreadyApplied);

        // Verify task exists in tasks.xml
        var tasksXmlPath = Path.Combine(_workspace, iterId, "tasks.xml");
        var tasksDoc = XDocument.Load(tasksXmlPath);
        var addedTask = tasksDoc.Descendants("task").FirstOrDefault(t => (string?)t.Attribute("id") == "20260824-task-feature-impl");
        Assert.IsNotNull(addedTask);
        Assert.AreEqual("pending", (string?)addedTask.Attribute("status"));

        // Idempotent retry
        var (retrySuccess, retryEnv, retryDiags) = TaskAdder.Add(_workspace, iterId, 1, requestXml);
        Assert.IsTrue(retrySuccess, $"Retry failed: {string.Join(", ", retryDiags.Select(d => d.Message))}");
        Assert.IsNotNull(retryEnv);
        Assert.IsTrue(retryEnv.AlreadyApplied);

        AssertDryRunReplayBlocked(
            _workspace,
            "task add",
            () => TaskAdder.Add(_workspace, iterId, 1, requestXml, dryRun: true));
    }

    [TestMethod]
    public void TaskAdd_OperationalOrigin_FromTaskQuickDryRun_SucceedsUnmodified()
    {
        var iterId = "20260824-test-feature";
        InitWorkspaceWithFeatureIteration(iterId);

        var quickInput = new QuickTaskInput(
            "Composed operational task",
            new List<string> { "src/**" },
            "operational task completed",
            "test composition across commands",
            Array.Empty<string>(),
            Array.Empty<string>(),
            new List<string> { "kind=quick" },
            iterId,
            1,
            false,
            true,
            "20260824-task-quick-op-dry",
            "20260824T120000Z-quick-op-dry");

        var (drySuccess, dryResult, _, dryDiags) = TaskQuick.Create(_workspace, quickInput);
        Assert.IsTrue(drySuccess, $"task quick dry-run failed: {string.Join(", ", dryDiags.Select(d => d.Message))}");
        Assert.IsNotNull(dryResult);
        var requestXml = dryResult.RequestXml;

        // Submit the dry-run output completely unmodified through TaskAdder.Add
        var (addSuccess, addEnv, addDiags) = TaskAdder.Add(_workspace, iterId, 1, requestXml);
        Assert.IsTrue(addSuccess, $"task add failed with unmodified operational request: {string.Join(", ", addDiags.Select(d => d.Message))}");
        Assert.IsNotNull(addEnv);
        Assert.IsFalse(addEnv.AlreadyApplied);

        var tasksXmlPath = Path.Combine(_workspace, iterId, "tasks.xml");
        var tasksDoc = XDocument.Load(tasksXmlPath);
        var addedTask = tasksDoc.Descendants("task").SingleOrDefault(t => (string?)t.Attribute("id") == "20260824-task-quick-op-dry");
        Assert.IsNotNull(addedTask);
        Assert.AreEqual("pending", (string?)addedTask.Attribute("status"));

        var originRef = addedTask.Element("origin")?.Elements("ref").SingleOrDefault();
        Assert.IsNotNull(originRef);
        Assert.AreEqual("iteration", (string?)originRef.Attribute("scope"));
        Assert.AreEqual("supports", (string?)originRef.Attribute("relation"));
        Assert.AreEqual(iterId, (string?)originRef.Attribute("target"));

        // Idempotent retry succeeds
        var (retrySuccess, retryEnv, retryDiags) = TaskAdder.Add(_workspace, iterId, 1, requestXml);
        Assert.IsTrue(retrySuccess, $"Retry failed: {string.Join(", ", retryDiags.Select(d => d.Message))}");
        Assert.IsNotNull(retryEnv);
        Assert.IsTrue(retryEnv.AlreadyApplied);
    }

    [TestMethod]
    public void TaskAdd_QuickStartDryRunRequest_SucceedsUnmodifiedAndRejectsImpostors()
    {
        var iterId = "20260824-test-feature";
        _workspace = CreateWorkspaceCopy();
        var (createSuccess, _, createDiags) = IterationCreator.Create(
            _workspace,
            iterId,
            "feature",
            activate: true,
            criteria: DefaultFeatureCriteria);
        Assert.IsTrue(createSuccess, string.Join(", ", createDiags.Select(d => d.Message)));

        var dependencyInput = new QuickTaskInput(
            "Pending dependency",
            new List<string> { "src/dependency/**" },
            "dependency completed",
            "exercise raw quick-start dependency enforcement",
            Array.Empty<string>(),
            Array.Empty<string>(),
            Array.Empty<string>(),
            iterId,
            1,
            false,
            false,
            "20260824-task-quick-start-dependency",
            "20260824T120000Z-quick-start-dependency");
        var (dependencySuccess, _, _, dependencyDiags) = TaskQuick.Create(_workspace, dependencyInput);
        Assert.IsTrue(dependencySuccess, string.Join(", ", dependencyDiags.Select(d => d.Message)));

        var quickInput = new QuickTaskInput(
            "Composed operational start task",
            new List<string> { "src/**" },
            "operational start task completed",
            "test start composition across commands",
            Array.Empty<string>(),
            Array.Empty<string>(),
            Array.Empty<string>(),
            iterId,
            2,
            true,
            true,
            "20260824-task-quick-start-dry",
            "20260824T120100Z-quick-start-dry");

        var (drySuccess, dryResult, _, dryDiags) = TaskQuick.Create(_workspace, quickInput);
        Assert.IsTrue(drySuccess, $"task quick --start dry-run failed: {string.Join(", ", dryDiags.Select(d => d.Message))}");
        Assert.IsNotNull(dryResult);

        var requestXml = dryResult.RequestXml;

        var positionalCompatibility = TaskAdder.Add(
            _workspace,
            iterId,
            0,
            requestXml,
            new TestClock(new DateTime(2026, 8, 24, 12, 1, 0, DateTimeKind.Utc)));
        Assert.IsFalse(positionalCompatibility.Success);
        Assert.IsTrue(positionalCompatibility.Diagnostics.Any(d => d.Code == DiagnosticCodes.InvalidArgument));

        var dependentDocument = XDocument.Parse(requestXml);
        var dependentTask = dependentDocument.Root!.Element("task")!;
        dependentTask.Element("origin")!.AddAfterSelf(
            new XElement(
                "dependencies",
                new XElement(
                    "ref",
                    new XAttribute("scope", "document"),
                    new XAttribute("target", "20260824-task-quick-start-dependency"),
                    new XAttribute("relation", "depends-on"))));
        var (dependentSuccess, _, dependentDiags) = TaskAdder.Add(
            _workspace,
            iterId,
            2,
            dependentDocument.ToString(SaveOptions.DisableFormatting));
        Assert.IsFalse(dependentSuccess, "Raw canonical quick-start must re-evaluate dependencies at execution time.");
        Assert.IsTrue(dependentDiags.Any(d => d.Code == DiagnosticCodes.TaskTransitionConflict));

        var (addSuccess, addEnv, addDiags) = TaskAdder.Add(_workspace, iterId, 2, requestXml);
        Assert.IsTrue(addSuccess, $"task add failed with unmodified quick-start request: {string.Join(", ", addDiags.Select(d => d.Message))}");
        Assert.IsNotNull(addEnv);

        var tasksDoc = XDocument.Load(Path.Combine(_workspace, iterId, "tasks.xml"));
        var addedTask = tasksDoc.Root!.Elements("task").Single(t => (string?)t.Attribute("id") == "20260824-task-quick-start-dry");
        Assert.AreEqual("in-progress", (string?)addedTask.Attribute("status"));
        Assert.AreEqual("2026-08-24T12:01:00Z", (string?)addedTask.Attribute("started_at"));
        var startRecord = addedTask.Element("records")!.Elements("record").Single(r => (string?)r.Attribute("kind") == "start");
        Assert.AreEqual("quick-task", (string?)startRecord.Attribute("actor"));
        Assert.AreEqual("20260824T120100Z-quick-start-dry", (string?)startRecord.Attribute("operation_id"));

        var impostors = new Dictionary<string, Action<XDocument>>
        {
            ["extra record"] = document => document.Root!.Element("task")!.Element("records")!.Add(
                new XElement(
                    "record",
                    new XAttribute("id", "20260824T120100Z-extra-impostor-record"),
                    new XAttribute("kind", "discussion"),
                    new XAttribute("status", "informational"),
                    new XAttribute("created_at", "2026-08-24T12:01:00Z"),
                    new XAttribute("actor", "quick-task"),
                    new XElement("summary", "Noncanonical extra record."))),
            ["repository exclude"] = document => document.Root!.Element("task")!.Element("scope")!.Element("repository")!.Add(
                new XElement("exclude", new XAttribute("path", "src/generated/**"))),
            ["context key points"] = document => document.Root!.Element("task")!.Element("context")!.Add(
                new XElement(
                    "key_points",
                    new XElement(
                        "point",
                        new XAttribute("id", "20260824T120100Z-point-impostor-context"),
                        "TaskQuick cannot generate this context."))),
            ["index summary mismatch"] = document => document.Root!.Element("task")!.Element("index")!.Element("summary")!.Value = "Different generated title."
        };

        foreach (var (name, mutate) in impostors)
        {
            var impostorDocument = XDocument.Parse(requestXml);
            mutate(impostorDocument);
            var impostorXml = impostorDocument.ToString(SaveOptions.DisableFormatting);
            var (impostorSuccess, _, impostorDiags) = TaskAdder.Add(_workspace, iterId, 3, impostorXml);
            Assert.IsFalse(impostorSuccess, $"{name} must not be accepted as canonical TaskQuick output.");
            Assert.IsTrue(
                impostorDiags.Any(d => d.Code == DiagnosticCodes.InvalidArgument),
                $"{name} diagnostics: {string.Join(", ", impostorDiags.Select(d => $"{d.Code}: {d.Message}"))}");
        }
    }

    [TestMethod]
    public void TaskAdd_QuickStart_RechecksAutomaticallyCapturedSpecRevisionUnderWriterLock()
    {
        const string iterId = "20260824-race-feature";
        _workspace = CreateWorkspaceCopy();
        var (createSuccess, _, createDiags) = IterationCreator.Create(
            _workspace,
            iterId,
            "feature",
            activate: true,
            criteria: DefaultFeatureCriteria);
        Assert.IsTrue(createSuccess, string.Join(", ", createDiags.Select(d => d.Message)));

        var quickInput = new QuickTaskInput(
            "Authorization race task",
            new List<string> { "src/**" },
            "authorization remains current through commit",
            "prove automatic spec read capture",
            Array.Empty<string>(),
            Array.Empty<string>(),
            Array.Empty<string>(),
            iterId,
            1,
            true,
            true,
            "20260824-task-quick-start-race",
            "20260824T120200Z-quick-start-race");
        var (drySuccess, dryResult, _, dryDiags) = TaskQuick.Create(_workspace, quickInput);
        Assert.IsTrue(drySuccess, string.Join(", ", dryDiags.Select(d => d.Message)));
        Assert.IsNotNull(dryResult);

        var tasksPath = Path.Combine(_workspace, iterId, "tasks.xml");
        var specPath = Path.Combine(_workspace, iterId, "spec.xml");
        var tasksBefore = File.ReadAllBytes(tasksPath);
        var originalSpecRevision = int.Parse(
            XDocument.Load(specPath).Root!.Attribute("revision")!.Value,
            System.Globalization.CultureInfo.InvariantCulture);
        var injector = new CallbackFaultInjector(
            FaultPhase.AfterRecoveryBeforePreconditionValidation,
            () =>
            {
                var spec = XDocument.Load(specPath);
                spec.Root!.SetAttributeValue("revision", originalSpecRevision + 1);
                spec.Save(specPath);
            });

        var (success, envelope, diagnostics) = TaskAdder.Add(
            _workspace,
            iterId,
            1,
            dryResult.RequestXml,
            faultInjector: injector);

        Assert.IsFalse(success);
        Assert.IsNull(envelope);
        Assert.IsTrue(
            diagnostics.Any(d =>
                d.Code == DiagnosticCodes.RevisionConflict &&
                d.Document == $"{iterId}/spec.xml" &&
                d.ExpectedRevision == originalSpecRevision &&
                d.ActualRevision == originalSpecRevision + 1),
            string.Join(", ", diagnostics.Select(d => $"{d.Code}: {d.Message}")));
        CollectionAssert.AreEqual(tasksBefore, File.ReadAllBytes(tasksPath), "The authorized tasks write must not be staged or published.");

        var tmpPath = Path.Combine(_workspace, "_tmp");
        if (Directory.Exists(tmpPath))
        {
            Assert.AreEqual(
                0,
                Directory.GetFileSystemEntries(tmpPath).Count(path =>
                    !path.EndsWith("writer.lock", StringComparison.OrdinalIgnoreCase)),
                "Revision conflict before precondition validation must not leave transaction artifacts.");
        }
    }

    private sealed class CallbackFaultInjector : IFaultInjector
    {
        private readonly FaultPhase _phase;
        private readonly Action _callback;
        private bool _invoked;

        public CallbackFaultInjector(FaultPhase phase, Action callback)
        {
            _phase = phase;
            _callback = callback;
        }

        public void InjectFaultIfMatched(FaultPhase phase)
        {
            if (!_invoked && phase == _phase)
            {
                _invoked = true;
                _callback();
            }
        }
    }

    [TestMethod]
    public void TaskAdd_OperationalOrigin_WrongTarget_FailsWithUsefulDiagnostics()
    {
        var iterId = "20260824-test-feature";
        InitWorkspaceWithFeatureIteration(iterId);

        var requestXml = $"""
<task-add
  id="20260824T120001Z-taskadd-wrong-target"
  actor="codex"
  occurred_at="2026-08-24T12:00:01Z">
  <task
    id="20260824-task-wrong-target"
    status="pending"
    created_at="2026-08-24T12:00:01Z"
    updated_at="2026-08-24T12:00:01Z">
    <index><summary>Wrong target.</summary></index>
    <title>Wrong Target</title>
    <objective>Fails on wrong target.</objective>
    <rationale>Test wrong target diagnostic.</rationale>
    <scope><repository path="src/test.cs"/></scope>
    <origin>
      <ref scope="iteration" target="20260824-other-iteration" relation="supports"/>
    </origin>
    <constraints/>
    <acceptance><criterion id="20260824-crit-wt" status="pending">Done.</criterion></acceptance>
    <context><summary>Test.</summary></context>
    <records/>
  </task>
</task-add>
""";

        var (success, _, diags) = TaskAdder.Add(_workspace, iterId, 1, requestXml);
        Assert.IsFalse(success);
        var diag = diags.SingleOrDefault(d => d.Code == DiagnosticCodes.InvalidReferenceTargetType);
        Assert.IsNotNull(diag, $"Expected INVALID_REFERENCE_TARGET_TYPE, got: {string.Join(", ", diags.Select(d => $"{d.Code}: {d.Message}"))}");
        Assert.IsTrue(diag.Message.Contains($"target='{iterId}'"), $"Expected target '{iterId}' in message: {diag.Message}");
        Assert.IsTrue(diag.Message.Contains("target='20260824-other-iteration'"), $"Actual target in message: {diag.Message}");
        Assert.IsTrue(diag.Message.Contains("count=1"), $"Count in message: {diag.Message}");
        Assert.IsTrue(diag.Message.Contains("scope='iteration'"), $"Scope in message: {diag.Message}");
        Assert.IsTrue(diag.Message.Contains("relation='supports'"), $"Relation in message: {diag.Message}");
    }

    [TestMethod]
    public void TaskAdd_OperationalOrigin_WrongRelation_FailsWithUsefulDiagnostics()
    {
        var iterId = "20260824-test-feature";
        InitWorkspaceWithFeatureIteration(iterId);

        var requestXml = $"""
<task-add
  id="20260824T120002Z-taskadd-wrong-rel"
  actor="codex"
  occurred_at="2026-08-24T12:00:02Z">
  <task
    id="20260824-task-wrong-rel"
    status="pending"
    created_at="2026-08-24T12:00:02Z"
    updated_at="2026-08-24T12:00:02Z">
    <index><summary>Wrong relation.</summary></index>
    <title>Wrong Relation</title>
    <objective>Fails on wrong relation.</objective>
    <rationale>Test wrong relation diagnostic.</rationale>
    <scope><repository path="src/test.cs"/></scope>
    <origin>
      <ref scope="iteration" target="{iterId}" relation="implements"/>
    </origin>
    <constraints/>
    <acceptance><criterion id="20260824-crit-wr" status="pending">Done.</criterion></acceptance>
    <context><summary>Test.</summary></context>
    <records/>
  </task>
</task-add>
""";

        var (success, _, diags) = TaskAdder.Add(_workspace, iterId, 1, requestXml);
        Assert.IsFalse(success);
        var diag = diags.SingleOrDefault(d => d.Code == DiagnosticCodes.InvalidReferenceTargetType);
        Assert.IsNotNull(diag, $"Expected INVALID_REFERENCE_TARGET_TYPE, got: {string.Join(", ", diags.Select(d => $"{d.Code}: {d.Message}"))}");
        Assert.IsTrue(diag.Message.Contains("relation='supports'"), $"Expected relation in message: {diag.Message}");
        Assert.IsTrue(diag.Message.Contains("relation='implements'"), $"Actual relation in message: {diag.Message}");
    }

    [TestMethod]
    public void TaskAdd_OperationalOrigin_WrongScope_FailsWithUsefulDiagnostics()
    {
        var iterId = "20260824-test-feature";
        InitWorkspaceWithFeatureIteration(iterId);

        var requestXml = $"""
<task-add
  id="20260824T120003Z-taskadd-wrong-scope"
  actor="codex"
  occurred_at="2026-08-24T12:00:03Z">
  <task
    id="20260824-task-wrong-scope"
    status="pending"
    created_at="2026-08-24T12:00:03Z"
    updated_at="2026-08-24T12:00:03Z">
    <index><summary>Wrong scope.</summary></index>
    <title>Wrong Scope</title>
    <objective>Fails on wrong scope.</objective>
    <rationale>Test wrong scope diagnostic.</rationale>
    <scope><repository path="src/test.cs"/></scope>
    <origin>
      <ref scope="document" target="{iterId}" relation="supports"/>
    </origin>
    <constraints/>
    <acceptance><criterion id="20260824-crit-ws" status="pending">Done.</criterion></acceptance>
    <context><summary>Test.</summary></context>
    <records/>
  </task>
</task-add>
""";

        var (success, _, diags) = TaskAdder.Add(_workspace, iterId, 1, requestXml);
        Assert.IsFalse(success);
        var diag = diags.SingleOrDefault(d => d.Code == DiagnosticCodes.InvalidReferenceTargetType);
        Assert.IsNotNull(diag, $"Expected INVALID_REFERENCE_TARGET_TYPE, got: {string.Join(", ", diags.Select(d => $"{d.Code}: {d.Message}"))}");
        Assert.IsTrue(diag.Message.Contains("scope='iteration'"), $"Expected scope in message: {diag.Message}");
        Assert.IsTrue(diag.Message.Contains("scope='document'"), $"Actual scope in message: {diag.Message}");
    }

    [TestMethod]
    public void TaskAdd_OperationalOrigin_MultipleRefs_FailsWithUsefulDiagnostics()
    {
        var iterId = "20260824-test-feature";
        InitWorkspaceWithFeatureIteration(iterId);

        var requestXml = $"""
<task-add
  id="20260824T120004Z-taskadd-multiple-refs"
  actor="codex"
  occurred_at="2026-08-24T12:00:04Z">
  <task
    id="20260824-task-multiple-refs"
    status="pending"
    created_at="2026-08-24T12:00:04Z"
    updated_at="2026-08-24T12:00:04Z">
    <index><summary>Multiple refs.</summary></index>
    <title>Multiple Refs</title>
    <objective>Fails on multiple refs.</objective>
    <rationale>Test multiple refs diagnostic.</rationale>
    <scope><repository path="src/test.cs"/></scope>
    <origin>
      <ref scope="iteration" target="{iterId}" relation="supports"/>
      <ref scope="iteration" target="{iterId}" relation="supports"/>
    </origin>
    <constraints/>
    <acceptance><criterion id="20260824-crit-mr" status="pending">Done.</criterion></acceptance>
    <context><summary>Test.</summary></context>
    <records/>
  </task>
</task-add>
""";

        var (success, _, diags) = TaskAdder.Add(_workspace, iterId, 1, requestXml);
        Assert.IsFalse(success);
        var diag = diags.SingleOrDefault(d => d.Code == DiagnosticCodes.InvalidReferenceTargetType);
        Assert.IsNotNull(diag, $"Expected INVALID_REFERENCE_TARGET_TYPE, got: {string.Join(", ", diags.Select(d => $"{d.Code}: {d.Message}"))}");
        Assert.IsTrue(diag.Message.Contains("count=1"), $"Expected count in message: {diag.Message}");
        Assert.IsTrue(diag.Message.Contains("count=2"), $"Actual count in message: {diag.Message}");
    }

    [TestMethod]
    public void TaskAdd_OperationalOrigin_DiagnosticsSanitizesWithoutLeakingUnsafeContent()
    {
        var iterId = "20260824-test-feature";
        InitWorkspaceWithFeatureIteration(iterId);

        // Target with control characters and extra length
        var longTarget = "20260824-long-" + new string('a', 100);
        var requestXml = $"""
<task-add
  id="20260824T120005Z-taskadd-unsafe"
  actor="codex"
  occurred_at="2026-08-24T12:00:05Z">
  <task
    id="20260824-task-unsafe"
    status="pending"
    created_at="2026-08-24T12:00:05Z"
    updated_at="2026-08-24T12:00:05Z">
    <index><summary>Unsafe target.</summary></index>
    <title>Unsafe Target</title>
    <objective>Sanitizes without leaking.</objective>
    <rationale>Test sanitization.</rationale>
    <scope><repository path="src/test.cs"/></scope>
    <origin>
      <ref scope="iteration" target="{longTarget}" relation="supports"/>
    </origin>
    <constraints/>
    <acceptance><criterion id="20260824-crit-unsafe" status="pending">Done.</criterion></acceptance>
    <context><summary>Test.</summary></context>
    <records/>
  </task>
</task-add>
""";

        var (success, _, diags) = TaskAdder.Add(_workspace, iterId, 1, requestXml);
        Assert.IsFalse(success);
        var diag = diags.Single(d => d.Code == DiagnosticCodes.InvalidReferenceTargetType);
        Assert.IsTrue(diag.Message.Contains("..."), "Diagnostic should truncate overly long input with ellipsis.");
        Assert.IsFalse(diag.Message.Contains(longTarget), "Raw overly long content must not leak unconstrained.");
        Assert.IsFalse(diag.Message.Any(char.IsControl), "Diagnostic must contain no raw control characters.");
    }

    [TestMethod]
    public void TaskAdd_NonPendingStatus_FailsClosed()
    {
        var iterId = "20260824-test-feature";
        InitWorkspaceWithFeatureIteration(iterId);

        var requestXml = $"""
<task-add
  id="20260824T090100Z-taskadd-bad-status"
  actor="codex"
  occurred_at="2026-08-24T09:01:00Z">
  <task
    id="20260824-task-inprog-attempt"
    status="in-progress"
    created_at="2026-08-24T09:01:00Z"
    updated_at="2026-08-24T09:01:00Z">
    <index>
      <summary>Invalid In-Progress Task.</summary>
    </index>
    <title>Invalid In-Progress Task</title>
    <objective>Invalid objective.</objective>
    <rationale>Attempting to add active task directly.</rationale>
    <scope>
      <repository path="src/main.cs"/>
    </scope>
    <origin>
      <ref scope="iteration" target="20260824-req-test-feature" relation="implements"/>
    </origin>
    <constraints/>
    <acceptance>
      <criterion id="20260824-crit-inprog" status="pending">
        Implementation passes tests.
      </criterion>
    </acceptance>
    <context>
      <summary>Context.</summary>
    </context>
    <records/>
  </task>
</task-add>
""";

        var (success, _, diags) = TaskAdder.Add(_workspace, iterId, 1, requestXml);
        Assert.IsFalse(success);
        Assert.IsTrue(diags.Any(d => d.Code == DiagnosticCodes.InvalidArgument || d.Code == DiagnosticCodes.SchemaValidationError));
    }

    [TestMethod]
    public void TaskRevise_HappyPath_ElaboratesConstraintsAndAcceptance()
    {
        var iterId = "20260824-test-feature";
        InitWorkspaceWithFeatureIteration(iterId);

        // First add a pending task
        var addXml = $"""
<task-add
  id="20260824T090200Z-taskadd-base"
  actor="codex"
  occurred_at="2026-08-24T09:02:00Z">
  <task
    id="20260824-task-to-revise"
    status="pending"
    created_at="2026-08-24T09:02:00Z"
    updated_at="2026-08-24T09:02:00Z">
    <index>
      <summary>Base Task Summary.</summary>
    </index>
    <title>Base Task</title>
    <objective>Base objective.</objective>
    <rationale>Initial base rationale.</rationale>
    <scope>
      <repository path="src/main.cs"/>
    </scope>
    <origin>
      <ref scope="iteration" target="20260824-req-test-feature" relation="implements"/>
    </origin>
    <constraints/>
    <acceptance>
      <criterion id="20260824-crit-base" status="pending">
        Base criterion.
      </criterion>
    </acceptance>
    <context>
      <summary>Base context.</summary>
    </context>
    <records/>
  </task>
</task-add>
""";
        var (addOk, _, _) = TaskAdder.Add(_workspace, iterId, 1, addXml);
        Assert.IsTrue(addOk);

        // Now revise it
        var reviseXml = """
<task-revise
  id="20260824T090300Z-taskrevise-01"
  actor="codex"
  occurred_at="2026-08-24T09:03:00Z">
  <rationale>Elaborated technical rationale.</rationale>
  <add_constraints>
    <constraint id="20260824-const-perf">
      Must execute under 100ms.
    </constraint>
  </add_constraints>
  <add_acceptance>
    <criterion id="20260824-crit-perf" status="pending">
      Benchmark verified.
    </criterion>
  </add_acceptance>
  <records>
    <record
      id="20260824T090300Z-rec-revise-01"
      kind="discussion"
      status="informational"
      created_at="2026-08-24T09:03:00Z"
      actor="codex">
      <summary>Added performance constraint and criterion.</summary>
    </record>
  </records>
</task-revise>
""";

        var (revOk, revEnv, revDiags) = TaskReviser.Revise(_workspace, iterId, "20260824-task-to-revise", 2, reviseXml);
        Assert.IsTrue(revOk, $"Revise failed: {string.Join(", ", revDiags.Select(d => d.Message))}");
        Assert.IsNotNull(revEnv);
        Assert.IsFalse(revEnv.AlreadyApplied);

        // Verify task in document
        var tasksXmlPath = Path.Combine(_workspace, iterId, "tasks.xml");
        var tasksDoc = XDocument.Load(tasksXmlPath);
        var task = tasksDoc.Descendants("task").First(t => (string?)t.Attribute("id") == "20260824-task-to-revise");
        Assert.AreEqual("Elaborated technical rationale.", task.Element("rationale")?.Value);
        Assert.IsNotNull(task.Element("constraints")?.Elements("constraint").FirstOrDefault(c => (string?)c.Attribute("id") == "20260824-const-perf"));
        Assert.IsNotNull(task.Element("acceptance")?.Elements("criterion").FirstOrDefault(c => (string?)c.Attribute("id") == "20260824-crit-perf"));

        AssertDryRunReplayBlocked(
            _workspace,
            "task revise",
            () => TaskReviser.Revise(_workspace, iterId, "20260824-task-to-revise", 2, reviseXml, dryRun: true));
    }

    [TestMethod]
    public void TaskExecution_RejectsProposedOriginRequirement()
    {
        var iterId = "20260824-test-feature";
        InitWorkspaceWithFeatureIteration(iterId);
        var add = """
<task-add id="20260824T092700Z-taskadd-proposed-origin" actor="codex" occurred_at="2026-08-24T09:27:00Z"><task id="20260824-task-proposed-origin" status="pending" created_at="2026-08-24T09:27:00Z" updated_at="2026-08-24T09:27:00Z"><index><summary>Proposed origin task.</summary></index><title>Proposed origin</title><objective>Must not execute.</objective><rationale>Await owner.</rationale><scope><repository path="src/proposed.cs"/></scope><origin><ref scope="iteration" target="20260824-req-test-feature" relation="implements"/></origin><constraints/><acceptance><criterion id="20260824-crit-proposed-origin" status="pending">Works.</criterion></acceptance><context><summary>Context.</summary></context><records/></task></task-add>
""";
        Assert.IsTrue(TaskAdder.Add(_workspace, iterId, 1, add).Success);
        var start = """
<task-update id="20260824T092800Z-taskstart-proposed-origin" transition="start" actor="codex" occurred_at="2026-08-24T09:28:00Z"><records><record id="20260824T092800Z-rec-proposed-origin" kind="start" status="informational" created_at="2026-08-24T09:28:00Z" actor="codex"><summary>Attempt start.</summary></record></records></task-update>
""";
        var result = TaskUpdater.Update(_workspace, iterId, "20260824-task-proposed-origin", 2, start);
        Assert.IsFalse(result.Success);
        Assert.IsTrue(result.Diagnostics.Any(d => d.Code == DiagnosticCodes.OwnerDecisionRequired));
    }

    [TestMethod]
    public void TaskRevise_StartedTaskOnlyAllowsAdditiveScopeExpansion()
    {
        var iterId = "20260824-test-feature";
        InitWorkspaceWithFeatureIteration(iterId);
        var activate = $"""
<iteration-confirmation id="20260824T093000Z-conf-activate-started-revise" iteration="{iterId}" action="activate" expected_spec_revision="1" expected_tasks_revision="1" actor="owner" decided_at="2026-08-24T09:30:00Z">
  <summary>Approve baseline requirement.</summary>
  <requirements><requirement target="20260824-req-test-feature" decision="approved"/></requirements>
  <acceptance><criterion target="20260824-crit-test-feature" decision="accepted"/></acceptance>
</iteration-confirmation>
""";
        Assert.IsTrue(IterationConfirmer.Confirm(_workspace, activate).Success);

        var add = """
<task-add id="20260824T093100Z-taskadd-started-revise" actor="codex" occurred_at="2026-08-24T09:31:00Z">
  <task id="20260824-task-started-revise" status="pending" created_at="2026-08-24T09:31:00Z" updated_at="2026-08-24T09:31:00Z">
    <index><summary>Started revise task.</summary></index><title>Started revise task</title><objective>Verify revision boundary.</objective><rationale>Original rationale.</rationale>
    <scope><repository path="src/original.cs"><include path="src/original.cs"/></repository></scope>
    <origin><ref scope="iteration" target="20260824-req-test-feature" relation="implements"/></origin><constraints/>
    <acceptance><criterion id="20260824-crit-started-revise" status="pending">Works.</criterion></acceptance><context><summary>Context.</summary></context><records/>
  </task>
</task-add>
""";
        Assert.IsTrue(TaskAdder.Add(_workspace, iterId, 1, add).Success);
        var start = """
<task-update id="20260824T093200Z-taskstart-started-revise" transition="start" actor="codex" occurred_at="2026-08-24T09:32:00Z"><records><record id="20260824T093200Z-rec-started-revise" kind="start" status="informational" created_at="2026-08-24T09:32:00Z" actor="codex"><summary>Start.</summary></record></records></task-update>
""";
        Assert.IsTrue(TaskUpdater.Update(_workspace, iterId, "20260824-task-started-revise", 2, start).Success);

        var rationaleRewrite = """
<task-revise id="20260824T093300Z-taskrevise-rationale" actor="codex" occurred_at="2026-08-24T09:33:00Z"><rationale>Rewritten rationale.</rationale><records><record id="20260824T093300Z-rec-rationale" kind="discussion" status="informational" created_at="2026-08-24T09:33:00Z" actor="codex"><summary>Attempt rationale rewrite.</summary></record></records></task-revise>
""";
        var rationaleResult = TaskReviser.Revise(_workspace, iterId, "20260824-task-started-revise", 3, rationaleRewrite);
        Assert.IsFalse(rationaleResult.Success);
        Assert.IsTrue(rationaleResult.Diagnostics.Any(d => d.Code == DiagnosticCodes.TaskRevisionNotAllowed));

        var narrowedScope = """
<task-revise id="20260824T093400Z-taskrevise-narrow" actor="codex" occurred_at="2026-08-24T09:34:00Z"><scope><repository path="src/other.cs"/></scope><records><record id="20260824T093400Z-rec-narrow" kind="discussion" status="informational" created_at="2026-08-24T09:34:00Z" actor="codex"><summary>Attempt narrowing.</summary></record></records></task-revise>
""";
        var narrowResult = TaskReviser.Revise(_workspace, iterId, "20260824-task-started-revise", 3, narrowedScope);
        Assert.IsFalse(narrowResult.Success);
        Assert.IsTrue(narrowResult.Diagnostics.Any(d => d.Code == DiagnosticCodes.TaskRevisionNotAllowed));

        var expandedScope = """
<task-revise id="20260824T093500Z-taskrevise-expand" actor="codex" occurred_at="2026-08-24T09:35:00Z"><scope><repository path="src/original.cs"><include path="src/original.cs"/></repository><repository path="src/added.cs"/></scope><records><record id="20260824T093500Z-rec-expand" kind="discussion" status="informational" created_at="2026-08-24T09:35:00Z" actor="codex"><summary>Expand scope.</summary></record></records></task-revise>
""";
        var expandResult = TaskReviser.Revise(_workspace, iterId, "20260824-task-started-revise", 3, expandedScope);
        Assert.IsTrue(expandResult.Success, string.Join(", ", expandResult.Diagnostics.Select(d => d.Message)));
    }

    [TestMethod]
    public void TaskSplit_HappyPath_SetsParentDispositionAndAddsSubtasks()
    {
        var iterId = "20260824-test-feature";
        InitWorkspaceWithFeatureIteration(iterId);

        // Add parent task
        var addXml = $"""
<task-add
  id="20260824T090400Z-taskadd-parent"
  actor="codex"
  occurred_at="2026-08-24T09:04:00Z">
  <task
    id="20260824-task-parent-split"
    status="pending"
    created_at="2026-08-24T09:04:00Z"
    updated_at="2026-08-24T09:04:00Z">
    <index>
      <summary>Large Parent Task Summary.</summary>
    </index>
    <title>Large Parent Task</title>
    <objective>Parent objective.</objective>
    <rationale>Too big, needs split.</rationale>
    <scope>
      <repository path="src/big.cs"/>
    </scope>
    <origin>
      <ref scope="iteration" target="20260824-req-test-feature" relation="implements"/>
    </origin>
    <constraints/>
    <acceptance>
      <criterion id="20260824-crit-parent" status="pending">
        Parent criterion.
      </criterion>
    </acceptance>
    <context>
      <summary>Parent context.</summary>
    </context>
    <records/>
  </task>
</task-add>
""";
        var (addOk, _, _) = TaskAdder.Add(_workspace, iterId, 1, addXml);
        Assert.IsTrue(addOk);

        var splitXml = $"""
<task-split
  id="20260824T090500Z-tasksplit-01"
  actor="codex"
  occurred_at="2026-08-24T09:05:00Z">
  <parent_disposition
    transition="supersede"
    rationale="Split into part A and part B.">
    <record
      id="20260824T090500Z-rec-split-disp"
      kind="discussion"
      status="informational"
      created_at="2026-08-24T09:05:00Z"
      actor="codex">
      <summary>Superseded by subtasks.</summary>
    </record>
  </parent_disposition>
  <subtasks>
    <task
      id="20260824-task-sub-1"
      status="pending"
      created_at="2026-08-24T09:05:00Z"
      updated_at="2026-08-24T09:05:00Z">
      <index>
        <summary>Subtask 1 summary.</summary>
      </index>
      <title>Subtask 1</title>
      <objective>Subtask 1 objective.</objective>
      <rationale>Part 1</rationale>
      <scope>
        <repository path="src/part1.cs"/>
      </scope>
      <origin>
        <ref scope="iteration" target="20260824-req-test-feature" relation="implements"/>
      </origin>
      <constraints/>
      <acceptance>
        <criterion id="20260824-crit-sub1" status="pending">
          Subtask 1 works.
        </criterion>
      </acceptance>
      <context>
        <summary>Subtask 1 context.</summary>
      </context>
      <records/>
    </task>
    <task
      id="20260824-task-sub-2"
      status="pending"
      created_at="2026-08-24T09:05:00Z"
      updated_at="2026-08-24T09:05:00Z">
      <index>
        <summary>Subtask 2 summary.</summary>
      </index>
      <title>Subtask 2</title>
      <objective>Subtask 2 objective.</objective>
      <rationale>Part 2</rationale>
      <scope>
        <repository path="src/part2.cs"/>
      </scope>
      <origin>
        <ref scope="iteration" target="20260824-req-test-feature" relation="implements"/>
      </origin>
      <constraints/>
      <acceptance>
        <criterion id="20260824-crit-sub2" status="pending">
          Subtask 2 works.
        </criterion>
      </acceptance>
      <context>
        <summary>Subtask 2 context.</summary>
      </context>
      <records/>
    </task>
  </subtasks>
</task-split>
""";

        var (splitOk, splitEnv, splitDiags) = TaskSplitter.Split(_workspace, iterId, "20260824-task-parent-split", 2, splitXml);
        Assert.IsTrue(splitOk, $"Split failed: {string.Join(", ", splitDiags.Select(d => d.Message))}");
        Assert.IsNotNull(splitEnv);
        Assert.IsFalse(splitEnv.AlreadyApplied);

        var tasksXmlPath = Path.Combine(_workspace, iterId, "tasks.xml");
        var tasksDoc = XDocument.Load(tasksXmlPath);
        var parent = tasksDoc.Descendants("task").First(t => (string?)t.Attribute("id") == "20260824-task-parent-split");
        Assert.AreEqual("superseded", (string?)parent.Attribute("status"));
        Assert.IsTrue(parent.Element("records")!.Elements("record").Any(r =>
            (string?)r.Attribute("id") == "20260824T090500Z-tasksplit-01-receipt" &&
            r.Element("impact")?.Value == "Split into part A and part B."), "Split rationale must be queryable from its generated receipt.");
        var (splitReplayOk, splitReplayEnv, splitReplayDiags) = TaskSplitter.Split(_workspace, iterId, "20260824-task-parent-split", 2, splitXml);
        Assert.IsTrue(splitReplayOk, string.Join(", ", splitReplayDiags.Select(d => d.Message)));
        Assert.IsTrue(splitReplayEnv!.AlreadyApplied);
        var (splitDivergentOk, _, splitDivergentDiags) = TaskSplitter.Split(_workspace, iterId, "20260824-task-parent-split", 2,
            splitXml.Replace("Split into part A and part B.", "Different split rationale.", StringComparison.Ordinal));
        Assert.IsFalse(splitDivergentOk);
        Assert.IsTrue(splitDivergentDiags.Any(d => d.Code == DiagnosticCodes.IdempotencyConflict));

        var sub1 = tasksDoc.Descendants("task").FirstOrDefault(t => (string?)t.Attribute("id") == "20260824-task-sub-1");
        var sub2 = tasksDoc.Descendants("task").FirstOrDefault(t => (string?)t.Attribute("id") == "20260824-task-sub-2");
        Assert.IsNotNull(sub1);
        Assert.IsNotNull(sub2);

        AssertDryRunReplayBlocked(
            _workspace,
            "task split",
            () => TaskSplitter.Split(_workspace, iterId, "20260824-task-parent-split", 2, splitXml, dryRun: true));
    }

    [TestMethod]
    public void RequirementPropose_ProposedStatus_Succeeds()
    {
        var iterId = "20260824-test-feature";
        InitWorkspaceWithFeatureIteration(iterId);

        var proposeXml = """
<requirement-propose
  id="20260824T091000Z-reqprop-01"
  actor="codex"
  occurred_at="2026-08-24T09:10:00Z">
  <requirement id="20260824-req-technical-extension" status="proposed">
    <index>
      <summary>Technical Extension Requirement Summary.</summary>
    </index>
    <statement>The system must support technical extension.</statement>
    <rationale>Discovered during implementation.</rationale>
  </requirement>
</requirement-propose>
""";

        var (propOk, propEnv, propDiags) = RequirementProposer.Propose(_workspace, iterId, 1, proposeXml);
        Assert.IsTrue(propOk, $"Propose failed: {string.Join(", ", propDiags.Select(d => d.Message))}");
        Assert.IsNotNull(propEnv);

        var specXmlPath = Path.Combine(_workspace, iterId, "spec.xml");
        var specDoc = XDocument.Load(specXmlPath);
        var req = specDoc.Descendants("requirement").FirstOrDefault(r => (string?)r.Attribute("id") == "20260824-req-technical-extension");
        Assert.IsNotNull(req);
        Assert.AreEqual("proposed", (string?)req.Attribute("status"));

        AssertDryRunReplayBlocked(
            _workspace,
            "requirement propose",
            () => RequirementProposer.Propose(_workspace, iterId, 1, proposeXml, dryRun: true));
    }

    [TestMethod]
    public void RequirementPropose_NonProposedStatus_FailsWithOwnerDecisionRequired()
    {
        var iterId = "20260824-test-feature";
        InitWorkspaceWithFeatureIteration(iterId);

        var proposeXml = """
<requirement-propose
  id="20260824T091100Z-reqprop-approved-attempt"
  actor="codex"
  occurred_at="2026-08-24T09:11:00Z">
  <requirement id="20260824-req-auto-approved" status="approved">
    <index>
      <summary>Auto Approved Requirement Summary.</summary>
    </index>
    <statement>Illegal self-approval statement.</statement>
    <rationale>Trying to bypass owner authority.</rationale>
  </requirement>
</requirement-propose>
""";

        var (propOk, _, propDiags) = RequirementProposer.Propose(_workspace, iterId, 1, proposeXml);
        Assert.IsFalse(propOk);
        Assert.IsTrue(propDiags.Any(d => d.Code == DiagnosticCodes.OwnerDecisionRequired));
    }

    [TestMethod]
    public void ChangePropose_HappyPath_AttachesFindingFreezesTaskAndProposesRequirement()
    {
        var iterId = "20260824-test-feature";
        InitWorkspaceWithFeatureIteration(iterId);

        // Add task to freeze
        var addXml = $"""
<task-add
  id="20260824T091500Z-taskadd-target"
  actor="codex"
  occurred_at="2026-08-24T09:15:00Z">
  <task
    id="20260824-task-to-freeze"
    status="pending"
    created_at="2026-08-24T09:15:00Z"
    updated_at="2026-08-24T09:15:00Z">
    <index>
      <summary>Task to Freeze Summary.</summary>
    </index>
    <title>Task to Freeze</title>
    <objective>Freeze objective.</objective>
    <rationale>Initial rationale</rationale>
    <scope>
      <repository path="src/main.cs"/>
    </scope>
    <origin>
      <ref scope="iteration" target="20260824-req-test-feature" relation="implements"/>
    </origin>
    <constraints/>
    <acceptance>
      <criterion id="20260824-crit-tf" status="pending">
        Criterion
      </criterion>
    </acceptance>
    <context>
      <summary>Initial context.</summary>
    </context>
    <records/>
  </task>
</task-add>
""";
        var (addOk, _, _) = TaskAdder.Add(_workspace, iterId, 1, addXml);
        Assert.IsTrue(addOk);

        var activateXml = $"""
<iteration-confirmation id="20260824T091530Z-conf-activate" iteration="{iterId}" action="activate" expected_spec_revision="1" expected_tasks_revision="2" actor="owner" decided_at="2026-08-24T09:15:30Z">
  <summary>Owner activated the baseline requirement.</summary>
  <requirements><requirement target="20260824-req-test-feature" decision="approved"/></requirements>
  <acceptance><criterion target="20260824-crit-test-feature" decision="accepted"/></acceptance>
</iteration-confirmation>
""";
        var (activateOk, _, activateDiags) = IterationConfirmer.Confirm(_workspace, activateXml);
        Assert.IsTrue(activateOk, $"Activation failed: {string.Join(", ", activateDiags.Select(d => d.Message))}");

        var changeProposeXml = """
<change-propose
  id="20260824T091600Z-changeprop-01"
  actor="codex"
  occurred_at="2026-08-24T09:16:00Z">
  <summary>Discovered requirement scope gap during execution.</summary>
  <finding_record task="20260824-task-to-freeze">
    <record
      id="20260824T091600Z-rec-finding-scope-gap"
      kind="finding"
      status="active"
      created_at="2026-08-24T09:16:00Z"
      actor="codex">
      <summary>Scope gap requires new requirement.</summary>
    </record>
  </finding_record>
  <freeze_tasks>
    <task target="20260824-task-to-freeze" reason="Blocked pending owner decision on requirement."/>
  </freeze_tasks>
  <proposed_requirements>
    <requirement id="20260824-req-scope-gap-fix" status="proposed">
      <index>
        <summary>Scope Gap Fix Requirement Summary.</summary>
      </index>
      <statement>The system must fix the scope gap.</statement>
      <rationale>Required for proper execution.</rationale>
    </requirement>
  </proposed_requirements>
</change-propose>
""";

        var (cpOk, cpEnv, cpDiags) = ChangeProposer.Propose(_workspace, iterId, 2, 2, changeProposeXml);
        Assert.IsTrue(cpOk, $"Change propose failed: {string.Join(", ", cpDiags.Select(d => d.Message))}");
        Assert.IsNotNull(cpEnv);

        AssertDryRunReplayBlocked(
            _workspace,
            "change propose",
            () => ChangeProposer.Propose(_workspace, iterId, 2, 2, changeProposeXml, dryRun: true));

        // Verify tasks.xml: task is blocked and has active finding
        var tasksDoc = XDocument.Load(Path.Combine(_workspace, iterId, "tasks.xml"));
        var task = tasksDoc.Descendants("task").First(t => (string?)t.Attribute("id") == "20260824-task-to-freeze");
        Assert.AreEqual("blocked", (string?)task.Attribute("status"));
        var activeFinding = task.Element("records")?.Elements("record").FirstOrDefault(r => (string?)r.Attribute("id") == "20260824T091600Z-rec-finding-scope-gap");
        Assert.IsNotNull(activeFinding);
        Assert.AreEqual("active", (string?)activeFinding.Attribute("status"));

        // Verify spec.xml: proposed requirement exists
        var specDoc = XDocument.Load(Path.Combine(_workspace, iterId, "spec.xml"));
        var proposedReq = specDoc.Descendants("requirement").FirstOrDefault(r => (string?)r.Attribute("id") == "20260824-req-scope-gap-fix");
        Assert.IsNotNull(proposedReq);
        Assert.AreEqual("proposed", (string?)proposedReq.Attribute("status"));

        // change propose owns a two-document transaction. An interruption after
        // the first publish must recover to the complete new state rather than
        // leaving a split specification/task view for the next writer.
        var interruptedXml = changeProposeXml
            .Replace("20260824T091600Z-changeprop-01", "20260824T091700Z-changeprop-fault", StringComparison.Ordinal)
            .Replace("20260824T091600Z-rec-finding-scope-gap", "20260824T091700Z-rec-finding-fault", StringComparison.Ordinal)
            .Replace("20260824-req-scope-gap-fix", "20260824-req-scope-gap-fault", StringComparison.Ordinal)
            .Replace("2026-08-24T09:16:00Z", "2026-08-24T09:17:00Z", StringComparison.Ordinal);
        var (faultSuccess, _, _) = ChangeProposer.Propose(
            _workspace, iterId, 2, 3, interruptedXml,
            faultInjector: new TestFaultInjector(FaultPhase.DuringMultiFileCommitAfterFirstFile));
        Assert.IsFalse(faultSuccess);
        var (recovered, recoveryError) = StartupRecovery.Run(_workspace);
        Assert.IsTrue(recovered, recoveryError?.Message);
        var recoveredValidation = SchemaValidator.Validate(_workspace);
        Assert.IsTrue(recoveredValidation.IsValid, string.Join("; ", recoveredValidation.Diagnostics.Select(d => d.Message)));
        var recoveredSpec = XDocument.Load(Path.Combine(_workspace, iterId, "spec.xml"));
        var recoveredTasks = XDocument.Load(Path.Combine(_workspace, iterId, "tasks.xml"));
        var recoveredRequirement = recoveredSpec.Descendants("requirement").Any(r => (string?)r.Attribute("id") == "20260824-req-scope-gap-fault");
        var recoveredFinding = recoveredTasks.Descendants("record").Any(r => (string?)r.Attribute("id") == "20260824T091700Z-rec-finding-fault");
        Assert.AreEqual(recoveredRequirement, recoveredFinding, "Recovery must converge change-propose documents to one complete old or new state.");
    }

    [TestMethod]
    public void ChangeApply_ExactDryRunReplay_RejectsPendingRecovery()
    {
        const string iterId = "20260823-xpath-core";
        _workspace = CreateWorkspaceCopy();
        var replanRequest = $"""
<iteration-confirmation id="20260904T161100Z-confirm-change-apply-replay" iteration="{iterId}" action="replan" expected_spec_revision="4" expected_tasks_revision="9" actor="owner" decided_at="2026-09-04T16:11:00Z">
  <summary>Authorize exact change-apply replay regression.</summary>
</iteration-confirmation>
""";
        var (replanSuccess, _, replanDiagnostics) = IterationConfirmer.Confirm(_workspace, replanRequest);
        Assert.IsTrue(replanSuccess, string.Join("; ", replanDiagnostics.Select(d => d.Message)));

        var applyRequest = """
<change-apply id="20260904T161200Z-change-apply-replay" actor="test" occurred_at="2026-09-04T16:12:00Z">
  <summary>Exercise exact change-apply dry-run replay.</summary>
  <task_dispositions>
    <task target="20260823-task-task-history" transition="cancel" rationale="Verify recovery before replay shortcut."/>
  </task_dispositions>
</change-apply>
""";
        var (success, envelope, diagnostics) = ChangeApplier.Apply(_workspace, iterId, 5, 9, applyRequest);
        Assert.IsTrue(success, string.Join("; ", diagnostics.Select(d => d.Message)));
        Assert.IsFalse(envelope!.AlreadyApplied);

        AssertDryRunReplayBlocked(
            _workspace,
            "change apply",
            () => ChangeApplier.Apply(_workspace, iterId, 5, 9, applyRequest, dryRun: true));
    }

    [TestMethod]
    public void ChangeApply_OutsideReplanningStatus_FailsWithChangeApplicationInvalid()
    {
        var iterId = "20260824-test-feature";
        InitWorkspaceWithFeatureIteration(iterId);

        var changeApplyXml = """
<change-apply
  id="20260824T092000Z-changeapply-bad-status"
  actor="codex"
  occurred_at="2026-08-24T09:20:00Z">
  <summary>Attempting change apply while iteration is draft/active.</summary>
</change-apply>
""";

        // spec.xml is currently in status="draft"
        var (caOk, _, caDiags) = ChangeApplier.Apply(_workspace, iterId, 1, 1, changeApplyXml);
        Assert.IsFalse(caOk);
        Assert.IsTrue(caDiags.Any(d => d.Code == DiagnosticCodes.ChangeApplicationInvalid));
    }

    [TestMethod]
    public void TaskUpdater_ReplanningFreeze_RejectsExecutionTransitions()
    {
        var iterId = "20260824-test-feature";
        InitWorkspaceWithFeatureIteration(iterId);

        // Add task
        var addXml = $"""
<task-add
  id="20260824T092500Z-taskadd-replanning"
  actor="codex"
  occurred_at="2026-08-24T09:25:00Z">
  <task
    id="20260824-task-freeze-test"
    status="pending"
    created_at="2026-08-24T09:25:00Z"
    updated_at="2026-08-24T09:25:00Z">
    <index>
      <summary>Task Freeze Test Summary.</summary>
    </index>
    <title>Task Freeze Test</title>
    <objective>Freeze objective.</objective>
    <rationale>Testing replanning freeze.</rationale>
    <scope>
      <repository path="src/main.cs"/>
    </scope>
    <origin>
      <ref scope="iteration" target="20260824-req-test-feature" relation="implements"/>
    </origin>
    <constraints/>
    <acceptance>
      <criterion id="20260824-crit-ft" status="pending">
        Criterion
      </criterion>
    </acceptance>
    <context>
      <summary>Freeze context.</summary>
    </context>
    <records/>
  </task>
</task-add>
""";
        var (addOk, _, _) = TaskAdder.Add(_workspace, iterId, 1, addXml);
        Assert.IsTrue(addOk);

        // Transition iteration to replanning
        var specXmlPath = Path.Combine(_workspace, iterId, "spec.xml");
        var specDoc = XDocument.Load(specXmlPath);
        specDoc.Root!.SetAttributeValue("status", "replanning");
        DogdouSpec.Core.Tasks.StatusTermHelper.SynchronizeStatusTerm(specDoc.Root, "replanning");
        var confs = specDoc.Root!.Element("confirmations");
        if (confs == null)
        {
            confs = new XElement("confirmations");
            specDoc.Root.Add(confs);
        }
        confs.Add(new XElement("confirmation",
            new XAttribute("id", "20260824T092530Z-conf-replan"),
            new XAttribute("action", "replan"),
            new XAttribute("decision", "accepted"),
            new XAttribute("actor", "owner"),
            new XAttribute("decided_at", "2026-08-24T09:25:30Z"),
            new XElement("summary", "Owner confirmed replanning.")));
        specDoc.Save(specXmlPath);

        // Attempt execution transition 'start' on task
        var startXml = """
<task-update
  id="20260824T092600Z-update-start-during-replanning"
  transition="start"
  actor="codex"
  occurred_at="2026-08-24T09:26:00Z">
  <records>
    <record
      id="20260824T092600Z-rec-start-rep"
      kind="start"
      status="informational"
      created_at="2026-08-24T09:26:00Z"
      actor="codex">
      <summary>Starting task.</summary>
    </record>
  </records>
</task-update>
""";

        var (startOk, _, startDiags) = TaskUpdater.Update(_workspace, iterId, "20260824-task-freeze-test", 2, startXml);
        Assert.IsFalse(startOk);
        Assert.IsTrue(startDiags.Any(d => d.Code == DiagnosticCodes.IterationReplanningExecutionFrozen));

        // Terminal transition 'supersede' should NOT be frozen
        var supersedeXml = """
<task-update
  id="20260824T092700Z-update-supersede-during-replanning"
  transition="supersede"
  actor="codex"
  occurred_at="2026-08-24T09:27:00Z">
  <records>
    <record
      id="20260824T092700Z-rec-supersede-rep"
      kind="discussion"
      status="informational"
      created_at="2026-08-24T09:27:00Z"
      actor="codex">
      <summary>Superseded task.</summary>
    </record>
  </records>
</task-update>
""";

        var (supOk, supEnv, supDiags) = TaskUpdater.Update(_workspace, iterId, "20260824-task-freeze-test", 2, supersedeXml);
        Assert.IsTrue(supOk, $"Supersede should succeed during replanning: {string.Join(", ", supDiags.Select(d => d.Message))}");
        Assert.IsNotNull(supEnv);
    }

    [TestMethod]
    public void TaskUpdater_TerminalTaskImmutability_RejectsMutations()
    {
        var iterId = "20260824-test-feature";
        InitWorkspaceWithFeatureIteration(iterId);

        // Add task
        var addXml = $"""
<task-add
  id="20260824T093000Z-taskadd-term"
  actor="codex"
  occurred_at="2026-08-24T09:30:00Z">
  <task
    id="20260824-task-terminal-immutability"
    status="pending"
    created_at="2026-08-24T09:30:00Z"
    updated_at="2026-08-24T09:30:00Z">
    <index>
      <summary>Terminal Immutability Task Summary.</summary>
    </index>
    <title>Terminal Immutability Task</title>
    <objective>Terminal objective.</objective>
    <rationale>Testing terminal immutability.</rationale>
    <scope>
      <repository path="src/main.cs"/>
    </scope>
    <origin>
      <ref scope="iteration" target="20260824-req-test-feature" relation="implements"/>
    </origin>
    <constraints/>
    <acceptance>
      <criterion id="20260824-crit-term" status="pending">
        Criterion
      </criterion>
    </acceptance>
    <context>
      <summary>Terminal context.</summary>
    </context>
    <records/>
  </task>
</task-add>
""";
        var (addOk, _, _) = TaskAdder.Add(_workspace, iterId, 1, addXml);
        Assert.IsTrue(addOk);

        // Supersede the task
        var cancelXml = """
<task-update
  id="20260824T093100Z-update-cancel-term"
  transition="cancel"
  actor="codex"
  occurred_at="2026-08-24T09:31:00Z">
  <records>
    <record
      id="20260824T093100Z-rec-cancel-term"
      kind="discussion"
      status="informational"
      created_at="2026-08-24T09:31:00Z"
      actor="codex">
      <summary>Cancelled task.</summary>
    </record>
  </records>
</task-update>
""";
        var (cancelOk, _, _) = TaskUpdater.Update(_workspace, iterId, "20260824-task-terminal-immutability", 2, cancelXml);
        Assert.IsTrue(cancelOk);

        // Try to transition cancelled task back to in-progress
        var restartXml = """
<task-update
  id="20260824T093200Z-update-restart-term"
  transition="start"
  actor="codex"
  occurred_at="2026-08-24T09:32:00Z">
  <records>
    <record
      id="20260824T093200Z-rec-restart-term"
      kind="start"
      status="informational"
      created_at="2026-08-24T09:32:00Z"
      actor="codex">
      <summary>Restarting.</summary>
    </record>
  </records>
</task-update>
""";
        var (resOk, _, resDiags) = TaskUpdater.Update(_workspace, iterId, "20260824-task-terminal-immutability", 3, restartXml);
        Assert.IsFalse(resOk);
        Assert.IsTrue(resDiags.Any(d => d.Code == DiagnosticCodes.TaskImmutable));

        // Try to append non-informational (completion) record to cancelled task
        var compRecXml = """
<task-update
  id="20260824T093300Z-update-comp-term"
  actor="codex"
  occurred_at="2026-08-24T09:33:00Z">
  <records>
    <record
      id="20260824T093300Z-rec-comp-term"
      kind="completion"
      status="informational"
      created_at="2026-08-24T09:33:00Z"
      actor="codex">
      <summary>Completed.</summary>
    </record>
  </records>
</task-update>
""";
        var (compOk, _, compDiags) = TaskUpdater.Update(_workspace, iterId, "20260824-task-terminal-immutability", 3, compRecXml);
        Assert.IsFalse(compOk);
        Assert.IsTrue(compDiags.Any(d => d.Code == DiagnosticCodes.TaskImmutable));

        // Appending informational record to cancelled task should SUCCEED
        var infoRecXml = """
<task-update
  id="20260824T093400Z-update-info-term"
  actor="codex"
  occurred_at="2026-08-24T09:34:00Z">
  <records>
    <record
      id="20260824T093400Z-rec-info-term"
      kind="discussion"
      status="informational"
      created_at="2026-08-24T09:34:00Z"
      actor="codex">
      <summary>Post-cancellation informational discussion.</summary>
    </record>
  </records>
</task-update>
""";
        var (infoOk, infoEnv, infoDiags) = TaskUpdater.Update(_workspace, iterId, "20260824-task-terminal-immutability", 3, infoRecXml);
        Assert.IsTrue(infoOk, $"Informational record append to terminal task must succeed: {string.Join(", ", infoDiags.Select(d => d.Message))}");
        Assert.IsNotNull(infoEnv);
    }

    private static void AssertDryRunReplayBlocked(
        string workspace,
        string family,
        Func<(bool Success, MutationEnvelope? Envelope, IReadOnlyList<Diagnostic> Diagnostics)> replay)
    {
        var before = Directory.GetFiles(workspace, "*.xml", SearchOption.AllDirectories)
            .Where(path => !path.Contains(Path.DirectorySeparatorChar + "_tmp" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            .ToDictionary(
                path => Path.GetRelativePath(workspace, path),
                File.ReadAllBytes,
                StringComparer.OrdinalIgnoreCase);
        var pendingRoot = Path.Combine(workspace, "_tmp", "tx_pending_" + family.Replace(' ', '_'));
        var pendingDirectory = Path.Combine(pendingRoot, "staged");
        Directory.CreateDirectory(pendingDirectory);

        var result = replay();

        Assert.IsFalse(result.Success, $"{family} exact dry-run replay must fail closed while recovery is pending.");
        Assert.IsTrue(
            result.Diagnostics.Any(d => d.Code == DiagnosticCodes.RecoveryFailed),
            $"{family} diagnostics: {string.Join("; ", result.Diagnostics.Select(d => d.Message))}");
        Assert.IsTrue(Directory.Exists(pendingDirectory), $"{family} dry-run must preserve pending recovery artifacts.");
        foreach (var (relativePath, originalBytes) in before)
        {
            CollectionAssert.AreEqual(
                originalBytes,
                File.ReadAllBytes(Path.Combine(workspace, relativePath)),
                $"{family} dry-run changed managed document '{relativePath}'.");
        }

        Directory.Delete(pendingRoot, recursive: true);
    }
}
