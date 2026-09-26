using DogdouSpec.Cli;
using DogdouSpec.Core.Resources;
using DogdouSpec.Core.Transactions;
using DogdouSpec.Core.Workspace;

namespace DogdouSpec.Cli.Tests;

[TestClass]
public sealed class SchemaDriftCliTests
{
    private string _tempDir = null!;
    private string _workspaceRoot = null!;

    [TestInitialize]
    public void SetUp()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "DogdouSpec_SchemaDriftCli_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        var (success, wsRoot, error) = WorkspaceInitializer.Initialize(_tempDir, _tempDir);
        Assert.IsTrue(success, $"Initialization failed: {error?.Message}");
        _workspaceRoot = wsRoot;
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

    private static (int ExitCode, string Stdout, string Stderr) ExecuteCli(params string[] args)
    {
        var originalOut = Console.Out;
        var originalErr = Console.Error;
        var originalDir = Environment.CurrentDirectory;

        using var outSw = new StringWriter();
        using var errSw = new StringWriter();

        try
        {
            Console.SetOut(outSw);
            Console.SetError(errSw);

            var exitCode = Program.Main(args);
            return (exitCode, outSw.ToString(), errSw.ToString());
        }
        finally
        {
            Console.SetOut(originalOut);
            Console.SetError(originalErr);
            Environment.CurrentDirectory = originalDir;
        }
    }

    [TestMethod]
    public void CliValidate_PristineInitializedWorkspace_ReturnsExitCode0()
    {
        // XML format
        var (xmlExit, xmlOut, xmlErr) = ExecuteCli("validate", "--workspace-root", _workspaceRoot, "--format", "xml");
        Assert.AreEqual(0, xmlExit, $"Validate should succeed on pristine workspace: {xmlErr}");
        Assert.IsTrue(xmlOut.Contains("valid=\"true\"", StringComparison.Ordinal), $"Expected valid=true in stdout: {xmlOut}");
        Assert.IsTrue(xmlOut.Contains("schema=\"passed\"", StringComparison.Ordinal), $"Expected schema=passed in stdout: {xmlOut}");

        // Human format
        var (humanExit, humanOut, humanErr) = ExecuteCli("validate", "--workspace-root", _workspaceRoot, "--format", "human");
        Assert.AreEqual(0, humanExit, $"Validate should succeed on pristine workspace: {humanErr}");
        Assert.IsTrue(humanOut.Contains("Validation passed:", StringComparison.Ordinal), $"Expected 'Validation passed:' in stdout: {humanOut}");
    }

    [TestMethod]
    public void CliValidate_ModifiedSchema_ReturnsExitCode3_AndReportsXmlAndHumanDiagnostics()
    {
        var specSchemaPath = Path.Combine(_workspaceRoot, "_schema", "spec.xsd");
        File.AppendAllText(specSchemaPath, "\n<!-- Drift injected -->\n");

        // XML output format
        var (xmlExit, xmlOut, xmlErr) = ExecuteCli("validate", "--workspace-root", _workspaceRoot, "--format", "xml");
        Assert.AreEqual(3, xmlExit, "Drifted schema validation must return exit code 3.");
        Assert.IsTrue(xmlErr.Contains("code=\"SCHEMA_DRIFT_DETECTED\"", StringComparison.Ordinal), $"Expected SCHEMA_DRIFT_DETECTED in stderr: {xmlErr}");
        Assert.IsTrue(xmlErr.Contains("document=\"_schema/spec.xsd\"", StringComparison.Ordinal), $"Expected document=_schema/spec.xsd in stderr: {xmlErr}");
        Assert.IsTrue(xmlErr.Contains("schemas.v1.spec.xsd", StringComparison.Ordinal), $"Expected embedded resource explanation in stderr: {xmlErr}");

        // Human output format
        var (humanExit, humanOut, humanErr) = ExecuteCli("validate", "--workspace-root", _workspaceRoot, "--format", "human");
        Assert.AreEqual(3, humanExit, "Drifted schema validation must return exit code 3.");
        Assert.IsTrue(humanErr.Contains("SCHEMA_DRIFT_DETECTED", StringComparison.Ordinal), $"Expected SCHEMA_DRIFT_DETECTED in stderr: {humanErr}");
        Assert.IsTrue(humanErr.Contains("_schema/spec.xsd", StringComparison.Ordinal), $"Expected _schema/spec.xsd in stderr: {humanErr}");
    }

