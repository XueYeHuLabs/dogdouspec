using DogdouSpec.Core.Diagnostics;
using DogdouSpec.Core.Resources;
using DogdouSpec.Core.Validation;
using DogdouSpec.Core.Workspace;

namespace DogdouSpec.Core.Tests;

[TestClass]
public sealed class ValidationTests
{
    private string _tempDir = null!;
    private static string RepoRoot = null!;

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
        _tempDir = Path.Combine(Path.GetTempPath(), "DogdouSpec_ValidationTests_" + Guid.NewGuid().ToString("N"));
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

    [TestMethod]
    public void Validate_DemoWorkspace_AllManagedXmlPasses()
    {
        var demoWorkspace = Path.Combine(RepoRoot, "docs", "demos", "v1-core", ".dogdouspec");
        Assert.IsTrue(Directory.Exists(demoWorkspace), $"Demo workspace not found at {demoWorkspace}");

        var result = SchemaValidator.Validate(demoWorkspace);

        Assert.IsTrue(result.IsValid, $"Demo workspace validation failed: {string.Join("; ", result.Diagnostics.Select(d => d.Message))}");
        Assert.AreEqual(4, result.CheckedDocumentsCount, "Should check knowledge.xml, backlog.xml, spec.xml, tasks.xml");
        Assert.AreEqual(0, result.Diagnostics.Count(d => d.Severity == "error"));
    }

    [TestMethod]
    public void Validate_DemoWorkspaceIterationScope_Passes()
    {
        var demoWorkspace = Path.Combine(RepoRoot, "docs", "demos", "v1-core", ".dogdouspec");

        var result = SchemaValidator.Validate(demoWorkspace, iterationId: "20260823-xpath-core");

        Assert.IsTrue(result.IsValid);
        Assert.AreEqual(2, result.CheckedDocumentsCount);
        Assert.AreEqual("iteration", result.Scope);
        Assert.AreEqual("20260823-xpath-core", result.IterationId);
    }

    [TestMethod]
    [DataRow("knowledge.xml")]
    [DataRow("backlog.xml")]
    [DataRow("20260823-xpath-core/spec.xml")]
    [DataRow("20260823-xpath-core/tasks.xml")]
    public void Validate_DemoWorkspaceDocumentScope_Passes(string relativeDocPath)
    {
        var demoWorkspace = Path.Combine(RepoRoot, "docs", "demos", "v1-core", ".dogdouspec");

        var result = SchemaValidator.Validate(demoWorkspace, relativeDocumentPath: relativeDocPath);

        Assert.IsTrue(result.IsValid, $"Validation for {relativeDocPath} failed: {string.Join("; ", result.Diagnostics.Select(d => d.Message))}");
        Assert.AreEqual(1, result.CheckedDocumentsCount);
        Assert.AreEqual("document", result.Scope);
        Assert.AreEqual(relativeDocPath, result.DocumentPath);
    }

    [TestMethod]
    public void Validate_ResearchSpecFixture_PassesValidation()
    {
        var fixturePath = Path.Combine(RepoRoot, "schemas", "v1", "fixtures", "research-spec.xml");
        Assert.IsTrue(File.Exists(fixturePath), "research-spec.xml fixture must exist");

        var iterDir = Path.Combine(_tempDir, "20260823-research");
        Directory.CreateDirectory(iterDir);
        var specFile = Path.Combine(iterDir, "spec.xml");
        File.Copy(fixturePath, specFile);

        var doc = new ManagedDocument("20260823-research/spec.xml", specFile, "20260823-research");

        var result = SchemaValidator.ValidateDocument(doc);

        Assert.IsTrue(result.IsValid, $"research-spec.xml failed validation: {string.Join("; ", result.Diagnostics.Select(d => d.Message))}");
        Assert.AreEqual(0, result.Diagnostics.Count(d => d.Severity == "error"));
    }

