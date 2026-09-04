using DogdouSpec.Core.Diagnostics;
using DogdouSpec.Core.Resources;
using DogdouSpec.Core.Validation;
using DogdouSpec.Core.Workspace;

namespace DogdouSpec.Core.Tests;

[TestClass]
public sealed class SchemaDriftTests
{
    private string _tempDir = null!;
    private static string RepoRoot = null!;
    private static readonly string[] ScopedCriteria = { "Detect schema drift in valid scoped workspaces." };

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
        _tempDir = Path.Combine(Path.GetTempPath(), "DogdouSpec_SchemaDriftTests_" + Guid.NewGuid().ToString("N"));
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

    private string CreateInitializedWorkspace()
    {
        var (success, wsRoot, error) = WorkspaceInitializer.Initialize(null, _tempDir);
        Assert.IsTrue(success, $"Initialization failed: {error?.Message}");
        return wsRoot;
    }

    [TestMethod]
    public void Validate_PristineInitializedWorkspace_SucceedsWithoutDrift()
    {
        var wsRoot = CreateInitializedWorkspace();

        var result = SchemaValidator.Validate(wsRoot);

        Assert.IsTrue(result.IsValid, $"Validation should pass for pristine workspace: {string.Join("; ", result.Diagnostics.Select(d => d.Message))}");
        Assert.AreEqual(0, result.Diagnostics.Count(d => d.Severity == "error"));
        Assert.AreEqual(2, result.CheckedDocumentsCount);
    }

    [TestMethod]
    public void Validate_ModifiedSchemaCopy_ReportsSchemaDriftDiagnostic()
    {
        var wsRoot = CreateInitializedWorkspace();
        var specSchemaPath = Path.Combine(wsRoot, "_schema", "spec.xsd");
        Assert.IsTrue(File.Exists(specSchemaPath));

        // Modify readable copy of spec.xsd
        File.AppendAllText(specSchemaPath, "\n<!-- Modified schema copy to simulate drift -->\n");

        var result = SchemaValidator.Validate(wsRoot);

        Assert.IsFalse(result.IsValid, "Validation must fail when schema copy has drifted.");
        var driftDiag = result.Diagnostics.FirstOrDefault(d => d.Code == WorkspaceSchemaDriftDetector.SchemaDriftDetected);
        Assert.IsNotNull(driftDiag, "Must report SCHEMA_DRIFT_DETECTED diagnostic.");
        Assert.AreEqual("_schema/spec.xsd", driftDiag.Document);
        Assert.IsTrue(driftDiag.Message.Contains("_schema/spec.xsd", StringComparison.Ordinal), "Message must explain target path.");
        Assert.IsTrue(driftDiag.Message.Contains("schemas.v1.spec.xsd", StringComparison.Ordinal), "Message must explain embedded authoritative source.");
    }

    [TestMethod]
    public void Validate_MultipleDriftedSchemas_ReportsAllDriftedSchemas()
    {
        var wsRoot = CreateInitializedWorkspace();
        var tasksSchemaPath = Path.Combine(wsRoot, "_schema", "tasks.xsd");
        var knowledgeSchemaPath = Path.Combine(wsRoot, "_schema", "knowledge.xsd");

        File.WriteAllText(tasksSchemaPath, "<!-- drift tasks -->");
        File.WriteAllText(knowledgeSchemaPath, "<!-- drift knowledge -->");

        var result = SchemaValidator.Validate(wsRoot);

        Assert.IsFalse(result.IsValid);
        var driftDiags = result.Diagnostics.Where(d => d.Code == WorkspaceSchemaDriftDetector.SchemaDriftDetected).ToList();
        Assert.AreEqual(2, driftDiags.Count, "Must report drift for each drifted schema.");
        Assert.IsTrue(driftDiags.Any(d => d.Document == "_schema/tasks.xsd"));
        Assert.IsTrue(driftDiags.Any(d => d.Document == "_schema/knowledge.xsd"));
    }

    [TestMethod]
    public void Validate_MalformedOrMaliciousLocalXsd_DoesNotCrashAndReportsDriftWithoutParsing()
    {
        var wsRoot = CreateInitializedWorkspace();
        var commonSchemaPath = Path.Combine(wsRoot, "_schema", "common.xsd");

        // Hostile / malformed content with entity injection attempt and broken tags
        var hostileContent = "<!DOCTYPE schema [ <!ENTITY xxe SYSTEM \"file:///c:/windows/win.ini\"> ]><xs:schema xmlns:xs=\"http://www.w3.org/2001/XMLSchema\">&xxe;<broken-syntax>";
        File.WriteAllText(commonSchemaPath, hostileContent);

        var result = SchemaValidator.Validate(wsRoot);

        Assert.IsFalse(result.IsValid);
        var driftDiag = result.Diagnostics.FirstOrDefault(d => d.Code == WorkspaceSchemaDriftDetector.SchemaDriftDetected);
        Assert.IsNotNull(driftDiag, "Must report drift on malformed local XSD without crashing.");
        Assert.AreEqual("_schema/common.xsd", driftDiag.Document);
    }

