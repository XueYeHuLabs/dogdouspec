using DogdouSpec.Cli;
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
}
