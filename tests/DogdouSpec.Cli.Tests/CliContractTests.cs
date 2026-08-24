using DogdouSpec.Cli;
using DogdouSpec.Core.Diagnostics;

namespace DogdouSpec.Cli.Tests;

[TestClass]
public sealed class CliContractTests
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
        _tempDir = Path.Combine(Path.GetTempPath(), "DogdouSpec_CliTests_" + Guid.NewGuid().ToString("N"));
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

    private static (int ExitCode, string Stdout, string Stderr) RunCli(params string[] args)
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
    public void WorkspaceDiscover_FromDemoSubdirectory_ReturnsXmlRootWithExitCode0()
    {
        var demoSubdir = Path.Combine(RepoRoot, "docs", "demos", "v1-core", ".dogdouspec", "20260823-xpath-core");
        Environment.CurrentDirectory = demoSubdir;

        var (exitCode, stdout, stderr) = RunCli("workspace", "discover", "--format", "xml");

        Assert.AreEqual(0, exitCode, $"Stderr: {stderr}");
        Assert.IsTrue(stdout.Contains("<workspace", StringComparison.Ordinal));
        Assert.IsTrue(stdout.Contains(".dogdouspec", StringComparison.Ordinal));
    }

    [TestMethod]
    public void WorkspaceDiscover_HumanFormat_ReturnsHumanOutputWithExitCode0()
    {
        var demoSubdir = Path.Combine(RepoRoot, "docs", "demos", "v1-core", ".dogdouspec", "20260823-xpath-core");
        Environment.CurrentDirectory = demoSubdir;

        var (exitCode, stdout, stderr) = RunCli("workspace", "discover", "--format", "human");

        Assert.AreEqual(0, exitCode, $"Stderr: {stderr}");
        Assert.IsTrue(stdout.Contains("Workspace root:", StringComparison.Ordinal));
    }

    [TestMethod]
    public void WorkspaceDiscover_NoWorkspaceFound_ReturnsDiagnosticsWithExitCode2()
    {
        Environment.CurrentDirectory = _tempDir;

        var (exitCode, stdout, stderr) = RunCli("workspace", "discover", "--format", "xml");

        Assert.AreEqual(2, exitCode);
        Assert.IsTrue(stderr.Contains("<diagnostics command=\"workspace discover\"", StringComparison.Ordinal));
        Assert.IsTrue(stderr.Contains(DiagnosticCodes.WorkspaceNotFound, StringComparison.Ordinal));
    }

    [TestMethod]
    public void WorkspaceInit_HappyPath_CreatesWorkspaceWithExitCode0()
    {
        Environment.CurrentDirectory = _tempDir;

        var (exitCode, stdout, stderr) = RunCli("workspace", "init", "--format", "xml");

        Assert.AreEqual(0, exitCode, $"Stderr: {stderr}");
        Assert.IsTrue(stdout.Contains("<workspace initialized=\"true\"", StringComparison.Ordinal));
        Assert.IsTrue(Directory.Exists(Path.Combine(_tempDir, ".dogdouspec")));
        Assert.IsTrue(File.Exists(Path.Combine(_tempDir, ".dogdouspec", "knowledge.xml")));
        Assert.IsTrue(File.Exists(Path.Combine(_tempDir, ".dogdouspec", "backlog.xml")));
    }

    [TestMethod]
    public void WorkspaceInit_Collision_FailsWithExitCode2()
    {
        Environment.CurrentDirectory = _tempDir;

        // Init once
        var (firstCode, _, _) = RunCli("workspace", "init", "--format", "xml");
        Assert.AreEqual(0, firstCode);

        // Init second time
        var (secondCode, _, secondErr) = RunCli("workspace", "init", "--format", "xml");

        Assert.AreEqual(2, secondCode);
        Assert.IsTrue(secondErr.Contains("<diagnostics command=\"workspace init\"", StringComparison.Ordinal));
        Assert.IsTrue(secondErr.Contains(DiagnosticCodes.ManagedStateExists, StringComparison.Ordinal));
    }

    [TestMethod]
    [DataRow("spec")]
    [DataRow("tasks")]
    [DataRow("knowledge")]
    [DataRow("backlog")]
    [DataRow("requests")]
    [DataRow("common")]
    public void SchemaShow_ValidSchema_WritesExactBytesWithExitCode0(string schemaName)
    {
        var (exitCode, stdout, stderr) = RunCli("schema", "show", "--name", schemaName, "--version", "1.0");

        Assert.AreEqual(0, exitCode, $"Stderr: {stderr}");
        Assert.IsTrue(stdout.Contains("<xs:schema", StringComparison.Ordinal));
    }

    [TestMethod]
    public void SchemaShow_UnknownSchema_ReturnsExitCode2()
    {
        var (exitCode, _, stderr) = RunCli("schema", "show", "--name", "non_existent_schema");

        Assert.AreEqual(2, exitCode);
        Assert.IsTrue(stderr.Contains(DiagnosticCodes.ResourceNotFound, StringComparison.Ordinal));
    }

    [TestMethod]
    [DataRow("backlog.item")]
    [DataRow("iteration.confirmation")]
    [DataRow("knowledge.entry")]
    [DataRow("record.discussion")]
    [DataRow("record.finding")]
    [DataRow("record.verification")]
    [DataRow("task.update")]
    public void TemplateShow_ValidTemplate_WritesExactBytesWithExitCode0(string templateName)
    {
        var (exitCode, stdout, stderr) = RunCli("template", "show", "--name", templateName, "--version", "1.0");

        Assert.AreEqual(0, exitCode, $"Stderr: {stderr}");
        Assert.IsTrue(stdout.Contains("<?xml", StringComparison.Ordinal));
    }

    [TestMethod]
    public void TemplateShow_UnknownTemplate_ReturnsExitCode2()
    {
        var (exitCode, _, stderr) = RunCli("template", "show", "--name", "non_existent_template");

        Assert.AreEqual(2, exitCode);
        Assert.IsTrue(stderr.Contains(DiagnosticCodes.ResourceNotFound, StringComparison.Ordinal));
    }

    [TestMethod]
    public void Validate_DemoWorkspace_ReturnsSuccessWithExitCode0()
    {
        var demoWorkspace = Path.Combine(RepoRoot, "docs", "demos", "v1-core", ".dogdouspec");

        var (exitCode, stdout, stderr) = RunCli("validate", "--workspace-root", demoWorkspace, "--format", "xml");

        Assert.AreEqual(0, exitCode, $"Stderr: {stderr}");
        Assert.IsTrue(stdout.Contains("<validation valid=\"true\" scope=\"workspace\" schema=\"passed\" semantic=\"passed\" checked_documents=\"4\"", StringComparison.Ordinal));
    }

    [TestMethod]
    public void Validate_DemoWorkspaceIterationScope_ReturnsSuccessWithExitCode0()
    {
        var demoWorkspace = Path.Combine(RepoRoot, "docs", "demos", "v1-core", ".dogdouspec");

        var (exitCode, stdout, stderr) = RunCli("validate", "--workspace-root", demoWorkspace, "--iteration", "20260823-xpath-core", "--format", "xml");

        Assert.AreEqual(0, exitCode, $"Stderr: {stderr}");
        Assert.IsTrue(stdout.Contains("<validation valid=\"true\" scope=\"iteration\" iteration=\"20260823-xpath-core\" schema=\"passed\" semantic=\"passed\" checked_documents=\"2\"", StringComparison.Ordinal));
    }

    [TestMethod]
    public void Validate_DemoWorkspaceDocumentScope_ReturnsSuccessWithExitCode0()
    {
        var demoWorkspace = Path.Combine(RepoRoot, "docs", "demos", "v1-core", ".dogdouspec");

        var (exitCode, stdout, stderr) = RunCli("validate", "--workspace-root", demoWorkspace, "--document", "knowledge.xml", "--format", "xml");

        Assert.AreEqual(0, exitCode, $"Stderr: {stderr}");
        Assert.IsTrue(stdout.Contains("<validation valid=\"true\" scope=\"document\" document=\"knowledge.xml\" schema=\"passed\" semantic=\"passed\" checked_documents=\"1\"", StringComparison.Ordinal));
    }

    [TestMethod]
    public void Validate_InvalidXmlDocument_ReturnsExitCode3AndDiagnostics()
    {
        // Initialize a workspace in temp dir
        var (initCode, _, _) = RunCli("workspace", "init", "--workspace-root", _tempDir, "--format", "xml");
        Assert.AreEqual(0, initCode);

        // Corrupt knowledge.xml with invalid XML schema element
        var knowledgePath = Path.Combine(_tempDir, ".dogdouspec", "knowledge.xml");
        File.WriteAllText(knowledgePath, """
<?xml version="1.0" encoding="utf-8"?>
<knowledge id="20260823-knowledge" schema_version="1.0" revision="1">
  <invalid-node>Bad</invalid-node>
</knowledge>
""");

        var (exitCode, stdout, stderr) = RunCli("validate", "--workspace-root", _tempDir, "--format", "xml");

        Assert.AreEqual(3, exitCode, "Validation failure must return exit code 3");
        Assert.IsTrue(stderr.Contains("<diagnostics command=\"validate\"", StringComparison.Ordinal));
        Assert.IsTrue(stderr.Contains(DiagnosticCodes.SchemaValidationError, StringComparison.Ordinal));
    }

    [TestMethod]
    public void Validate_DtdXmlDocument_ReturnsExitCode3AndDtdDiagnostic()
    {
        var (initCode, _, _) = RunCli("workspace", "init", "--workspace-root", _tempDir, "--format", "xml");
        Assert.AreEqual(0, initCode);

        var knowledgePath = Path.Combine(_tempDir, ".dogdouspec", "knowledge.xml");
        File.WriteAllText(knowledgePath, """
<?xml version="1.0" encoding="utf-8"?>
<!DOCTYPE knowledge [ <!ENTITY test "test"> ]>
<knowledge id="20260823-knowledge" schema_version="1.0" revision="1">
  <index><summary>Test</summary></index>
</knowledge>
""");

        var (exitCode, stdout, stderr) = RunCli("validate", "--workspace-root", _tempDir, "--format", "xml");

        Assert.AreEqual(3, exitCode, "DTD failure must return exit code 3");
        Assert.IsTrue(stderr.Contains("<diagnostics command=\"validate\"", StringComparison.Ordinal));
        Assert.IsTrue(stderr.Contains(DiagnosticCodes.DtdProhibited, StringComparison.Ordinal));
    }

    [TestMethod]
    public void Validate_IterationAndDocumentMutuallyExclusive_ReturnsExitCode2()
    {
        var demoWorkspace = Path.Combine(RepoRoot, "docs", "demos", "v1-core", ".dogdouspec");

        var (exitCode, stdout, stderr) = RunCli(
            "validate",
            "--workspace-root", demoWorkspace,
            "--iteration", "20260823-xpath-core",
            "--document", "knowledge.xml",
            "--format", "xml");

        Assert.AreEqual(2, exitCode, "Mutually exclusive options must return exit code 2");
        Assert.IsTrue(stderr.Contains("<diagnostics command=\"validate\"", StringComparison.Ordinal));
        Assert.IsTrue(stderr.Contains(DiagnosticCodes.InvalidArgument, StringComparison.Ordinal));
        Assert.IsTrue(string.IsNullOrEmpty(stdout), "No stdout output on argument error");
    }

    [TestMethod]
    public void Cli_ParseError_ReturnsExitCode2AndStructuredDiagnostics()
    {
        var (exitCode, stdout, stderr) = RunCli("validate", "--invalid-option-name", "--format", "xml");

        Assert.AreEqual(2, exitCode, "Parse errors must return exit code 2");
        Assert.IsTrue(stderr.Contains("<diagnostics command=\"dogdouspec\"", StringComparison.Ordinal));
        Assert.IsTrue(stderr.Contains(DiagnosticCodes.InvalidArgument, StringComparison.Ordinal));
        Assert.IsTrue(string.IsNullOrEmpty(stdout), "No stdout output on parse error");
    }

    [TestMethod]
    public void Cli_SchemaShowMissingName_ReturnsExitCode2()
    {
        var (exitCode, stdout, stderr) = RunCli("schema", "show");

        Assert.AreEqual(2, exitCode, "Missing required option must return exit code 2");
        Assert.IsTrue(stderr.Contains(DiagnosticCodes.InvalidArgument, StringComparison.Ordinal));
        Assert.IsTrue(string.IsNullOrEmpty(stdout));
    }

    [TestMethod]
    public void Validate_IterationWithTraversal_ReturnsExitCode2()
    {
        var demoWorkspace = Path.Combine(RepoRoot, "docs", "demos", "v1-core", ".dogdouspec");

        var (exitCode, stdout, stderr) = RunCli(
            "validate",
            "--workspace-root", demoWorkspace,
            "--iteration", "../20260823-xpath-core",
            "--format", "xml");

        Assert.AreEqual(2, exitCode, "Iteration traversal must return exit code 2");
        Assert.IsTrue(
            stderr.Contains(DiagnosticCodes.PathTraversalDetected, StringComparison.Ordinal) ||
            stderr.Contains(DiagnosticCodes.PathEscapeDetected, StringComparison.Ordinal) ||
            stderr.Contains(DiagnosticCodes.InvalidArgument, StringComparison.Ordinal));
    }

    [TestMethod]
    public void Validate_IterationWithInvalidCasing_ReturnsExitCode2()
    {
        var demoWorkspace = Path.Combine(RepoRoot, "docs", "demos", "v1-core", ".dogdouspec");

        var (exitCode, stdout, stderr) = RunCli(
            "validate",
            "--workspace-root", demoWorkspace,
            "--iteration", "20260823-XPath-Core",
            "--format", "xml");

        Assert.AreEqual(2, exitCode, "Invalid casing in iteration ID must return exit code 2");
        Assert.IsTrue(stderr.Contains(DiagnosticCodes.InvalidArgument, StringComparison.Ordinal));
    }

    [TestMethod]
    public void Validate_TasksXmlWithIterationRoot_ReturnsExitCode3()
    {
        var (initCode, _, _) = RunCli("workspace", "init", "--workspace-root", _tempDir, "--format", "xml");
        Assert.AreEqual(0, initCode);

        // Put spec.xml content in tasks.xml
        var specXmlContent = File.ReadAllText(Path.Combine(RepoRoot, "docs", "demos", "v1-core", ".dogdouspec", "20260823-xpath-core", "spec.xml"));
        var iterDir = Path.Combine(_tempDir, ".dogdouspec", "20260823-test");
        Directory.CreateDirectory(iterDir);
        File.WriteAllText(Path.Combine(iterDir, "spec.xml"), specXmlContent);
        File.WriteAllText(Path.Combine(iterDir, "tasks.xml"), specXmlContent); // Intentionally bad root

        var (exitCode, stdout, stderr) = RunCli("validate", "--workspace-root", _tempDir, "--iteration", "20260823-test", "--format", "xml");

        Assert.AreEqual(3, exitCode, "Mismatched document root must fail schema validation with exit code 3");
        Assert.IsTrue(stderr.Contains(DiagnosticCodes.SchemaValidationError, StringComparison.Ordinal));
    }

    [TestMethod]
    public void Validate_MissingRequiredRootDocument_ReturnsExitCode3()
    {
        var (initCode, _, _) = RunCli("workspace", "init", "--workspace-root", _tempDir, "--format", "xml");
        Assert.AreEqual(0, initCode);

        // Delete knowledge.xml
        File.Delete(Path.Combine(_tempDir, ".dogdouspec", "knowledge.xml"));

        var (exitCode, stdout, stderr) = RunCli("validate", "--workspace-root", _tempDir, "--format", "xml");

        Assert.AreEqual(3, exitCode, "Missing required root document must fail validation with exit code 3");
        Assert.IsTrue(stderr.Contains(DiagnosticCodes.DocumentNotFound, StringComparison.Ordinal));
    }

    [TestMethod]
    [DataRow("spec.xml")]
    [DataRow("tasks.xml")]
    [DataRow("_schema/spec.xsd")]
    [DataRow("_skill/README.md")]
    [DataRow("20260823-xpath-core/sub/spec.xml")]
    [DataRow("20260823-xpath-core/requests.xml")]
    [DataRow("requests.xml")]
    public void Validate_NonManagedDocumentReference_ReturnsExitCode2(string nonManagedDoc)
    {
        var demoWorkspace = Path.Combine(RepoRoot, "docs", "demos", "v1-core", ".dogdouspec");

        var (exitCode, stdout, stderr) = RunCli(
            "validate",
            "--workspace-root", demoWorkspace,
            "--document", nonManagedDoc,
            "--format", "xml");

        Assert.AreEqual(2, exitCode, $"Non-managed document '{nonManagedDoc}' must return exit code 2");
        Assert.IsTrue(stderr.Contains("<diagnostics command=\"validate\"", StringComparison.Ordinal));
        Assert.IsTrue(stderr.Contains(DiagnosticCodes.InvalidArgument, StringComparison.Ordinal));
    }

    [TestMethod]
    public void Validate_EscapingWorkspaceRoot_ReturnsExitCode2WithEscapeDetected()
    {
        var outsideDir = Path.Combine(Path.GetTempPath(), "DogdouSpec_CliOut_" + Guid.NewGuid().ToString("N"));
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

            var (exitCode, stdout, stderr) = RunCli(
                "validate",
                "--workspace-root", dogdouJunction,
                "--format", "xml");

            Assert.AreEqual(2, exitCode, "Escaping workspace root must return exit code 2");
            Assert.IsTrue(stderr.Contains("<diagnostics command=\"validate\"", StringComparison.Ordinal));
            Assert.IsTrue(stderr.Contains(DiagnosticCodes.PathEscapeDetected, StringComparison.Ordinal));
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
