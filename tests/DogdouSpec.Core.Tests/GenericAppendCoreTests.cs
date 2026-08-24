using System.Globalization;
using System.Text;
using System.Xml.Linq;
using DogdouSpec.Core.Append;
using DogdouSpec.Core.Diagnostics;
using DogdouSpec.Core.Security;
using DogdouSpec.Core.Time;
using DogdouSpec.Core.Transactions;
using DogdouSpec.Core.Validation;

namespace DogdouSpec.Core.Tests;

[TestClass]
public sealed class GenericAppendCoreTests
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
        _tempDir = Path.Combine(Path.GetTempPath(), "DogdouSpec_AppendTests_" + Guid.NewGuid().ToString("N"));
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

    [TestMethod]
    public void Append_RecordToTask_HappyPath_UpdatesRevisionAndTasksXml()
    {
        var workspace = CreateWorkspaceCopy();
        var recordXml = """
<record
  id="20260823T041500Z-record-projection-discussion"
  kind="discussion"
  status="informational"
  created_at="2026-08-23T04:15:00Z"
  actor="codex">
  <summary>Compared two projection implementations.</summary>
  <outcome>Focused tests are required before selecting one.</outcome>
</record>
""";

        var (success, env, diags) = GenericAppender.Append(
            workspace,
            documentPath: "20260823-xpath-core/tasks.xml",
            parentXPath: "//task[@id='20260823-task-xpath-projection']/records",
            expectedRevision: 9,
            fragmentXml: recordXml);

        Assert.IsTrue(success, $"Append failed: {string.Join("; ", diags.Select(d => d.Message))}");
        Assert.IsNotNull(env);
        Assert.AreEqual("append", env.Command);
        Assert.IsFalse(env.AlreadyApplied);
        Assert.AreEqual(1, env.Documents.Count);
        Assert.AreEqual("20260823-xpath-core/tasks.xml", env.Documents[0].Path);
        Assert.AreEqual(9, env.Documents[0].PreviousRevision);
        Assert.AreEqual(10, env.Documents[0].Revision);

        // Verify XML on disk
        var targetFile = Path.Combine(workspace, "20260823-xpath-core", "tasks.xml");
        var content = File.ReadAllText(targetFile);
        Assert.IsTrue(content.Contains("revision=\"10\"", StringComparison.Ordinal));
        Assert.IsTrue(content.Contains("20260823T041500Z-record-projection-discussion", StringComparison.Ordinal));
    }

    [TestMethod]
    public void Append_ProposedRequirement_HappyPath_UpdatesRevisionAndSpecXml()
    {
        var workspace = CreateWorkspaceCopy();
        var reqXml = """
<requirement
  id="20260823-req-generic-append-support"
  status="proposed">
  <index>
    <summary>Generic append helper mutates document history.</summary>
    <term key="topic" value="generic-append"/>
  </index>
  <statement>CLI supports generic append with single parent element selection.</statement>
  <rationale>Atomic history addition without full document rewrite.</rationale>
</requirement>
""";

        var (success, env, diags) = GenericAppender.Append(
            workspace,
            documentPath: "20260823-xpath-core/spec.xml",
            parentXPath: "/iteration/product/requirements",
            expectedRevision: 4,
            fragmentXml: reqXml);

        Assert.IsTrue(success, $"Append failed: {string.Join("; ", diags.Select(d => d.Message))}");
        Assert.IsNotNull(env);
        Assert.AreEqual(1, env.Documents.Count);
        Assert.AreEqual(5, env.Documents[0].Revision);

        var targetFile = Path.Combine(workspace, "20260823-xpath-core", "spec.xml");
        var content = File.ReadAllText(targetFile);
        Assert.IsTrue(content.Contains("revision=\"5\"", StringComparison.Ordinal));
        Assert.IsTrue(content.Contains("20260823-req-generic-append-support", StringComparison.Ordinal));
    }

    [TestMethod]
    public void Append_DiscussionRecordToRequirementWithRecords_HappyPath_UpdatesRevisionAndSpecXml()
    {
        var workspace = CreateWorkspaceCopy();
        // First add records container to requirement in spec.xml for test
        var specFile = Path.Combine(workspace, "20260823-xpath-core", "spec.xml");
        var specDoc = XDocument.Load(specFile);
        var req = specDoc.Descendants("requirement").First(r => (string?)r.Attribute("id") == "20260823-req-iteration-discovery");
        req.Add(new XElement("records"));
        specDoc.Save(specFile);

        var recordXml = """
<record
  id="20260823T042000Z-record-req-discussion"
  kind="discussion"
  status="informational"
  created_at="2026-08-23T04:20:00Z"
  actor="codex">
  <summary>Discussed requirement scope clarification.</summary>
</record>
""";

        var (success, env, diags) = GenericAppender.Append(
            workspace,
            documentPath: "20260823-xpath-core/spec.xml",
            parentXPath: "//requirement[@id='20260823-req-iteration-discovery']/records",
            expectedRevision: 4,
            fragmentXml: recordXml);

        Assert.IsTrue(success, $"Append failed: {string.Join("; ", diags.Select(d => d.Message))}");
        Assert.IsNotNull(env);
        Assert.AreEqual(1, env.Documents.Count);
        Assert.AreEqual(5, env.Documents[0].Revision);

        var content = File.ReadAllText(specFile);
        Assert.IsTrue(content.Contains("revision=\"5\"", StringComparison.Ordinal));
        Assert.IsTrue(content.Contains("20260823T042000Z-record-req-discussion", StringComparison.Ordinal));
    }

    [TestMethod]
    public void Append_ProposedKnowledgeEntry_HappyPath_UpdatesRevisionAndKnowledgeXml()
    {
        var workspace = CreateWorkspaceCopy();
        var entryXml = """
<entry
  id="20260823-knowledge-xpath-eval"
  status="proposed"
  created_at="2026-08-23T04:30:00Z">
  <index>
    <summary>XPath 1.0 engine evaluates standard syntax and functions.</summary>
    <term key="topic" value="xpath"/>
  </index>
  <statement>XPath 1.0 query execution requires secure reader settings.</statement>
</entry>
""";

        var (success, env, diags) = GenericAppender.Append(
            workspace,
            documentPath: "knowledge.xml",
            parentXPath: "/knowledge",
            expectedRevision: 2,
            fragmentXml: entryXml);

        Assert.IsTrue(success, $"Append failed: {string.Join("; ", diags.Select(d => d.Message))}");
        Assert.IsNotNull(env);
        Assert.AreEqual("knowledge.xml", env.Documents[0].Path);
        Assert.AreEqual(3, env.Documents[0].Revision);

        var targetFile = Path.Combine(workspace, "knowledge.xml");
        var content = File.ReadAllText(targetFile);
        Assert.IsTrue(content.Contains("revision=\"3\"", StringComparison.Ordinal));
        Assert.IsTrue(content.Contains("20260823-knowledge-xpath-eval", StringComparison.Ordinal));
    }

    [TestMethod]
    public void Append_OpenBacklogItem_HappyPath_UpdatesRevisionAndBacklogXml()
    {
        var workspace = CreateWorkspaceCopy();
        var itemXml = """
<item
  id="20260823-backlog-cache-optimization"
  status="open"
  created_at="2026-08-23T04:40:00Z">
  <index>
    <summary>Cache compiled schema sets across multiple requests.</summary>
    <term key="component" value="schema"/>
  </index>
  <statement>Schema caching improves query throughput.</statement>
  <rationale>Repeated schema recompilation is unnecessary.</rationale>
  <impact>Lower latency for repeated validations.</impact>
  <source>
    <ref scope="project" target="20260823-task-xpath-projection" relation="originates-from"/>
  </source>
  <review_condition>When performance profiling indicates schema compilation bottleneck.</review_condition>
</item>
""";

        var (success, env, diags) = GenericAppender.Append(
            workspace,
            documentPath: "backlog.xml",
            parentXPath: "/backlog/items",
            expectedRevision: 1,
            fragmentXml: itemXml);

        Assert.IsTrue(success, $"Append failed: {string.Join("; ", diags.Select(d => d.Message))}");
        Assert.IsNotNull(env);
        Assert.AreEqual("backlog.xml", env.Documents[0].Path);
        Assert.AreEqual(2, env.Documents[0].Revision);

        var targetFile = Path.Combine(workspace, "backlog.xml");
        var content = File.ReadAllText(targetFile);
        Assert.IsTrue(content.Contains("revision=\"2\"", StringComparison.Ordinal));
        Assert.IsTrue(content.Contains("20260823-backlog-cache-optimization", StringComparison.Ordinal));
    }

    [TestMethod]
    public void Append_InvalidParentXPath_ZeroMatches_FailsWithCardinalityConflict()
    {
        var workspace = CreateWorkspaceCopy();
        var recordXml = """
<record
  id="20260823T041500Z-record-test"
  kind="discussion"
  status="informational"
  created_at="2026-08-23T04:15:00Z"
  actor="codex">
  <summary>Test record.</summary>
</record>
""";

        var (success, env, diags) = GenericAppender.Append(
            workspace,
            documentPath: "20260823-xpath-core/tasks.xml",
            parentXPath: "//task[@id='non-existent-task-id']/records",
            expectedRevision: 9,
            fragmentXml: recordXml);

        Assert.IsFalse(success);
        Assert.IsNull(env);
        Assert.IsTrue(diags.Any(d => d.Code == DiagnosticCodes.CardinalityConflict));
        Assert.AreEqual(4, DiagnosticsEnvelope.GetExitCodeForCode(DiagnosticCodes.CardinalityConflict));
    }

    [TestMethod]
    public void Append_InvalidParentXPath_MultipleMatches_FailsWithCardinalityConflict()
    {
        var workspace = CreateWorkspaceCopy();
        var recordXml = """
<record
  id="20260823T041500Z-record-test"
  kind="discussion"
  status="informational"
  created_at="2026-08-23T04:15:00Z"
  actor="codex">
  <summary>Test record.</summary>
</record>
""";

        var (success, env, diags) = GenericAppender.Append(
            workspace,
            documentPath: "20260823-xpath-core/tasks.xml",
            parentXPath: "//task/records", // Multiple tasks exist in demo workspace!
            expectedRevision: 9,
            fragmentXml: recordXml);

        Assert.IsFalse(success);
        Assert.IsNull(env);
        Assert.IsTrue(diags.Any(d => d.Code == DiagnosticCodes.CardinalityConflict));
        Assert.AreEqual(4, DiagnosticsEnvelope.GetExitCodeForCode(DiagnosticCodes.CardinalityConflict));
    }

    [TestMethod]
    public void Append_ScalarParentXPath_FailsWithInvalidArgument()
    {
        var workspace = CreateWorkspaceCopy();
        var recordXml = """
<record
  id="20260823T041500Z-record-test"
  kind="discussion"
  status="informational"
  created_at="2026-08-23T04:15:00Z"
  actor="codex">
  <summary>Test record.</summary>
</record>
""";

        var (success, env, diags) = GenericAppender.Append(
            workspace,
            documentPath: "20260823-xpath-core/tasks.xml",
            parentXPath: "count(//task)",
            expectedRevision: 9,
            fragmentXml: recordXml);

        Assert.IsFalse(success);
        Assert.IsNull(env);
        Assert.IsTrue(diags.Any(d => d.Code == DiagnosticCodes.InvalidArgument));
        Assert.AreEqual(2, DiagnosticsEnvelope.GetExitCodeForCode(DiagnosticCodes.InvalidArgument));
    }

    [TestMethod]
    public void Append_ProjectionParentXPath_FailsWithInvalidArgument()
    {
        var workspace = CreateWorkspaceCopy();
        var recordXml = """
<record
  id="20260823T041500Z-record-test"
  kind="discussion"
  status="informational"
  created_at="2026-08-23T04:15:00Z"
  actor="codex">
  <summary>Test record.</summary>
</record>
""";

        var (success, env, diags) = GenericAppender.Append(
            workspace,
            documentPath: "20260823-xpath-core/tasks.xml",
            parentXPath: "ds:filter(//task[@id='20260823-task-xpath-projection'], 'records')/records",
            expectedRevision: 9,
            fragmentXml: recordXml);

        Assert.IsFalse(success);
        Assert.IsNull(env);
        Assert.IsTrue(diags.Any(d => d.Code == DiagnosticCodes.InvalidArgument));
        Assert.AreEqual(2, DiagnosticsEnvelope.GetExitCodeForCode(DiagnosticCodes.InvalidArgument));
    }

    [TestMethod]
    public void Append_WrongFragmentType_FailsWithSchemaValidationError()
    {
        var workspace = CreateWorkspaceCopy();
        var invalidXml = """
<invalid-element
  id="20260823T041500Z-invalid"
  created_at="2026-08-23T04:15:00Z">
  <data>Invalid</data>
</invalid-element>
""";

        var (success, env, diags) = GenericAppender.Append(
            workspace,
            documentPath: "20260823-xpath-core/tasks.xml",
            parentXPath: "//task[@id='20260823-task-xpath-projection']/records",
            expectedRevision: 9,
            fragmentXml: invalidXml);

        Assert.IsFalse(success);
        Assert.IsNull(env);
        Assert.IsTrue(diags.Any(d => d.Code == DiagnosticCodes.SchemaValidationError));
        Assert.AreEqual(3, DiagnosticsEnvelope.GetExitCodeForCode(DiagnosticCodes.SchemaValidationError));
    }

    [TestMethod]
    public void Append_InvalidIdGrammar_FailsWithInvalidIdGrammar()
    {
        var workspace = CreateWorkspaceCopy();
        var invalidIdXml = """
<record
  id="BAD_ID_NOT_TIME_FIRST"
  kind="discussion"
  status="informational"
  created_at="2026-08-23T04:15:00Z"
  actor="codex">
  <summary>Bad ID.</summary>
</record>
""";

        var (success, env, diags) = GenericAppender.Append(
            workspace,
            documentPath: "20260823-xpath-core/tasks.xml",
            parentXPath: "//task[@id='20260823-task-xpath-projection']/records",
            expectedRevision: 9,
            fragmentXml: invalidIdXml);

        Assert.IsFalse(success);
        Assert.IsNull(env);
        Assert.IsTrue(diags.Any(d => d.Code == DiagnosticCodes.InvalidIdGrammar));
        Assert.AreEqual(3, DiagnosticsEnvelope.GetExitCodeForCode(DiagnosticCodes.InvalidIdGrammar));
    }

    [TestMethod]
    public void Append_DuplicateIdentical_IdempotentRetry_ReturnsAlreadyApplied()
    {
        var workspace = CreateWorkspaceCopy();
        var recordXml = """
<record
  id="20260823T041500Z-record-projection-discussion"
  kind="discussion"
  status="informational"
  created_at="2026-08-23T04:15:00Z"
  actor="codex">
  <summary>Compared two projection implementations.</summary>
  <outcome>Focused tests are required before selecting one.</outcome>
</record>
""";

        // First append
        var (success1, env1, diags1) = GenericAppender.Append(
            workspace,
            documentPath: "20260823-xpath-core/tasks.xml",
            parentXPath: "//task[@id='20260823-task-xpath-projection']/records",
            expectedRevision: 9,
            fragmentXml: recordXml);

        Assert.IsTrue(success1, $"First append failed: {string.Join("; ", diags1.Select(d => d.Message))}");
        Assert.IsFalse(env1!.AlreadyApplied);
        Assert.AreEqual(10, env1.Documents[0].Revision);

        // Retry supplying pre-commit expected revision (9)
        var (success2, env2, diags2) = GenericAppender.Append(
            workspace,
            documentPath: "20260823-xpath-core/tasks.xml",
            parentXPath: "//task[@id='20260823-task-xpath-projection']/records",
            expectedRevision: 9,
            fragmentXml: recordXml);

        Assert.IsTrue(success2, $"Retry with pre-commit revision failed: {string.Join("; ", diags2.Select(d => d.Message))}");
        Assert.IsTrue(env2!.AlreadyApplied);
        Assert.AreEqual(10, env2.Documents[0].Revision);

        // Retry supplying current revision (10)
        var (success3, env3, diags3) = GenericAppender.Append(
            workspace,
            documentPath: "20260823-xpath-core/tasks.xml",
            parentXPath: "//task[@id='20260823-task-xpath-projection']/records",
            expectedRevision: 10,
            fragmentXml: recordXml);

        Assert.IsTrue(success3, $"Retry with current revision failed: {string.Join("; ", diags3.Select(d => d.Message))}");
        Assert.IsTrue(env3!.AlreadyApplied);
        Assert.AreEqual(10, env3.Documents[0].Revision);
    }

    [TestMethod]
    public void Append_DuplicateDifferentContent_FailsWithIdempotencyConflict()
    {
        var workspace = CreateWorkspaceCopy();
        var recordXml1 = """
<record
  id="20260823T041500Z-record-projection-discussion"
  kind="discussion"
  status="informational"
  created_at="2026-08-23T04:15:00Z"
  actor="codex">
  <summary>Original summary.</summary>
</record>
""";

        var (success1, _, _) = GenericAppender.Append(
            workspace,
            documentPath: "20260823-xpath-core/tasks.xml",
            parentXPath: "//task[@id='20260823-task-xpath-projection']/records",
            expectedRevision: 9,
            fragmentXml: recordXml1);
        Assert.IsTrue(success1);

        // Submit same ID with different summary
        var recordXml2 = """
<record
  id="20260823T041500Z-record-projection-discussion"
  kind="discussion"
  status="informational"
  created_at="2026-08-23T04:15:00Z"
  actor="codex">
  <summary>Different conflicting summary.</summary>
</record>
""";

        var (success2, env2, diags2) = GenericAppender.Append(
            workspace,
            documentPath: "20260823-xpath-core/tasks.xml",
            parentXPath: "//task[@id='20260823-task-xpath-projection']/records",
            expectedRevision: 10,
            fragmentXml: recordXml2);

        Assert.IsFalse(success2);
        Assert.IsNull(env2);
        Assert.IsTrue(diags2.Any(d => d.Code == DiagnosticCodes.IdempotencyConflict));
        Assert.AreEqual(4, DiagnosticsEnvelope.GetExitCodeForCode(DiagnosticCodes.IdempotencyConflict));
    }

    [TestMethod]
    public void Append_DuplicateInDifferentDocument_FailsWithIdempotencyConflict()
    {
        var workspace = CreateWorkspaceCopy();
        // ID that already exists in spec.xml: 20260823-req-iteration-discovery
        var recordXml = """
<record
  id="20260823-req-iteration-discovery"
  kind="discussion"
  status="informational"
  created_at="2026-08-23T04:15:00Z"
  actor="codex">
  <summary>Duplicate ID.</summary>
</record>
""";

        var (success, env, diags) = GenericAppender.Append(
            workspace,
            documentPath: "20260823-xpath-core/tasks.xml",
            parentXPath: "//task[@id='20260823-task-xpath-projection']/records",
            expectedRevision: 9,
            fragmentXml: recordXml);

        Assert.IsFalse(success);
        Assert.IsNull(env);
        Assert.IsTrue(diags.Any(d => d.Code == DiagnosticCodes.IdempotencyConflict));
        Assert.AreEqual(4, DiagnosticsEnvelope.GetExitCodeForCode(DiagnosticCodes.IdempotencyConflict));
    }

    [TestMethod]
    public void Append_StaleRevision_FailsWithRevisionConflict()
    {
        var workspace = CreateWorkspaceCopy();
        var recordXml = """
<record
  id="20260823T041500Z-record-test"
  kind="discussion"
  status="informational"
  created_at="2026-08-23T04:15:00Z"
  actor="codex">
  <summary>Test summary.</summary>
</record>
""";

        var (success, env, diags) = GenericAppender.Append(
            workspace,
            documentPath: "20260823-xpath-core/tasks.xml",
            parentXPath: "//task[@id='20260823-task-xpath-projection']/records",
            expectedRevision: 99, // Stale! Actual is 9
            fragmentXml: recordXml);

        Assert.IsFalse(success);
        Assert.IsNull(env);
        Assert.IsTrue(diags.Any(d => d.Code == DiagnosticCodes.RevisionConflict));
        var diag = diags.First(d => d.Code == DiagnosticCodes.RevisionConflict);
        Assert.AreEqual(99, diag.ExpectedRevision);
        Assert.AreEqual(9, diag.ActualRevision);
        Assert.AreEqual(4, DiagnosticsEnvelope.GetExitCodeForCode(DiagnosticCodes.RevisionConflict));
    }

    [TestMethod]
    public void Append_ProtectedConfirmation_FailsWithOwnerDecisionRequired()
    {
        var workspace = CreateWorkspaceCopy();
        var confirmationXml = """
<confirmation
  id="20260823T050000Z-confirm-illegal"
  action="complete"
  decision="accepted"
  actor="owner"
  decided_at="2026-08-23T05:00:00Z">
  <summary>Illegal direct confirmation append.</summary>
</confirmation>
""";

        var (success, env, diags) = GenericAppender.Append(
            workspace,
            documentPath: "20260823-xpath-core/spec.xml",
            parentXPath: "/iteration/confirmations",
            expectedRevision: 4,
            fragmentXml: confirmationXml);

        Assert.IsFalse(success);
        Assert.IsNull(env);
        Assert.IsTrue(diags.Any(d => d.Code == DiagnosticCodes.OwnerDecisionRequired));
        Assert.AreEqual(5, DiagnosticsEnvelope.GetExitCodeForCode(DiagnosticCodes.OwnerDecisionRequired));
    }

    [TestMethod]
    public void Append_ProtectedRequirementApprovedStatus_FailsWithOwnerDecisionRequired()
    {
        var workspace = CreateWorkspaceCopy();
        var reqXml = """
<requirement
  id="20260823-req-new-approved"
  status="approved">
  <index><summary>Approved requirement.</summary></index>
  <statement>Statement</statement>
  <rationale>Rationale</rationale>
</requirement>
""";

        var (success, env, diags) = GenericAppender.Append(
            workspace,
            documentPath: "20260823-xpath-core/spec.xml",
            parentXPath: "/iteration/product/requirements",
            expectedRevision: 4,
            fragmentXml: reqXml);

        Assert.IsFalse(success);
        Assert.IsNull(env);
        Assert.IsTrue(diags.Any(d => d.Code == DiagnosticCodes.OwnerDecisionRequired));
        Assert.AreEqual(5, DiagnosticsEnvelope.GetExitCodeForCode(DiagnosticCodes.OwnerDecisionRequired));
    }

    [TestMethod]
    public void Append_ProtectedDesignDecisionAcceptedStatus_FailsWithOwnerDecisionRequired()
    {
        var workspace = CreateWorkspaceCopy();
        var decXml = """
<decision
  id="20260823-dec-accepted"
  status="accepted">
  <index><summary>Accepted decision.</summary></index>
  <rationale>Rationale</rationale>
</decision>
""";

        var (success, env, diags) = GenericAppender.Append(
            workspace,
            documentPath: "20260823-xpath-core/spec.xml",
            parentXPath: "/iteration/design/decisions",
            expectedRevision: 4,
            fragmentXml: decXml);

        Assert.IsFalse(success);
        Assert.IsNull(env);
        Assert.IsTrue(diags.Any(d => d.Code == DiagnosticCodes.OwnerDecisionRequired));
        Assert.AreEqual(5, DiagnosticsEnvelope.GetExitCodeForCode(DiagnosticCodes.OwnerDecisionRequired));
    }

    [TestMethod]
    public void Append_ProtectedKnowledgeVerifiedStatus_FailsWithOwnerDecisionRequired()
    {
        var workspace = CreateWorkspaceCopy();
        var entryXml = """
<entry
  id="20260823-knowledge-verified"
  status="verified"
  created_at="2026-08-23T04:30:00Z">
  <index><summary>Verified knowledge.</summary></index>
  <statement>Statement</statement>
</entry>
""";

        var (success, env, diags) = GenericAppender.Append(
            workspace,
            documentPath: "knowledge.xml",
            parentXPath: "/knowledge",
            expectedRevision: 2,
            fragmentXml: entryXml);

        Assert.IsFalse(success);
        Assert.IsNull(env);
        Assert.IsTrue(diags.Any(d => d.Code == DiagnosticCodes.OwnerDecisionRequired));
        Assert.AreEqual(5, DiagnosticsEnvelope.GetExitCodeForCode(DiagnosticCodes.OwnerDecisionRequired));
    }

    [TestMethod]
    public void Append_ProtectedBacklogScheduledStatus_FailsWithOwnerDecisionRequired()
    {
        var workspace = CreateWorkspaceCopy();
        var itemXml = """
<item
  id="20260823-backlog-scheduled"
  status="scheduled"
  created_at="2026-08-23T04:40:00Z">
  <index><summary>Scheduled backlog item.</summary></index>
  <statement>Statement</statement>
  <rationale>Rationale</rationale>
  <impact>Impact</impact>
  <source><ref scope="project" target="20260823-task-xpath-projection" relation="originates-from"/></source>
  <review_condition>Review</review_condition>
</item>
""";

        var (success, env, diags) = GenericAppender.Append(
            workspace,
            documentPath: "backlog.xml",
            parentXPath: "/backlog/items",
            expectedRevision: 1,
            fragmentXml: itemXml);

        Assert.IsFalse(success);
        Assert.IsNull(env);
        Assert.IsTrue(diags.Any(d => d.Code == DiagnosticCodes.OwnerDecisionRequired));
        Assert.AreEqual(5, DiagnosticsEnvelope.GetExitCodeForCode(DiagnosticCodes.OwnerDecisionRequired));
    }

    [TestMethod]
    public void Append_DtdFragment_FailsWithDtdProhibited()
    {
        var workspace = CreateWorkspaceCopy();
        var dtdXml = """
<!DOCTYPE record [ <!ENTITY test "test"> ]>
<record
  id="20260823T041500Z-record-dtd"
  kind="discussion"
  status="informational"
  created_at="2026-08-23T04:15:00Z"
  actor="codex">
  <summary>&test;</summary>
</record>
""";

        var (success, env, diags) = GenericAppender.Append(
            workspace,
            documentPath: "20260823-xpath-core/tasks.xml",
            parentXPath: "//task[@id='20260823-task-xpath-projection']/records",
            expectedRevision: 9,
            fragmentXml: dtdXml);

        Assert.IsFalse(success);
        Assert.IsNull(env);
        Assert.IsTrue(diags.Any(d => d.Code == DiagnosticCodes.DtdProhibited));
        Assert.AreEqual(2, DiagnosticsEnvelope.GetExitCodeForCode(DiagnosticCodes.DtdProhibited));
    }

    [TestMethod]
    public void Append_VariablesInParentXPath_ResolvesCorrectElement()
    {
        var workspace = CreateWorkspaceCopy();
        var recordXml = """
<record
  id="20260823T041500Z-record-var-test"
  kind="discussion"
  status="informational"
  created_at="2026-08-23T04:15:00Z"
  actor="codex">
  <summary>Tested variables in parent XPath.</summary>
</record>
""";

        var vars = new Dictionary<string, string>
        {
            ["task_id"] = "20260823-task-xpath-projection"
        };

        var (success, env, diags) = GenericAppender.Append(
            workspace,
            documentPath: "20260823-xpath-core/tasks.xml",
            parentXPath: "//task[@id=$task_id]/records",
            expectedRevision: 9,
            fragmentXml: recordXml,
            variables: vars);

        Assert.IsTrue(success, $"Append with variable failed: {string.Join("; ", diags.Select(d => d.Message))}");
        Assert.IsNotNull(env);
        Assert.AreEqual(10, env.Documents[0].Revision);
    }

    [TestMethod]
    public void Append_Clock_UpdatesUpdatedAtOnSpecRootOnly()
    {
        var workspace = CreateWorkspaceCopy();
        var fixedTime = new DateTime(2026, 8, 23, 12, 34, 56, DateTimeKind.Utc);
        var clock = new TestClock(fixedTime);

        var reqXml = """
<requirement
  id="20260823-req-clock-test"
  status="proposed">
  <index><summary>Clock test requirement.</summary></index>
  <statement>Statement</statement>
  <rationale>Rationale</rationale>
</requirement>
""";

        var (success, env, diags) = GenericAppender.Append(
            workspace,
            documentPath: "20260823-xpath-core/spec.xml",
            parentXPath: "/iteration/product/requirements",
            expectedRevision: 4,
            fragmentXml: reqXml,
            clock: clock);

        Assert.IsTrue(success, $"Append failed: {string.Join("; ", diags.Select(d => d.Message))}");
        var specFile = Path.Combine(workspace, "20260823-xpath-core", "spec.xml");
        var content = File.ReadAllText(specFile);
        Assert.IsTrue(content.Contains("updated_at=\"2026-08-23T12:34:56Z\"", StringComparison.Ordinal));

        // Now append to tasks.xml - tasks root has NO updated_at attribute so it should NOT be added
        var taskRecXml = """
<record
  id="20260823T042000Z-record-tasks-clock-test"
  kind="discussion"
  status="informational"
  created_at="2026-08-23T04:20:00Z"
  actor="codex">
  <summary>Tasks clock test.</summary>
</record>
""";

        var (taskSuccess, _, _) = GenericAppender.Append(
            workspace,
            documentPath: "20260823-xpath-core/tasks.xml",
            parentXPath: "//task[@id='20260823-task-xpath-projection']/records",
            expectedRevision: 9,
            fragmentXml: taskRecXml,
            clock: clock);

        Assert.IsTrue(taskSuccess);
        var tasksFile = Path.Combine(workspace, "20260823-xpath-core", "tasks.xml");
        var tasksDoc = XDocument.Load(tasksFile);
        Assert.IsNull(tasksDoc.Root?.Attribute("updated_at"), "Root tasks element must not have updated_at attribute");
    }

    [TestMethod]
    public void Append_CrashRecovery_CleansUpStagingDirectoryAndLeavesOriginalIntact()
    {
        var workspace = CreateWorkspaceCopy();
        var tasksFile = Path.Combine(workspace, "20260823-xpath-core", "tasks.xml");
        var originalContent = File.ReadAllText(tasksFile);

        var recordXml = """
<record
  id="20260823T041500Z-record-crash-test"
  kind="discussion"
  status="informational"
  created_at="2026-08-23T04:15:00Z"
  actor="codex">
  <summary>Crash test record.</summary>
</record>
""";

        var faultInjector = new TestFaultInjector(FaultPhase.AfterStagingBeforeValidation);

        var (success, env, diags) = GenericAppender.Append(
            workspace,
            documentPath: "20260823-xpath-core/tasks.xml",
            parentXPath: "//task[@id='20260823-task-xpath-projection']/records",
            expectedRevision: 9,
            fragmentXml: recordXml,
            faultInjector: faultInjector);

        Assert.IsFalse(success);
        Assert.IsNull(env);

        // Original file must be completely untouched
        var currentContent = File.ReadAllText(tasksFile);
        Assert.AreEqual(originalContent, currentContent);
    }
}
