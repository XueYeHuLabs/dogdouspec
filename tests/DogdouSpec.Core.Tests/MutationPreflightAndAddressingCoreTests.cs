using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using DogdouSpec.Core.Diagnostics;
using DogdouSpec.Core.Iterations;
using DogdouSpec.Core.Revisions;
using DogdouSpec.Core.Security;
using DogdouSpec.Core.Validation;

namespace DogdouSpec.Core.Tests;

[TestClass]
public sealed class MutationPreflightAndAddressingCoreTests
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
        _tempDir = Path.Combine(Path.GetTempPath(), "DogdouSpec_PreflightCoreTests_" + Guid.NewGuid().ToString("N"));
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

    private static string ComputeFileSha256(string path)
    {
        using var stream = File.OpenRead(path);
        using var sha = SHA256.Create();
        return Convert.ToHexString(sha.ComputeHash(stream));
    }

    [TestMethod]
    public void AddressResolver_ShorthandAndLegacyAddressing_ResolvesExpectedPaths()
    {
        // Legacy full path without iteration
        var (ok1, path1, iter1, err1) = DocumentAddressResolver.Resolve(null, "20260823-xpath-core/spec.xml");
        Assert.IsTrue(ok1);
        Assert.IsNull(err1);
        Assert.AreEqual("20260823-xpath-core/spec.xml", path1);
        Assert.AreEqual("20260823-xpath-core", iter1);

        // Root document without iteration
        var (ok2, path2, iter2, err2) = DocumentAddressResolver.Resolve(null, "backlog.xml");
        Assert.IsTrue(ok2);
        Assert.IsNull(err2);
        Assert.AreEqual("backlog.xml", path2);
        Assert.IsNull(iter2);

        // Shorthand with iteration
        var (ok3, path3, iter3, err3) = DocumentAddressResolver.Resolve("20260823-xpath-core", "spec.xml");
        Assert.IsTrue(ok3);
        Assert.IsNull(err3);
        Assert.AreEqual("20260823-xpath-core/spec.xml", path3);
        Assert.AreEqual("20260823-xpath-core", iter3);

        var (ok4, path4, iter4, err4) = DocumentAddressResolver.Resolve("20260823-xpath-core", "tasks.xml");
        Assert.IsTrue(ok4);
        Assert.IsNull(err4);
        Assert.AreEqual("20260823-xpath-core/tasks.xml", path4);
        Assert.AreEqual("20260823-xpath-core", iter4);

        // Full path with matching iteration
        var (ok5, path5, iter5, err5) = DocumentAddressResolver.Resolve("20260823-xpath-core", "20260823-xpath-core/spec.xml");
        Assert.IsTrue(ok5);
        Assert.IsNull(err5);
        Assert.AreEqual("20260823-xpath-core/spec.xml", path5);
        Assert.AreEqual("20260823-xpath-core", iter5);

        // Iteration only when document not required
        var (ok6, path6, iter6, err6) = DocumentAddressResolver.Resolve("20260823-xpath-core", null, requireDocument: false);
        Assert.IsTrue(ok6);
        Assert.IsNull(err6);
        Assert.IsNull(path6);
        Assert.AreEqual("20260823-xpath-core", iter6);
    }

    [TestMethod]
    public void AddressResolver_ConflictsAndTraversals_RejectedFailClosed()
    {
        // Conflict: iteration with another iteration's document path
        var (ok1, _, _, err1) = DocumentAddressResolver.Resolve("20260823-xpath-core", "other-iter/spec.xml");
        Assert.IsFalse(ok1);
        Assert.IsNotNull(err1);
        Assert.AreEqual(DiagnosticCodes.InvalidArgument, err1.Code);

        // Conflict: iteration paired with root document
        var (ok2, _, _, err2) = DocumentAddressResolver.Resolve("20260823-xpath-core", "backlog.xml");
        Assert.IsFalse(ok2);
        Assert.IsNotNull(err2);
        Assert.AreEqual(DiagnosticCodes.InvalidArgument, err2.Code);

        var (ok3, _, _, err3) = DocumentAddressResolver.Resolve("20260823-xpath-core", "knowledge.xml");
        Assert.IsFalse(ok3);
        Assert.IsNotNull(err3);
        Assert.AreEqual(DiagnosticCodes.InvalidArgument, err3.Code);

        // Traversal rejection
        var (ok4, _, _, err4) = DocumentAddressResolver.Resolve(null, "../escaped.xml");
        Assert.IsFalse(ok4);
        Assert.IsNotNull(err4);
        Assert.AreEqual(DiagnosticCodes.PathTraversalDetected, err4.Code);

        var (ok5, _, _, err5) = DocumentAddressResolver.Resolve(null, "..");
        Assert.IsFalse(ok5);
        Assert.IsNotNull(err5);
        Assert.AreEqual(DiagnosticCodes.PathTraversalDetected, err5.Code);

        var (ok6, _, _, err6) = DocumentAddressResolver.Resolve("20260823-xpath-core", "../escaped.xml");
        Assert.IsFalse(ok6);
        Assert.IsNotNull(err6);
        Assert.AreEqual(DiagnosticCodes.PathTraversalDetected, err6.Code);

        // Rooted/absolute path rejection
        var (ok7, _, _, err7) = DocumentAddressResolver.Resolve(null, "/absolute/path.xml");
        Assert.IsFalse(ok7);
        Assert.IsNotNull(err7);
        Assert.AreEqual(DiagnosticCodes.PathEscapeDetected, err7.Code);

        // Alternate data stream syntax rejection
        var (ok8, _, _, err8) = DocumentAddressResolver.Resolve(null, "spec.xml:stream");
        Assert.IsFalse(ok8);
        Assert.IsNotNull(err8);
        Assert.AreEqual(DiagnosticCodes.InvalidPath, err8.Code);

        // requireDocument=true when document is null
        var (ok9, _, _, err9) = DocumentAddressResolver.Resolve("20260823-xpath-core", null, requireDocument: true);
        Assert.IsFalse(ok9);
        Assert.IsNotNull(err9);
        Assert.AreEqual(DiagnosticCodes.InvalidArgument, err9.Code);
    }

    [TestMethod]
    public void RevisionResolver_MalformedOrMissing_FailsClosedNeverDefaultsToOne()
    {
        var ws = CreateWorkspaceCopy();
        var tasksRelPath = "20260823-xpath-core/tasks.xml";
        var fullTasksPath = Path.Combine(ws, "20260823-xpath-core", "tasks.xml");

        // Normal file reads positive revision
        var (okValid, revValid, errValid) = DocumentRevisionResolver.ReadDocumentRevision(ws, tasksRelPath);
        Assert.IsTrue(okValid);
        Assert.IsNull(errValid);
        Assert.AreEqual(9, revValid);

        // 1. Missing revision attribute entirely
        var doc = XDocument.Load(fullTasksPath);
        doc.Root!.Attribute("revision")?.Remove();
        doc.Save(fullTasksPath);

        var (ok1, rev1, err1) = DocumentRevisionResolver.ReadDocumentRevision(ws, tasksRelPath);
        Assert.IsFalse(ok1, "Must fail when revision attribute is missing.");
        Assert.AreNotEqual(1, rev1, "Must never default to 1 on missing revision.");
        Assert.AreEqual(0, rev1);
        Assert.IsNotNull(err1);
        Assert.AreEqual(DiagnosticCodes.XmlParseError, err1.Code);

        var (resOk1, resRev1, resErr1) = DocumentRevisionResolver.ResolveExpectedRevision(ws, tasksRelPath, null);
        Assert.IsFalse(resOk1);
        Assert.AreEqual(0, resRev1);
        Assert.IsNotNull(resErr1);

        // 2. Empty string revision
        doc.Root!.SetAttributeValue("revision", "");
        doc.Save(fullTasksPath);

        var (ok2, rev2, err2) = DocumentRevisionResolver.ReadDocumentRevision(ws, tasksRelPath);
        Assert.IsFalse(ok2, "Must fail when revision attribute is empty.");
        Assert.AreEqual(0, rev2);
        Assert.IsNotNull(err2);

        // 3. Non-numeric revision
        doc.Root!.SetAttributeValue("revision", "malformed_rev");
        doc.Save(fullTasksPath);

        var (ok3, rev3, err3) = DocumentRevisionResolver.ReadDocumentRevision(ws, tasksRelPath);
        Assert.IsFalse(ok3, "Must fail when revision is non-numeric.");
        Assert.AreEqual(0, rev3);
        Assert.IsNotNull(err3);

        // 4. Zero revision
        doc.Root!.SetAttributeValue("revision", "0");
        doc.Save(fullTasksPath);

        var (ok4, rev4, err4) = DocumentRevisionResolver.ReadDocumentRevision(ws, tasksRelPath);
        Assert.IsFalse(ok4, "Must fail when revision is zero.");
        Assert.AreEqual(0, rev4);
        Assert.IsNotNull(err4);

        // 5. Negative revision
        doc.Root!.SetAttributeValue("revision", "-3");
        doc.Save(fullTasksPath);

        var (ok5, rev5, err5) = DocumentRevisionResolver.ReadDocumentRevision(ws, tasksRelPath);
        Assert.IsFalse(ok5, "Must fail when revision is negative.");
        Assert.AreEqual(0, rev5);
        Assert.IsNotNull(err5);

        // 6. Explicit negative revision rejected
        var (okExp, revExp, errExp) = DocumentRevisionResolver.ResolveExpectedRevision(ws, tasksRelPath, -1);
        Assert.IsFalse(okExp);
        Assert.AreEqual(0, revExp);
        Assert.IsNotNull(errExp);
        Assert.AreEqual(DiagnosticCodes.InvalidArgument, errExp.Code);

        // 7. Non-existent file fails closed
        var (okMissing, revMissing, errMissing) = DocumentRevisionResolver.ReadDocumentRevision(ws, "20260823-nonexistent/tasks.xml");
        Assert.IsFalse(okMissing);
        Assert.AreEqual(0, revMissing);
        Assert.IsNotNull(errMissing);
        Assert.AreEqual(DiagnosticCodes.DocumentNotFound, errMissing.Code);
    }

    [TestMethod]
    public void Preflight_TaskAdd_ValidatesAndReportsProspectiveRevisionWithoutWriting()
    {
        var ws = CreateWorkspaceCopy();
        var tasksPath = Path.Combine(ws, "20260823-xpath-core", "tasks.xml");
        var originalHash = ComputeFileSha256(tasksPath);
        var originalMtime = File.GetLastWriteTimeUtc(tasksPath);

        var requestXml = """
<?xml version="1.0" encoding="utf-8"?>
<task-add id="20260823T150000Z-taskadd-preflight"
          actor="unit-test"
          occurred_at="2026-08-23T15:00:00Z">
  <task id="20260823-task-preflight-unit-test" status="pending" created_at="2026-08-23T15:00:00Z" updated_at="2026-08-23T15:00:00Z">
    <index>
      <summary>Preflight test task.</summary>
      <term key="kind" value="feature" />
    </index>
    <title>Preflight test task</title>
    <objective>Verify mutation preflight executes without disk writes.</objective>
    <rationale>Testing preflight zero-write guarantee.</rationale>
    <scope>
      <repository path=".">
        <include path="src/DogdouSpec.Core/" />
      </repository>
    </scope>
    <origin>
      <ref scope="iteration" target="20260823-req-iteration-discovery" relation="implements" />
    </origin>
    <constraints />
    <acceptance>
      <criterion id="20260823-task-preflight-done" status="pending">Preflight completes successfully.</criterion>
    </acceptance>
    <context>
      <summary>Context for preflight.</summary>
    </context>
    <review required="false" />
    <records />
  </task>
</task-add>
""";

        var (success, result, diags) = MutationPreflight.Preflight(ws, requestXml, "20260823-xpath-core");
        Assert.IsTrue(success, diags.Count > 0 ? diags[0].Message : "Preflight failed");
        Assert.IsNotNull(result);
        Assert.AreEqual("task-add", result.RequestType);
        Assert.AreEqual("20260823-xpath-core", result.IterationId);
        Assert.AreEqual("20260823-task-preflight-unit-test", result.TaskId);
        Assert.IsNotNull(result.ProspectiveEnvelope);
        Assert.AreEqual("task add", result.ProspectiveEnvelope.Command);
        Assert.AreEqual(1, result.ProspectiveEnvelope.Documents.Count);
        Assert.AreEqual("20260823-xpath-core/tasks.xml", result.ProspectiveEnvelope.Documents[0].Path);
        Assert.AreEqual(10, result.ProspectiveEnvelope.Documents[0].Revision);
        Assert.AreEqual(9, result.ProspectiveEnvelope.Documents[0].PreviousRevision);

        // ZERO DISK WRITES: file on disk must be completely unmodified
        var currentHash = ComputeFileSha256(tasksPath);
        var currentMtime = File.GetLastWriteTimeUtc(tasksPath);
        Assert.AreEqual(originalHash, currentHash, "tasks.xml content must not have changed during preflight.");
        Assert.AreEqual(originalMtime, currentMtime, "tasks.xml timestamp must not have changed during preflight.");

        // _tmp directory should have no lingering transaction artifacts
        var tmpDir = Path.Combine(ws, "_tmp");
        if (Directory.Exists(tmpDir))
        {
            var lingering = Directory.GetFileSystemEntries(tmpDir);
            Assert.AreEqual(0, lingering.Length, "No lingering files in _tmp after preflight.");
        }
    }

    [TestMethod]
    public void Preflight_InvalidSchemaOrConflict_FailsClosedWithoutWriting()
    {
        var ws = CreateWorkspaceCopy();
        var tasksPath = Path.Combine(ws, "20260823-xpath-core", "tasks.xml");
        var originalHash = ComputeFileSha256(tasksPath);

        // 1. Schema violation: missing required <objective> element in <task>
        var invalidSchemaXml = """
<?xml version="1.0" encoding="utf-8"?>
<task-add id="20260823T150000Z-taskadd-broken"
          actor="unit-test"
          occurred_at="2026-08-23T15:00:00Z">
  <task id="20260823-task-broken" status="pending" created_at="2026-08-23T15:00:00Z" updated_at="2026-08-23T15:00:00Z">
    <title>Missing objective</title>
  </task>
</task-add>
""";

        var (s1, r1, d1) = MutationPreflight.Preflight(ws, invalidSchemaXml, "20260823-xpath-core");
        Assert.IsFalse(s1, "Schema invalid XML must fail preflight.");
        Assert.IsNull(r1);
        Assert.IsTrue(d1.Any(d => d.Code == DiagnosticCodes.SchemaValidationError), "Must produce schema validation error.");
        Assert.AreEqual(originalHash, ComputeFileSha256(tasksPath), "File must remain untouched.");

        // 2. State violation: expected revision conflict
        var conflictXml = """
<?xml version="1.0" encoding="utf-8"?>
<task-add id="20260823T150000Z-taskadd-conflict"
          actor="unit-test"
          occurred_at="2026-08-23T15:00:00Z">
  <task id="20260823-task-conflict" status="pending" created_at="2026-08-23T15:00:00Z" updated_at="2026-08-23T15:00:00Z">
    <index>
      <summary>Conflict summary.</summary>
      <term key="kind" value="feature" />
    </index>
    <title>Conflict task</title>
    <objective>Expect revision 999 which does not match.</objective>
    <rationale>Testing conflict.</rationale>
    <scope>
      <repository path=".">
        <include path="src/" />
      </repository>
    </scope>
    <origin>
      <ref scope="iteration" target="20260823-req-iteration-discovery" relation="implements" />
    </origin>
    <constraints />
    <acceptance>
      <criterion id="20260823-task-conflict-done" status="pending">Done.</criterion>
    </acceptance>
    <context>
      <summary>Context.</summary>
    </context>
    <review required="false" />
    <records />
  </task>
</task-add>
""";

        var (s2, r2, d2) = MutationPreflight.Preflight(ws, conflictXml, "20260823-xpath-core", expectedRevision: 999);
        Assert.IsFalse(s2, "Revision mismatch must fail preflight.");
        Assert.IsNull(r2);
        Assert.IsTrue(d2.Any(d => d.Code == DiagnosticCodes.RevisionConflict), $"Must produce revision conflict error, but got: {string.Join(", ", d2.Select(d => d.Code + ": " + d.Message))}");
        Assert.AreEqual(originalHash, ComputeFileSha256(tasksPath), "File must remain untouched.");
    }

    [TestMethod]
    public void IterationComplete_RequiresAndEnforcesTasksReadPrecondition()
    {
        var ws = CreateWorkspaceCopy();

        // 1. Missing expected_tasks_revision fails closed
        var reqNoTasksRev = """
<?xml version="1.0" encoding="utf-8"?>
<iteration-confirmation
  id="20260823T180000Z-confirm-complete"
  iteration="20260823-xpath-core"
  action="complete"
  expected_spec_revision="4"
  actor="owner-instruction"
  decided_at="2026-08-23T18:00:00Z">
  <summary>Complete iteration without tasks rev.</summary>
</iteration-confirmation>
""";

        var (ok1, _, diags1) = IterationConfirmer.Confirm(ws, reqNoTasksRev, dryRun: true);
        Assert.IsFalse(ok1, "Completion without expected_tasks_revision must fail.");
        Assert.IsTrue(diags1.Any(d => d.Code == DiagnosticCodes.InvalidArgument && d.Message.Contains("expected_tasks_revision")),
            "Diagnostic must state expected_tasks_revision is required.");

        // 2. Mismatched expected_tasks_revision fails with revision conflict on tasks.xml
        var reqWrongTasksRev = """
<?xml version="1.0" encoding="utf-8"?>
<iteration-confirmation
  id="20260823T180000Z-confirm-complete"
  iteration="20260823-xpath-core"
  action="complete"
  expected_spec_revision="4"
  expected_tasks_revision="999"
  actor="owner-instruction"
  decided_at="2026-08-23T18:00:00Z">
  <summary>Complete iteration with wrong tasks rev.</summary>
</iteration-confirmation>
""";

        var (ok2, _, diags2) = IterationConfirmer.Confirm(ws, reqWrongTasksRev, dryRun: true);
        Assert.IsFalse(ok2, "Completion with wrong expected_tasks_revision must fail.");
        Assert.IsTrue(diags2.Any(d => d.Code == DiagnosticCodes.RevisionConflict && d.Document == "20260823-xpath-core/tasks.xml"),
            $"Diagnostic must be revision conflict on tasks.xml, but got: {string.Join(", ", diags2.Select(d => d.Code + " (" + d.Document + "): " + d.Message))}");
    }
}