    [TestMethod]
    public void Validate_StructurallyInvalidFixture_FailsClosedWithSchemaValidationError()
    {
        var invalidXml = """
<?xml version="1.0" encoding="utf-8"?>
<knowledge
  id="20260823-knowledge"
  schema_version="1.0"
  revision="1">
  <invalid-tag>Not in schema</invalid-tag>
</knowledge>
""";
        var docPath = Path.Combine(_tempDir, "knowledge.xml");
        File.WriteAllText(docPath, invalidXml);

        var doc = new ManagedDocument("knowledge.xml", docPath);

        var result = SchemaValidator.ValidateDocument(doc);

        Assert.IsFalse(result.IsValid);
        Assert.IsTrue(result.Diagnostics.Any(d => d.Code == DiagnosticCodes.SchemaValidationError));
        var error = result.Diagnostics.First(d => d.Code == DiagnosticCodes.SchemaValidationError);
        Assert.IsNotNull(error.Line);
        Assert.IsNotNull(error.Column);
    }

    [TestMethod]
    public void Validate_MalformedXml_FailsClosedWithXmlParseError()
    {
        var malformedXml = """
<?xml version="1.0" encoding="utf-8"?>
<knowledge id="20260823-knowledge" schema_version="1.0" revision="1">
  <unclosed-tag>
</knowledge>
""";
        var docPath = Path.Combine(_tempDir, "knowledge.xml");
        File.WriteAllText(docPath, malformedXml);

        var doc = new ManagedDocument("knowledge.xml", docPath);

        var result = SchemaValidator.ValidateDocument(doc);

        Assert.IsFalse(result.IsValid);
        Assert.IsTrue(result.Diagnostics.Any(d => d.Code == DiagnosticCodes.XmlParseError));
    }

    [TestMethod]
    public void Validate_DtdXml_FailsClosedWithDtdProhibited()
    {
        var dtdXml = """
<?xml version="1.0" encoding="utf-8"?>
<!DOCTYPE knowledge [
  <!ELEMENT knowledge ANY>
  <!ENTITY ext SYSTEM "http://example.com/malicious">
]>
<knowledge id="20260823-knowledge" schema_version="1.0" revision="1">
  <index><summary>Test</summary></index>
</knowledge>
""";
        var docPath = Path.Combine(_tempDir, "knowledge.xml");
        File.WriteAllText(docPath, dtdXml);

        var doc = new ManagedDocument("knowledge.xml", docPath);

        var result = SchemaValidator.ValidateDocument(doc);

        Assert.IsFalse(result.IsValid);
        Assert.IsTrue(result.Diagnostics.Any(d => d.Code == DiagnosticCodes.DtdProhibited));
    }

    [TestMethod]
    public void Validate_TasksXmlWithIterationRoot_FailsExactSchemaValidation()
    {
        // tasks.xml containing valid <iteration> root must fail because tasks.xsd does not declare <iteration>
        var specXmlContent = File.ReadAllText(Path.Combine(RepoRoot, "docs", "demos", "v1-core", ".dogdouspec", "20260823-xpath-core", "spec.xml"));
        var tasksPath = Path.Combine(_tempDir, "tasks.xml");
        File.WriteAllText(tasksPath, specXmlContent);

        var doc = new ManagedDocument("20260823-test/tasks.xml", tasksPath, "20260823-test");
        var result = SchemaValidator.ValidateDocument(doc);

        Assert.IsFalse(result.IsValid, "tasks.xml containing <iteration> root must fail schema validation");
        Assert.IsTrue(result.Diagnostics.Any(d => d.Code == DiagnosticCodes.SchemaValidationError));
    }

