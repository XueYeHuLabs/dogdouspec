using System.Xml.Linq;
using DogdouSpec.Core.Diagnostics;
using DogdouSpec.Core.Iterations;
using DogdouSpec.Core.Time;
using DogdouSpec.Core.Transactions;
using DogdouSpec.Core.Validation;

namespace DogdouSpec.Core.Tests;

[TestClass]
public sealed class IterationCreationAndListingTests
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
        _tempDir = Path.Combine(Path.GetTempPath(), "DogdouSpec_IterTests_" + Guid.NewGuid().ToString("N"));
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
    public void Create_FeatureIteration_CreatesValidSpecAndTasksWithDeterministicIds()
    {
        var workspace = CreateWorkspaceCopy();
        var fixedTime = new DateTime(2026, 8, 23, 10, 0, 0, DateTimeKind.Utc);
        var clock = new TestClock(fixedTime);

        var iterId = "20260823-atomic-writes";
        var (success, env, diags) = IterationCreator.Create(
            workspace,
            iterId,
            "feature",
            clock);

        Assert.IsTrue(success, $"Creation failed: {string.Join("; ", diags.Select(d => d.Message))}");
        Assert.IsNotNull(env);
        Assert.AreEqual("iteration create", env.Command);
        Assert.AreEqual(2, env.Documents.Count);
        Assert.AreEqual("20260823-atomic-writes/spec.xml", env.Documents[0].Path);
        Assert.AreEqual(1, env.Documents[0].Revision);
        Assert.AreEqual("20260823-atomic-writes/tasks.xml", env.Documents[1].Path);
        Assert.AreEqual(1, env.Documents[1].Revision);

        // Check files on disk
        var iterDir = Path.Combine(workspace, iterId);
        Assert.IsTrue(Directory.Exists(iterDir));

        var specPath = Path.Combine(iterDir, "spec.xml");
        var tasksPath = Path.Combine(iterDir, "tasks.xml");
        Assert.IsTrue(File.Exists(specPath));
        Assert.IsTrue(File.Exists(tasksPath));

        // Verify spec.xml contents
        var specXDoc = XDocument.Load(specPath);
        var specRoot = specXDoc.Root!;
        Assert.AreEqual(iterId, specRoot.Attribute("id")?.Value);
        Assert.AreEqual("feature", specRoot.Attribute("kind")?.Value);
        Assert.AreEqual("draft", specRoot.Attribute("status")?.Value);
        Assert.AreEqual("1", specRoot.Attribute("revision")?.Value);
        Assert.AreEqual("1.0", specRoot.Attribute("schema_version")?.Value);
        Assert.AreEqual("2026-08-23T10:00:00Z", specRoot.Attribute("created_at")?.Value);
        Assert.IsNotNull(specRoot.Element("product"));
        Assert.IsNull(specRoot.Element("research"));
        Assert.IsNull(specRoot.Element("design"));
        Assert.IsNotNull(specRoot.Element("confirmations"));
        Assert.AreEqual(0, specRoot.Element("confirmations")!.Elements().Count());

        // Check deliverable, requirement, criterion
        var deliv = specRoot.Descendants("deliverable").FirstOrDefault();
        Assert.IsNotNull(deliv);
        Assert.AreEqual("20260823-deliv-atomic-writes", deliv.Attribute("id")?.Value);

        var req = specRoot.Descendants("requirement").FirstOrDefault();
        Assert.IsNotNull(req);
        Assert.AreEqual("20260823-req-atomic-writes", req.Attribute("id")?.Value);
        Assert.AreEqual("proposed", req.Attribute("status")?.Value);

        var crit = specRoot.Descendants("criterion").FirstOrDefault();
        Assert.IsNotNull(crit);
        Assert.AreEqual("20260823-crit-atomic-writes", crit.Attribute("id")?.Value);
        Assert.AreEqual("pending", crit.Attribute("decision")?.Value);

        // Verify tasks.xml contents
        var tasksXDoc = XDocument.Load(tasksPath);
        var tasksRoot = tasksXDoc.Root!;
        Assert.AreEqual("20260823-tasks-atomic-writes", tasksRoot.Attribute("id")?.Value);
        Assert.AreEqual(iterId, tasksRoot.Attribute("iteration")?.Value);
        Assert.AreEqual("1", tasksRoot.Attribute("revision")?.Value);
        Assert.AreEqual("1.0", tasksRoot.Attribute("schema_version")?.Value);
        Assert.AreEqual(0, tasksRoot.Elements("task").Count());

        // Validate whole workspace
        var val = SchemaValidator.Validate(workspace);
        Assert.IsTrue(val.IsValid, $"Workspace validation failed: {string.Join("; ", val.Diagnostics.Select(d => $"{d.Code}: {d.Message}"))}");
    }

    [TestMethod]
    public void Create_ResearchIteration_CreatesValidSpecAndTasksWithDeterministicIds()
    {
        var workspace = CreateWorkspaceCopy();
        var fixedTime = new DateTime(2026, 8, 23, 11, 0, 0, DateTimeKind.Utc);
        var clock = new TestClock(fixedTime);

        var iterId = "20260823-concurrency-research";
        var (success, env, diags) = IterationCreator.Create(
            workspace,
            iterId,
            "research",
            clock);

        Assert.IsTrue(success, $"Creation failed: {string.Join("; ", diags.Select(d => d.Message))}");
        Assert.IsNotNull(env);

        var iterDir = Path.Combine(workspace, iterId);
        var specPath = Path.Combine(iterDir, "spec.xml");
        var tasksPath = Path.Combine(iterDir, "tasks.xml");

        var specXDoc = XDocument.Load(specPath);
        var specRoot = specXDoc.Root!;
        Assert.AreEqual(iterId, specRoot.Attribute("id")?.Value);
        Assert.AreEqual("research", specRoot.Attribute("kind")?.Value);
        Assert.AreEqual("draft", specRoot.Attribute("status")?.Value);
        Assert.IsNotNull(specRoot.Element("research"));
        Assert.IsNull(specRoot.Element("product"));

        var q = specRoot.Descendants("question").FirstOrDefault();
        Assert.IsNotNull(q);
        Assert.AreEqual("20260823-q-concurrency-research", q.Attribute("id")?.Value);
        Assert.AreEqual("open", q.Attribute("status")?.Value);

        var crit = specRoot.Descendants("criterion").FirstOrDefault();
        Assert.IsNotNull(crit);
        Assert.AreEqual("20260823-crit-concurrency-research", crit.Attribute("id")?.Value);
        Assert.AreEqual("pending", crit.Attribute("decision")?.Value);

        var tasksXDoc = XDocument.Load(tasksPath);
        var tasksRoot = tasksXDoc.Root!;
        Assert.AreEqual("20260823-tasks-concurrency-research", tasksRoot.Attribute("id")?.Value);
        Assert.AreEqual(iterId, tasksRoot.Attribute("iteration")?.Value);

        // Validate whole workspace
        var val = SchemaValidator.Validate(workspace);
        Assert.IsTrue(val.IsValid, $"Workspace validation failed: {string.Join("; ", val.Diagnostics.Select(d => $"{d.Code}: {d.Message}"))}");
    }

    [TestMethod]
    public void Create_ExistingTargetCollision_FailsWithoutMutationWithExitCode4()
    {
        var workspace = CreateWorkspaceCopy();
        var existingIter = "20260823-xpath-core";

        var (success, env, diags) = IterationCreator.Create(
            workspace,
            existingIter,
            "feature");

        Assert.IsFalse(success);
        Assert.IsNull(env);
        Assert.IsTrue(diags.Any(d => d.Code == DiagnosticCodes.IterationAlreadyExists));
        var diag = diags.First(d => d.Code == DiagnosticCodes.IterationAlreadyExists);
        Assert.AreEqual(4, DiagnosticsEnvelope.GetExitCodeForCode(diag.Code));
    }

    [TestMethod]
    public void Create_InvalidIdGrammar_FailsWithExitCode2()
    {
        var workspace = CreateWorkspaceCopy();

        var invalidIds = new[]
        {
            "invalid_name",
            "20260823",
            "20260823-UPPERCASE",
            "20260823-name_with_underscore",
            "../traversal",
            ""
        };

        foreach (var badId in invalidIds)
        {
            var (success, env, diags) = IterationCreator.Create(workspace, badId, "feature");
            Assert.IsFalse(success, $"ID '{badId}' should have been rejected");
            Assert.IsNull(env);
            Assert.IsTrue(diags.Count > 0);
            var code = diags[0].Code;
            Assert.AreEqual(2, DiagnosticsEnvelope.GetExitCodeForCode(code));
        }
    }

    [TestMethod]
    public void Create_InvalidKind_FailsWithExitCode2()
    {
        var workspace = CreateWorkspaceCopy();

        var (success, env, diags) = IterationCreator.Create(
            workspace,
            "20260823-new-work",
            "unknown_kind");

        Assert.IsFalse(success);
        Assert.IsNull(env);
        Assert.AreEqual(DiagnosticCodes.InvalidArgument, diags[0].Code);
        Assert.AreEqual(2, DiagnosticsEnvelope.GetExitCodeForCode(diags[0].Code));
    }

    [TestMethod]
    public void Create_SimulatedCrashes_LeavesNoLeftoverOrPartialIteration()
    {
        var phases = new[]
        {
            FaultPhase.BeforeStaging,
            FaultPhase.AfterStagingBeforeValidation,
            FaultPhase.AfterValidationBeforeCommitMarker,
            FaultPhase.AfterCommitMarkerBeforePublish
        };

        foreach (var phase in phases)
        {
            var workspace = CreateWorkspaceCopy();
            var iterId = "20260823-crash-test-" + phase.ToString().ToLowerInvariant();
            var injector = new TestFaultInjector(phase);

            var (success, env, diags) = IterationCreator.Create(
                workspace,
                iterId,
                "feature",
                faultInjector: injector);

            Assert.IsFalse(success, $"Create should have failed at phase {phase}");
            Assert.IsNull(env);

            // Target directory must not exist in workspace
            var targetDir = Path.Combine(workspace, iterId);
            Assert.IsFalse(Directory.Exists(targetDir), $"Target dir must not exist after crash at {phase}");

            // Run startup recovery
            var (recSuccess, recErr) = StartupRecovery.Run(workspace);
            Assert.IsTrue(recSuccess);
            Assert.IsNull(recErr);

            // Staging folder in _tmp must be gone
            var tmpEntries = Directory.GetFileSystemEntries(Path.Combine(workspace, "_tmp"));
            Assert.AreEqual(0, tmpEntries.Length(e => !e.EndsWith("writer.lock", StringComparison.OrdinalIgnoreCase)));
        }
    }

    [TestMethod]
    public void List_MultipleIterations_ReturnsInDateOrderWithCompactMetadata()
    {
        var workspace = CreateWorkspaceCopy();

        // Add a second iteration
        var (createSuccess, _, _) = IterationCreator.Create(
            workspace,
            "20260824-second-iteration",
            "research");
        Assert.IsTrue(createSuccess);

        var (success, result, diags) = IterationLister.List(workspace);

        Assert.IsTrue(success, $"List failed: {string.Join("; ", diags.Select(d => d.Message))}");
        Assert.IsNotNull(result);
        Assert.AreEqual(0, diags.Count);
        Assert.AreEqual(2, result.Iterations.Count);

        // Date sorted order
        Assert.AreEqual("20260823-xpath-core", result.Iterations[0].Id);
        Assert.AreEqual("feature", result.Iterations[0].Kind);
        Assert.AreEqual("active", result.Iterations[0].Status);
        Assert.AreEqual(4, result.Iterations[0].SpecRevision);
        Assert.AreEqual(9, result.Iterations[0].TasksRevision);
        Assert.IsNotNull(result.Iterations[0].IndexElement);

        Assert.AreEqual("20260824-second-iteration", result.Iterations[1].Id);
        Assert.AreEqual("research", result.Iterations[1].Kind);
        Assert.AreEqual("draft", result.Iterations[1].Status);
        Assert.AreEqual(1, result.Iterations[1].SpecRevision);
        Assert.AreEqual(1, result.Iterations[1].TasksRevision);

        // Check XML output format
        var xml = result.ToXmlString();
        Assert.IsTrue(xml.Contains("<iterations workspace="));
        Assert.IsTrue(xml.Contains("id=\"20260823-xpath-core\""));
        Assert.IsTrue(xml.Contains("id=\"20260824-second-iteration\""));
        Assert.IsTrue(xml.Contains("spec_revision=\"4\""));
        Assert.IsTrue(xml.Contains("tasks_revision=\"9\""));

        // Check Human output format
        var human = result.ToHumanString();
        Assert.IsTrue(human.Contains("20260823-xpath-core"));
        Assert.IsTrue(human.Contains("20260824-second-iteration"));
    }

    [TestMethod]
    public void List_IgnoresNonCandidateDirectories_AndTmpSchemaSkill()
    {
        var workspace = CreateWorkspaceCopy();

        // Create random non-candidate directories
        Directory.CreateDirectory(Path.Combine(workspace, "random_folder"));
        Directory.CreateDirectory(Path.Combine(workspace, "docs"));
        Directory.CreateDirectory(Path.Combine(workspace, "_tmp", "temp_staging"));

        var (success, result, diags) = IterationLister.List(workspace);

        Assert.IsTrue(success);
        Assert.IsNotNull(result);
        Assert.AreEqual(0, diags.Count);
        Assert.AreEqual(1, result.Iterations.Count);
        Assert.AreEqual("20260823-xpath-core", result.Iterations[0].Id);
    }

    [TestMethod]
    public void List_MalformedCandidateDirectory_ReportsStructuredDiagnosticsWithExitCode3()
    {
        var workspace = CreateWorkspaceCopy();

        // Create a malformed candidate directory missing tasks.xml
        var malformedDir = Path.Combine(workspace, "20260825-broken-iteration");
        Directory.CreateDirectory(malformedDir);
        File.WriteAllText(Path.Combine(malformedDir, "spec.xml"), "<iteration id=\"20260825-broken-iteration\" schema_version=\"1.0\" revision=\"1\" kind=\"feature\" status=\"draft\" created_at=\"2026-08-25T00:00:00Z\" updated_at=\"2026-08-25T00:00:00Z\"><index><summary>Test</summary></index><product><objective>Test</objective><deliverables><deliverable id=\"20260825-deliv-test\"><index><summary>Test</summary></index><description>Test</description></deliverable></deliverables><scope><included/><excluded/></scope><requirements><requirement id=\"20260825-req-test\" status=\"proposed\"><index><summary>Test</summary></index><statement>Test</statement><rationale>Test</rationale></requirement></requirements><acceptance><criterion id=\"20260825-crit-test\" decision=\"pending\">Test</criterion></acceptance></product><confirmations/></iteration>");
        // tasks.xml is missing!

        var (success, result, diags) = IterationLister.List(workspace);

        Assert.IsFalse(success);
        Assert.IsNull(result);
        Assert.IsTrue(diags.Any(d => d.Code == DiagnosticCodes.DocumentNotFound && d.Document == "20260825-broken-iteration/tasks.xml"));
        var envelope = new DiagnosticsEnvelope("iteration list", diags);
        Assert.AreEqual(2, envelope.GetExitCode());
    }

    [TestMethod]
    public void List_CandidateDirectoryWithIdMismatch_ReportsDiagnostic()
    {
        var workspace = CreateWorkspaceCopy();

        // Create candidate directory with mismatched root id
        var mismatchedDir = Path.Combine(workspace, "20260825-dir-name");
        Directory.CreateDirectory(mismatchedDir);
        File.WriteAllText(Path.Combine(mismatchedDir, "spec.xml"), "<iteration id=\"20260825-different-id\" schema_version=\"1.0\" revision=\"1\" kind=\"feature\" status=\"draft\" created_at=\"2026-08-25T00:00:00Z\" updated_at=\"2026-08-25T00:00:00Z\"><index><summary>Test</summary></index><product><objective>Test</objective><deliverables><deliverable id=\"20260825-deliv-test\"><index><summary>Test</summary></index><description>Test</description></deliverable></deliverables><scope><included/><excluded/></scope><requirements><requirement id=\"20260825-req-test\" status=\"proposed\"><index><summary>Test</summary></index><statement>Test</statement><rationale>Test</rationale></requirement></requirements><acceptance><criterion id=\"20260825-crit-test\" decision=\"pending\">Test</criterion></acceptance></product><confirmations/></iteration>");
        File.WriteAllText(Path.Combine(mismatchedDir, "tasks.xml"), "<tasks id=\"20260825-tasks-test\" iteration=\"20260825-different-id\" schema_version=\"1.0\" revision=\"1\"><index><summary>Test</summary></index></tasks>");

        var (success, result, diags) = IterationLister.List(workspace);

        Assert.IsFalse(success);
        Assert.IsNull(result);
        Assert.IsTrue(diags.Any(d => d.Code == DiagnosticCodes.IterationIdMismatch || d.Code == DiagnosticCodes.TasksIterationMismatch));
        var envelope = new DiagnosticsEnvelope("iteration list", diags);
        Assert.AreEqual(3, envelope.GetExitCode());
    }
}

file static class Extensions
{
    public static int Length(this string[] array, Func<string, bool> predicate) =>
        array.Count(predicate);
}