    [TestMethod]
    public void CliValidate_ScopedIterationValidation_DetectsDriftAndReturnsExitCode3()
    {
        var iterId = "20260905-scoped-cli-drift";
        var (createExit, _, createError) = ExecuteCli(
            "iteration", "create", "--id", iterId, "--kind", "feature",
            "--criterion", "Detect schema drift in valid scoped workspaces.",
            "--workspace-root", _workspaceRoot, "--format", "xml");
        Assert.AreEqual(0, createExit, createError);
        var (cleanExit, _, cleanError) = ExecuteCli(
            "validate", "--iteration", iterId, "--workspace-root", _workspaceRoot, "--format", "xml");
        Assert.AreEqual(0, cleanExit, cleanError);
        var tasksSchemaPath = Path.Combine(_workspaceRoot, "_schema", "tasks.xsd");
        File.AppendAllText(tasksSchemaPath, "\n<!-- Drift in tasks.xsd -->\n");

        var (exitCode, stdout, stderr) = ExecuteCli(
            "validate",
            "--workspace-root", _workspaceRoot,
            "--iteration", iterId,
            "--format", "human");

        Assert.AreEqual(3, exitCode, $"Scoped iteration validate should fail with exit code 3: {stderr}");
        Assert.IsTrue(stderr.Contains("SCHEMA_DRIFT_DETECTED", StringComparison.Ordinal), $"Expected SCHEMA_DRIFT_DETECTED in stderr: {stderr}");
        Assert.IsTrue(stderr.Contains("_schema/tasks.xsd", StringComparison.Ordinal), $"Expected _schema/tasks.xsd in stderr: {stderr}");
    }

    [TestMethod]
    public void CliValidate_ScopedDocumentValidation_DetectsDriftAndReturnsExitCode3()
    {
        var knowledgeSchemaPath = Path.Combine(_workspaceRoot, "_schema", "knowledge.xsd");
        File.AppendAllText(knowledgeSchemaPath, "\n<!-- Drift in knowledge.xsd -->\n");

        var (exitCode, stdout, stderr) = ExecuteCli(
            "validate",
            "--workspace-root", _workspaceRoot,
            "--document", "backlog.xml",
            "--format", "xml");

        Assert.AreEqual(3, exitCode, $"Scoped document validate should fail with exit code 3: {stderr}");
        Assert.IsTrue(stderr.Contains("code=\"SCHEMA_DRIFT_DETECTED\"", StringComparison.Ordinal), $"Expected SCHEMA_DRIFT_DETECTED in stderr: {stderr}");
        Assert.IsTrue(stderr.Contains("document=\"_schema/knowledge.xsd\"", StringComparison.Ordinal), $"Expected document=_schema/knowledge.xsd in stderr: {stderr}");
    }

    [TestMethod]
    public void CliValidate_AbsentSchemaDirectory_SucceedsWithExitCode0()
    {
        var schemaDir = Path.Combine(_workspaceRoot, "_schema");
        if (Directory.Exists(schemaDir))
        {
            Directory.Delete(schemaDir, recursive: true);
        }

        var (exitCode, stdout, stderr) = ExecuteCli(
            "validate",
            "--workspace-root", _workspaceRoot,
            "--format", "human");

        Assert.AreEqual(0, exitCode, $"Validate should succeed when _schema is absent: {stderr}");
        Assert.IsTrue(stdout.Contains("Validation passed:", StringComparison.Ordinal));
    }

    [TestMethod]
    public void CliValidate_MalformedLocalXsd_ReturnsExitCode3WithoutCrashing()
    {
        var commonSchemaPath = Path.Combine(_workspaceRoot, "_schema", "common.xsd");
        File.WriteAllText(commonSchemaPath, "<!DOCTYPE schema [ <!ENTITY xxe SYSTEM \"file:///c:/windows/win.ini\"> ]><bad-xml");

        var (exitCode, stdout, stderr) = ExecuteCli(
            "validate",
            "--workspace-root", _workspaceRoot,
            "--format", "xml");

        Assert.AreEqual(3, exitCode, $"Malformed local XSD must return exit code 3: {stderr}");
        Assert.IsTrue(stderr.Contains("code=\"SCHEMA_DRIFT_DETECTED\"", StringComparison.Ordinal), $"Expected SCHEMA_DRIFT_DETECTED in stderr: {stderr}");
        Assert.IsTrue(stderr.Contains("document=\"_schema/common.xsd\"", StringComparison.Ordinal), $"Expected document=_schema/common.xsd in stderr: {stderr}");
    }

    [TestMethod]
    public void SchemaStatus_PristineWorkspace_ReportsAllCopiesMatching()
    {
        var (exitCode, stdout, stderr) = ExecuteCli(
            "schema", "status", "--workspace-root", _workspaceRoot, "--format", "xml");

        Assert.AreEqual(0, exitCode, stderr);
        Assert.IsTrue(stdout.Contains("<schema-status", StringComparison.Ordinal));
        Assert.IsTrue(stdout.Contains("in_sync=\"true\"", StringComparison.Ordinal));
        Assert.IsTrue(stdout.Contains($"matching=\"{EmbeddedResources.SchemaNames.Count}\"", StringComparison.Ordinal));
    }

    [TestMethod]
    public void SchemaStatus_DriftAndMissingCopy_ReportsFactsWithoutMutation()
    {
        var modifiedPath = Path.Combine(_workspaceRoot, "_schema", "spec.xsd");
        var missingPath = Path.Combine(_workspaceRoot, "_schema", "tasks.xsd");
        File.WriteAllText(modifiedPath, "repository drift");
        File.Delete(missingPath);

        var (exitCode, stdout, stderr) = ExecuteCli(
            "schema", "status", "--workspace-root", _workspaceRoot, "--format", "xml");

        Assert.AreEqual(1, exitCode, stderr);
        Assert.IsTrue(stdout.Contains("path=\"_schema/spec.xsd\" state=\"modified\"", StringComparison.Ordinal));
        Assert.IsTrue(stdout.Contains("path=\"_schema/tasks.xsd\" state=\"missing\"", StringComparison.Ordinal));
        Assert.AreEqual("repository drift", File.ReadAllText(modifiedPath));
        Assert.IsFalse(File.Exists(missingPath));
    }

