using System.Runtime.CompilerServices;
using DogdouSpec.Core.Diagnostics;
using DogdouSpec.Core.Validation;
using DogdouSpec.Core.Workspace;

namespace DogdouSpec.Core.Tests;

[TestClass]
public sealed class SemanticValidationTests
{
    private static string RepoRoot = null!;
    private string _tempDir = null!;

    [ClassInitialize]
    public static void ClassInit(TestContext context)
    {
        RepoRoot = FindRepositoryRootFromSource()
            ?? FindRepositoryRoot(Environment.CurrentDirectory)
            ?? FindRepositoryRoot(AppDomain.CurrentDomain.BaseDirectory)
            ?? string.Empty;
        Assert.IsFalse(string.IsNullOrEmpty(RepoRoot), "Repository root could not be located.");
    }

    private static string? FindRepositoryRoot(string startDirectory)
    {
        if (string.IsNullOrWhiteSpace(startDirectory))
        {
            return null;
        }

        for (var current = new DirectoryInfo(Path.GetFullPath(startDirectory)); current != null; current = current.Parent)
        {
            if (File.Exists(Path.Combine(current.FullName, "DogdouSpec.slnx")) ||
                File.Exists(Path.Combine(current.FullName, "DogdouSpec.sln")))
            {
                return current.FullName;
            }
        }

        return null;
    }

    private static string? FindRepositoryRootFromSource([CallerFilePath] string sourceFile = "") =>
        FindRepositoryRoot(Path.GetDirectoryName(sourceFile) ?? string.Empty);

