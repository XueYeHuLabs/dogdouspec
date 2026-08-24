using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using DogdouSpec.Core.Diagnostics;
using DogdouSpec.Core.Tasks;
using DogdouSpec.Core.Time;
using DogdouSpec.Core.Transactions;
using DogdouSpec.Core.Validation;

namespace DogdouSpec.Core.Tests;

[TestClass]
public sealed class TransactionApplyCoreTests
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
        _tempDir = Path.Combine(Path.GetTempPath(), "DogdouSpec_TransactionTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    [TestCleanup]
    public void TearDown()
    {
        if (Directory.Exists(_tempDir))
        {
            try { Directory.Delete(_tempDir, true); } catch { }
        }
    }

    private string CreateWorkspaceCopy()
    {
        var source = Path.Combine(RepoRoot, "docs", "demos", "v1-core", ".dogdouspec");
        var destination = Path.Combine(_tempDir, ".dogdouspec");
        CopyDirectory(source, destination);
        return destination;
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.GetFiles(source))
        {
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), true);
        }
        foreach (var directory in Directory.GetDirectories(source))
        {
            CopyDirectory(directory, Path.Combine(destination, Path.GetFileName(directory)));
        }
    }

    private static byte[] Hash(string path) => SHA256.HashData(File.ReadAllBytes(path));

    [TestMethod]
    public void Apply_VariablesAndSequentialOperations_CommitOneRevision()
    {
        var workspace = CreateWorkspaceCopy();
        var request = """
            <transaction operation_id="20260823T130000Z-core-sequential">
              <variables><variable name="task_id">20260823-task-xpath-projection</variable></variables>
              <document path="20260823-xpath-core/tasks.xml" expected_revision="9">
                <assert test="count(/tasks/task[@id=$task_id]) = 1"/>
                <append-child select="/tasks/task[@id=$task_id]/records" expect="1">
                  <record id="20260823T130000Z-record-core-sequential" kind="discussion" status="informational" created_at="2026-08-23T13:00:00Z" actor="core-test">
                    <summary>Sequential transaction record.</summary>
                  </record>
                </append-child>
                <set-attribute select="/tasks/task[@id=$task_id]" expect="1" name="agent" value="transaction-core"/>
                <replace-node select="/tasks/task[@id=$task_id]/context/summary" expect="1">
                  <summary>Context replaced by the transaction Core test.</summary>
                </replace-node>
              </document>
            </transaction>
            """;

        var (success, envelope, diagnostics) = TransactionApplier.Apply(workspace, request,
            new TestClock(new DateTime(2026, 8, 23, 13, 0, 0, DateTimeKind.Utc)));

        Assert.IsTrue(success, string.Join(Environment.NewLine, diagnostics.Select(d => d.Message)));
        Assert.IsNotNull(envelope);
        Assert.IsFalse(envelope.AlreadyApplied);
        Assert.AreEqual(1, envelope.Documents.Count);

        var tasks = XDocument.Load(Path.Combine(workspace, "20260823-xpath-core", "tasks.xml"));
        Assert.AreEqual("10", tasks.Root!.Attribute("revision")?.Value);
        var task = tasks.Root.Elements("task").Single(e => (string?)e.Attribute("id") == "20260823-task-xpath-projection");
        Assert.AreEqual("transaction-core", task.Attribute("agent")?.Value);
        Assert.AreEqual("Context replaced by the transaction Core test.", task.Element("context")!.Element("summary")!.Value);
        Assert.AreEqual(1, task.Descendants("record").Count(e => (string?)e.Attribute("id") == "20260823T130000Z-record-core-sequential"));
    }

    [TestMethod]
    public void Apply_RemoveAttributeAndExpectZero_AreDeterministic()
    {
        var workspace = CreateWorkspaceCopy();
        var tasksPath = Path.Combine(workspace, "20260823-xpath-core", "tasks.xml");
        var request = """
            <transaction operation_id="20260823T130100Z-core-remove">
              <document path="20260823-xpath-core/tasks.xml" expected_revision="9">
                <set-attribute select="/tasks/task[@id='20260823-task-xpath-projection']" expect="1" name="agent" value="temporary"/>
                <remove-node select="/tasks/task[@id='20260823-task-xpath-projection']/@agent" expect="1"/>
                <set-attribute select="/tasks/task[@id='does-not-exist']" expect="0" name="agent" value="never-written"/>
              </document>
            </transaction>
            """;

        var (success, _, diagnostics) = TransactionApplier.Apply(workspace, request);
        Assert.IsTrue(success, string.Join(Environment.NewLine, diagnostics.Select(d => d.Message)));
        var tasks = XDocument.Load(tasksPath);
        Assert.AreEqual("10", tasks.Root!.Attribute("revision")?.Value);
        var task = tasks.Descendants("task").Single(e => (string?)e.Attribute("id") == "20260823-task-xpath-projection");
        Assert.IsNull(task.Attribute("agent"));
    }

    [TestMethod]
    public void Apply_SemanticNoOp_DoesNotRewriteOrIncrementRevision()
    {
        var workspace = CreateWorkspaceCopy();
        var tasksPath = Path.Combine(workspace, "20260823-xpath-core", "tasks.xml");
        var before = Hash(tasksPath);
        var request = """
            <transaction operation_id="20260823T130200Z-core-noop">
              <document path="20260823-xpath-core/tasks.xml" expected_revision="9">
                <assert test="count(/tasks/task) &gt; 0"/>
                <set-attribute select="/tasks/task[@id='does-not-exist']" expect="0" name="agent" value="none"/>
              </document>
            </transaction>
            """;

        var (success, envelope, diagnostics) = TransactionApplier.Apply(workspace, request);
        Assert.IsTrue(success, string.Join(Environment.NewLine, diagnostics.Select(d => d.Message)));
        Assert.IsTrue(envelope!.AlreadyApplied);
        Assert.AreEqual(0, envelope.Documents.Count);
        CollectionAssert.AreEqual(before, Hash(tasksPath));
    }

    [TestMethod]
    public void Apply_InvalidSelectorsAndControlMetadata_PreserveBytes()
    {
        var cases = new[]
        {
            "<set-attribute select=\"count(/tasks/task)\" expect=\"1\" name=\"agent\" value=\"x\"/>",
            "<set-attribute select=\"ds:filter(/tasks/task, '@id')\" expect=\"3\" name=\"agent\" value=\"x\"/>",
            "<set-attribute select=\"/tasks/task\" expect=\"99\" name=\"agent\" value=\"x\"/>",
            "<set-attribute select=\"/tasks\" expect=\"1\" name=\"revision\" value=\"100\"/>",
            "<remove-node select=\"/tasks\" expect=\"1\"/>",
            "<replace-node select=\"/tasks\" expect=\"1\"><tasks id=\"20260823-invalid-root\" iteration=\"20260823-xpath-core\" schema_version=\"1.0\" revision=\"9\"><index><summary>invalid</summary></index></tasks></replace-node>",
            "<append-child select=\"/tasks/task[1]/records\" expect=\"1\"><record id=\"20260823T130300Z-spoof\" kind=\"discussion\" status=\"informational\" created_at=\"2026-08-23T13:03:00Z\" actor=\"test\" operation_id=\"20260823T130300Z-fake\"><summary>spoof</summary></record></append-child>"
        };

        var sequence = 0;
        foreach (var operation in cases)
        {
            var workspace = CreateWorkspaceCopy();
            var tasksPath = Path.Combine(workspace, "20260823-xpath-core", "tasks.xml");
            var before = Hash(tasksPath);
            var request = $"<transaction operation_id=\"20260823T1303{sequence:00}Z-invalid\"><document path=\"20260823-xpath-core/tasks.xml\" expected_revision=\"9\">{operation}</document></transaction>";

            var (success, _, diagnostics) = TransactionApplier.Apply(workspace, request);
            Assert.IsFalse(success, $"Invalid operation unexpectedly succeeded: {operation}");
            Assert.IsTrue(diagnostics.Count > 0);
            CollectionAssert.AreEqual(before, Hash(tasksPath), $"Failure rewrote tasks.xml: {operation}");
            sequence++;
        }
    }

    [TestMethod]
    public void Apply_DuplicateVariablesAndPaths_PreserveBytes()
    {
        var cases = new[]
        {
            "<variables><variable name=\"id\">one</variable><variable name=\"id\">two</variable></variables><document path=\"20260823-xpath-core/tasks.xml\" expected_revision=\"9\"><assert test=\"true()\"/></document>",
            "<document path=\"20260823-xpath-core/tasks.xml\" expected_revision=\"9\"><assert test=\"true()\"/></document><document path=\"20260823-xpath-core/tasks.xml\" expected_revision=\"9\"><assert test=\"true()\"/></document>"
        };

        foreach (var body in cases)
        {
            var workspace = CreateWorkspaceCopy();
            var tasksPath = Path.Combine(workspace, "20260823-xpath-core", "tasks.xml");
            var before = Hash(tasksPath);
            var request = $"<transaction operation_id=\"20260823T130400Z-duplicate\">{body}</transaction>";
            var (success, _, _) = TransactionApplier.Apply(workspace, request);
            Assert.IsFalse(success);
            CollectionAssert.AreEqual(before, Hash(tasksPath));
        }
    }

    [TestMethod]
    public void Apply_MalformedDtdAndSchemaInvalidRequests_PreserveBytes()
    {
        var requests = new[]
        {
            "<transaction",
            "<!DOCTYPE transaction [<!ENTITY xxe SYSTEM 'file:///c:/windows/win.ini'>]><transaction operation_id='20260823T130500Z-dtd'><document path='20260823-xpath-core/tasks.xml' expected_revision='9'><assert test='true()'/></document></transaction>",
            "<transaction operation_id='20260823T130501Z-schema'><document path='20260823-xpath-core/tasks.xml' expected_revision='9'><unknown/></document></transaction>"
        };

        foreach (var request in requests)
        {
            var workspace = CreateWorkspaceCopy();
            var tasksPath = Path.Combine(workspace, "20260823-xpath-core", "tasks.xml");
            var before = Hash(tasksPath);
            var (success, _, diagnostics) = TransactionApplier.Apply(workspace, request);
            Assert.IsFalse(success);
            Assert.IsTrue(diagnostics.Count > 0);
            CollectionAssert.AreEqual(before, Hash(tasksPath));
        }
    }

    [TestMethod]
    public void Apply_ProtectedResolvedStateMatrix_ReturnsOwnerDecisionAndPreservesBytes()
    {
        var cases = new[]
        {
            ("20260823-xpath-core/spec.xml", 4, "<set-attribute select=\"/iteration\" expect=\"1\" name=\"status\" value=\"completed\"/>"),
            ("20260823-xpath-core/spec.xml", 4, "<remove-node select=\"/iteration/confirmations\" expect=\"1\"/>"),
            ("20260823-xpath-core/spec.xml", 4, "<set-attribute select=\"//requirement[@id='20260823-req-iteration-discovery']\" expect=\"1\" name=\"status\" value=\"proposed\"/>"),
            ("20260823-xpath-core/spec.xml", 4, "<set-attribute select=\"//decision[@id='20260823-design-filesystem-index']\" expect=\"1\" name=\"status\" value=\"proposed\"/>"),
            ("20260823-xpath-core/spec.xml", 4, "<set-attribute select=\"//criterion[@decision='pending'][1]\" expect=\"1\" name=\"decision\" value=\"accepted\"/>"),
            ("knowledge.xml", 2, "<set-attribute select=\"/knowledge/entry[@id='20260801-knowledge-xml-authority']\" expect=\"1\" name=\"status\" value=\"proposed\"/>")
        };

        var index = 0;
        foreach (var (path, revision, operation) in cases)
        {
            var workspace = CreateWorkspaceCopy();
            var fullPath = Path.Combine(workspace, path.Replace('/', Path.DirectorySeparatorChar));
            var before = Hash(fullPath);
            var request = $"<transaction operation_id=\"20260823T1306{index:00}Z-protected\"><document path=\"{path}\" expected_revision=\"{revision}\">{operation}</document></transaction>";
            var (success, _, diagnostics) = TransactionApplier.Apply(workspace, request);
            Assert.IsFalse(success, $"Protected operation unexpectedly succeeded: {operation}");
            Assert.IsTrue(diagnostics.Any(d => d.Code == DiagnosticCodes.OwnerDecisionRequired), string.Join(Environment.NewLine, diagnostics.Select(d => d.Message)));
            CollectionAssert.AreEqual(before, Hash(fullPath));
            index++;
        }
    }

    [TestMethod]
    public void Apply_AllowedProposedPlanningContent_SucceedsAndValidates()
    {
        var workspace = CreateWorkspaceCopy();
        var request = """
            <transaction operation_id="20260823T130700Z-proposed-planning">
              <document path="20260823-xpath-core/spec.xml" expected_revision="4">
                <append-child select="/iteration/product/requirements" expect="1">
                  <requirement id="20260823-req-transaction-planning" status="proposed">
                    <index><summary>Proposed transaction planning requirement.</summary><term key="topic" value="transaction"/></index>
                    <statement>Keep low-level transaction use explicit and bounded.</statement>
                    <rationale>Planning proposals are not product approval decisions.</rationale>
                  </requirement>
                </append-child>
              </document>
            </transaction>
            """;

        var (success, _, diagnostics) = TransactionApplier.Apply(workspace, request);
        Assert.IsTrue(success, string.Join(Environment.NewLine, diagnostics.Select(d => d.Message)));
        Assert.IsTrue(SchemaValidator.Validate(workspace).IsValid);
    }

    [TestMethod]
    public void Apply_DurableTaskReceiptCannotBeRemoved()
    {
        var workspace = CreateWorkspaceCopy();
        var update = """
            <task-update id="20260823T130800Z-create-receipt" actor="core-test" occurred_at="2026-08-23T13:08:00Z">
              <records>
                <record id="20260823T130800Z-record-receipt" kind="discussion" status="informational" created_at="2026-08-23T13:08:00Z" actor="core-test">
                  <summary>Create a durable receipt for the transaction guard test.</summary>
                </record>
              </records>
            </task-update>
            """;
        var (updateSuccess, _, updateDiagnostics) = TaskUpdater.Update(
            workspace, "20260823-xpath-core", "20260823-task-xpath-projection", 9, update);
        Assert.IsTrue(updateSuccess, string.Join(Environment.NewLine, updateDiagnostics.Select(d => d.Message)));

        var tasksPath = Path.Combine(workspace, "20260823-xpath-core", "tasks.xml");
        var before = Hash(tasksPath);
        var transaction = """
            <transaction operation_id="20260823T130801Z-remove-receipt">
              <document path="20260823-xpath-core/tasks.xml" expected_revision="10">
                <remove-node select="//record[@id='20260823T130800Z-record-receipt']" expect="1"/>
              </document>
            </transaction>
            """;
        var (success, _, diagnostics) = TransactionApplier.Apply(workspace, transaction);
        Assert.IsFalse(success);
        Assert.IsTrue(diagnostics.Any(d => d.Code == DiagnosticCodes.IdempotencyConflict));
        CollectionAssert.AreEqual(before, Hash(tasksPath));
    }

    [TestMethod]
    public void Apply_MultiDocumentProspectiveFailure_PreservesEveryTarget()
    {
        var workspace = CreateWorkspaceCopy();
        var tasksPath = Path.Combine(workspace, "20260823-xpath-core", "tasks.xml");
        var backlogPath = Path.Combine(workspace, "backlog.xml");
        var tasksBefore = Hash(tasksPath);
        var backlogBefore = Hash(backlogPath);
        var request = """
            <transaction operation_id="20260823T130850Z-multidoc-invalid">
              <document path="20260823-xpath-core/tasks.xml" expected_revision="9">
                <set-attribute select="//task[@id='20260823-task-xpath-projection']" expect="1" name="agent" value="must-not-commit"/>
              </document>
              <document path="backlog.xml" expected_revision="1">
                <append-child select="/backlog/items" expect="1">
                  <item id="20260823T130850Z-invalid-backlog" status="open" created_at="2026-08-23T13:08:50Z"/>
                </append-child>
              </document>
            </transaction>
            """;

        var (success, _, diagnostics) = TransactionApplier.Apply(workspace, request);
        Assert.IsFalse(success);
        Assert.IsTrue(diagnostics.Any(d => d.Code == DiagnosticCodes.SchemaValidationError));
        CollectionAssert.AreEqual(tasksBefore, Hash(tasksPath));
        CollectionAssert.AreEqual(backlogBefore, Hash(backlogPath));
    }

    [TestMethod]
    public void Apply_MultiDocumentFaultRecovery_ConvergesToValidWorkspace()
    {
        var workspace = CreateWorkspaceCopy();
        var request = """
            <transaction operation_id="20260823T130900Z-multidoc-fault">
              <document path="20260823-xpath-core/tasks.xml" expected_revision="9">
                <set-attribute select="//task[@id='20260823-task-xpath-projection']" expect="1" name="agent" value="fault-recovery"/>
              </document>
              <document path="backlog.xml" expected_revision="1">
                <append-child select="/backlog/items" expect="1">
                  <item id="20260823T130900Z-backlog-fault" status="open" created_at="2026-08-23T13:09:00Z">
                    <index><summary>Fault recovery item.</summary></index>
                    <statement>Exercise multi-document recovery.</statement>
                    <rationale>Recovery must converge complete XML files.</rationale>
                    <impact>Test-only disposable workspace.</impact>
                    <source><ref scope="project" target="20260823-task-xpath-projection" relation="derived-from"/></source>
                    <review_condition>Discard with the test workspace.</review_condition>
                  </item>
                </append-child>
              </document>
            </transaction>
            """;

        var (success, _, diagnostics) = TransactionApplier.Apply(
            workspace,
            request,
            new TestClock(new DateTime(2026, 8, 23, 13, 9, 0, DateTimeKind.Utc)),
            new TestFaultInjector(FaultPhase.DuringMultiFileCommitAfterFirstFile));
        Assert.IsFalse(success);
        Assert.IsTrue(diagnostics.Count > 0);

        var marker = Directory.GetFiles(Path.Combine(workspace, "_tmp"), "recovery.xml", SearchOption.AllDirectories).Single();
        Assert.AreEqual("20260823T130900Z-multidoc-fault", XDocument.Load(marker).Root!.Attribute("id")?.Value);

        var (recoverySuccess, recoveryError) = StartupRecovery.Run(workspace);
        Assert.IsTrue(recoverySuccess, recoveryError?.Message);
        var validation = SchemaValidator.Validate(workspace);
        Assert.IsTrue(validation.IsValid, string.Join(Environment.NewLine, validation.Diagnostics.Select(d => d.Message)));

        var taskRevision = XDocument.Load(Path.Combine(workspace, "20260823-xpath-core", "tasks.xml")).Root!.Attribute("revision")!.Value;
        var backlogRevision = XDocument.Load(Path.Combine(workspace, "backlog.xml")).Root!.Attribute("revision")!.Value;
        Assert.IsTrue((taskRevision == "9" && backlogRevision == "1") || (taskRevision == "10" && backlogRevision == "2"));
    }
}
