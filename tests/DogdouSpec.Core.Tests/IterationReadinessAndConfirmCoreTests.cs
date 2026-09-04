using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using DogdouSpec.Core.Diagnostics;
using DogdouSpec.Core.Iterations;
using DogdouSpec.Core.Security;
using DogdouSpec.Core.Time;
using DogdouSpec.Core.Transactions;
using DogdouSpec.Core.Validation;

namespace DogdouSpec.Core.Tests;

[TestClass]
public sealed class IterationReadinessAndConfirmCoreTests
{
    private static string RepoRoot = null!;
    private static readonly string[] SubstantiveCriterion = new[] { "Substantive criterion defined." };
    private static readonly string[] FeatureActivationCriterion = new[] { "Feature activation criterion defined." };
    private static readonly string[] InitialApprovedCriterion = new[] { "Initial approved criterion." };
    private string _tempDir = null!;

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
        _tempDir = Path.Combine(Path.GetTempPath(), "DogdouSpec_ReadinessConfirmTests_" + Guid.NewGuid().ToString("N"));
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
            catch
            {
                // Ignore cleanup errors
            }
        }
    }

    private string CreateWorkspaceCopy()
    {
        var srcDemo = Path.Combine(RepoRoot, "docs", "demos", "v1-core", ".dogdouspec");
        var destDir = Path.Combine(_tempDir, ".dogdouspec");
        CopyDirectory(srcDemo, destDir);
        return destDir;
    }

    private static void CopyDirectory(string sourceDir, string targetDir)
    {
        Directory.CreateDirectory(targetDir);
        foreach (var file in Directory.GetFiles(sourceDir))
        {
            File.Copy(file, Path.Combine(targetDir, Path.GetFileName(file)), true);
        }
        foreach (var dir in Directory.GetDirectories(sourceDir))
        {
            CopyDirectory(dir, Path.Combine(targetDir, Path.GetFileName(dir)));
        }
    }

    private static void MakeAllTasksTerminal(string workspace, string iterationId)
    {
        var tasksPath = Path.Combine(workspace, iterationId, "tasks.xml");
        var tasksDoc = XDocument.Load(tasksPath);
        foreach (var task in tasksDoc.Descendants("task"))
        {
            task.SetAttributeValue("status", "done");
            task.SetAttributeValue("started_at", "2026-08-23T03:00:00Z");
            task.SetAttributeValue("completed_at", "2026-08-23T04:00:00Z");
            task.SetAttributeValue("updated_at", "2026-08-23T04:00:00Z");

            var criteria = task.Element("acceptance")?.Elements("criterion").ToList() ?? new List<XElement>();
            foreach (var crit in criteria)
            {
                crit.SetAttributeValue("status", "passed");
            }

            var recordsEl = task.Element("records");
            if (recordsEl == null)
            {
                recordsEl = new XElement("records");
                task.Add(recordsEl);
            }

            // Remove any active findings
            foreach (var rec in recordsEl.Elements("record").ToList())
            {
                if (rec.Attribute("kind")?.Value == "finding" && rec.Attribute("status")?.Value == "active")
                {
                    rec.SetAttributeValue("status", "resolved");
                }
            }

            // Ensure completion record exists covering all criteria
            var hasComp = recordsEl.Elements("record").Any(r => r.Attribute("kind")?.Value == "completion");
            if (!hasComp)
            {
                var compRec = new XElement("record",
                    new XAttribute("id", $"20260823T040000Z-record-{task.Attribute("id")?.Value}-comp"),
                    new XAttribute("kind", "completion"),
                    new XAttribute("status", "informational"),
                    new XAttribute("created_at", "2026-08-23T04:00:00Z"),
                    new XAttribute("actor", "codex"),
                    new XElement("summary", "Task completed."),
                    new XElement("covers", criteria.Select(c => new XElement("ref",
                        new XAttribute("scope", "document"),
                        new XAttribute("target", c.Attribute("id")?.Value ?? string.Empty),
                        new XAttribute("relation", "covers")))));
                recordsEl.Add(compRec);
            }
        }
        tasksDoc.Save(tasksPath);
    }

    private static string ComputeFileSha256(string filePath)
    {
        using var sha = SHA256.Create();
        using var stream = File.OpenRead(filePath);
        var hash = sha.ComputeHash(stream);
        return Convert.ToHexString(hash);
    }

    #region 1. Iteration Readiness Tests

    [TestMethod]
    public void Readiness_OnDemoWorkspaceWithCompletedTasks_CompletionPhase_ReportsTechnicallyReady()
    {
        var workspace = CreateWorkspaceCopy();
        MakeAllTasksTerminal(workspace, "20260823-xpath-core");

        var (success, result, diagnostics) = IterationReadiness.Assess(workspace, "20260823-xpath-core", "completion");

        Assert.IsTrue(success);
        Assert.AreEqual(0, diagnostics.Count);
        Assert.IsNotNull(result);
        Assert.AreEqual("20260823-xpath-core", result.IterationId);
        Assert.AreEqual("completion", result.Phase);
        Assert.AreEqual(4, result.SpecRevision);
        Assert.AreEqual(9, result.TasksRevision);
        Assert.IsTrue(result.TechnicallyReady);
        Assert.IsTrue(result.OwnerConfirmationRequired);
        Assert.AreEqual(6, result.ProductDecisions.Total);
        Assert.AreEqual(6, result.ProductDecisions.PendingAcceptanceCriteria);
        Assert.AreEqual("complete", result.RequiredAction.Action);

        var xml = result.ToXmlString();
        Assert.IsTrue(xml.Contains("technically_ready=\"true\""));
        Assert.IsTrue(xml.Contains("owner_confirmation_required=\"true\""));
        Assert.IsTrue(xml.Contains("<product"));

        var human = result.ToHumanString();
        Assert.IsTrue(human.Contains("Iteration Readiness: 20260823-xpath-core"));
        Assert.IsTrue(human.Contains("Technically Ready: true"));
    }

    [TestMethod]
    public void Readiness_OnRawDemoWorkspace_CompletionPhase_ReportsNotReadyDueToIncompleteTasks()
    {
        var workspace = CreateWorkspaceCopy();
        var (success, result, diagnostics) = IterationReadiness.Assess(workspace, "20260823-xpath-core", "completion");

        // Assessing an incomplete iteration returns success=true (command succeeded) but technically_ready=false
        Assert.IsTrue(success);
        Assert.AreEqual(0, diagnostics.Count);
        Assert.IsNotNull(result);
        Assert.IsFalse(result.TechnicallyReady);
        Assert.IsTrue(result.TechnicalChecks.Any(c => c.Name == "tasks_terminal" && c.Result != "passed"));
    }

    [TestMethod]
    public void Readiness_CompletionPhase_ReportsNotReady_WhenProposedRequirementExists()
    {
        var workspace = CreateWorkspaceCopy();
        MakeAllTasksTerminal(workspace, "20260823-xpath-core");

        // Add a proposed requirement to spec.xml
        var specPath = Path.Combine(workspace, "20260823-xpath-core", "spec.xml");
        var specDoc = XDocument.Load(specPath);
        specDoc.Root?.Element("product")?.Element("requirements")?.Add(new XElement("requirement",
            new XAttribute("id", "20260825-req-unapproved"),
            new XAttribute("status", "proposed"),
            new XElement("index", new XElement("summary", "Proposed req"), new XElement("term", new XAttribute("key", "kind"), new XAttribute("value", "requirement"))),
            new XElement("statement", "Statement"),
            new XElement("rationale", "Rationale")));
        specDoc.Save(specPath);

        var (success, result, diagnostics) = IterationReadiness.Assess(workspace, "20260823-xpath-core", "completion");

        Assert.IsTrue(success);
        Assert.AreEqual(0, diagnostics.Count);
        Assert.IsNotNull(result);
        Assert.IsFalse(result.TechnicallyReady);
        Assert.IsTrue(result.TechnicalChecks.Any(c => c.Name == "no_proposed_requirements" && c.Result != "passed"));
    }

    [TestMethod]
    public void Readiness_CompletionPhase_ReportsNotReady_WhenProposedDesignDecisionExists()
    {
        var workspace = CreateWorkspaceCopy();
        MakeAllTasksTerminal(workspace, "20260823-xpath-core");

        // Add a proposed design decision to spec.xml
        var specPath = Path.Combine(workspace, "20260823-xpath-core", "spec.xml");
        var specDoc = XDocument.Load(specPath);
        specDoc.Root?.Element("design")?.Element("decisions")?.Add(new XElement("decision",
            new XAttribute("id", "20260825-dec-unapproved"),
            new XAttribute("status", "proposed"),
            new XElement("index", new XElement("summary", "Proposed dec"), new XElement("term", new XAttribute("key", "kind"), new XAttribute("value", "decision"))),
            new XElement("rationale", "Rationale")));
        specDoc.Save(specPath);

        var (success, result, diagnostics) = IterationReadiness.Assess(workspace, "20260823-xpath-core", "completion");

        Assert.IsTrue(success);
        Assert.AreEqual(0, diagnostics.Count);
        Assert.IsNotNull(result);
        Assert.IsFalse(result.TechnicallyReady);
        Assert.IsTrue(result.TechnicalChecks.Any(c => c.Name == "no_proposed_design_decisions" && c.Result != "passed"));
    }

    [TestMethod]
    public void Readiness_DraftIteration_ActivationPhase_ReportsReadyToActivate()
    {
        var workspace = CreateWorkspaceCopy();
        var iterId = "20260824-test-draft";
        var (createOk, _, _) = IterationCreator.Create(workspace, iterId, "feature");
        Assert.IsTrue(createOk);

        var (success, result, diagnostics) = IterationReadiness.Assess(workspace, iterId, "activation");

        Assert.IsTrue(success);
        Assert.AreEqual(0, diagnostics.Count);
        Assert.IsNotNull(result);
        Assert.AreEqual(iterId, result.IterationId);
        Assert.AreEqual("activation", result.Phase);
        Assert.IsFalse(result.TechnicallyReady);
        var critCheck = result.TechnicalChecks.FirstOrDefault(c => c.Name == "criteria_defined");
        Assert.IsNotNull(critCheck);
        Assert.AreEqual("failed", critCheck.Result);
        Assert.AreEqual("activate", result.RequiredAction.Action);

        var definedIterId = "20260824-test-defined";
        var (createDefinedOk, _, _) = IterationCreator.Create(workspace, definedIterId, "feature", criteria: SubstantiveCriterion);
        Assert.IsTrue(createDefinedOk);
        var (definedSuccess, definedResult, _) = IterationReadiness.Assess(workspace, definedIterId, "activation");
        Assert.IsTrue(definedSuccess);
        Assert.IsNotNull(definedResult);
        Assert.IsTrue(definedResult.TechnicallyReady);
    }

    [TestMethod]
    public void Readiness_ReplanningIteration_ActivationPhase_ReportsReadyToContinue()
    {
        var workspace = CreateWorkspaceCopy();

        // 1. Transition to replanning via confirm
        var replanXml = @"<?xml version=""1.0"" encoding=""utf-8""?>
<iteration-confirmation
  id=""20260824T120000Z-confirm-replan""
  iteration=""20260823-xpath-core""
  action=""replan""
  expected_spec_revision=""4""
  actor=""architect""
  decided_at=""2026-08-24T12:00:00Z"">
  <summary>Replanning iteration due to scope change.</summary>
</iteration-confirmation>";

        var (replanOk, _, replanDiags) = IterationConfirmer.Confirm(workspace, replanXml);
        Assert.IsTrue(replanOk, replanDiags.Count > 0 ? replanDiags[0].Message : "");

        var (success, result, diagnostics) = IterationReadiness.Assess(workspace, "20260823-xpath-core", "activation");

        Assert.IsTrue(success, diagnostics.Count > 0 ? diagnostics[0].Message : "");
        Assert.AreEqual(0, diagnostics.Count);
        Assert.IsNotNull(result);
        Assert.IsTrue(result.TechnicallyReady);
        Assert.AreEqual("continue", result.RequiredAction.Action);
    }

    [TestMethod]
    public void Readiness_NonExistentIteration_ReturnsDiagnosticError()
    {
        var workspace = CreateWorkspaceCopy();
        var (success, result, diagnostics) = IterationReadiness.Assess(workspace, "20260899-missing-iteration", "activation");

        Assert.IsFalse(success);
        Assert.IsNull(result);
        Assert.IsTrue(diagnostics.Any(d => d.Code == DiagnosticCodes.DocumentNotFound));
    }

    #endregion

    #region 2. Iteration Confirm Lifecycle State Machine Tests

    [TestMethod]
    public void Confirm_Activation_DraftToActive_Success_AndTasksPreserved()
    {
        var workspace = CreateWorkspaceCopy();
        var iterId = "20260824-feature-activate";
        var clock = new TestClock(new DateTime(2026, 8, 24, 12, 0, 0, DateTimeKind.Utc));
        var (createOk, _, _) = IterationCreator.Create(workspace, iterId, "feature", clock, criteria: FeatureActivationCriterion);
        Assert.IsTrue(createOk);

        var tasksPath = Path.Combine(workspace, iterId, "tasks.xml");
        var tasksHashBefore = ComputeFileSha256(tasksPath);

        var requestXml = $@"<?xml version=""1.0"" encoding=""utf-8""?>
<iteration-confirmation
  id=""20260824T120000Z-confirm-activate""
  iteration=""{iterId}""
  action=""activate""
  expected_spec_revision=""1""
  actor=""lead-developer""
  decided_at=""2026-08-24T12:00:00Z"">
  <summary>Owner approved iteration plan and initiated activation.</summary>
  <requirements>
    <requirement target=""20260824-req-feature-activate"" decision=""approved""/>
  </requirements>
</iteration-confirmation>";

        var (success, envelope, diags) = IterationConfirmer.Confirm(workspace, requestXml);

        Assert.IsTrue(success, diags.Count > 0 ? diags[0].Message : "");
        Assert.AreEqual(0, diags.Count);
        Assert.IsNotNull(envelope);
        Assert.IsFalse(envelope.AlreadyApplied);

        // Verify spec.xml updated
        var specPath = Path.Combine(workspace, iterId, "spec.xml");
        var specDoc = XDocument.Load(specPath);
        Assert.AreEqual("2", specDoc.Root?.Attribute("revision")?.Value);
        Assert.AreEqual("active", specDoc.Root?.Attribute("status")?.Value);
        Assert.AreEqual("2026-08-24T12:00:00Z", specDoc.Root?.Attribute("updated_at")?.Value);

        var conf = specDoc.Root?.Element("confirmations")?.Elements("confirmation").FirstOrDefault();
        Assert.IsNotNull(conf);
        Assert.AreEqual("activate", conf.Attribute("action")?.Value);
        Assert.AreEqual("accepted", conf.Attribute("decision")?.Value);

        // Verify tasks.xml is 100% byte-identical
        var tasksHashAfter = ComputeFileSha256(tasksPath);
        Assert.AreEqual(tasksHashBefore, tasksHashAfter);
    }

    [TestMethod]
    public void Confirm_Activation_WithProposedRequirement_RequiresExplicitDecision()
    {
        var workspace = CreateWorkspaceCopy();
        var iterId = "20260824-feature-unapproved-req";
        var clock = new TestClock(new DateTime(2026, 8, 24, 12, 0, 0, DateTimeKind.Utc));
        var (createOk, _, _) = IterationCreator.Create(workspace, iterId, "feature", clock);
        Assert.IsTrue(createOk);

        // Attempt activation without explicit decision for proposed req created by template
        var requestXml = $@"<?xml version=""1.0"" encoding=""utf-8""?>
<iteration-confirmation
  id=""20260824T120000Z-confirm-activate""
  iteration=""{iterId}""
  action=""activate""
  expected_spec_revision=""1""
  actor=""lead-developer""
  decided_at=""2026-08-24T12:00:00Z"">
  <summary>Attempt activation leaving proposed requirement.</summary>
</iteration-confirmation>";

        var (success, _, diags) = IterationConfirmer.Confirm(workspace, requestXml);

        Assert.IsFalse(success);
        Assert.IsTrue(diags.Any(d => d.Code == DiagnosticCodes.OwnerDecisionRequired));
    }

    [TestMethod]
    public void Confirm_ReplanAndContinue_TransitionsCorrectly()
    {
        var workspace = CreateWorkspaceCopy();

        // 1. Replan active -> replanning (spec revision 4 in demo)
        var replanXml = @"<?xml version=""1.0"" encoding=""utf-8""?>
<iteration-confirmation
  id=""20260824T120000Z-confirm-replan""
  iteration=""20260823-xpath-core""
  action=""replan""
  expected_spec_revision=""4""
  actor=""architect""
  decided_at=""2026-08-24T12:00:00Z"">
  <summary>Replanning iteration due to scope change.</summary>
</iteration-confirmation>";

        var (replanOk, _, replanDiags) = IterationConfirmer.Confirm(workspace, replanXml);
        Assert.IsTrue(replanOk, replanDiags.Count > 0 ? replanDiags[0].Message : "");

        var specPath = Path.Combine(workspace, "20260823-xpath-core", "spec.xml");
        var specDoc = XDocument.Load(specPath);
        Assert.AreEqual("replanning", specDoc.Root?.Attribute("status")?.Value);
        Assert.AreEqual("5", specDoc.Root?.Attribute("revision")?.Value);

        // 2. Continue replanning -> active
        var continueXml = @"<?xml version=""1.0"" encoding=""utf-8""?>
<iteration-confirmation
  id=""20260824T130000Z-confirm-continue""
  iteration=""20260823-xpath-core""
  action=""continue""
  expected_spec_revision=""5""
  actor=""architect""
  decided_at=""2026-08-24T13:00:00Z"">
  <summary>Replanning completed, resuming execution.</summary>
</iteration-confirmation>";

        var (continueOk, _, continueDiags) = IterationConfirmer.Confirm(workspace, continueXml);
        Assert.IsTrue(continueOk, continueDiags.Count > 0 ? continueDiags[0].Message : "");

        specDoc = XDocument.Load(specPath);
        Assert.AreEqual("active", specDoc.Root?.Attribute("status")?.Value);
        Assert.AreEqual("6", specDoc.Root?.Attribute("revision")?.Value);
    }

    [TestMethod]
    public void Confirm_Completion_OnDemoWorkspaceWithCompletedTasks_Succeeds()
    {
        var workspace = CreateWorkspaceCopy();
        MakeAllTasksTerminal(workspace, "20260823-xpath-core");

        var tasksPath = Path.Combine(workspace, "20260823-xpath-core", "tasks.xml");
        var tasksHashBefore = ComputeFileSha256(tasksPath);

        var requestXml = @"<?xml version=""1.0"" encoding=""utf-8""?>
<iteration-confirmation
  id=""20260824T140000Z-confirm-complete""
  iteration=""20260823-xpath-core""
  action=""complete""
  expected_spec_revision=""4""
  expected_tasks_revision=""9""
  actor=""owner""
  decided_at=""2026-08-24T14:00:00Z"">
  <summary>Owner validated XPath core implementation and accepted all criteria.</summary>
  <acceptance>
    <criterion target=""20260823-accept-directory-overview"" decision=""accepted""/>
    <criterion target=""20260823-accept-resume-task"" decision=""accepted""/>
    <criterion target=""20260823-accept-integrated-verification"" decision=""accepted""/>
    <criterion target=""20260823-accept-no-truncation"" decision=""accepted""/>
    <criterion target=""20260823-accept-structured-reasoning"" decision=""accepted""/>
    <criterion target=""20260823-accept-template-append"" decision=""accepted""/>
  </acceptance>
</iteration-confirmation>";

        var (success, envelope, diags) = IterationConfirmer.Confirm(workspace, requestXml);

        Assert.IsTrue(success, diags.Count > 0 ? diags[0].Message : "");
        Assert.AreEqual(0, diags.Count);
        Assert.IsNotNull(envelope);

        var specPath = Path.Combine(workspace, "20260823-xpath-core", "spec.xml");
        var specDoc = XDocument.Load(specPath);
        Assert.AreEqual("completed", specDoc.Root?.Attribute("status")?.Value);
        Assert.AreEqual("2026-08-24T14:00:00Z", specDoc.Root?.Attribute("completed_at")?.Value);
        Assert.AreEqual("5", specDoc.Root?.Attribute("revision")?.Value);

        // tasks.xml remains byte-identical
        var tasksHashAfter = ComputeFileSha256(tasksPath);
        Assert.AreEqual(tasksHashBefore, tasksHashAfter);

        // Whole prospective validation passes
        var valResult = SchemaValidator.Validate(workspace);
        Assert.IsTrue(valResult.IsValid, valResult.Diagnostics.Count > 0 ? valResult.Diagnostics[0].Message : "");
    }

    [TestMethod]
    public void Confirm_Completion_WithWaivedCriterion_RequiresRationale()
    {
        var workspace = CreateWorkspaceCopy();
        MakeAllTasksTerminal(workspace, "20260823-xpath-core");

        // 1. Waive without rationale -> fails
        var failXml = @"<?xml version=""1.0"" encoding=""utf-8""?>
<iteration-confirmation
  id=""20260824T140000Z-confirm-waive-fail""
  iteration=""20260823-xpath-core""
  action=""complete""
  expected_spec_revision=""4""
  expected_tasks_revision=""9""
  actor=""owner""
  decided_at=""2026-08-24T14:00:00Z"">
  <summary>Owner waiving security criteria without rationale.</summary>
  <acceptance>
    <criterion target=""20260823-accept-directory-overview"" decision=""accepted""/>
    <criterion target=""20260823-accept-resume-task"" decision=""accepted""/>
    <criterion target=""20260823-accept-integrated-verification"" decision=""accepted""/>
    <criterion target=""20260823-accept-no-truncation"" decision=""accepted""/>
    <criterion target=""20260823-accept-structured-reasoning"" decision=""accepted""/>
    <criterion target=""20260823-accept-template-append"" decision=""waived""/>
  </acceptance>
</iteration-confirmation>";

        var (failOk, _, failDiags) = IterationConfirmer.Confirm(workspace, failXml);
        Assert.IsFalse(failOk);
        Assert.IsTrue(failDiags.Any(d => d.Code == DiagnosticCodes.WaiverRationaleMissing));

        // 2. Waive with rationale -> succeeds
        var successXml = @"<?xml version=""1.0"" encoding=""utf-8""?>
<iteration-confirmation
  id=""20260824T140000Z-confirm-waive-ok""
  iteration=""20260823-xpath-core""
  action=""complete""
  expected_spec_revision=""4""
  expected_tasks_revision=""9""
  actor=""owner""
  decided_at=""2026-08-24T14:00:00Z"">
  <summary>Owner accepted core criteria and waived template append criterion.</summary>
  <acceptance>
    <criterion target=""20260823-accept-directory-overview"" decision=""accepted""/>
    <criterion target=""20260823-accept-resume-task"" decision=""accepted""/>
    <criterion target=""20260823-accept-integrated-verification"" decision=""accepted""/>
    <criterion target=""20260823-accept-no-truncation"" decision=""accepted""/>
    <criterion target=""20260823-accept-structured-reasoning"" decision=""accepted""/>
    <criterion target=""20260823-accept-template-append"" decision=""waived""/>
  </acceptance>
  <rationale>Template append criterion waived because CLI helpers are provided in follow-on iteration.</rationale>
</iteration-confirmation>";

        var (okSuccess, _, okDiags) = IterationConfirmer.Confirm(workspace, successXml);
        Assert.IsTrue(okSuccess, okDiags.Count > 0 ? okDiags[0].Message : "");
    }

    [TestMethod]
    public void Confirm_Completion_RejectsWhenProposedRequirementExists()
    {
        var workspace = CreateWorkspaceCopy();
        MakeAllTasksTerminal(workspace, "20260823-xpath-core");

        // Add a proposed requirement to spec.xml
        var specPath = Path.Combine(workspace, "20260823-xpath-core", "spec.xml");
        var specDoc = XDocument.Load(specPath);
        specDoc.Root?.Element("product")?.Element("requirements")?.Add(new XElement("requirement",
            new XAttribute("id", "20260825-req-unapproved"),
            new XAttribute("status", "proposed"),
            new XElement("index", new XElement("summary", "Proposed req"), new XElement("term", new XAttribute("key", "kind"), new XAttribute("value", "requirement"))),
            new XElement("statement", "Statement"),
            new XElement("rationale", "Rationale")));
        specDoc.Save(specPath);

        var requestXml = @"<?xml version=""1.0"" encoding=""utf-8""?>
<iteration-confirmation
  id=""20260824T140000Z-confirm-complete-blocked""
  iteration=""20260823-xpath-core""
  action=""complete""
  expected_spec_revision=""4""
  expected_tasks_revision=""9""
  actor=""owner""
  decided_at=""2026-08-24T14:00:00Z"">
  <summary>Attempt completion with proposed requirement.</summary>
  <acceptance>
    <criterion target=""20260823-accept-directory-overview"" decision=""accepted""/>
    <criterion target=""20260823-accept-resume-task"" decision=""accepted""/>
    <criterion target=""20260823-accept-integrated-verification"" decision=""accepted""/>
    <criterion target=""20260823-accept-no-truncation"" decision=""accepted""/>
    <criterion target=""20260823-accept-structured-reasoning"" decision=""accepted""/>
    <criterion target=""20260823-accept-template-append"" decision=""accepted""/>
  </acceptance>
</iteration-confirmation>";

        var (success, _, diags) = IterationConfirmer.Confirm(workspace, requestXml);

        Assert.IsFalse(success);
        Assert.IsTrue(diags.Any(d => d.Code == DiagnosticCodes.OwnerDecisionRequired));
    }

    [TestMethod]
    public void Confirm_Completion_RejectsWhenProposedDesignDecisionExists()
    {
        var workspace = CreateWorkspaceCopy();
        MakeAllTasksTerminal(workspace, "20260823-xpath-core");

        // Add a proposed design decision to spec.xml
        var specPath = Path.Combine(workspace, "20260823-xpath-core", "spec.xml");
        var specDoc = XDocument.Load(specPath);
        specDoc.Root?.Element("design")?.Element("decisions")?.Add(new XElement("decision",
            new XAttribute("id", "20260825-dec-unapproved"),
            new XAttribute("status", "proposed"),
            new XElement("index", new XElement("summary", "Proposed dec"), new XElement("term", new XAttribute("key", "kind"), new XAttribute("value", "decision"))),
            new XElement("rationale", "Rationale")));
        specDoc.Save(specPath);

        var requestXml = @"<?xml version=""1.0"" encoding=""utf-8""?>
<iteration-confirmation
  id=""20260824T140000Z-confirm-complete-blocked-dec""
  iteration=""20260823-xpath-core""
  action=""complete""
  expected_spec_revision=""4""
  expected_tasks_revision=""9""
  actor=""owner""
  decided_at=""2026-08-24T14:00:00Z"">
  <summary>Attempt completion with proposed design decision.</summary>
  <acceptance>
    <criterion target=""20260823-accept-directory-overview"" decision=""accepted""/>
    <criterion target=""20260823-accept-resume-task"" decision=""accepted""/>
    <criterion target=""20260823-accept-integrated-verification"" decision=""accepted""/>
    <criterion target=""20260823-accept-no-truncation"" decision=""accepted""/>
    <criterion target=""20260823-accept-structured-reasoning"" decision=""accepted""/>
    <criterion target=""20260823-accept-template-append"" decision=""accepted""/>
  </acceptance>
</iteration-confirmation>";

        var (success, _, diags) = IterationConfirmer.Confirm(workspace, requestXml);

        Assert.IsFalse(success);
        Assert.IsTrue(diags.Any(d => d.Code == DiagnosticCodes.OwnerDecisionRequired));
    }

    [TestMethod]
    public void Confirm_AcceptDesignChange_AppendsNewDesignDecision()
    {
        var workspace = CreateWorkspaceCopy();

        var requestXml = @"<?xml version=""1.0"" encoding=""utf-8""?>
<iteration-confirmation
  id=""20260824T150000Z-confirm-design""
  iteration=""20260823-xpath-core""
  action=""accept-design-change""
  expected_spec_revision=""4""
  expected_tasks_revision=""9""
  actor=""architect""
  decided_at=""2026-08-24T15:00:00Z"">
  <summary>Accepted memory buffer pooling design decision.</summary>
  <new_design_decision id=""20260824-dec-pooling"" status=""proposed"">
    <index>
      <summary>Buffer pooling architecture decision.</summary>
      <term key=""kind"" value=""decision""/>
    </index>
    <rationale>Eliminates GC pressure in tight loop.</rationale>
  </new_design_decision>
  <design>
    <decision target=""20260824-dec-pooling"" decision=""accepted""/>
  </design>
</iteration-confirmation>";

        var (success, _, diags) = IterationConfirmer.Confirm(workspace, requestXml);

        Assert.IsTrue(success, diags.Count > 0 ? diags[0].Message : "");

        var specPath = Path.Combine(workspace, "20260823-xpath-core", "spec.xml");
        var specDoc = XDocument.Load(specPath);
        Assert.AreEqual("active", specDoc.Root?.Attribute("status")?.Value);
        Assert.AreEqual("5", specDoc.Root?.Attribute("revision")?.Value);

        var newDec = specDoc.Root?.Element("design")?.Element("decisions")?.Elements("decision")
            .FirstOrDefault(d => d.Attribute("id")?.Value == "20260824-dec-pooling");
        Assert.IsNotNull(newDec);
        Assert.AreEqual("accepted", newDec.Attribute("status")?.Value);
    }

    [TestMethod]
    public void Confirm_AcceptDesignChange_RejectsNonProposedEmbeddedStatus()
    {
        var workspace = CreateWorkspaceCopy();

        var requestXml = @"<?xml version=""1.0"" encoding=""utf-8""?>
<iteration-confirmation
  id=""20260824T150000Z-confirm-design-invalid-status""
  iteration=""20260823-xpath-core""
  action=""accept-design-change""
  expected_spec_revision=""4""
  expected_tasks_revision=""9""
  actor=""architect""
  decided_at=""2026-08-24T15:00:00Z"">
  <summary>Attempt embedding accepted status in new_design_decision.</summary>
  <new_design_decision id=""20260824-dec-pooling"" status=""accepted"">
    <index>
      <summary>Buffer pooling architecture decision.</summary>
      <term key=""kind"" value=""decision""/>
    </index>
    <rationale>Eliminates GC pressure in tight loop.</rationale>
  </new_design_decision>
  <design>
    <decision target=""20260824-dec-pooling"" decision=""accepted""/>
  </design>
</iteration-confirmation>";

        var (success, _, diags) = IterationConfirmer.Confirm(workspace, requestXml);

        Assert.IsFalse(success);
        Assert.IsTrue(diags.Any(d => d.Code == DiagnosticCodes.InvalidArgument));
    }

    [TestMethod]
    public void Confirm_AcceptDesignChange_RejectsMissingExplicitTarget()
    {
        var workspace = CreateWorkspaceCopy();

        var requestXml = @"<?xml version=""1.0"" encoding=""utf-8""?>
<iteration-confirmation
  id=""20260824T150000Z-confirm-design-missing-target""
  iteration=""20260823-xpath-core""
  action=""accept-design-change""
  expected_spec_revision=""4""
  expected_tasks_revision=""9""
  actor=""architect""
  decided_at=""2026-08-24T15:00:00Z"">
  <summary>Introduced new_design_decision without targeting it in design section.</summary>
  <new_design_decision id=""20260824-dec-pooling"" status=""proposed"">
    <index>
      <summary>Buffer pooling architecture decision.</summary>
      <term key=""kind"" value=""decision""/>
    </index>
    <rationale>Eliminates GC pressure in tight loop.</rationale>
  </new_design_decision>
</iteration-confirmation>";

        var (success, _, diags) = IterationConfirmer.Confirm(workspace, requestXml);

        Assert.IsFalse(success);
        Assert.IsTrue(diags.Any(d => d.Code == DiagnosticCodes.OwnerDecisionRequired));
    }

    [TestMethod]
    public void Confirm_RejectsDuplicateTargetsInRequest()
    {
        var workspace = CreateWorkspaceCopy();

        var requestXml = @"<?xml version=""1.0"" encoding=""utf-8""?>
<iteration-confirmation
  id=""20260824T150000Z-confirm-dup-target""
  iteration=""20260823-xpath-core""
  action=""replan""
  expected_spec_revision=""4""
  actor=""architect""
  decided_at=""2026-08-24T15:00:00Z"">
  <summary>Duplicate target in confirmation request.</summary>
  <requirements>
    <requirement target=""20260823-req-parser"" decision=""approved""/>
    <requirement target=""20260823-req-parser"" decision=""superseded""/>
  </requirements>
</iteration-confirmation>";

        var (success, _, diags) = IterationConfirmer.Confirm(workspace, requestXml);

        Assert.IsFalse(success);
        Assert.IsTrue(diags.Any(d => d.Code == DiagnosticCodes.DuplicateConfirmationTarget));
    }

    #endregion

    #region 3. Revisions, Timestamps, Idempotency & Fault Injection

    [TestMethod]
    public void Confirm_ExpectedRevisionMismatch_FailsWithRevisionConflict()
    {
        var workspace = CreateWorkspaceCopy();

        var requestXml = @"<?xml version=""1.0"" encoding=""utf-8""?>
<iteration-confirmation
  id=""20260824T160000Z-confirm-rev-mismatch""
  iteration=""20260823-xpath-core""
  action=""replan""
  expected_spec_revision=""99""
  actor=""lead""
  decided_at=""2026-08-24T16:00:00Z"">
  <summary>Replan with stale revision.</summary>
</iteration-confirmation>";

        var (success, _, diags) = IterationConfirmer.Confirm(workspace, requestXml);

        Assert.IsFalse(success);
        Assert.IsTrue(diags.Any(d => d.Code == DiagnosticCodes.RevisionConflict));
    }

    [TestMethod]
    public void Confirm_TimestampBackdating_Fails()
    {
        var workspace = CreateWorkspaceCopy();

        // decided_at earlier than updated_at (2026-08-23T04:20:00Z in demo)
        var requestXml = @"<?xml version=""1.0"" encoding=""utf-8""?>
<iteration-confirmation
  id=""20260820T000000Z-confirm-backdate""
  iteration=""20260823-xpath-core""
  action=""replan""
  expected_spec_revision=""4""
  actor=""lead""
  decided_at=""2026-08-20T00:00:00Z"">
  <summary>Replan with backdated timestamp.</summary>
</iteration-confirmation>";

        var (success, _, diags) = IterationConfirmer.Confirm(workspace, requestXml);

        Assert.IsFalse(success);
        Assert.IsTrue(diags.Any(d => d.Code == DiagnosticCodes.InvalidArgument && d.Message.Contains("Backdating")));
    }

    [TestMethod]
    public void Confirm_DurableIdempotency_ExactRetrySucceeds()
    {
        var workspace = CreateWorkspaceCopy();

        var requestXml = @"<?xml version=""1.0"" encoding=""utf-8""?>
<iteration-confirmation
  id=""20260824T170000Z-confirm-idempotent""
  iteration=""20260823-xpath-core""
  action=""replan""
  expected_spec_revision=""4""
  actor=""lead""
  decided_at=""2026-08-24T17:00:00Z"">
  <summary>Replan iteration for testing idempotency.</summary>
</iteration-confirmation>";

        // 1. Initial confirmation succeeds
        var (firstOk, firstEnv, firstDiags) = IterationConfirmer.Confirm(workspace, requestXml);
        Assert.IsTrue(firstOk, firstDiags.Count > 0 ? firstDiags[0].Message : "");
        Assert.IsFalse(firstEnv!.AlreadyApplied);

        // 2. Retry with original expected_spec_revision=4 succeeds with alreadyApplied=true
        var (retryOk1, retryEnv1, retryDiags1) = IterationConfirmer.Confirm(workspace, requestXml);
        Assert.IsTrue(retryOk1, retryDiags1.Count > 0 ? retryDiags1[0].Message : "");
        Assert.IsTrue(retryEnv1!.AlreadyApplied);

        // 3. Retry with current spec revision=5 also succeeds with alreadyApplied=true
        var retryXmlWithNewRev = requestXml.Replace("expected_spec_revision=\"4\"", "expected_spec_revision=\"5\"");
        var (retryOk2, retryEnv2, retryDiags2) = IterationConfirmer.Confirm(workspace, retryXmlWithNewRev);
        Assert.IsTrue(retryOk2, retryDiags2.Count > 0 ? retryDiags2[0].Message : "");
        Assert.IsTrue(retryEnv2!.AlreadyApplied);

        var replaySpecPath = Path.Combine(workspace, "20260823-xpath-core", "spec.xml");
        var replayTasksPath = Path.Combine(workspace, "20260823-xpath-core", "tasks.xml");
        var replaySpecHash = ComputeFileSha256(replaySpecPath);
        var replayTasksHash = ComputeFileSha256(replayTasksPath);
        var pendingRoot = Path.Combine(workspace, "_tmp", "tx_pending_iteration_confirm_replay");
        var pendingDirectory = Path.Combine(pendingRoot, "staged");
        Directory.CreateDirectory(pendingDirectory);
        var (blockedReplay, _, blockedDiagnostics) = IterationConfirmer.Confirm(workspace, requestXml, dryRun: true);
        Assert.IsFalse(blockedReplay);
        Assert.IsTrue(blockedDiagnostics.Any(d => d.Code == DiagnosticCodes.RecoveryFailed));
        Assert.IsTrue(Directory.Exists(pendingDirectory), "Iteration-confirm dry-run must preserve pending recovery artifacts.");
        Assert.AreEqual(replaySpecHash, ComputeFileSha256(replaySpecPath));
        Assert.AreEqual(replayTasksHash, ComputeFileSha256(replayTasksPath));
        Directory.Delete(pendingRoot, recursive: true);

        // 4. Retry same ID with different summary or action -> fails with IDEMPOTENCY_CONFLICT
        var conflictXml = requestXml.Replace("Replan iteration for testing idempotency.", "Changed summary content.");
        var (conflictOk, _, conflictDiags) = IterationConfirmer.Confirm(workspace, conflictXml);
        Assert.IsFalse(conflictOk);
        Assert.IsTrue(conflictDiags.Any(d => d.Code == DiagnosticCodes.IdempotencyConflict));
    }

    [TestMethod]
    public void Confirm_DurableIdempotency_FailsWithConflict_WhenStateDrifts()
    {
        // Reproduction 1: accept-design-change adds new decision, subsequent confirmation supersedes it, replay first confirmation fails
        var workspace = CreateWorkspaceCopy();

        // 1. First accept-design-change adds decision 20260825-dec-audit and accepts it (rev 4 -> 5)
        var firstXml = @"<?xml version=""1.0"" encoding=""utf-8""?>
<iteration-confirmation
  id=""20260825T100000Z-confirm-design-audit""
  iteration=""20260823-xpath-core""
  action=""accept-design-change""
  expected_spec_revision=""4""
  expected_tasks_revision=""9""
  actor=""architect""
  decided_at=""2026-08-25T10:00:00Z"">
  <summary>First design decision added.</summary>
  <new_design_decision id=""20260825-dec-audit"" status=""proposed"">
    <index>
      <summary>Audit design decision.</summary>
      <term key=""kind"" value=""decision""/>
    </index>
    <rationale>Initial audit rationale.</rationale>
  </new_design_decision>
  <design>
    <decision target=""20260825-dec-audit"" decision=""accepted""/>
  </design>
</iteration-confirmation>";

        var (firstOk, _, firstDiags) = IterationConfirmer.Confirm(workspace, firstXml);
        Assert.IsTrue(firstOk, firstDiags.Count > 0 ? firstDiags[0].Message : "");

        // 2. Second accept-design-change explicitly supersedes it (rev 5 -> 6)
        var secondXml = @"<?xml version=""1.0"" encoding=""utf-8""?>
<iteration-confirmation
  id=""20260825T110000Z-confirm-design-supersede""
  iteration=""20260823-xpath-core""
  action=""accept-design-change""
  expected_spec_revision=""5""
  expected_tasks_revision=""9""
  actor=""architect""
  decided_at=""2026-08-25T11:00:00Z"">
  <summary>Superseding the audit decision.</summary>
  <design>
    <decision target=""20260825-dec-audit"" decision=""superseded""/>
  </design>
</iteration-confirmation>";

        var (secondOk, _, secondDiags) = IterationConfirmer.Confirm(workspace, secondXml);
        Assert.IsTrue(secondOk, secondDiags.Count > 0 ? secondDiags[0].Message : "");

        // 3. Replay first confirmation ID with expected_spec_revision=6 -> MUST FAIL with IDEMPOTENCY_CONFLICT
        var replayXmlWithRev6 = firstXml.Replace("expected_spec_revision=\"4\"", "expected_spec_revision=\"6\"");
        var (replayOk6, _, replayDiags6) = IterationConfirmer.Confirm(workspace, replayXmlWithRev6);
        Assert.IsFalse(replayOk6);
        Assert.IsTrue(replayDiags6.Any(d => d.Code == DiagnosticCodes.IdempotencyConflict));

        // 4. Replay first confirmation ID with original expected_spec_revision=4 -> MUST ALSO FAIL with IDEMPOTENCY_CONFLICT
        var (replayOk4, _, replayDiags4) = IterationConfirmer.Confirm(workspace, firstXml);
        Assert.IsFalse(replayOk4);
        Assert.IsTrue(replayDiags4.Any(d => d.Code == DiagnosticCodes.IdempotencyConflict));
    }

    [TestMethod]
    public void Confirm_DurableIdempotency_FailsWithConflict_WhenTimestampOrRevisionDrifts()
    {
        var workspace = CreateWorkspaceCopy();

        // 1. Replan (rev 4 -> 5, updated_at = 2026-08-24T12:00:00Z)
        var replanXml = @"<?xml version=""1.0"" encoding=""utf-8""?>
<iteration-confirmation
  id=""20260824T120000Z-confirm-replan""
  iteration=""20260823-xpath-core""
  action=""replan""
  expected_spec_revision=""4""
  actor=""architect""
  decided_at=""2026-08-24T12:00:00Z"">
  <summary>Replanning iteration.</summary>
</iteration-confirmation>";

        var (replanOk, _, _) = IterationConfirmer.Confirm(workspace, replanXml);
        Assert.IsTrue(replanOk);

        // 2. Continue (rev 5 -> 6, updated_at = 2026-08-24T13:00:00Z)
        var continueXml = @"<?xml version=""1.0"" encoding=""utf-8""?>
<iteration-confirmation
  id=""20260824T130000Z-confirm-continue""
  iteration=""20260823-xpath-core""
  action=""continue""
  expected_spec_revision=""5""
  actor=""architect""
  decided_at=""2026-08-24T13:00:00Z"">
  <summary>Continuing iteration.</summary>
</iteration-confirmation>";

        var (continueOk, _, _) = IterationConfirmer.Confirm(workspace, continueXml);
        Assert.IsTrue(continueOk);

        // 3. Replay replan confirmation with expected_spec_revision=6 -> fails with IDEMPOTENCY_CONFLICT
        var replayReplanXml = replanXml.Replace("expected_spec_revision=\"4\"", "expected_spec_revision=\"6\"");
        var (replayOk, _, replayDiags) = IterationConfirmer.Confirm(workspace, replayReplanXml);
        Assert.IsFalse(replayOk);
        Assert.IsTrue(replayDiags.Any(d => d.Code == DiagnosticCodes.IdempotencyConflict));
    }

    [TestMethod]
    public void Confirm_FaultInjection_RollbackPreservesOriginalBytes()
    {
        var workspace = CreateWorkspaceCopy();
        var specPath = Path.Combine(workspace, "20260823-xpath-core", "spec.xml");
        var specHashBefore = ComputeFileSha256(specPath);

        var requestXml = @"<?xml version=""1.0"" encoding=""utf-8""?>
<iteration-confirmation
  id=""20260824T180000Z-confirm-fault""
  iteration=""20260823-xpath-core""
  action=""replan""
  expected_spec_revision=""4""
  actor=""lead""
  decided_at=""2026-08-24T18:00:00Z"">
  <summary>Fault injection test.</summary>
</iteration-confirmation>";

        var faultInjector = new TestFaultInjector(FaultPhase.AfterStagingBeforeValidation);
        var (success, _, diags) = IterationConfirmer.Confirm(workspace, requestXml, faultInjector: faultInjector);

        Assert.IsFalse(success);
        Assert.IsTrue(diags.Any(d => d.Code == DiagnosticCodes.CommitFailed));

        // Original spec.xml is intact
        var specHashAfter = ComputeFileSha256(specPath);
        Assert.AreEqual(specHashBefore, specHashAfter);
    }

    [TestMethod]
    public void Confirm_InvalidRequirementDecision_ReportsAllowedTokens()
    {
        var workspace = CreateWorkspaceCopy();
        var requestXml = @"<?xml version=""1.0"" encoding=""utf-8""?>
<iteration-confirmation
  id=""20260824T181000Z-confirm-req-inv""
  iteration=""20260823-xpath-core""
  action=""replan""
  expected_spec_revision=""4""
  actor=""owner""
  decided_at=""2026-08-24T18:10:00Z"">
  <summary>Invalid decision test.</summary>
  <requirements>
    <requirement target=""20260823-req-xpath-eval"" decision=""accepted""/>
  </requirements>
</iteration-confirmation>";

        var (success, _, diags) = IterationConfirmer.Confirm(workspace, requestXml);
        Assert.IsFalse(success);
        var diag = diags.FirstOrDefault(d => d.Code == DiagnosticCodes.InvalidArgument);
        Assert.IsNotNull(diag, $"Expected INVALID_ARGUMENT diagnostic, got: {string.Join("; ", diags.Select(d => $"{d.Code}: {d.Message}"))}");
        Assert.IsTrue(diag.Message.Contains("approved, superseded, withdrawn"), $"Diagnostic should report allowed requirement tokens, got: {diag.Message}");
    }

    [TestMethod]
    public void Confirm_InvalidDesignDecision_ReportsAllowedTokens()
    {
        var workspace = CreateWorkspaceCopy();
        var requestXml = @"<?xml version=""1.0"" encoding=""utf-8""?>
<iteration-confirmation
  id=""20260824T181100Z-confirm-des-inv""
  iteration=""20260823-xpath-core""
  action=""replan""
  expected_spec_revision=""4""
  actor=""owner""
  decided_at=""2026-08-24T18:11:00Z"">
  <summary>Invalid design decision test.</summary>
  <design>
    <decision target=""20260823-dec-evaluator-engine"" decision=""approved""/>
  </design>
</iteration-confirmation>";

        var (success, _, diags) = IterationConfirmer.Confirm(workspace, requestXml);
        Assert.IsFalse(success);
        var diag = diags.FirstOrDefault(d => d.Code == DiagnosticCodes.InvalidArgument);
        Assert.IsNotNull(diag, $"Expected INVALID_ARGUMENT diagnostic, got: {string.Join("; ", diags.Select(d => $"{d.Code}: {d.Message}"))}");
        Assert.IsTrue(diag.Message.Contains("accepted, rejected, superseded"), $"Diagnostic should report allowed design tokens, got: {diag.Message}");
    }

    [TestMethod]
    public void Confirm_InvalidAcceptanceDecision_ReportsAllowedTokens()
    {
        var workspace = CreateWorkspaceCopy();
        var requestXml = @"<?xml version=""1.0"" encoding=""utf-8""?>
<iteration-confirmation
  id=""20260824T181200Z-confirm-acc-inv""
  iteration=""20260823-xpath-core""
  action=""replan""
  expected_spec_revision=""4""
  actor=""owner""
  decided_at=""2026-08-24T18:12:00Z"">
  <summary>Invalid acceptance decision test.</summary>
  <acceptance>
    <criterion target=""20260823-accept-xpath-eval"" decision=""approved""/>
  </acceptance>
</iteration-confirmation>";

        var (success, _, diags) = IterationConfirmer.Confirm(workspace, requestXml);
        Assert.IsFalse(success);
        var diag = diags.FirstOrDefault(d => d.Code == DiagnosticCodes.InvalidArgument);
        Assert.IsNotNull(diag, $"Expected INVALID_ARGUMENT diagnostic, got: {string.Join("; ", diags.Select(d => $"{d.Code}: {d.Message}"))}");
        Assert.IsTrue(diag.Message.Contains("accepted, rejected, waived"), $"Diagnostic should report allowed acceptance tokens, got: {diag.Message}");
    }

    [TestMethod]
    public void Confirm_AcceptDesignChange_WithNonDesignTargets_FailsWithActionInapplicable()
    {
        var workspace = CreateWorkspaceCopy();
        var requestXml = @"<?xml version=""1.0"" encoding=""utf-8""?>
<iteration-confirmation
  id=""20260824T181300Z-confirm-act-inapp""
  iteration=""20260823-xpath-core""
  action=""accept-design-change""
  expected_spec_revision=""4""
  actor=""owner""
  decided_at=""2026-08-24T18:13:00Z"">
  <summary>Action inapplicable test.</summary>
  <requirements>
    <requirement target=""20260823-req-xpath-eval"" decision=""approved""/>
  </requirements>
</iteration-confirmation>";

        var (success, _, diags) = IterationConfirmer.Confirm(workspace, requestXml);
        Assert.IsFalse(success);
        var diag = diags.FirstOrDefault(d => d.Code == DiagnosticCodes.OwnerDecisionRequired);
        Assert.IsNotNull(diag, $"Expected OWNER_DECISION_REQUIRED diagnostic, got: {string.Join("; ", diags.Select(d => $"{d.Code}: {d.Message}"))}");
        Assert.IsTrue(diag.Message.Contains("<design>"), $"Diagnostic should report allowed target <design>, got: {diag.Message}");
    }

    [TestMethod]
    public void Confirm_SynchronizesStatusTerms_SpecRootAndDecisions()
    {
        var workspace = CreateWorkspaceCopy();
        var specPath = Path.Combine(workspace, "20260823-xpath-core", "spec.xml");
        var xdocBefore = XDocument.Load(specPath);
        xdocBefore.Root!.Element("index")!.Add(new XElement("term", new XAttribute("key", "status"), new XAttribute("value", "active")));
        xdocBefore.Save(specPath);
        var rootStatusTermBefore = xdocBefore.Root!.Element("index")?.Elements("term").FirstOrDefault(t => (string?)t.Attribute("key") == "status");
        Assert.IsNotNull(rootStatusTermBefore);
        Assert.AreEqual("active", (string?)rootStatusTermBefore.Attribute("value"));

        var replanXml = @"<?xml version=""1.0"" encoding=""utf-8""?>
<iteration-confirmation
  id=""20260824T181500Z-confirm-sync-term""
  iteration=""20260823-xpath-core""
  action=""replan""
  expected_spec_revision=""4""
  actor=""lead""
  decided_at=""2026-08-24T18:15:00Z"">
  <summary>Moving to replanning to test term sync.</summary>
</iteration-confirmation>";

        var (success, _, diags) = IterationConfirmer.Confirm(workspace, replanXml);
        Assert.IsTrue(success, string.Join("; ", diags.Select(d => d.Message)));

        var xdocAfter = XDocument.Load(specPath);
        Assert.AreEqual("replanning", (string?)xdocAfter.Root!.Attribute("status"));
        var rootStatusTermAfter = xdocAfter.Root!.Element("index")?.Elements("term").FirstOrDefault(t => (string?)t.Attribute("key") == "status");
        Assert.IsNotNull(rootStatusTermAfter);
        Assert.AreEqual("replanning", (string?)rootStatusTermAfter.Attribute("value"));

        var valResult = SchemaValidator.Validate(workspace);
        Assert.IsTrue(valResult.IsValid, string.Join("; ", valResult.Diagnostics.Select(d => $"{d.Code}: {d.Message}")));
    }

    [TestMethod]
    public void CriterionAuthor_DefineAndAdd_Success_FeatureAndResearch()
    {
        var workspace = CreateWorkspaceCopy();
        var fixedTime = new DateTime(2026, 8, 27, 10, 0, 0, DateTimeKind.Utc);
        var clock = new TestClock(fixedTime);

        // 1. Feature iteration define and add
        var featIterId = "20260827-crit-feat";
        var (fCreateOk, _, _) = IterationCreator.Create(workspace, featIterId, "feature", clock);
        Assert.IsTrue(fCreateOk);

        var (defOk, defEnv, defDiags) = IterationCriterionAuthor.Define(
            workspace,
            featIterId,
            "Defined first feature criterion.",
            clock: clock);
        Assert.IsTrue(defOk, string.Join("; ", defDiags.Select(d => d.Message)));
        Assert.IsNotNull(defEnv);

        var featSpec1 = XDocument.Load(Path.Combine(workspace, featIterId, "spec.xml"));
        Assert.AreEqual("2", featSpec1.Root?.Attribute("revision")?.Value);
        var featCrit1 = featSpec1.Descendants("criterion").ToList();
        Assert.AreEqual(1, featCrit1.Count);
        Assert.AreEqual("20260827-crit-crit-feat", featCrit1[0].Attribute("id")?.Value);
        Assert.AreEqual("Defined first feature criterion.", featCrit1[0].Value);

        var (addOk, addEnv, addDiags) = IterationCriterionAuthor.Add(
            workspace,
            featIterId,
            "Added second feature criterion.",
            clock: clock);
        Assert.IsTrue(addOk, string.Join("; ", addDiags.Select(d => d.Message)));
        Assert.IsNotNull(addEnv);

        var featSpec2 = XDocument.Load(Path.Combine(workspace, featIterId, "spec.xml"));
        Assert.AreEqual("3", featSpec2.Root?.Attribute("revision")?.Value);
        var featCrit2 = featSpec2.Descendants("criterion").ToList();
        Assert.AreEqual(2, featCrit2.Count);
        Assert.AreEqual("20260827-crit-crit-feat", featCrit2[0].Attribute("id")?.Value);
        Assert.AreEqual("20260827-crit-crit-feat-2", featCrit2[1].Attribute("id")?.Value);
        Assert.AreEqual("Added second feature criterion.", featCrit2[1].Value);

        // 2. Research iteration define and add
        var resIterId = "20260827-crit-res";
        var (rCreateOk, _, _) = IterationCreator.Create(workspace, resIterId, "research", clock);
        Assert.IsTrue(rCreateOk);

        var (rDefOk, _, rDefDiags) = IterationCriterionAuthor.Define(
            workspace,
            resIterId,
            "Defined first research criterion.",
            clock: clock);
        Assert.IsTrue(rDefOk, string.Join("; ", rDefDiags.Select(d => d.Message)));

        var (rAddOk, _, rAddDiags) = IterationCriterionAuthor.Add(
            workspace,
            resIterId,
            "Added second research criterion.",
            clock: clock);
        Assert.IsTrue(rAddOk, string.Join("; ", rAddDiags.Select(d => d.Message)));

        var resSpec = XDocument.Load(Path.Combine(workspace, resIterId, "spec.xml"));
        Assert.AreEqual("3", resSpec.Root?.Attribute("revision")?.Value);
        var resCrit = resSpec.Descendants("criterion").ToList();
        Assert.AreEqual(2, resCrit.Count);
        Assert.AreEqual("20260827-crit-crit-res", resCrit[0].Attribute("id")?.Value);
        Assert.AreEqual("20260827-crit-crit-res-2", resCrit[1].Attribute("id")?.Value);

        // Whole workspace validation
        var valResult = SchemaValidator.Validate(workspace);
        Assert.IsTrue(valResult.IsValid, string.Join("; ", valResult.Diagnostics.Select(d => $"{d.Code}: {d.Message}")));
    }

    [TestMethod]
    public void CriterionAuthor_StaleRevision_FailsClosed()
    {
        var workspace = CreateWorkspaceCopy();
        var iterId = "20260827-stale-rev";
        var (createOk, _, _) = IterationCreator.Create(workspace, iterId, "feature");
        Assert.IsTrue(createOk);

        var specPath = Path.Combine(workspace, iterId, "spec.xml");
        var hashBefore = ComputeFileSha256(specPath);

        // 1. Define with stale expected revision (expected 99, actual is 1)
        var (defOk, _, defDiags) = IterationCriterionAuthor.Define(
            workspace,
            iterId,
            "Defined criterion",
            expectedSpecRevision: 99);
        Assert.IsFalse(defOk);
        Assert.IsTrue(defDiags.Any(d => d.Code == DiagnosticCodes.RevisionConflict));
        Assert.AreEqual(hashBefore, ComputeFileSha256(specPath));

        // 2. Add with stale expected revision (expected 99, actual is 1)
        var (addOk, _, addDiags) = IterationCriterionAuthor.Add(
            workspace,
            iterId,
            "Added criterion",
            expectedSpecRevision: 99);
        Assert.IsFalse(addOk);
        Assert.IsTrue(addDiags.Any(d => d.Code == DiagnosticCodes.RevisionConflict));
        Assert.AreEqual(hashBefore, ComputeFileSha256(specPath));
    }

    [TestMethod]
    public void CriterionAuthor_BlankAndPlaceholder_Rejected()
    {
        var workspace = CreateWorkspaceCopy();
        var iterId = "20260827-blank-crit";
        var (createOk, _, _) = IterationCreator.Create(workspace, iterId, "feature");
        Assert.IsTrue(createOk);

        var specPath = Path.Combine(workspace, iterId, "spec.xml");
        var hashBefore = ComputeFileSha256(specPath);

        // Blank text
        var (defEmptyOk, _, defEmptyDiags) = IterationCriterionAuthor.Define(workspace, iterId, "   \t ");
        Assert.IsFalse(defEmptyOk);
        Assert.IsTrue(defEmptyDiags.Any(d => d.Code == DiagnosticCodes.CriterionUndefined));
        Assert.AreEqual(hashBefore, ComputeFileSha256(specPath));

        // Seeded placeholder literal
        var (defPlOk, _, defPlDiags) = IterationCriterionAuthor.Define(workspace, iterId, "Product criterion pending definition.");
        Assert.IsFalse(defPlOk);
        Assert.IsTrue(defPlDiags.Any(d => d.Code == DiagnosticCodes.CriterionUndefined));
        Assert.AreEqual(hashBefore, ComputeFileSha256(specPath));

        // Add with placeholder literal
        var (addPlOk, _, addPlDiags) = IterationCriterionAuthor.Add(workspace, iterId, "Research criterion pending definition.");
        Assert.IsFalse(addPlOk);
        Assert.IsTrue(addPlDiags.Any(d => d.Code == DiagnosticCodes.CriterionUndefined));
        Assert.AreEqual(hashBefore, ComputeFileSha256(specPath));
    }

    [TestMethod]
    public void CriterionAuthor_DecidedCriterionRewrite_Rejected()
    {
        var workspace = CreateWorkspaceCopy();
        var iterId = "20260827-decided-rewrite";
        var clock = new TestClock(new DateTime(2026, 8, 27, 12, 0, 0, DateTimeKind.Utc));
        var (createOk, _, _) = IterationCreator.Create(workspace, iterId, "feature", clock, criteria: InitialApprovedCriterion);
        Assert.IsTrue(createOk);

        // Activate iteration with accepted criterion
        var activateXml = $"""
<iteration-confirmation id="20260827T120000Z-confirm-activate" iteration="{iterId}" action="activate" expected_spec_revision="1" actor="owner" decided_at="2026-08-27T12:00:00Z">
  <summary>Activation.</summary>
  <requirements><requirement target="20260827-req-decided-rewrite" decision="approved"/></requirements>
  <acceptance><criterion target="20260827-crit-decided-rewrite" decision="accepted"/></acceptance>
</iteration-confirmation>
""";
        var (actOk, _, actDiags) = IterationConfirmer.Confirm(workspace, activateXml);
        Assert.IsTrue(actOk, string.Join("; ", actDiags.Select(d => d.Message)));

        var specPath = Path.Combine(workspace, iterId, "spec.xml");
        var hashBefore = ComputeFileSha256(specPath);

        // Attempt to rewrite decided criterion
        var (rewriteOk, _, rewriteDiags) = IterationCriterionAuthor.Define(
            workspace,
            iterId,
            "Rewriting decided criterion text",
            criterionId: "20260827-crit-decided-rewrite");
        Assert.IsFalse(rewriteOk);
        Assert.IsTrue(rewriteDiags.Any(d => d.Code == DiagnosticCodes.OwnerDecisionRequired));
        Assert.IsTrue(rewriteDiags.First(d => d.Code == DiagnosticCodes.OwnerDecisionRequired).Message.Contains("Decided criteria cannot be rewritten"));
        Assert.AreEqual(hashBefore, ComputeFileSha256(specPath));
    }

    [TestMethod]
    public void Readiness_ActivationAndCompletion_ReportsCriteriaDefinedFailed_WhenPlaceholder()
    {
        var workspace = CreateWorkspaceCopy();
        var iterId = "20260827-readiness-placeholder";
        var (createOk, _, _) = IterationCreator.Create(workspace, iterId, "feature");
        Assert.IsTrue(createOk);

        // 1. Activation phase: placeholder fails criteria_defined
        var (actSuccess, actResult, _) = IterationReadiness.Assess(workspace, iterId, "activation");
        Assert.IsTrue(actSuccess);
        Assert.IsNotNull(actResult);
        Assert.IsFalse(actResult.TechnicallyReady);
        var actCritCheck = actResult.TechnicalChecks.FirstOrDefault(c => c.Name == "criteria_defined");
        Assert.IsNotNull(actCritCheck);
        Assert.AreEqual("failed", actCritCheck.Result);
        var actVerifDim = actResult.Dimensions.FirstOrDefault(d => d.Name == "verification_completeness");
        Assert.IsNotNull(actVerifDim);
        Assert.AreEqual("failed", actVerifDim.Status);

        // 2. Completion phase: active iteration with all tasks terminal but criterion still placeholder
        MakeAllTasksTerminal(workspace, "20260823-xpath-core");
        var activeSpecPath = Path.Combine(workspace, "20260823-xpath-core", "spec.xml");
        var activeSpecDoc = XDocument.Load(activeSpecPath);
        var firstCrit = activeSpecDoc.Descendants("criterion").First();
        firstCrit.Value = "Product criterion pending definition.";
        activeSpecDoc.Save(activeSpecPath);

        var (compSuccess, compResult, compDiags) = IterationReadiness.Assess(workspace, "20260823-xpath-core", "completion");
        Assert.IsTrue(compSuccess, string.Join("; ", compDiags.Select(d => d.Message)));
        Assert.IsNotNull(compResult);
        Assert.IsFalse(compResult.TechnicallyReady);
        var compCritCheck = compResult.TechnicalChecks.FirstOrDefault(c => c.Name == "criteria_defined");
        Assert.IsNotNull(compCritCheck);
        Assert.AreEqual("failed", compCritCheck.Result);
        var compVerifDim = compResult.Dimensions.FirstOrDefault(d => d.Name == "verification_completeness");
        Assert.IsNotNull(compVerifDim);
        Assert.AreEqual("failed", compVerifDim.Status);
    }

    [TestMethod]
    public void Confirm_RawActivateContinueComplete_FailsClosed_OnUndefinedCriterion()
    {
        var workspace = CreateWorkspaceCopy();
        var iterId = "20260827-failclosed-conf";
        var clock = new TestClock(new DateTime(2026, 8, 27, 14, 0, 0, DateTimeKind.Utc));
        var (createOk, _, _) = IterationCreator.Create(workspace, iterId, "feature", clock);
        Assert.IsTrue(createOk);

        var specPath = Path.Combine(workspace, iterId, "spec.xml");
        var tasksPath = Path.Combine(workspace, iterId, "tasks.xml");
        var specHashBefore = ComputeFileSha256(specPath);
        var tasksHashBefore = ComputeFileSha256(tasksPath);

        // 1. Raw activate on draft iteration with placeholder criterion fails closed
        var activateXml = $"""
<iteration-confirmation id="20260827T140000Z-confirm-act-fail" iteration="{iterId}" action="activate" expected_spec_revision="1" actor="owner" decided_at="2026-08-27T14:00:00Z">
  <summary>Activate with placeholder.</summary>
  <requirements><requirement target="20260827-req-failclosed-conf" decision="approved"/></requirements>
</iteration-confirmation>
""";
        var (actOk, _, actDiags) = IterationConfirmer.Confirm(workspace, activateXml);
        Assert.IsFalse(actOk);
        Assert.IsTrue(actDiags.Any(d => d.Code == DiagnosticCodes.CriterionUndefined));
        Assert.AreEqual(specHashBefore, ComputeFileSha256(specPath));
        Assert.AreEqual(tasksHashBefore, ComputeFileSha256(tasksPath));

        // 2. Raw continue on replanning iteration with placeholder criterion fails closed
        var specDoc = XDocument.Load(specPath);
        specDoc.Root!.SetAttributeValue("status", "replanning");
        specDoc.Save(specPath);
        var specHashReplanning = ComputeFileSha256(specPath);

        var continueXml = $"""
<iteration-confirmation id="20260827T140100Z-confirm-cont-fail" iteration="{iterId}" action="continue" expected_spec_revision="1" actor="owner" decided_at="2026-08-27T14:01:00Z">
  <summary>Continue with placeholder.</summary>
  <requirements><requirement target="20260827-req-failclosed-conf" decision="approved"/></requirements>
</iteration-confirmation>
""";
        var (contOk, _, contDiags) = IterationConfirmer.Confirm(workspace, continueXml);
        Assert.IsFalse(contOk);
        Assert.IsTrue(contDiags.Any(d => d.Code == DiagnosticCodes.CriterionUndefined));
        Assert.AreEqual(specHashReplanning, ComputeFileSha256(specPath));
        Assert.AreEqual(tasksHashBefore, ComputeFileSha256(tasksPath));

        // 3. Raw complete on active iteration with placeholder criterion fails closed
        specDoc = XDocument.Load(specPath);
        specDoc.Root!.SetAttributeValue("status", "active");
        specDoc.Save(specPath);
        MakeAllTasksTerminal(workspace, iterId);
        var specHashActive = ComputeFileSha256(specPath);
        var tasksHashTerminal = ComputeFileSha256(tasksPath);

        var completeXml = $"""
<iteration-confirmation id="20260827T140200Z-confirm-comp-fail" iteration="{iterId}" action="complete" expected_spec_revision="1" expected_tasks_revision="1" actor="owner" decided_at="2026-08-27T14:02:00Z">
  <summary>Complete with placeholder.</summary>
  <requirements><requirement target="20260827-req-failclosed-conf" decision="approved"/></requirements>
  <acceptance><criterion target="20260827-crit-failclosed-conf" decision="accepted"/></acceptance>
</iteration-confirmation>
""";
        var (compOk, _, compDiags) = IterationConfirmer.Confirm(workspace, completeXml);
        Assert.IsFalse(compOk);
        Assert.IsTrue(compDiags.Any(d => d.Code == DiagnosticCodes.CriterionUndefined));
        Assert.AreEqual(specHashActive, ComputeFileSha256(specPath));
        Assert.AreEqual(tasksHashTerminal, ComputeFileSha256(tasksPath));
    }

    #endregion
}