    [TestInitialize]
    public void SetUp()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "DogdouSpec_SemanticValTests_" + Guid.NewGuid().ToString("N"));
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

    #region 1. Baseline Valid Workspaces

    [TestMethod]
    public void Validate_DemoWorkspace_PassesSemanticValidation()
    {
        var workspace = CreateWorkspaceCopy();

        var result = SchemaValidator.Validate(workspace);

        Assert.IsTrue(result.IsValid, $"Demo workspace failed semantic validation: {string.Join("; ", result.Diagnostics.Select(d => $"{d.Code}: {d.Message}"))}");
        Assert.AreEqual(0, result.Diagnostics.Count(d => d.Severity == "error"));
        Assert.AreEqual(4, result.CheckedDocumentsCount);
    }

    [TestMethod]
    public void Validate_ValidResearchIteration_PassesSemanticValidation()
    {
        var workspace = CreateWorkspaceCopy();

        // Create research iteration directory
        var researchIterDir = Path.Combine(workspace, "20260823-research-projection-order");
        Directory.CreateDirectory(researchIterDir);

        var researchFixture = Path.Combine(RepoRoot, "schemas", "v1", "fixtures", "research-spec.xml");
        File.Copy(researchFixture, Path.Combine(researchIterDir, "spec.xml"));

        var tasksXml = """
<?xml version="1.0" encoding="utf-8"?>
<tasks
  id="20260823-research-tasks"
  iteration="20260823-research-projection-order"
  schema_version="1.0"
  revision="1">
  <index>
    <summary>Research Tasks</summary>
    <term key="iteration" value="20260823-research-projection-order"/>
  </index>
</tasks>
""";
        File.WriteAllText(Path.Combine(researchIterDir, "tasks.xml"), tasksXml);

        var result = SchemaValidator.Validate(workspace, iterationId: "20260823-research-projection-order");

        Assert.IsTrue(result.IsValid, $"Research iteration failed validation: {string.Join("; ", result.Diagnostics.Select(d => $"{d.Code}: {d.Message}"))}");
        Assert.AreEqual(0, result.Diagnostics.Count(d => d.Severity == "error"));
        Assert.AreEqual(2, result.CheckedDocumentsCount);
    }

    #endregion

    #region 2. Identity and Document Ownership

    [TestMethod]
    public void Validate_DuplicateIdAcrossDocuments_FailsWithDuplicateId()
    {
        var workspace = CreateWorkspaceCopy();

        // Put duplicate ID in knowledge.xml that collides with a task in tasks.xml
        var knowledgePath = Path.Combine(workspace, "knowledge.xml");
        var knowledgeContent = File.ReadAllText(knowledgePath);
        knowledgeContent = knowledgeContent.Replace(
            "20260801-knowledge-xml-authority",
            "20260823-task-iteration-layout"); // Collides with tasks.xml task id
        File.WriteAllText(knowledgePath, knowledgeContent);

        var result = SchemaValidator.Validate(workspace);

        Assert.IsFalse(result.IsValid);
        Assert.IsTrue(result.Diagnostics.Any(d => d.Code == DiagnosticCodes.DuplicateId));
        var dupDiags = result.Diagnostics.Where(d => d.Code == DiagnosticCodes.DuplicateId).ToList();
        Assert.IsTrue(dupDiags.Count >= 2, "Duplicate ID must be reported for each duplicate occurrence");
    }

    [TestMethod]
    public void Validate_DuplicateIdWithinDocument_FailsWithDuplicateId()
    {
        var workspace = CreateWorkspaceCopy();

        var tasksPath = Path.Combine(workspace, "20260823-xpath-core", "tasks.xml");
        var tasksContent = File.ReadAllText(tasksPath);
        tasksContent = tasksContent.Replace(
            "20260823-task-task-history",
            "20260823-task-xpath-projection"); // Duplicate task ID
        File.WriteAllText(tasksPath, tasksContent);

        var result = SchemaValidator.Validate(workspace);

        Assert.IsFalse(result.IsValid);
        Assert.IsTrue(result.Diagnostics.Any(d => d.Code == DiagnosticCodes.DuplicateId));
    }

    [TestMethod]
    public void Validate_InvalidTimeFirstIdGrammar_FailsWithInvalidIdGrammar()
    {
        var workspace = CreateWorkspaceCopy();

        var tasksPath = Path.Combine(workspace, "20260823-xpath-core", "tasks.xml");
        var tasksContent = File.ReadAllText(tasksPath);
        // Change a task ID to non-time-first format (violating [0-9]{8}...)
        tasksContent = tasksContent.Replace(
            "id=\"20260823-task-task-history\"",
            "id=\"task-without-date-prefix\"");
        File.WriteAllText(tasksPath, tasksContent);

        // Even though XSD also checks, semantic validator reports stable diagnostic
        var result = SchemaValidator.Validate(workspace);

        Assert.IsFalse(result.IsValid);
        Assert.IsTrue(result.Diagnostics.Any(d => d.Code == DiagnosticCodes.InvalidIdGrammar || d.Code == DiagnosticCodes.SchemaValidationError));
    }

    [TestMethod]
    public void Validate_IterationRootIdMismatch_FailsWithIterationIdMismatch()
    {
        var workspace = CreateWorkspaceCopy();

        var specPath = Path.Combine(workspace, "20260823-xpath-core", "spec.xml");
        var specContent = File.ReadAllText(specPath);
        // Change root ID to something different from directory name
        specContent = specContent.Replace(
            "id=\"20260823-xpath-core\"",
            "id=\"20260823-different-id\"");
        File.WriteAllText(specPath, specContent);

        var result = SchemaValidator.Validate(workspace);

        Assert.IsFalse(result.IsValid);
        Assert.IsTrue(result.Diagnostics.Any(d => d.Code == DiagnosticCodes.IterationIdMismatch));
    }

    [TestMethod]
    public void Validate_TasksIterationMismatch_FailsWithTasksIterationMismatch()
    {
        var workspace = CreateWorkspaceCopy();

        var tasksPath = Path.Combine(workspace, "20260823-xpath-core", "tasks.xml");
        var tasksContent = File.ReadAllText(tasksPath);
        // Change iteration attribute on <tasks>
        tasksContent = tasksContent.Replace(
            "iteration=\"20260823-xpath-core\"",
            "iteration=\"20260823-mismatched-iter\"");
        File.WriteAllText(tasksPath, tasksContent);

        var result = SchemaValidator.Validate(workspace);

        Assert.IsFalse(result.IsValid);
        Assert.IsTrue(result.Diagnostics.Any(d => d.Code == DiagnosticCodes.TasksIterationMismatch));
    }

    [TestMethod]
    public void Validate_WorkKindFeatureWithResearchBody_FailsWithWorkKindMismatch()
    {
        var workspace = CreateWorkspaceCopy();

        var specPath = Path.Combine(workspace, "20260823-xpath-core", "spec.xml");
        var specContent = File.ReadAllText(specPath);
        // Change <product> to <research> while kind is still feature
        specContent = specContent.Replace("<product>", "<research><objective>test</objective><questions><question id=\"20260823-q1\" status=\"open\"><index><summary>s</summary></index><statement>s</statement><rationale>r</rationale></question></questions><method>m</method><boundaries><item>b</item></boundaries><outputs><item>o</item></outputs><acceptance><criterion id=\"20260823-c1\" decision=\"pending\">c</criterion></acceptance></research><product-ignored>")
                                 .Replace("</product>", "</product-ignored>");
        File.WriteAllText(specPath, specContent);

        var result = SchemaValidator.Validate(workspace);

        Assert.IsFalse(result.IsValid);
        Assert.IsTrue(result.Diagnostics.Any(d => d.Code == DiagnosticCodes.WorkKindMismatch || d.Code == DiagnosticCodes.SchemaValidationError));
    }

    [TestMethod]
    public void Validate_WorkKindResearchWithProductBody_FailsWithWorkKindMismatch()
    {
        var workspace = CreateWorkspaceCopy();

        // Create research iter directory with feature spec.xml content (having <product>)
        var researchIterDir = Path.Combine(workspace, "20260823-research-test");
        Directory.CreateDirectory(researchIterDir);

        var demoSpec = File.ReadAllText(Path.Combine(workspace, "20260823-xpath-core", "spec.xml"));
        var modifiedSpec = demoSpec.Replace("id=\"20260823-xpath-core\"", "id=\"20260823-research-test\"")
                                   .Replace("kind=\"feature\"", "kind=\"research\"");
        File.WriteAllText(Path.Combine(researchIterDir, "spec.xml"), modifiedSpec);

        var tasksXml = """
<?xml version="1.0" encoding="utf-8"?>
<tasks id="20260823-research-tasks" iteration="20260823-research-test" schema_version="1.0" revision="1">
  <index><summary>Tasks</summary></index>
</tasks>
""";
        File.WriteAllText(Path.Combine(researchIterDir, "tasks.xml"), tasksXml);

        var result = SchemaValidator.Validate(workspace, iterationId: "20260823-research-test");

        Assert.IsFalse(result.IsValid);
        Assert.IsTrue(result.Diagnostics.Any(d => d.Code == DiagnosticCodes.WorkKindMismatch));
    }

    #endregion

    #region 3. Reference Resolution and Scoping

    [TestMethod]
    public void Validate_DanglingReference_FailsWithDanglingReference()
    {
        var workspace = CreateWorkspaceCopy();

        var tasksPath = Path.Combine(workspace, "20260823-xpath-core", "tasks.xml");
        var tasksContent = File.ReadAllText(tasksPath).Replace("\r\n", "\n");
        tasksContent = tasksContent.Replace(
            "target=\"20260823-req-iteration-discovery\"",
            "target=\"20260823-req-non-existent\"");
        File.WriteAllText(tasksPath, tasksContent);

        var result = SchemaValidator.Validate(workspace);

        Assert.IsFalse(result.IsValid);
        Assert.IsTrue(result.Diagnostics.Any(d => d.Code == DiagnosticCodes.DanglingReference));
    }

    [TestMethod]
    public void Validate_DocumentScopeReferenceTargetingOtherDocument_FailsWithScopeViolation()
    {
        var workspace = CreateWorkspaceCopy();

        var tasksPath = Path.Combine(workspace, "20260823-xpath-core", "tasks.xml");
        var tasksContent = File.ReadAllText(tasksPath).Replace("\r\n", "\n");
        // Change origin ref (which targets spec.xml) to scope="document"
        tasksContent = tasksContent.Replace(
            "scope=\"iteration\"\n        target=\"20260823-req-iteration-discovery\"",
            "scope=\"document\"\n        target=\"20260823-req-iteration-discovery\"");
        File.WriteAllText(tasksPath, tasksContent);

        var result = SchemaValidator.Validate(workspace);

        Assert.IsFalse(result.IsValid);
        Assert.IsTrue(result.Diagnostics.Any(d => d.Code == DiagnosticCodes.ReferenceScopeViolation));
    }

    [TestMethod]
    public void Validate_IterationScopeReferenceTargetingOtherIteration_FailsWithScopeViolation()
    {
        var workspace = CreateWorkspaceCopy();

        // In spec.xml, change source ref to scope="iteration" while targeting knowledge.xml (outside iteration)
        var specPath = Path.Combine(workspace, "20260823-xpath-core", "spec.xml");
        var specContent = File.ReadAllText(specPath).Replace("\r\n", "\n");
        specContent = specContent.Replace(
            "scope=\"project\"\n            target=\"20260801-knowledge-xml-authority\"",
            "scope=\"iteration\"\n            target=\"20260801-knowledge-xml-authority\"");
        File.WriteAllText(specPath, specContent);

        var result = SchemaValidator.Validate(workspace);

        Assert.IsFalse(result.IsValid);
        Assert.IsTrue(result.Diagnostics.Any(d => d.Code == DiagnosticCodes.ReferenceScopeViolation));
    }

    [TestMethod]
    public void Validate_ScopeNotNarrowest_SameDocumentRefUsingIterationScope_FailsWithScopeNotNarrowest()
    {
        var workspace = CreateWorkspaceCopy();

        var tasksPath = Path.Combine(workspace, "20260823-xpath-core", "tasks.xml");
        var tasksContent = File.ReadAllText(tasksPath).Replace("\r\n", "\n");
        // Change task dependency ref (in same tasks.xml) from scope="document" to scope="iteration"
        tasksContent = tasksContent.Replace(
            "scope=\"document\"\n        target=\"20260823-task-iteration-layout\"",
            "scope=\"iteration\"\n        target=\"20260823-task-iteration-layout\"");
        File.WriteAllText(tasksPath, tasksContent);

        var result = SchemaValidator.Validate(workspace);

        Assert.IsFalse(result.IsValid);
        Assert.IsTrue(result.Diagnostics.Any(d => d.Code == DiagnosticCodes.ReferenceScopeNotNarrowest));
    }

    [TestMethod]
    public void Validate_ScopeNotNarrowest_SameIterationRefUsingProjectScope_FailsWithScopeNotNarrowest()
    {
        var workspace = CreateWorkspaceCopy();

        var tasksPath = Path.Combine(workspace, "20260823-xpath-core", "tasks.xml");
        var tasksContent = File.ReadAllText(tasksPath).Replace("\r\n", "\n");
        // Change origin ref from scope="iteration" to scope="project"
        tasksContent = tasksContent.Replace(
            "scope=\"iteration\"\n        target=\"20260823-req-iteration-discovery\"",
            "scope=\"project\"\n        target=\"20260823-req-iteration-discovery\"");
        File.WriteAllText(tasksPath, tasksContent);

        var result = SchemaValidator.Validate(workspace);

        Assert.IsFalse(result.IsValid);
        Assert.IsTrue(result.Diagnostics.Any(d => d.Code == DiagnosticCodes.ReferenceScopeNotNarrowest));
    }

    [TestMethod]
    public void Validate_InvalidReferenceTargetType_TaskDependencyNotTargetingTask_Fails()
    {
        var workspace = CreateWorkspaceCopy();

        var tasksPath = Path.Combine(workspace, "20260823-xpath-core", "tasks.xml");
        var tasksContent = File.ReadAllText(tasksPath).Replace("\r\n", "\n");
        // Change task depends-on to target a criterion or requirement instead of a task
        tasksContent = tasksContent.Replace(
            "scope=\"document\"\n        target=\"20260823-task-iteration-layout\"\n        relation=\"depends-on\"",
            "scope=\"iteration\"\n        target=\"20260823-req-iteration-discovery\"\n        relation=\"depends-on\"");
        File.WriteAllText(tasksPath, tasksContent);

        var result = SchemaValidator.Validate(workspace);

        Assert.IsFalse(result.IsValid);
        Assert.IsTrue(result.Diagnostics.Any(d => d.Code == DiagnosticCodes.InvalidReferenceTargetType));
    }

    [TestMethod]
    public void Validate_InvalidReferenceTargetType_RecordCoversNotTargetingCriterion_Fails()
    {
        var workspace = CreateWorkspaceCopy();

        var tasksPath = Path.Combine(workspace, "20260823-xpath-core", "tasks.xml");
        var tasksContent = File.ReadAllText(tasksPath).Replace("\r\n", "\n");
        // Change record covers to target a task instead of criterion
        tasksContent = tasksContent.Replace(
            "target=\"20260823-taskaccept-layout-visible\"\n            relation=\"covers\"",
            "target=\"20260823-task-iteration-layout\"\n            relation=\"covers\"");
        File.WriteAllText(tasksPath, tasksContent);

        var result = SchemaValidator.Validate(workspace);

        Assert.IsFalse(result.IsValid);
        Assert.IsTrue(result.Diagnostics.Any(d => d.Code == DiagnosticCodes.InvalidReferenceTargetType));
    }

    #endregion

    #region 4. Task Graphs and Terminal Predicates

    [TestMethod]
    public void Validate_TaskSelfDependency_FailsWithDependencyCycle()
    {
        var workspace = CreateWorkspaceCopy();

        var tasksPath = Path.Combine(workspace, "20260823-xpath-core", "tasks.xml");
        var tasksContent = File.ReadAllText(tasksPath).Replace("\r\n", "\n");
        // Make task-xpath-projection depend on itself
        tasksContent = tasksContent.Replace(
            "target=\"20260823-task-iteration-layout\"\n        relation=\"depends-on\"",
            "target=\"20260823-task-xpath-projection\"\n        relation=\"depends-on\"");
        File.WriteAllText(tasksPath, tasksContent);

        var result = SchemaValidator.Validate(workspace);

        Assert.IsFalse(result.IsValid);
        Assert.IsTrue(result.Diagnostics.Any(d => d.Code == DiagnosticCodes.DependencyCycle));
    }

    [TestMethod]
    public void Validate_TaskDependencyCycle_FailsWithDependencyCycle()
    {
        var workspace = CreateWorkspaceCopy();

        var tasksPath = Path.Combine(workspace, "20260823-xpath-core", "tasks.xml");
        var tasksContent = File.ReadAllText(tasksPath).Replace("\r\n", "\n");
        // Layout task (first task) now depends on atomic-update task, which depends on xpath-projection, which depends on layout
        tasksContent = tasksContent.Replace(
            "<origin>\n      <ref\n        scope=\"iteration\"\n        target=\"20260823-req-iteration-discovery\"\n        relation=\"implements\"/>\n    </origin>",
            "<origin>\n      <ref\n        scope=\"iteration\"\n        target=\"20260823-req-iteration-discovery\"\n        relation=\"implements\"/>\n    </origin>\n    <dependencies>\n      <ref\n        scope=\"document\"\n        target=\"20260823-task-atomic-update\"\n        relation=\"depends-on\"/>\n    </dependencies>");
        File.WriteAllText(tasksPath, tasksContent);

        var result = SchemaValidator.Validate(workspace);

        Assert.IsFalse(result.IsValid);
        Assert.IsTrue(result.Diagnostics.Any(d => d.Code == DiagnosticCodes.DependencyCycle));
    }

    [TestMethod]
    public void Validate_DoneTaskWithPendingCriterion_FailsWithTaskCriterionNotTerminal()
    {
        var workspace = CreateWorkspaceCopy();

        var tasksPath = Path.Combine(workspace, "20260823-xpath-core", "tasks.xml");
        var tasksContent = File.ReadAllText(tasksPath).Replace("\r\n", "\n");
        // In the done layout task, change one criterion to status="pending"
        tasksContent = tasksContent.Replace(
            "id=\"20260823-taskaccept-layout-visible\" status=\"passed\"",
            "id=\"20260823-taskaccept-layout-visible\" status=\"pending\"");
        File.WriteAllText(tasksPath, tasksContent);

        var result = SchemaValidator.Validate(workspace);

        Assert.IsFalse(result.IsValid);
        Assert.IsTrue(result.Diagnostics.Any(d => d.Code == DiagnosticCodes.TaskCriterionNotTerminal));
    }

    [TestMethod]
    public void Validate_DoneTaskMissingCompletedAt_FailsWithTaskCompletedAtMissing()
    {
        var workspace = CreateWorkspaceCopy();

        var tasksPath = Path.Combine(workspace, "20260823-xpath-core", "tasks.xml");
        var tasksContent = File.ReadAllText(tasksPath).Replace("\r\n", "\n");
        // Remove completed_at attribute from the done layout task
        tasksContent = tasksContent.Replace(
            "completed_at=\"2026-08-23T03:00:00Z\"\n    ",
            "");
        File.WriteAllText(tasksPath, tasksContent);

        var result = SchemaValidator.Validate(workspace);

        Assert.IsFalse(result.IsValid);
        Assert.IsTrue(result.Diagnostics.Any(d => d.Code == DiagnosticCodes.TaskCompletedAtMissing));
    }

    [TestMethod]
    public void Validate_DoneTaskMissingCompletionRecord_FailsWithTaskCompletionRecordMissing()
    {
        var workspace = CreateWorkspaceCopy();

        var tasksPath = Path.Combine(workspace, "20260823-xpath-core", "tasks.xml");
        var tasksContent = File.ReadAllText(tasksPath).Replace("\r\n", "\n");
        // Change the completion record to a discussion record
        tasksContent = tasksContent.Replace(
            "kind=\"completion\"",
            "kind=\"discussion\"");
        File.WriteAllText(tasksPath, tasksContent);

        var result = SchemaValidator.Validate(workspace);

        Assert.IsFalse(result.IsValid);
        Assert.IsTrue(result.Diagnostics.Any(d => d.Code == DiagnosticCodes.TaskCompletionRecordMissing));
    }

    [TestMethod]
    public void Validate_DoneTaskUncoveredCriterion_FailsWithTaskCriterionNotCovered()
    {
        var workspace = CreateWorkspaceCopy();

        var tasksPath = Path.Combine(workspace, "20260823-xpath-core", "tasks.xml");
        var tasksContent = File.ReadAllText(tasksPath).Replace("\r\n", "\n");
        // Remove one covers ref from the completion record
        tasksContent = tasksContent.Replace(
            "<ref\n            scope=\"document\"\n            target=\"20260823-taskaccept-layout-visible\"\n            relation=\"covers\"/>",
            "");
        File.WriteAllText(tasksPath, tasksContent);

        var result = SchemaValidator.Validate(workspace);

        Assert.IsFalse(result.IsValid);
        Assert.IsTrue(result.Diagnostics.Any(d => d.Code == DiagnosticCodes.TaskCriterionNotCovered));
    }

    [TestMethod]
    public void Validate_DoneTaskWithActiveFinding_FailsWithActiveFindingBlocksCompletion()
    {
        var workspace = CreateWorkspaceCopy();

        var tasksPath = Path.Combine(workspace, "20260823-xpath-core", "tasks.xml");
        var tasksContent = File.ReadAllText(tasksPath).Replace("\r\n", "\n");
        // Add an active finding record to the done layout task
        var findingRecord = """
      <record
        id="20260823T025500Z-record-layout-finding"
        kind="finding"
        status="active"
        created_at="2026-08-23T02:55:00Z"
        actor="codex">
        <summary>Active blocker finding.</summary>
      </record>
""";
        tasksContent = tasksContent.Replace("</records>", findingRecord + "\n    </records>");
        File.WriteAllText(tasksPath, tasksContent);

        var result = SchemaValidator.Validate(workspace);

        Assert.IsFalse(result.IsValid);
        Assert.IsTrue(result.Diagnostics.Any(d => d.Code == DiagnosticCodes.TaskActiveFindingBlocksCompletion));
    }

    [TestMethod]
    public void Validate_NonDoneTaskWithCompletedAt_FailsWithTaskNonDoneHasCompletedAt()
    {
        var workspace = CreateWorkspaceCopy();

        var tasksPath = Path.Combine(workspace, "20260823-xpath-core", "tasks.xml");
        var tasksContent = File.ReadAllText(tasksPath).Replace("\r\n", "\n");
        // Add completed_at to the in-progress projection task
        tasksContent = tasksContent.Replace(
            "status=\"in-progress\"",
            "status=\"in-progress\" completed_at=\"2026-08-23T04:00:00Z\"");
        File.WriteAllText(tasksPath, tasksContent);

        var result = SchemaValidator.Validate(workspace);

        Assert.IsFalse(result.IsValid);
        Assert.IsTrue(result.Diagnostics.Any(d => d.Code == DiagnosticCodes.TaskNonDoneHasCompletedAt));
    }

    #endregion

    #region 5. Protected Product State and Confirmations

    [TestMethod]
    public void Validate_ActiveIterationWithoutActivationConfirmation_FailsWithMissingConfirmationProvenance()
    {
        var workspace = CreateWorkspaceCopy();

        var specPath = Path.Combine(workspace, "20260823-xpath-core", "spec.xml");
        var specContent = File.ReadAllText(specPath).Replace("\r\n", "\n");
        // Change activation confirmation action to something else
        specContent = specContent.Replace("action=\"activate\"", "action=\"continue\"");
        File.WriteAllText(specPath, specContent);

        var result = SchemaValidator.Validate(workspace);

        Assert.IsFalse(result.IsValid);
        Assert.IsTrue(result.Diagnostics.Any(d => d.Code == DiagnosticCodes.MissingConfirmationProvenance));
    }

    [TestMethod]
    public void Validate_ApprovedRequirementWithoutConfirmationProvenance_Fails()
    {
        var workspace = CreateWorkspaceCopy();

        var specPath = Path.Combine(workspace, "20260823-xpath-core", "spec.xml");
        var specContent = File.ReadAllText(specPath).Replace("\r\n", "\n");
        // Make iteration draft and remove confirmations
        var confIdx = specContent.IndexOf("<confirmations>", StringComparison.Ordinal);
        if (confIdx >= 0)
        {
            specContent = string.Concat(specContent.AsSpan(0, confIdx), "<confirmations/>\n</iteration>");
        }
        specContent = specContent.Replace("status=\"active\"", "status=\"draft\"");
        File.WriteAllText(specPath, specContent);

        var result = SchemaValidator.Validate(workspace);

        Assert.IsFalse(result.IsValid);
        Assert.IsTrue(result.Diagnostics.Any(d => d.Code == DiagnosticCodes.MissingConfirmationProvenance));
    }

    [TestMethod]
    public void Validate_SupersededRequirementWithoutTargetedConfirmation_Fails()
    {
        var workspace = CreateWorkspaceCopy();

        var specPath = Path.Combine(workspace, "20260823-xpath-core", "spec.xml");
        var specContent = File.ReadAllText(specPath).Replace("\r\n", "\n");
        // Change requirement status to superseded without adding a targeted confirmation entry
        specContent = specContent.Replace(
            "id=\"20260823-req-iteration-discovery\" status=\"approved\"",
            "id=\"20260823-req-iteration-discovery\" status=\"superseded\"");
        File.WriteAllText(specPath, specContent);

        var result = SchemaValidator.Validate(workspace);

        Assert.IsFalse(result.IsValid);
        Assert.IsTrue(result.Diagnostics.Any(d => d.Code == DiagnosticCodes.MissingConfirmationProvenance));
    }

    [TestMethod]
    public void Validate_AcceptedProductCriterionWithoutTargetedConfirmation_Fails()
    {
        var workspace = CreateWorkspaceCopy();

        var specPath = Path.Combine(workspace, "20260823-xpath-core", "spec.xml");
        var specContent = File.ReadAllText(specPath).Replace("\r\n", "\n");
        // Change a pending product criterion to accepted without targeted confirmation
        specContent = specContent.Replace(
            "id=\"20260823-accept-directory-overview\"\n        decision=\"pending\"",
            "id=\"20260823-accept-directory-overview\"\n        decision=\"accepted\"");
        File.WriteAllText(specPath, specContent);

        var result = SchemaValidator.Validate(workspace);

        Assert.IsFalse(result.IsValid);
        Assert.IsTrue(result.Diagnostics.Any(d => d.Code == DiagnosticCodes.MissingConfirmationProvenance));
    }

    [TestMethod]
    public void Validate_CompletedIterationWithoutCompletionConfirmation_Fails()
    {
        var workspace = CreateWorkspaceCopy();

        var specPath = Path.Combine(workspace, "20260823-xpath-core", "spec.xml");
        var specContent = File.ReadAllText(specPath);
        specContent = specContent.Replace("status=\"active\"", "status=\"completed\" completed_at=\"2026-08-23T06:00:00Z\"");
        File.WriteAllText(specPath, specContent);

        var result = SchemaValidator.Validate(workspace);

        Assert.IsFalse(result.IsValid);
        Assert.IsTrue(result.Diagnostics.Any(d => d.Code == DiagnosticCodes.MissingConfirmationProvenance));
    }

    [TestMethod]
    public void Validate_CompletedIterationWithNonTerminalTask_FailsWithCompletionPredicateFailed()
    {
        var workspace = CreateWorkspaceCopy();

        var specPath = Path.Combine(workspace, "20260823-xpath-core", "spec.xml");
        var specContent = File.ReadAllText(specPath);
        // Add completion confirmation to spec.xml
        var completionConf = """
    <confirmation
      id="20260823T060000Z-confirmation-completion"
      action="complete"
      decision="accepted"
      actor="owner"
      decided_at="2026-08-23T06:00:00Z">
      <summary>Iteration completed.</summary>
      <acceptance>
        <criterion target="20260823-accept-directory-overview" decision="accepted"/>
        <criterion target="20260823-accept-resume-task" decision="accepted"/>
        <criterion target="20260823-accept-integrated-verification" decision="accepted"/>
        <criterion target="20260823-accept-no-truncation" decision="accepted"/>
        <criterion target="20260823-accept-structured-reasoning" decision="accepted"/>
        <criterion target="20260823-accept-template-append" decision="accepted"/>
      </acceptance>
    </confirmation>
""";
        specContent = specContent.Replace("status=\"active\"", "status=\"completed\" completed_at=\"2026-08-23T06:00:00Z\"")
                                 .Replace("decision=\"pending\"", "decision=\"accepted\"")
                                 .Replace("</confirmations>", completionConf + "\n  </confirmations>");
        File.WriteAllText(specPath, specContent);

        // tasks.xml still has unfinished tasks (task-xpath-projection is in-progress)
        var result = SchemaValidator.Validate(workspace);

        Assert.IsFalse(result.IsValid);
        Assert.IsTrue(result.Diagnostics.Any(d => d.Code == DiagnosticCodes.IterationCompletionPredicateFailed));
    }

    [TestMethod]
    public void Validate_WaivedCriterionMissingRationaleSummary_FailsWithWaiverRationaleMissing()
    {
        var workspace = CreateWorkspaceCopy();

        var specPath = Path.Combine(workspace, "20260823-xpath-core", "spec.xml");
        var specContent = File.ReadAllText(specPath);

        // Mark a criterion waived and add confirmation with empty summary
        var waiverConf = """
    <confirmation
      id="20260823T050000Z-confirmation-waiver"
      action="continue"
      decision="accepted"
      actor="owner"
      decided_at="2026-08-23T05:00:00Z">
      <summary></summary>
      <acceptance>
        <criterion target="20260823-accept-no-truncation" decision="waived"/>
      </acceptance>
    </confirmation>
""";
        specContent = specContent.Replace("id=\"20260823-accept-no-truncation\"\n        decision=\"pending\"", "id=\"20260823-accept-no-truncation\"\n        decision=\"waived\"")
                                 .Replace("</confirmations>", waiverConf + "\n  </confirmations>");
        File.WriteAllText(specPath, specContent);

        var result = SchemaValidator.Validate(workspace);

        Assert.IsFalse(result.IsValid);
        Assert.IsTrue(result.Diagnostics.Any(d => d.Code == DiagnosticCodes.WaiverRationaleMissing));
    }

    #endregion

    #region 6. Deterministic Ordering and Safe Traversal

    [TestMethod]
    public void Validate_SchemaInvalidDocument_DoesNotCauseSemanticCrash()
    {
        var workspace = CreateWorkspaceCopy();

        var specPath = Path.Combine(workspace, "20260823-xpath-core", "spec.xml");
        File.WriteAllText(specPath, "<iteration><invalid-tag/></iteration>");

        var result = SchemaValidator.Validate(workspace);

        Assert.IsFalse(result.IsValid);
        Assert.IsTrue(result.Diagnostics.Any(d => d.Code == DiagnosticCodes.SchemaValidationError));
        // Ensure semantic validator was NOT run on invalid XML and didn't crash
        Assert.IsFalse(result.Diagnostics.Any(d => d.Code == DiagnosticCodes.IterationIdMismatch));
    }

    [TestMethod]
    public void Validate_DeterministicDiagnosticOrdering_MatchesContract()
    {
        var workspace = CreateWorkspaceCopy();

        // Introduce multiple diagnostics
        var tasksPath = Path.Combine(workspace, "20260823-xpath-core", "tasks.xml");
        var tasksContent = File.ReadAllText(tasksPath);
        tasksContent = tasksContent.Replace("id=\"20260823-task-task-history\"", "id=\"20260823-task-xpath-projection\"") // duplicate ID
                                   .Replace("scope=\"document\"\n        target=\"20260823-task-iteration-layout\"", "scope=\"project\"\n        target=\"20260823-task-iteration-layout\""); // scope not narrowest
        File.WriteAllText(tasksPath, tasksContent);

        var result = SchemaValidator.Validate(workspace);

        Assert.IsFalse(result.IsValid);
        Assert.IsTrue(result.Diagnostics.Count > 1);

        // Verify sorted order
        for (int i = 0; i < result.Diagnostics.Count - 1; i++)
        {
            var d1 = result.Diagnostics[i];
            var d2 = result.Diagnostics[i + 1];

            var docComp = string.Compare(d1.Document, d2.Document, StringComparison.Ordinal);
            if (docComp != 0)
            {
                Assert.IsTrue(docComp < 0, $"Documents must be ordered: {d1.Document} before {d2.Document}");
                continue;
            }

            var line1 = d1.Line ?? int.MaxValue;
            var line2 = d2.Line ?? int.MaxValue;
            if (line1 != line2)
            {
                Assert.IsTrue(line1 <= line2, $"Lines must be ordered: {line1} <= {line2}");
                continue;
            }

            var col1 = d1.Column ?? int.MaxValue;
            var col2 = d2.Column ?? int.MaxValue;
            if (col1 != col2)
            {
                Assert.IsTrue(col1 <= col2, $"Columns must be ordered: {col1} <= {col2}");
                continue;
            }

            var codeComp = string.Compare(d1.Code, d2.Code, StringComparison.Ordinal);
            if (codeComp != 0)
            {
                Assert.IsTrue(codeComp <= 0, $"Codes must be ordered: {d1.Code} <= {d2.Code}");
                continue;
            }

            var msgComp = string.Compare(d1.Message, d2.Message, StringComparison.Ordinal);
            Assert.IsTrue(msgComp <= 0, $"Messages must be ordered: {d1.Message} <= {d2.Message}");
        }
    }

    [TestMethod]
    public void Validate_CancelledIterationWithoutCancellationConfirmation_Fails()
    {
        var workspace = CreateWorkspaceCopy();

        var specPath = Path.Combine(workspace, "20260823-xpath-core", "spec.xml");
        var specContent = File.ReadAllText(specPath).Replace("\r\n", "\n");
        specContent = specContent.Replace("status=\"active\"", "status=\"cancelled\"");
        File.WriteAllText(specPath, specContent);

        var result = SchemaValidator.Validate(workspace);

        Assert.IsFalse(result.IsValid);
        Assert.IsTrue(result.Diagnostics.Any(d => d.Code == DiagnosticCodes.MissingConfirmationProvenance));
    }

    [TestMethod]
    public void Validate_SupersededIterationWithoutSupersessionConfirmation_Fails()
    {
        var workspace = CreateWorkspaceCopy();

        var specPath = Path.Combine(workspace, "20260823-xpath-core", "spec.xml");
        var specContent = File.ReadAllText(specPath).Replace("\r\n", "\n");
        specContent = specContent.Replace("status=\"active\"", "status=\"superseded\"");
        File.WriteAllText(specPath, specContent);

        var result = SchemaValidator.Validate(workspace);

        Assert.IsFalse(result.IsValid);
        Assert.IsTrue(result.Diagnostics.Any(d => d.Code == DiagnosticCodes.MissingConfirmationProvenance));
    }

    [TestMethod]
    public void Validate_WithdrawnRequirementWithoutConfirmationProvenance_Fails()
    {
        var workspace = CreateWorkspaceCopy();

        var specPath = Path.Combine(workspace, "20260823-xpath-core", "spec.xml");
        var specContent = File.ReadAllText(specPath).Replace("\r\n", "\n");
        specContent = specContent.Replace("id=\"20260823-req-iteration-discovery\" status=\"approved\"", "id=\"20260823-req-iteration-discovery\" status=\"withdrawn\"");
        File.WriteAllText(specPath, specContent);

        var result = SchemaValidator.Validate(workspace);

        Assert.IsFalse(result.IsValid);
        Assert.IsTrue(result.Diagnostics.Any(d => d.Code == DiagnosticCodes.MissingConfirmationProvenance));
    }

    [TestMethod]
    public void Validate_AnsweredResearchQuestionWithoutConfirmationProvenance_Fails()
    {
        var workspace = CreateWorkspaceCopy();

        var researchIterDir = Path.Combine(workspace, "20260823-research-projection-order");
        Directory.CreateDirectory(researchIterDir);
        var fixture = Path.Combine(RepoRoot, "schemas", "v1", "fixtures", "research-spec.xml");
        var specContent = File.ReadAllText(fixture).Replace("\r\n", "\n");
        // Mark question answered without adding confirmation
        specContent = specContent.Replace("status=\"open\"", "status=\"answered\"");
        File.WriteAllText(Path.Combine(researchIterDir, "spec.xml"), specContent);

        var tasksXml = """
<?xml version="1.0" encoding="utf-8"?>
<tasks id="20260823-research-tasks" iteration="20260823-research-projection-order" schema_version="1.0" revision="1">
  <index><summary>Research Tasks</summary></index>
</tasks>
""";
        File.WriteAllText(Path.Combine(researchIterDir, "tasks.xml"), tasksXml);

        var result = SchemaValidator.Validate(workspace, iterationId: "20260823-research-projection-order");

        Assert.IsFalse(result.IsValid);
        Assert.IsTrue(result.Diagnostics.Any(d => d.Code == DiagnosticCodes.MissingConfirmationProvenance));
    }

    [TestMethod]
    public void Validate_RejectedDesignDecisionWithoutConfirmationProvenance_Fails()
    {
        var workspace = CreateWorkspaceCopy();

        var specPath = Path.Combine(workspace, "20260823-xpath-core", "spec.xml");
        var specContent = File.ReadAllText(specPath).Replace("\r\n", "\n");
        specContent = specContent.Replace("id=\"20260823-design-filesystem-index\" status=\"accepted\"", "id=\"20260823-design-filesystem-index\" status=\"rejected\"");
        File.WriteAllText(specPath, specContent);

        var result = SchemaValidator.Validate(workspace);

        Assert.IsFalse(result.IsValid);
        Assert.IsTrue(result.Diagnostics.Any(d => d.Code == DiagnosticCodes.MissingConfirmationProvenance));
    }

    [TestMethod]
    public void Validate_CompletedIterationMissingCompletedAt_Fails()
    {
        var workspace = CreateWorkspaceCopy();

        var specPath = Path.Combine(workspace, "20260823-xpath-core", "spec.xml");
        var specContent = File.ReadAllText(specPath).Replace("\r\n", "\n");
        // Add completion confirmation but omit completed_at attribute on iteration root
        var completionConf = """
    <confirmation
      id="20260823T060000Z-confirmation-completion"
      action="complete"
      decision="accepted"
      actor="owner"
      decided_at="2026-08-23T06:00:00Z">
      <summary>Iteration completed.</summary>
      <acceptance>
        <criterion target="20260823-accept-directory-overview" decision="accepted"/>
        <criterion target="20260823-accept-resume-task" decision="accepted"/>
        <criterion target="20260823-accept-integrated-verification" decision="accepted"/>
        <criterion target="20260823-accept-no-truncation" decision="accepted"/>
        <criterion target="20260823-accept-structured-reasoning" decision="accepted"/>
        <criterion target="20260823-accept-template-append" decision="accepted"/>
      </acceptance>
    </confirmation>
""";
        specContent = specContent.Replace("status=\"active\"", "status=\"completed\"")
                                 .Replace("decision=\"pending\"", "decision=\"accepted\"")
                                 .Replace("</confirmations>", completionConf + "\n  </confirmations>");
        File.WriteAllText(specPath, specContent);

        var result = SchemaValidator.Validate(workspace);

        Assert.IsFalse(result.IsValid);
        Assert.IsTrue(result.Diagnostics.Any(d => d.Code == DiagnosticCodes.IterationCompletedAtMissing));
    }

    [TestMethod]
    public void Validate_MultiNodeDependencyCycle_FailsWithDeterministicCyclePath()
    {
        var workspace = CreateWorkspaceCopy();

        var tasksPath = Path.Combine(workspace, "20260823-xpath-core", "tasks.xml");
        var tasksContent = File.ReadAllText(tasksPath).Replace("\r\n", "\n");
        // In the first task (20260823-task-iteration-layout), add dependency to 20260823-task-atomic-update (which depends on 20260823-task-xpath-projection, which depends on layout)
        tasksContent = tasksContent.Replace(
            "<origin>\n      <ref\n        scope=\"iteration\"\n        target=\"20260823-req-iteration-discovery\"\n        relation=\"implements\"/>\n    </origin>",
            "<origin>\n      <ref\n        scope=\"iteration\"\n        target=\"20260823-req-iteration-discovery\"\n        relation=\"implements\"/>\n    </origin>\n    <dependencies>\n      <ref\n        scope=\"document\"\n        target=\"20260823-task-atomic-update\"\n        relation=\"depends-on\"/>\n    </dependencies>");
        File.WriteAllText(tasksPath, tasksContent);

        var result = SchemaValidator.Validate(workspace);

        Assert.IsFalse(result.IsValid);
        var cycleDiag = result.Diagnostics.FirstOrDefault(d => d.Code == DiagnosticCodes.DependencyCycle);
        Assert.IsNotNull(cycleDiag, $"Expected DEPENDENCY_CYCLE but got: {string.Join("; ", result.Diagnostics.Select(d => $"{d.Code}: {d.Message}"))}");
        Assert.IsTrue(cycleDiag.Message.Contains("cycle detected in task dependencies:", StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public void Validate_CrossIterationTaskDependencyCycle_FailsWithDependencyCycle()
    {
        var workspace = CreateWorkspaceCopy();

        // Create second iteration
        var iter2Dir = Path.Combine(workspace, "20260824-feature-two");
        Directory.CreateDirectory(iter2Dir);

        var spec2Xml = """
<?xml version="1.0" encoding="utf-8"?>
<iteration id="20260824-feature-two" schema_version="1.0" revision="1" kind="feature" status="active" created_at="2026-08-24T00:00:00Z" updated_at="2026-08-24T00:00:00Z">
  <index><summary>Feature Two</summary></index>
  <product>
    <objective>Feature two objective</objective>
    <deliverables>
      <deliverable id="20260824-deliv-two"><index><summary>D2</summary></index><description>D2</description></deliverable>
    </deliverables>
    <scope><included/><excluded/></scope>
    <requirements>
      <requirement id="20260824-req-two" status="approved"><index><summary>R2</summary></index><statement>R2</statement><rationale>R2</rationale></requirement>
    </requirements>
    <acceptance>
      <criterion id="20260824-crit-two" decision="pending">C2</criterion>
    </acceptance>
  </product>
  <confirmations>
    <confirmation id="20260824T000000Z-conf-act" action="activate" decision="accepted" actor="owner" decided_at="2026-08-24T00:00:00Z">
      <summary>Activated</summary>
    </confirmation>
  </confirmations>
</iteration>
""";
        File.WriteAllText(Path.Combine(iter2Dir, "spec.xml"), spec2Xml);

        // Iteration 2 task depends on Iteration 1 task (20260823-task-iteration-layout)
        var tasks2Xml = """
<?xml version="1.0" encoding="utf-8"?>
<tasks id="20260824-tasks-two" iteration="20260824-feature-two" schema_version="1.0" revision="1">
  <index><summary>Tasks Two</summary></index>
  <task id="20260824-task-two-alpha" status="pending" created_at="2026-08-24T00:00:00Z" updated_at="2026-08-24T00:00:00Z">
    <index><summary>Task Alpha in Iter 2</summary></index>
    <title>Task Alpha</title>
    <objective>Task Alpha Objective</objective>
    <rationale>Task Alpha Rationale</rationale>
    <scope><repository path="."><include path="*"/></repository></scope>
    <origin>
      <ref scope="iteration" target="20260824-req-two" relation="implements"/>
    </origin>
    <dependencies>
      <ref scope="project" target="20260823-task-iteration-layout" relation="depends-on"/>
    </dependencies>
    <constraints/>
    <acceptance>
      <criterion id="20260824-taskcrit-two" status="pending">Criteria</criterion>
    </acceptance>
    <context><summary>Context</summary></context>
    <records/>
  </task>
</tasks>
""";
        File.WriteAllText(Path.Combine(iter2Dir, "tasks.xml"), tasks2Xml);

        // Make Iteration 1 task (20260823-task-iteration-layout) depend on 20260824-task-two-alpha (creating cross-iteration cycle)
        var tasksPath1 = Path.Combine(workspace, "20260823-xpath-core", "tasks.xml");
        var tasksContent1 = File.ReadAllText(tasksPath1).Replace("\r\n", "\n");
        tasksContent1 = tasksContent1.Replace(
            "<origin>\n      <ref\n        scope=\"iteration\"\n        target=\"20260823-req-iteration-discovery\"\n        relation=\"implements\"/>\n    </origin>",
            "<origin>\n      <ref\n        scope=\"iteration\"\n        target=\"20260823-req-iteration-discovery\"\n        relation=\"implements\"/>\n    </origin>\n    <dependencies>\n      <ref\n        scope=\"project\"\n        target=\"20260824-task-two-alpha\"\n        relation=\"depends-on\"/>\n    </dependencies>");
        File.WriteAllText(tasksPath1, tasksContent1);

        var result = SchemaValidator.Validate(workspace);

        Assert.IsFalse(result.IsValid);
        Assert.IsTrue(result.Diagnostics.Any(d => d.Code == DiagnosticCodes.DependencyCycle), $"Expected DEPENDENCY_CYCLE but got: {string.Join("; ", result.Diagnostics.Select(d => $"{d.Code}: {d.Message}"))}");
    }

    [TestMethod]
    public void Validate_ValidCrossIterationTaskDependency_PassesValidation()
    {
        var workspace = CreateWorkspaceCopy();

        // Create second iteration
        var iter2Dir = Path.Combine(workspace, "20260824-feature-two");
        Directory.CreateDirectory(iter2Dir);

        var spec2Xml = """
<?xml version="1.0" encoding="utf-8"?>
<iteration id="20260824-feature-two" schema_version="1.0" revision="1" kind="feature" status="active" created_at="2026-08-24T00:00:00Z" updated_at="2026-08-24T00:00:00Z">
  <index><summary>Feature Two</summary></index>
  <product>
    <objective>Feature two objective</objective>
    <deliverables>
      <deliverable id="20260824-deliv-two"><index><summary>D2</summary></index><description>D2</description></deliverable>
    </deliverables>
    <scope><included/><excluded/></scope>
    <requirements>
      <requirement id="20260824-req-two" status="approved"><index><summary>R2</summary></index><statement>R2</statement><rationale>R2</rationale></requirement>
    </requirements>
    <acceptance>
      <criterion id="20260824-crit-two" decision="pending">C2</criterion>
    </acceptance>
  </product>
  <confirmations>
    <confirmation id="20260824T000000Z-conf-act" action="activate" decision="accepted" actor="owner" decided_at="2026-08-24T00:00:00Z">
      <summary>Activated</summary>
    </confirmation>
  </confirmations>
</iteration>
""";
        File.WriteAllText(Path.Combine(iter2Dir, "spec.xml"), spec2Xml);

        // Iteration 2 task depends on Iteration 1 task (20260823-task-iteration-layout) with scope="project" (no cycle)
        var tasks2Xml = """
<?xml version="1.0" encoding="utf-8"?>
<tasks id="20260824-tasks-two" iteration="20260824-feature-two" schema_version="1.0" revision="1">
  <index><summary>Tasks Two</summary></index>
  <task id="20260824-task-two-alpha" status="pending" created_at="2026-08-24T00:00:00Z" updated_at="2026-08-24T00:00:00Z">
    <index><summary>Task Alpha in Iter 2</summary></index>
    <title>Task Alpha</title>
    <objective>Task Alpha Objective</objective>
    <rationale>Task Alpha Rationale</rationale>
    <scope><repository path="."><include path="*"/></repository></scope>
    <origin>
      <ref scope="iteration" target="20260824-req-two" relation="implements"/>
    </origin>
    <dependencies>
      <ref scope="project" target="20260823-task-iteration-layout" relation="depends-on"/>
    </dependencies>
    <constraints/>
    <acceptance>
      <criterion id="20260824-taskcrit-two" status="pending">Criteria</criterion>
    </acceptance>
    <context><summary>Context</summary></context>
    <records/>
  </task>
</tasks>
""";
        File.WriteAllText(Path.Combine(iter2Dir, "tasks.xml"), tasks2Xml);

        var result = SchemaValidator.Validate(workspace);

        Assert.IsTrue(result.IsValid, $"Expected valid cross-iteration dependency to pass but failed: {string.Join("; ", result.Diagnostics.Select(d => $"{d.Code}: {d.Message}"))}");
    }

    [TestMethod]
    public void Validate_DependsOnRelationOutsideTaskDependencies_DoesNotTriggerTaskTargetTypeError()
    {
        var workspace = CreateWorkspaceCopy();

        // Put a ref with relation="depends-on" inside design/decisions/decision/sources (where ref list is permitted)
        var specPath = Path.Combine(workspace, "20260823-xpath-core", "spec.xml");
        var specContent = File.ReadAllText(specPath).Replace("\r\n", "\n");
        // In spec.xml, the decision sources ref targets knowledge.xml with relation="depends-on"
        specContent = specContent.Replace(
            "relation=\"informs\"",
            "relation=\"depends-on\"");
        File.WriteAllText(specPath, specContent);

        var result = SchemaValidator.Validate(workspace);

        // Should not complain about invalid reference target type (knowledge entry is not a task, but relation is not inside task/dependencies)
        Assert.IsFalse(result.Diagnostics.Any(d => d.Code == DiagnosticCodes.InvalidReferenceTargetType), $"Unexpected INVALID_REFERENCE_TARGET_TYPE: {string.Join("; ", result.Diagnostics.Select(d => $"{d.Code}: {d.Message}"))}");
    }

    [TestMethod]
    public void Validate_ConfirmationDanglingRequirementTarget_FailsWithDanglingReference()
    {
        var workspace = CreateWorkspaceCopy();

        var specPath = Path.Combine(workspace, "20260823-xpath-core", "spec.xml");
        var specContent = File.ReadAllText(specPath).Replace("\r\n", "\n");
        var badConf = """
    <confirmation
      id="20260823T040000Z-confirmation-bad-req"
      action="continue"
      decision="accepted"
      actor="owner"
      decided_at="2026-08-23T04:00:00Z">
      <summary>Bad confirmation</summary>
      <requirements>
        <requirement target="20260823-req-non-existent" decision="approved"/>
      </requirements>
    </confirmation>
""";
        specContent = specContent.Replace("</confirmations>", badConf + "\n  </confirmations>");
        File.WriteAllText(specPath, specContent);

        var result = SchemaValidator.Validate(workspace);

        Assert.IsFalse(result.IsValid);
        Assert.IsTrue(result.Diagnostics.Any(d => d.Code == DiagnosticCodes.DanglingReference), $"Expected DANGLING_REFERENCE but got: {string.Join("; ", result.Diagnostics.Select(d => $"{d.Code}: {d.Message}"))}");
    }

    [TestMethod]
    public void Validate_ConfirmationCrossIterationTarget_FailsWithReferenceScopeViolation()
    {
        var workspace = CreateWorkspaceCopy();

        // Create second iteration
        var iter2Dir = Path.Combine(workspace, "20260824-feature-two");
        Directory.CreateDirectory(iter2Dir);

        var spec2Xml = """
<?xml version="1.0" encoding="utf-8"?>
<iteration id="20260824-feature-two" schema_version="1.0" revision="1" kind="feature" status="active" created_at="2026-08-24T00:00:00Z" updated_at="2026-08-24T00:00:00Z">
  <index><summary>Feature Two</summary></index>
  <product>
    <objective>Feature two objective</objective>
    <deliverables>
      <deliverable id="20260824-deliv-two"><index><summary>D2</summary></index><description>D2</description></deliverable>
    </deliverables>
    <scope><included/><excluded/></scope>
    <requirements>
      <requirement id="20260824-req-two" status="approved"><index><summary>R2</summary></index><statement>R2</statement><rationale>R2</rationale></requirement>
    </requirements>
    <acceptance>
      <criterion id="20260824-crit-two" decision="pending">C2</criterion>
    </acceptance>
  </product>
  <confirmations>
    <confirmation id="20260824T000000Z-conf-act" action="activate" decision="accepted" actor="owner" decided_at="2026-08-24T00:00:00Z">
      <summary>Activated</summary>
    </confirmation>
    <confirmation id="20260824T010000Z-conf-cross" action="continue" decision="accepted" actor="owner" decided_at="2026-08-24T01:00:00Z">
      <summary>Cross iteration confirmation</summary>
      <requirements>
        <requirement target="20260823-req-iteration-discovery" decision="approved"/>
      </requirements>
    </confirmation>
  </confirmations>
</iteration>
""";
        File.WriteAllText(Path.Combine(iter2Dir, "spec.xml"), spec2Xml);

        var tasks2Xml = """
<?xml version="1.0" encoding="utf-8"?>
<tasks id="20260824-tasks-two" iteration="20260824-feature-two" schema_version="1.0" revision="1">
  <index><summary>Tasks Two</summary></index>
</tasks>
""";
        File.WriteAllText(Path.Combine(iter2Dir, "tasks.xml"), tasks2Xml);

        var result = SchemaValidator.Validate(workspace);

        Assert.IsFalse(result.IsValid);
        Assert.IsTrue(result.Diagnostics.Any(d => d.Code == DiagnosticCodes.ReferenceScopeViolation), $"Expected REFERENCE_SCOPE_VIOLATION but got: {string.Join("; ", result.Diagnostics.Select(d => $"{d.Code}: {d.Message}"))}");
    }

    [TestMethod]
    public void Validate_ConfirmationWrongTargetType_FailsWithInvalidReferenceTargetType()
    {
        var workspace = CreateWorkspaceCopy();

        var specPath = Path.Combine(workspace, "20260823-xpath-core", "spec.xml");
        var specContent = File.ReadAllText(specPath).Replace("\r\n", "\n");
        // Confirmation requirement targeting an acceptance criterion instead of a requirement
        var badConf = """
    <confirmation
      id="20260823T040000Z-confirmation-wrong-type"
      action="continue"
      decision="accepted"
      actor="owner"
      decided_at="2026-08-23T04:00:00Z">
      <summary>Wrong target type</summary>
      <requirements>
        <requirement target="20260823-accept-directory-overview" decision="approved"/>
      </requirements>
    </confirmation>
""";
        specContent = specContent.Replace("</confirmations>", badConf + "\n  </confirmations>");
        File.WriteAllText(specPath, specContent);

        var result = SchemaValidator.Validate(workspace);

        Assert.IsFalse(result.IsValid);
        Assert.IsTrue(result.Diagnostics.Any(d => d.Code == DiagnosticCodes.InvalidReferenceTargetType), $"Expected INVALID_REFERENCE_TARGET_TYPE but got: {string.Join("; ", result.Diagnostics.Select(d => $"{d.Code}: {d.Message}"))}");
    }

    [TestMethod]
    public void Validate_ConfirmationAcceptanceTargetingTaskCriterion_FailsWithInvalidReferenceTargetType()
    {
        var workspace = CreateWorkspaceCopy();

        var specPath = Path.Combine(workspace, "20260823-xpath-core", "spec.xml");
        var specContent = File.ReadAllText(specPath).Replace("\r\n", "\n");
        // Confirmation acceptance targeting a criterion in tasks.xml instead of product criterion in spec.xml
        var badConf = """
    <confirmation
      id="20260823T040000Z-confirmation-task-crit"
      action="continue"
      decision="accepted"
      actor="owner"
      decided_at="2026-08-23T04:00:00Z">
      <summary>Task criterion target</summary>
      <acceptance>
        <criterion target="20260823-taskaccept-layout-visible" decision="accepted"/>
      </acceptance>
    </confirmation>
""";
        specContent = specContent.Replace("</confirmations>", badConf + "\n  </confirmations>");
        File.WriteAllText(specPath, specContent);

        var result = SchemaValidator.Validate(workspace);

        Assert.IsFalse(result.IsValid);
        Assert.IsTrue(result.Diagnostics.Any(d => d.Code == DiagnosticCodes.InvalidReferenceTargetType), $"Expected INVALID_REFERENCE_TARGET_TYPE but got: {string.Join("; ", result.Diagnostics.Select(d => $"{d.Code}: {d.Message}"))}");
    }

    [TestMethod]
    public void Validate_ConfirmationContradictoryDecisions_FailsWithContradictoryConfirmationDecision()
    {
        var workspace = CreateWorkspaceCopy();

        var specPath = Path.Combine(workspace, "20260823-xpath-core", "spec.xml");
        var specContent = File.ReadAllText(specPath).Replace("\r\n", "\n");
        var badConf = """
    <confirmation
      id="20260823T040000Z-confirmation-contradictory"
      action="continue"
      decision="accepted"
      actor="owner"
      decided_at="2026-08-23T04:00:00Z">
      <summary>Contradictory decisions</summary>
      <acceptance>
        <criterion target="20260823-accept-directory-overview" decision="accepted"/>
        <criterion target="20260823-accept-directory-overview" decision="rejected"/>
      </acceptance>
    </confirmation>
""";
        specContent = specContent.Replace("</confirmations>", badConf + "\n  </confirmations>");
        File.WriteAllText(specPath, specContent);

        var result = SchemaValidator.Validate(workspace);

        Assert.IsFalse(result.IsValid);
        Assert.IsTrue(result.Diagnostics.Any(d => d.Code == DiagnosticCodes.ContradictoryConfirmationDecision), $"Expected CONTRADICTORY_CONFIRMATION_DECISION but got: {string.Join("; ", result.Diagnostics.Select(d => $"{d.Code}: {d.Message}"))}");
    }

    [TestMethod]
    public void Validate_ConfirmationQuestionWrongTargetType_FailsWithInvalidReferenceTargetType()
    {
        var workspace = CreateWorkspaceCopy();

        var specPath = Path.Combine(workspace, "20260823-xpath-core", "spec.xml");
        var specContent = File.ReadAllText(specPath).Replace("\r\n", "\n");
        // Confirmation question targeting a requirement instead of a question
        var badConf = """
    <confirmation
      id="20260823T040000Z-confirmation-wrong-question-type"
      action="continue"
      decision="accepted"
      actor="owner"
      decided_at="2026-08-23T04:00:00Z">
      <summary>Wrong question target type</summary>
      <questions>
        <question target="20260823-req-iteration-discovery" decision="answered"/>
      </questions>
    </confirmation>
""";
        specContent = specContent.Replace("</confirmations>", badConf + "\n  </confirmations>");
        File.WriteAllText(specPath, specContent);

        var result = SchemaValidator.Validate(workspace);

        Assert.IsFalse(result.IsValid);
        Assert.IsTrue(result.Diagnostics.Any(d => d.Code == DiagnosticCodes.InvalidReferenceTargetType), $"Expected INVALID_REFERENCE_TARGET_TYPE but got: {string.Join("; ", result.Diagnostics.Select(d => $"{d.Code}: {d.Message}"))}");
    }

    [TestMethod]
    public void Validate_ConfirmationDesignDecisionWrongTargetType_FailsWithInvalidReferenceTargetType()
    {
        var workspace = CreateWorkspaceCopy();

        var specPath = Path.Combine(workspace, "20260823-xpath-core", "spec.xml");
        var specContent = File.ReadAllText(specPath).Replace("\r\n", "\n");
        // Confirmation design decision targeting a requirement instead of a decision
        var badConf = """
    <confirmation
      id="20260823T040000Z-confirmation-wrong-decision-type"
      action="continue"
      decision="accepted"
      actor="owner"
      decided_at="2026-08-23T04:00:00Z">
      <summary>Wrong design decision target type</summary>
      <design>
        <decision target="20260823-req-iteration-discovery" decision="accepted"/>
      </design>
    </confirmation>
""";
        specContent = specContent.Replace("</confirmations>", badConf + "\n  </confirmations>");
        File.WriteAllText(specPath, specContent);

        var result = SchemaValidator.Validate(workspace);

        Assert.IsFalse(result.IsValid);
        Assert.IsTrue(result.Diagnostics.Any(d => d.Code == DiagnosticCodes.InvalidReferenceTargetType), $"Expected INVALID_REFERENCE_TARGET_TYPE but got: {string.Join("; ", result.Diagnostics.Select(d => $"{d.Code}: {d.Message}"))}");
    }

    [TestMethod]
    public void Validate_ConfirmationDuplicateTarget_FailsWithDuplicateConfirmationTarget()
    {
        var workspace = CreateWorkspaceCopy();

        var specPath = Path.Combine(workspace, "20260823-xpath-core", "spec.xml");
        var specContent = File.ReadAllText(specPath).Replace("\r\n", "\n");
        var badConf = """
    <confirmation
      id="20260823T040000Z-confirmation-duplicate-target"
      action="continue"
      decision="accepted"
      actor="owner"
      decided_at="2026-08-23T04:00:00Z">
      <summary>Duplicate target</summary>
      <acceptance>
        <criterion target="20260823-accept-directory-overview" decision="accepted"/>
        <criterion target="20260823-accept-directory-overview" decision="accepted"/>
      </acceptance>
    </confirmation>
""";
        specContent = specContent.Replace("</confirmations>", badConf + "\n  </confirmations>");
        File.WriteAllText(specPath, specContent);

        var result = SchemaValidator.Validate(workspace);

        Assert.IsFalse(result.IsValid);
        Assert.IsTrue(result.Diagnostics.Any(d => d.Code == DiagnosticCodes.DuplicateConfirmationTarget), $"Expected DUPLICATE_CONFIRMATION_TARGET but got: {string.Join("; ", result.Diagnostics.Select(d => $"{d.Code}: {d.Message}"))}");
    }

    [TestMethod]
    public void Validate_ScopedDocumentWithSchemaInvalidNonTarget_FailsWithSemanticContextIncomplete()
    {
        var workspace = CreateWorkspaceCopy();

        // Corrupt backlog.xml with schema error
        var backlogPath = Path.Combine(workspace, "backlog.xml");
        File.WriteAllText(backlogPath, """
<?xml version="1.0" encoding="utf-8"?>
<backlog id="20260823-backlog" schema_version="1.0" revision="1">
  <invalid-element>Corrupted</invalid-element>
</backlog>
""");

        // Validate scoped tasks.xml document (which is internally valid and targets spec.xml)
        var result = SchemaValidator.Validate(workspace, relativeDocumentPath: "20260823-xpath-core/tasks.xml");

        Assert.IsFalse(result.IsValid);
        Assert.IsTrue(result.Diagnostics.Any(d => d.Code == DiagnosticCodes.SemanticContextIncomplete), $"Expected SEMANTIC_CONTEXT_INCOMPLETE but got: {string.Join("; ", result.Diagnostics.Select(d => $"{d.Code}: {d.Message}"))}");
        // Must attribute to the scoped document path
        Assert.AreEqual("20260823-xpath-core/tasks.xml", result.Diagnostics[0].Document);
        // Must NOT leak false dangling reference diagnostics or raw schema diagnostics for backlog.xml
        Assert.IsFalse(result.Diagnostics.Any(d => d.Code == DiagnosticCodes.DanglingReference));
        Assert.IsFalse(result.Diagnostics.Any(d => d.Code == DiagnosticCodes.SchemaValidationError));
    }

    [TestMethod]
    public void Validate_ScopedIterationWithMalformedNonTargetContext_FailsWithSemanticContextIncomplete()
    {
        var workspace = CreateWorkspaceCopy();

        // Create an incomplete/malformed non-target iteration folder (missing tasks.xml)
        var brokenIterDir = Path.Combine(workspace, "20260824-broken-iteration");
        Directory.CreateDirectory(brokenIterDir);
        File.WriteAllText(Path.Combine(brokenIterDir, "spec.xml"), "<iteration id=\"20260824-broken-iteration\"/>");

        // Validate the valid 20260823-xpath-core iteration
        var result = SchemaValidator.Validate(workspace, iterationId: "20260823-xpath-core");

        Assert.IsFalse(result.IsValid);
        Assert.IsTrue(result.Diagnostics.Any(d => d.Code == DiagnosticCodes.SemanticContextIncomplete), $"Expected SEMANTIC_CONTEXT_INCOMPLETE but got: {string.Join("; ", result.Diagnostics.Select(d => $"{d.Code}: {d.Message}"))}");
    }

    [TestMethod]
    public void Validate_CompletedIterationWithMixedTerminalTaskDispositions_PassesValidation()
    {
        var workspace = CreateWorkspaceCopy();

        // In demo workspace tasks.xml, layout task is done; change projection to transferred, atomic-update to superseded, and task-history to cancelled
        var tasksPath = Path.Combine(workspace, "20260823-xpath-core", "tasks.xml");
        var tasksContent = File.ReadAllText(tasksPath).Replace("\r\n", "\n");
        tasksContent = tasksContent.Replace("id=\"20260823-task-xpath-projection\"\n    status=\"in-progress\"", "id=\"20260823-task-xpath-projection\"\n    status=\"transferred\"")
                                   .Replace("id=\"20260823-task-atomic-update\"\n    status=\"pending\"", "id=\"20260823-task-atomic-update\"\n    status=\"superseded\"")
                                   .Replace("id=\"20260823-task-task-history\"\n    status=\"pending\"", "id=\"20260823-task-task-history\"\n    status=\"cancelled\"");
        File.WriteAllText(tasksPath, tasksContent);

        // In spec.xml, mark iteration completed and all criteria accepted
        var specPath = Path.Combine(workspace, "20260823-xpath-core", "spec.xml");
        var specContent = File.ReadAllText(specPath);
        var completionConf = """
    <confirmation
      id="20260823T060000Z-confirmation-completion"
      action="complete"
      decision="accepted"
      actor="owner"
      decided_at="2026-08-23T06:00:00Z">
      <summary>Iteration completed.</summary>
      <acceptance>
        <criterion target="20260823-accept-directory-overview" decision="accepted"/>
        <criterion target="20260823-accept-resume-task" decision="accepted"/>
        <criterion target="20260823-accept-integrated-verification" decision="accepted"/>
        <criterion target="20260823-accept-no-truncation" decision="accepted"/>
        <criterion target="20260823-accept-structured-reasoning" decision="accepted"/>
        <criterion target="20260823-accept-template-append" decision="accepted"/>
      </acceptance>
    </confirmation>
""";
        specContent = specContent.Replace("status=\"active\"", "status=\"completed\" completed_at=\"2026-08-23T06:00:00Z\"")
                                 .Replace("decision=\"pending\"", "decision=\"accepted\"")
                                 .Replace("</confirmations>", completionConf + "\n  </confirmations>");
        File.WriteAllText(specPath, specContent);

        var result = SchemaValidator.Validate(workspace);

        Assert.IsTrue(result.IsValid, $"Completed iteration with mixed terminal task dispositions should pass: {string.Join("; ", result.Diagnostics.Select(d => $"{d.Code}: {d.Message}"))}");
    }

    [TestMethod]
    [DataRow("verification")]
    [DataRow("transferred")]
    [DataRow("superseded")]
    [DataRow("cancelled")]
    [DataRow("in-progress")]
    [DataRow("pending")]
    [DataRow("blocked")]
    public void Validate_NonDoneTaskWithCompletedAt_FailsValidation(string status)
    {
        var workspace = CreateWorkspaceCopy();

        var tasksPath = Path.Combine(workspace, "20260823-xpath-core", "tasks.xml");
        var tasksContent = File.ReadAllText(tasksPath).Replace("\r\n", "\n");
        // Add completed_at to task with given status
        tasksContent = tasksContent.Replace(
            "id=\"20260823-task-xpath-projection\"\n    status=\"in-progress\"",
            $"id=\"20260823-task-xpath-projection\"\n    status=\"{status}\" completed_at=\"2026-08-23T04:00:00Z\"");
        File.WriteAllText(tasksPath, tasksContent);

        var result = SchemaValidator.Validate(workspace);

        Assert.IsFalse(result.IsValid);
        Assert.IsTrue(result.Diagnostics.Any(d => d.Code == DiagnosticCodes.TaskNonDoneHasCompletedAt), $"Expected TASK_NON_DONE_HAS_COMPLETED_AT for status '{status}' but got: {string.Join("; ", result.Diagnostics.Select(d => $"{d.Code}: {d.Message}"))}");
    }

    [TestMethod]
    public void Validate_SuccessContract_ContainsSchemaAndSemanticPassedAttributes()
    {
        var workspace = CreateWorkspaceCopy();

        // Workspace scope
        var wsResult = SchemaValidator.Validate(workspace);
        Assert.IsTrue(wsResult.IsValid);
        var wsXml = wsResult.ToSuccessXmlString();
        Assert.IsTrue(wsXml.Contains("schema=\"passed\"", StringComparison.Ordinal));
        Assert.IsTrue(wsXml.Contains("semantic=\"passed\"", StringComparison.Ordinal));
        Assert.IsTrue(wsXml.Contains("scope=\"workspace\"", StringComparison.Ordinal));
        Assert.IsTrue(wsXml.Contains("valid=\"true\"", StringComparison.Ordinal));

        // Iteration scope
        var iterResult = SchemaValidator.Validate(workspace, iterationId: "20260823-xpath-core");
        Assert.IsTrue(iterResult.IsValid);
        var iterXml = iterResult.ToSuccessXmlString();
        Assert.IsTrue(iterXml.Contains("schema=\"passed\"", StringComparison.Ordinal));
        Assert.IsTrue(iterXml.Contains("semantic=\"passed\"", StringComparison.Ordinal));
        Assert.IsTrue(iterXml.Contains("scope=\"iteration\"", StringComparison.Ordinal));
        Assert.IsTrue(iterXml.Contains("iteration=\"20260823-xpath-core\"", StringComparison.Ordinal));

        // Document scope
        var docResult = SchemaValidator.Validate(workspace, relativeDocumentPath: "20260823-xpath-core/tasks.xml");
        Assert.IsTrue(docResult.IsValid);
        var docXml = docResult.ToSuccessXmlString();
        Assert.IsTrue(docXml.Contains("schema=\"passed\"", StringComparison.Ordinal));
        Assert.IsTrue(docXml.Contains("semantic=\"passed\"", StringComparison.Ordinal));
        Assert.IsTrue(docXml.Contains("scope=\"document\"", StringComparison.Ordinal));
        Assert.IsTrue(docXml.Contains("document=\"20260823-xpath-core/tasks.xml\"", StringComparison.Ordinal));
    }

    [TestMethod]
    public void Validate_InProgressTaskWithUnfinishedDependencies_PassesValidation()
    {
        var workspace = CreateWorkspaceCopy();

        // In demo workspace, task-xpath-projection depends on task-iteration-layout (which is done)
        // task-task-history depends on task-xpath-projection (which is in-progress) and task-task-history is pending
        // This is non-blocking and completely valid!
        var result = SchemaValidator.Validate(workspace);

        Assert.IsTrue(result.IsValid);
    }

    [TestMethod]
    public void Validate_DocumentScopedValidation_ResolvesWorkspaceContextWithoutFalseDanglingError()
    {
        var workspace = CreateWorkspaceCopy();

        // Validate only tasks.xml (which has references to spec.xml with scope="iteration")
        var result = SchemaValidator.Validate(workspace, relativeDocumentPath: "20260823-xpath-core/tasks.xml");

        Assert.IsTrue(result.IsValid, $"Document validation failed: {string.Join("; ", result.Diagnostics.Select(d => $"{d.Code}: {d.Message}"))}");
        Assert.AreEqual(0, result.Diagnostics.Count(d => d.Severity == "error"));
        Assert.AreEqual(1, result.CheckedDocumentsCount);
    }

    [TestMethod]
    public void Validate_UnsafeTaskScopeDeclarations_FailClosed()
    {
        var workspace = CreateWorkspaceCopy();
        var tasksPath = Path.Combine(workspace, "20260823-xpath-core", "tasks.xml");
        var document = System.Xml.Linq.XDocument.Load(tasksPath);
        var repository = document.Root!.Elements("task").First().Element("scope")!.Element("repository")!;
        repository.SetAttributeValue("path", "../escape");
        repository.Element("include")!.SetAttributeValue("path", "/src/**");
        document.Save(tasksPath);

        var result = SchemaValidator.Validate(workspace);

        Assert.IsFalse(result.IsValid);
        Assert.IsTrue(result.Diagnostics.Count(diagnostic => diagnostic.Code == DiagnosticCodes.InvalidPath) >= 2,
            string.Join("; ", result.Diagnostics.Select(diagnostic => $"{diagnostic.Code}: {diagnostic.Message}")));
    }

    #endregion
}