    [TestMethod]
    public void Validate_EntirelyAbsentSchemaDirectory_PassesValidation()
    {
        var wsRoot = CreateInitializedWorkspace();
        var schemaDir = Path.Combine(wsRoot, "_schema");
        if (Directory.Exists(schemaDir))
        {
            Directory.Delete(schemaDir, recursive: true);
        }

        var result = SchemaValidator.Validate(wsRoot);

        Assert.IsTrue(result.IsValid, "Absent _schema directory is an allowed optional copy state (embedded schemas are used).");
        Assert.AreEqual(0, result.Diagnostics.Count(d => d.Severity == "error"));
    }

    [TestMethod]
    public void Validate_PartialSchemaCopiesAbsent_PassesValidationIfPresentMatch()
    {
        var wsRoot = CreateInitializedWorkspace();
        // Remove spec.xsd and tasks.xsd, keep common.xsd, knowledge.xsd, backlog.xsd, requests.xsd
        File.Delete(Path.Combine(wsRoot, "_schema", "spec.xsd"));
        File.Delete(Path.Combine(wsRoot, "_schema", "tasks.xsd"));

        var result = SchemaValidator.Validate(wsRoot);

        Assert.IsTrue(result.IsValid, "Partially absent schema copies are valid if remaining copies match embedded.");
        Assert.AreEqual(0, result.Diagnostics.Count(d => d.Severity == "error"));
    }

    [TestMethod]
    public void Validate_ScopedIterationValidation_DetectsDrift()
    {
        var wsRoot = CreateInitializedWorkspace();

        // Create an iteration directory with valid spec.xml and tasks.xml
        var iterId = "20260905-schema-drift-feature";
        var (created, _, createDiagnostics) = DogdouSpec.Core.Iterations.IterationCreator.Create(
            wsRoot, iterId, "feature", criteria: ScopedCriteria);
        Assert.IsTrue(created, string.Join("; ", createDiagnostics.Select(d => d.Message)));
        Assert.IsTrue(SchemaValidator.Validate(wsRoot, iterationId: iterId).IsValid,
            "The unchanged scoped workspace must be valid before introducing drift.");
        // Mutate tasks.xsd
        var tasksSchemaPath = Path.Combine(wsRoot, "_schema", "tasks.xsd");
        File.AppendAllText(tasksSchemaPath, "\n<!-- Drift -->\n");

        var result = SchemaValidator.Validate(wsRoot, iterationId: iterId);

        Assert.IsFalse(result.IsValid, "Scoped iteration validation must detect schema drift.");
        var driftDiag = result.Diagnostics.FirstOrDefault(d => d.Code == WorkspaceSchemaDriftDetector.SchemaDriftDetected);
        Assert.IsNotNull(driftDiag);
        Assert.AreEqual("_schema/tasks.xsd", driftDiag.Document);
    }

    [TestMethod]
    public void Validate_ScopedDocumentValidation_DetectsDrift()
    {
        var wsRoot = CreateInitializedWorkspace();

        // Mutate backlog.xsd
        var backlogSchemaPath = Path.Combine(wsRoot, "_schema", "backlog.xsd");
        File.AppendAllText(backlogSchemaPath, "\n<!-- Drift in backlog.xsd -->\n");

        var result = SchemaValidator.Validate(wsRoot, relativeDocumentPath: "knowledge.xml");

        Assert.IsFalse(result.IsValid, "Scoped document validation must detect schema drift.");
        var driftDiag = result.Diagnostics.FirstOrDefault(d => d.Code == WorkspaceSchemaDriftDetector.SchemaDriftDetected);
        Assert.IsNotNull(driftDiag);
        Assert.AreEqual("_schema/backlog.xsd", driftDiag.Document);
    }

    [TestMethod]
    public void Validate_DirectoryInsteadOfSchemaFile_FailsClosedWithDiagnostic()
    {
        var wsRoot = CreateInitializedWorkspace();
        var specSchemaPath = Path.Combine(wsRoot, "_schema", "spec.xsd");
        File.Delete(specSchemaPath);
        Directory.CreateDirectory(specSchemaPath);

        var result = SchemaValidator.Validate(wsRoot);

        Assert.IsFalse(result.IsValid, "Directory in place of schema file must fail closed.");
        var unreadableDiag = result.Diagnostics.FirstOrDefault(d => d.Code == WorkspaceSchemaDriftDetector.UnreadableSchemaCopy && d.Document == "_schema/spec.xsd");
        Assert.IsNotNull(unreadableDiag, "Must report UNREADABLE_SCHEMA_COPY diagnostic.");
    }

