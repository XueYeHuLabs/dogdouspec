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
        Assert.IsTrue(result.TechnicallyReady);
        Assert.AreEqual("activate", result.RequiredAction.Action);
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
        var (createOk, _, _) = IterationCreator.Create(workspace, iterId, "feature");
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
        var (createOk, _, _) = IterationCreator.Create(workspace, iterId, "feature");
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

    #endregion
}