    [TestMethod]
    public void SchemaSync_DriftAndMissingCopy_RepairsAtomicallyAndIsIdempotent()
    {
        var modifiedPath = Path.Combine(_workspaceRoot, "_schema", "spec.xsd");
        var missingPath = Path.Combine(_workspaceRoot, "_schema", "tasks.xsd");
        var repositoryCopyPath = Path.Combine(_workspaceRoot, "_schema", "repository-extension.xsd");
        File.WriteAllText(modifiedPath, "repository drift");
        File.Delete(missingPath);
        File.WriteAllText(repositoryCopyPath, "repository-owned schema extension");

        var (firstExit, firstOut, firstErr) = ExecuteCli(
            "schema", "sync", "--expected-version", "1.0",
            "--workspace-root", _workspaceRoot, "--format", "xml");

        Assert.AreEqual(0, firstExit, firstErr);
        Assert.IsTrue(firstOut.Contains("changed=\"2\"", StringComparison.Ordinal));
        CollectionAssert.AreEqual(EmbeddedResources.GetSchemaBytes("spec", "1.0")!, File.ReadAllBytes(modifiedPath));
        CollectionAssert.AreEqual(EmbeddedResources.GetSchemaBytes("tasks", "1.0")!, File.ReadAllBytes(missingPath));
        Assert.AreEqual("repository-owned schema extension", File.ReadAllText(repositoryCopyPath));
        var (validateExit, _, validateErr) = ExecuteCli("validate", "--workspace-root", _workspaceRoot, "--format", "xml");
        Assert.AreEqual(0, validateExit, validateErr);

        var (secondExit, secondOut, secondErr) = ExecuteCli(
            "schema", "sync", "--expected-version", "1.0",
            "--workspace-root", _workspaceRoot, "--format", "xml");
        Assert.AreEqual(0, secondExit, secondErr);
        Assert.IsTrue(secondOut.Contains("changed=\"0\"", StringComparison.Ordinal));
    }

    [TestMethod]
    public void SchemaSync_UnsupportedExpectedVersion_RefusesWithoutMutation()
    {
        var schemaPath = Path.Combine(_workspaceRoot, "_schema", "spec.xsd");
        var before = File.ReadAllBytes(schemaPath);

        var (exitCode, _, stderr) = ExecuteCli(
            "schema", "sync", "--expected-version", "9.9",
            "--workspace-root", _workspaceRoot, "--format", "xml");

        Assert.AreEqual(2, exitCode);
        Assert.IsTrue(stderr.Contains("UNSUPPORTED_VERSION", StringComparison.Ordinal));
        CollectionAssert.AreEqual(before, File.ReadAllBytes(schemaPath));
    }

    [TestMethod]
    public void SchemaSync_ManagedDocumentVersionMismatch_RefusesWithoutMutation()
    {
        var schemaPath = Path.Combine(_workspaceRoot, "_schema", "spec.xsd");
        File.WriteAllText(schemaPath, "repository drift");
        var backlogPath = Path.Combine(_workspaceRoot, "backlog.xml");
        File.WriteAllText(backlogPath, File.ReadAllText(backlogPath).Replace("schema_version=\"1.0\"", "schema_version=\"0.9\"", StringComparison.Ordinal));

        var (exitCode, _, stderr) = ExecuteCli(
            "schema", "sync", "--expected-version", "1.0",
            "--workspace-root", _workspaceRoot, "--format", "xml");

        Assert.AreEqual(2, exitCode);
        Assert.IsTrue(stderr.Contains("UNSUPPORTED_VERSION", StringComparison.Ordinal));
        Assert.IsTrue(stderr.Contains("backlog.xml", StringComparison.Ordinal));
        Assert.AreEqual("repository drift", File.ReadAllText(schemaPath));
    }

    [TestMethod]
    public void SchemaSync_ConcurrentWriterLock_ReturnsConflictWithoutMutation()
    {
        var schemaPath = Path.Combine(_workspaceRoot, "_schema", "spec.xsd");
        File.WriteAllText(schemaPath, "repository drift");
        var (acquired, workspaceLock, error) = WorkspaceLock.Acquire(_workspaceRoot);
        Assert.IsTrue(acquired, error?.Message);
        Assert.IsNotNull(workspaceLock);

        using (workspaceLock)
        {
            var (exitCode, _, stderr) = ExecuteCli(
                "schema", "sync", "--expected-version", "1.0",
                "--workspace-root", _workspaceRoot, "--format", "xml");
            Assert.AreEqual(4, exitCode);
            Assert.IsTrue(stderr.Contains("LOCK_CONFLICT", StringComparison.Ordinal));
        }

        Assert.AreEqual("repository drift", File.ReadAllText(schemaPath));
    }
}