    [TestMethod]
    public void Validate_SpecXmlWithTasksRoot_FailsExactSchemaValidation()
    {
        // spec.xml containing valid <tasks> root must fail because spec.xsd does not declare <tasks>
        var tasksXmlContent = File.ReadAllText(Path.Combine(RepoRoot, "docs", "demos", "v1-core", ".dogdouspec", "20260823-xpath-core", "tasks.xml"));
        var specPath = Path.Combine(_tempDir, "spec.xml");
        File.WriteAllText(specPath, tasksXmlContent);

        var doc = new ManagedDocument("20260823-test/spec.xml", specPath, "20260823-test");
        var result = SchemaValidator.ValidateDocument(doc);

        Assert.IsFalse(result.IsValid, "spec.xml containing <tasks> root must fail schema validation");
        Assert.IsTrue(result.Diagnostics.Any(d => d.Code == DiagnosticCodes.SchemaValidationError));
    }

    [TestMethod]
    public void Validate_XsiNoNamespaceSchemaLocation_CannotOverrideAuthoritativeSchema()
    {
        var xmlWithHint = """
<?xml version="1.0" encoding="utf-8"?>
<knowledge
  xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance"
  xsi:noNamespaceSchemaLocation="http://evil.com/fake.xsd"
  id="20260823-knowledge"
  schema_version="1.0"
  revision="1">
  <invalid-node>Should fail against authoritative XSD</invalid-node>
</knowledge>
""";
        var docPath = Path.Combine(_tempDir, "knowledge.xml");
        File.WriteAllText(docPath, xmlWithHint);

        var doc = new ManagedDocument("knowledge.xml", docPath);
        var result = SchemaValidator.ValidateDocument(doc);

        Assert.IsFalse(result.IsValid);
        Assert.IsTrue(result.Diagnostics.Any(d => d.Code == DiagnosticCodes.SchemaValidationError));
    }

    [TestMethod]
    public void Validate_XsiSchemaLocation_CannotOverrideAuthoritativeSchema()
    {
        var xmlWithHint = """
<?xml version="1.0" encoding="utf-8"?>
<knowledge
  xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance"
  xsi:schemaLocation="urn:test http://evil.com/fake.xsd"
  id="20260823-knowledge"
  schema_version="1.0"
  revision="1">
  <invalid-node>Should fail against authoritative XSD</invalid-node>
</knowledge>
""";
        var docPath = Path.Combine(_tempDir, "knowledge.xml");
        File.WriteAllText(docPath, xmlWithHint);

        var doc = new ManagedDocument("knowledge.xml", docPath);
        var result = SchemaValidator.ValidateDocument(doc);

        Assert.IsFalse(result.IsValid);
        Assert.IsTrue(result.Diagnostics.Any(d => d.Code == DiagnosticCodes.SchemaValidationError));
    }

    [TestMethod]
    public void Validate_InlineSchema_CannotWeakenValidation()
    {
        var xmlWithInline = """
<?xml version="1.0" encoding="utf-8"?>
<knowledge
  xmlns:xs="http://www.w3.org/2001/XMLSchema"
  id="20260823-knowledge"
  schema_version="1.0"
  revision="1">
  <xs:schema id="inlineSchema">
    <xs:element name="anything" type="xs:anyType"/>
  </xs:schema>
</knowledge>
""";
        var docPath = Path.Combine(_tempDir, "knowledge.xml");
        File.WriteAllText(docPath, xmlWithInline);

        var doc = new ManagedDocument("knowledge.xml", docPath);
        var result = SchemaValidator.ValidateDocument(doc);

        Assert.IsFalse(result.IsValid);
        Assert.IsTrue(result.Diagnostics.Any(d => d.Code == DiagnosticCodes.SchemaValidationError));
    }

    [TestMethod]
    public void Validate_WorkspaceMissingKnowledgeXml_FailsWithDocumentNotFound()
    {
        var (initSuccess, root, _) = WorkspaceInitializer.Initialize(null, _tempDir);
        Assert.IsTrue(initSuccess);

        var knowledgeFile = Path.Combine(_tempDir, ".dogdouspec", "knowledge.xml");
        File.Delete(knowledgeFile);

        var result = SchemaValidator.Validate(root);

        Assert.IsFalse(result.IsValid);
        Assert.IsTrue(result.Diagnostics.Any(d => d.Code == DiagnosticCodes.DocumentNotFound && d.Document == "knowledge.xml"));
    }