    [TestMethod]
    public void Validate_OversizedSchemaCopy_FailsClosedWithDiagnostic()
    {
        var wsRoot = CreateInitializedWorkspace();
        var specSchemaPath = Path.Combine(wsRoot, "_schema", "spec.xsd");

        // Write 1.5 MB file
        var oversizedData = new byte[1024 * 1024 + 512 * 1024];
        Array.Fill<byte>(oversizedData, (byte)' ');
        File.WriteAllBytes(specSchemaPath, oversizedData);

        var result = SchemaValidator.Validate(wsRoot);

        Assert.IsFalse(result.IsValid, "Oversized schema file must fail closed.");
        var driftDiag = result.Diagnostics.FirstOrDefault(d => d.Code == WorkspaceSchemaDriftDetector.SchemaDriftDetected && d.Document == "_schema/spec.xsd");
        Assert.IsNotNull(driftDiag, "Must report SCHEMA_DRIFT_DETECTED on oversized file.");
        Assert.IsTrue(driftDiag.Message.Contains("exceeds maximum allowed size", StringComparison.Ordinal));
    }

    [TestMethod]
    public void Validate_IsReadOnly_DoesNotMutateOrRepairFiles()
    {
        var wsRoot = CreateInitializedWorkspace();
        var specSchemaPath = Path.Combine(wsRoot, "_schema", "spec.xsd");
        var customContent = "<!-- Custom schema drift content -->";
        File.WriteAllText(specSchemaPath, customContent);

        var result = SchemaValidator.Validate(wsRoot);

        Assert.IsFalse(result.IsValid);
        // Verify file was NOT overwritten or repaired
        var contentAfter = File.ReadAllText(specSchemaPath);
        Assert.AreEqual(customContent, contentAfter, "Validation must be read-only and must not repair drifted files.");
    }

    [TestMethod]
    public void DirectDetector_UnsupportedVersion_ReturnsError()
    {
        var wsRoot = CreateInitializedWorkspace();
        var diags = WorkspaceSchemaDriftDetector.DetectDrift(wsRoot, version: "99.0");

        Assert.AreEqual(1, diags.Count);
        Assert.AreEqual(DiagnosticCodes.UnsupportedVersion, diags[0].Code);
    }

    [TestMethod]
    public void Validate_SameLengthContentChange_ReportsDrift()
    {
        var wsRoot = CreateInitializedWorkspace();
        var path = Path.Combine(wsRoot, "_schema", "common.xsd");
        var bytes = File.ReadAllBytes(path);
        bytes[0] ^= 1;
        File.WriteAllBytes(path, bytes);

        var result = SchemaValidator.Validate(wsRoot);

        Assert.IsFalse(result.IsValid);
        Assert.IsTrue(result.Diagnostics.Any(d => d.Code == WorkspaceSchemaDriftDetector.SchemaDriftDetected));
        CollectionAssert.AreEqual(bytes, File.ReadAllBytes(path));
    }

    [TestMethod]
    public void Validate_LockedSchemaCopy_ReportsUnreadableInsteadOfAbsence()
    {
        var wsRoot = CreateInitializedWorkspace();
        var path = Path.Combine(wsRoot, "_schema", "spec.xsd");
        using var locked = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        var result = SchemaValidator.Validate(wsRoot);

        Assert.IsFalse(result.IsValid);
        Assert.IsTrue(result.Diagnostics.Any(d => d.Code == WorkspaceSchemaDriftDetector.UnreadableSchemaCopy
            && d.Document == "_schema/spec.xsd"));
    }

    [TestMethod]
    public void Validate_SchemaDirectoryReplacedByFile_ReportsUnreadable()
    {
        var wsRoot = CreateInitializedWorkspace();
        var path = Path.Combine(wsRoot, "_schema");
        Directory.Delete(path, recursive: true);
        File.WriteAllText(path, "not a schema directory");

        var result = SchemaValidator.Validate(wsRoot);

        Assert.IsFalse(result.IsValid);
        Assert.IsTrue(result.Diagnostics.Any(d => d.Code == WorkspaceSchemaDriftDetector.UnreadableSchemaCopy
            && d.Document == "_schema"));
    }

    [TestMethod]
    public void Validate_PermissiveLocalSchema_DoesNotOverrideEmbeddedValidation()
    {
        var wsRoot = CreateInitializedWorkspace();
        File.WriteAllText(Path.Combine(wsRoot, "backlog.xml"), "<backlog />");
        File.WriteAllText(Path.Combine(wsRoot, "_schema", "backlog.xsd"),
            "<xs:schema xmlns:xs=\"http://www.w3.org/2001/XMLSchema\"><xs:element name=\"backlog\" type=\"xs:anyType\" /></xs:schema>");

        var result = SchemaValidator.Validate(wsRoot);

        Assert.IsFalse(result.IsValid);
        Assert.IsTrue(result.Diagnostics.Any(d => d.Code == WorkspaceSchemaDriftDetector.SchemaDriftDetected));
        Assert.IsTrue(result.Diagnostics.Any(d => d.Code == DiagnosticCodes.SchemaValidationError
            && d.Document == "backlog.xml"), "The embedded schema must still reject the invalid document.");
    }
}