    [TestMethod]
    public void Validate_WorkspaceMissingBacklogXml_FailsWithDocumentNotFound()
    {
        var (initSuccess, root, _) = WorkspaceInitializer.Initialize(null, _tempDir);
        Assert.IsTrue(initSuccess);

        var backlogFile = Path.Combine(_tempDir, ".dogdouspec", "backlog.xml");
        File.Delete(backlogFile);

        var result = SchemaValidator.Validate(root);

        Assert.IsFalse(result.IsValid);
        Assert.IsTrue(result.Diagnostics.Any(d => d.Code == DiagnosticCodes.DocumentNotFound && d.Document == "backlog.xml"));
    }

    [TestMethod]
    public void Validate_WorkspaceCandidateIterationMissingSpecXml_FailsWithDocumentNotFound()
    {
        var (initSuccess, root, _) = WorkspaceInitializer.Initialize(null, _tempDir);
        Assert.IsTrue(initSuccess);

        var iterDir = Path.Combine(_tempDir, ".dogdouspec", "20260823-test-feature");
        Directory.CreateDirectory(iterDir);

        // Create only tasks.xml
        var demoTasks = Path.Combine(RepoRoot, "docs", "demos", "v1-core", ".dogdouspec", "20260823-xpath-core", "tasks.xml");
        File.Copy(demoTasks, Path.Combine(iterDir, "tasks.xml"));

        var result = SchemaValidator.Validate(root);

        Assert.IsFalse(result.IsValid);
        Assert.IsTrue(result.Diagnostics.Any(d => d.Code == DiagnosticCodes.DocumentNotFound && d.Document == "20260823-test-feature/spec.xml"));
    }

    [TestMethod]
    public void Validate_WorkspaceCandidateIterationMissingTasksXml_FailsWithDocumentNotFound()
    {
        var (initSuccess, root, _) = WorkspaceInitializer.Initialize(null, _tempDir);
        Assert.IsTrue(initSuccess);

        var iterDir = Path.Combine(_tempDir, ".dogdouspec", "20260823-test-feature");
        Directory.CreateDirectory(iterDir);

        // Create only spec.xml
        var demoSpec = Path.Combine(RepoRoot, "docs", "demos", "v1-core", ".dogdouspec", "20260823-xpath-core", "spec.xml");
        File.Copy(demoSpec, Path.Combine(iterDir, "spec.xml"));

        var result = SchemaValidator.Validate(root);

        Assert.IsFalse(result.IsValid);
        Assert.IsTrue(result.Diagnostics.Any(d => d.Code == DiagnosticCodes.DocumentNotFound && d.Document == "20260823-test-feature/tasks.xml"));
    }

    [TestMethod]
    public void Validate_WorkspaceNonCandidateDirectory_IsIgnored()
    {
        var (initSuccess, root, _) = WorkspaceInitializer.Initialize(null, _tempDir);
        Assert.IsTrue(initSuccess);

        // Create an ordinary directory that does not match YYYYMMDD-name
        var nonCandidateDir = Path.Combine(_tempDir, ".dogdouspec", "scratch-notes");
        Directory.CreateDirectory(nonCandidateDir);
        File.WriteAllText(Path.Combine(nonCandidateDir, "notes.txt"), "Some notes");

        var result = SchemaValidator.Validate(root);

        Assert.IsTrue(result.IsValid, "Non-candidate directory should be ignored without causing validation errors");
        Assert.AreEqual(2, result.CheckedDocumentsCount);
    }

    [TestMethod]
    public void ValidateDocument_DocumentExceeding16MB_ReturnsLimitExceededDiagnostic()
    {
        var largeXml = $"<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<knowledge id=\"20260823-knowledge\" schema_version=\"1.0\" revision=\"1\">\n  <!-- {new string('x', 17 * 1024 * 1024)} -->\n  <index><summary>Test</summary></index>\n</knowledge>\n";
        var docPath = Path.Combine(_tempDir, "knowledge.xml");
        File.WriteAllText(docPath, largeXml);

        var doc = new ManagedDocument("knowledge.xml", docPath);
        var result = SchemaValidator.ValidateDocument(doc);

        Assert.IsFalse(result.IsValid);
        Assert.IsTrue(result.Diagnostics.Any(d => d.Code == DiagnosticCodes.LimitExceeded), $"Expected LIMIT_EXCEEDED diagnostic, got: {string.Join("; ", result.Diagnostics.Select(d => $"{d.Code}: {d.Message}"))}");
    }

    [TestMethod]
    public void Validate_NonExistentDocument_FailsWithDocumentNotFound()
    {
        var demoWorkspace = Path.Combine(RepoRoot, "docs", "demos", "v1-core", ".dogdouspec");

        var result = SchemaValidator.Validate(demoWorkspace, relativeDocumentPath: "20260823-missing/spec.xml");

        Assert.IsFalse(result.IsValid);
        Assert.IsTrue(result.Diagnostics.Any(d => d.Code == DiagnosticCodes.DocumentNotFound));
    }

    [TestMethod]
    [DataRow("spec.xml")]
    [DataRow("tasks.xml")]
    [DataRow("_schema/spec.xsd")]
    [DataRow("_skill/README.md")]
    [DataRow("arbitrary-folder/spec.xml")]
    [DataRow("20260823-xpath-core/sub/spec.xml")]
    [DataRow("20260823-xpath-core/requests.xml")]
    [DataRow("requests.xml")]
    public void Validate_NonManagedDocumentReference_FailsWithInvalidArgument(string invalidDocPath)
    {
        var demoWorkspace = Path.Combine(RepoRoot, "docs", "demos", "v1-core", ".dogdouspec");

        var result = SchemaValidator.Validate(demoWorkspace, relativeDocumentPath: invalidDocPath);

        Assert.IsFalse(result.IsValid);
        Assert.IsTrue(result.Diagnostics.Any(d => d.Code == DiagnosticCodes.InvalidArgument));
    }

    [TestMethod]
    public void Validate_NonExistentIteration_FailsWithIterationNotFound()
    {
        var demoWorkspace = Path.Combine(RepoRoot, "docs", "demos", "v1-core", ".dogdouspec");

        var result = SchemaValidator.Validate(demoWorkspace, iterationId: "99999999-missing");

        Assert.IsFalse(result.IsValid);
        Assert.IsTrue(result.Diagnostics.Any(d => d.Code == DiagnosticCodes.IterationNotFound));
    }

    [TestMethod]
    public void Validate_EscapingWorkspaceRoot_FailsClosedWithEscapeDetected()
    {
        var outsideDir = Path.Combine(Path.GetTempPath(), "DogdouSpec_OutVal_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outsideDir);

        try
        {
            var dogdouJunction = Path.Combine(_tempDir, ".dogdouspec");

            var psi = new System.Diagnostics.ProcessStartInfo("cmd.exe", $"/c mklink /J \"{dogdouJunction}\" \"{outsideDir}\"")
            {
                CreateNoWindow = true,
                UseShellExecute = false
            };
            using var proc = System.Diagnostics.Process.Start(psi);
            proc?.WaitForExit();

            if (!Directory.Exists(dogdouJunction))
            {
                Assert.Inconclusive("Directory junction creation is not supported or not permitted in this environment.");
            }

            var result = SchemaValidator.Validate(dogdouJunction);

            Assert.IsFalse(result.IsValid, "Escaping workspace root must fail validation");
            Assert.IsTrue(result.Diagnostics.Any(d => d.Code == DiagnosticCodes.PathEscapeDetected));
        }
        finally
        {
            if (Directory.Exists(outsideDir))
            {
                try { Directory.Delete(outsideDir, true); } catch { }
            }
        }
    }
}
