using DogdouSpec.Cli;
using DogdouSpec.Core.Diagnostics;

namespace DogdouSpec.Cli.Tests;

[TestClass]
public sealed class AppendCliTests
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
        _tempDir = Path.Combine(Path.GetTempPath(), "DogdouSpec_AppendCliTests_" + Guid.NewGuid().ToString("N"));
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

    private static (int ExitCode, string Stdout, string Stderr) RunCli(params string[] args)
    {
        return RunCliWithStdin(null, args);
    }

    private static (int ExitCode, string Stdout, string Stderr) RunCliWithStdin(string? stdinInput, params string[] args)
    {
        var originalOut = Console.Out;
        var originalErr = Console.Error;
        var originalIn = Console.In;
        var originalDir = Environment.CurrentDirectory;

        using var outSw = new StringWriter();
        using var errSw = new StringWriter();
        using var inSr = new StringReader(stdinInput ?? string.Empty);

        try
        {
            Console.SetOut(outSw);
            Console.SetError(errSw);
            Console.SetIn(inSr);

            var exitCode = Program.Main(args);
            return (exitCode, outSw.ToString(), errSw.ToString());
        }
        finally
        {
            Console.SetOut(originalOut);
            Console.SetError(originalErr);
            Console.SetIn(originalIn);
            Environment.CurrentDirectory = originalDir;
        }
    }

    [TestMethod]
    public void Append_HelpOutput_ExplicitlyStatesCommandIsMutating()
    {
        var (exitCode, stdout, stderr) = RunCli("append", "--help");

        Assert.AreEqual(0, exitCode, $"Stderr: {stderr}");
        Assert.IsTrue(stdout.Contains("mutating", StringComparison.OrdinalIgnoreCase), "Help text must indicate command is mutating");
    }

    [TestMethod]
    public void Append_MutuallyExclusive_StdinAndFile_ReturnsExitCode2()
    {
        var workspace = CreateWorkspaceCopy();
        var tempFile = Path.Combine(_tempDir, "fragment.xml");
        File.WriteAllText(tempFile, "<record id=\"20260823T041500Z-test\"/>");

        var (exitCode, stdout, stderr) = RunCli(
            "append",
            "--workspace-root", workspace,
            "--document", "20260823-xpath-core/tasks.xml",
            "--parent-xpath", "//task[@id='20260823-task-xpath-projection']/records",
            "--expected-revision", "9",
            "--stdin",
            "--file", tempFile,
            "--format", "xml");

        Assert.AreEqual(2, exitCode);
        Assert.IsTrue(stderr.Contains("<diagnostics command=\"append\"", StringComparison.Ordinal));
        Assert.IsTrue(stderr.Contains(DiagnosticCodes.InvalidArgument, StringComparison.Ordinal));
    }

    [TestMethod]
    public void Append_NeitherStdinNorFile_ReturnsExitCode2()
    {
        var workspace = CreateWorkspaceCopy();

        var (exitCode, stdout, stderr) = RunCli(
            "append",
            "--workspace-root", workspace,
            "--document", "20260823-xpath-core/tasks.xml",
            "--parent-xpath", "//task[@id='20260823-task-xpath-projection']/records",
            "--expected-revision", "9",
            "--format", "xml");

        Assert.AreEqual(2, exitCode);
        Assert.IsTrue(stderr.Contains("<diagnostics command=\"append\"", StringComparison.Ordinal));
        Assert.IsTrue(stderr.Contains(DiagnosticCodes.InvalidArgument, StringComparison.Ordinal));
    }

    [TestMethod]
    public void Append_MissingDocument_ReturnsExitCode2()
    {
        var workspace = CreateWorkspaceCopy();

        var (exitCode, stdout, stderr) = RunCliWithStdin(
            "<record id=\"20260823T041500Z-test\"/>",
            "append",
            "--workspace-root", workspace,
            "--parent-xpath", "//task[@id='20260823-task-xpath-projection']/records",
            "--expected-revision", "9",
            "--stdin");

        Assert.AreEqual(2, exitCode);
        Assert.IsTrue(stderr.Contains(DiagnosticCodes.InvalidArgument, StringComparison.Ordinal));
    }

    [TestMethod]
    public void Append_MissingParentXPath_ReturnsExitCode2()
    {
        var workspace = CreateWorkspaceCopy();

        var (exitCode, stdout, stderr) = RunCliWithStdin(
            "<record id=\"20260823T041500Z-test\"/>",
            "append",
            "--workspace-root", workspace,
            "--document", "20260823-xpath-core/tasks.xml",
            "--expected-revision", "9",
            "--stdin");

        Assert.AreEqual(2, exitCode);
        Assert.IsTrue(stderr.Contains(DiagnosticCodes.InvalidArgument, StringComparison.Ordinal));
    }

    [TestMethod]
    public void Append_MissingExpectedRevision_ReturnsExitCode2()
    {
        var workspace = CreateWorkspaceCopy();

        var (exitCode, stdout, stderr) = RunCliWithStdin(
            "<record id=\"20260823T041500Z-test\"/>",
            "append",
            "--workspace-root", workspace,
            "--document", "20260823-xpath-core/tasks.xml",
            "--parent-xpath", "//task[@id='20260823-task-xpath-projection']/records",
            "--stdin");

        Assert.AreEqual(2, exitCode);
        Assert.IsTrue(stderr.Contains(DiagnosticCodes.InvalidArgument, StringComparison.Ordinal));
    }

    [TestMethod]
    public void Append_FileXmlFormat_HappyPath_ReturnsMutationEnvelopeWithExitCode0()
    {
        var workspace = CreateWorkspaceCopy();
        var recordFile = Path.Combine(_tempDir, "record.xml");
        File.WriteAllText(recordFile, """
<record
  id="20260823T041500Z-record-file-test"
  kind="discussion"
  status="informational"
  created_at="2026-08-23T04:15:00Z"
  actor="codex">
  <summary>Appended via file option.</summary>
</record>
""");

        var (exitCode, stdout, stderr) = RunCli(
            "append",
            "--workspace-root", workspace,
            "--document", "20260823-xpath-core/tasks.xml",
            "--parent-xpath", "//task[@id='20260823-task-xpath-projection']/records",
            "--expected-revision", "9",
            "--file", recordFile,
            "--format", "xml");

        Assert.AreEqual(0, exitCode, $"Stderr: {stderr}");
        Assert.IsTrue(stdout.Contains("<mutation command=\"append\" already_applied=\"false\">", StringComparison.Ordinal));
        Assert.IsTrue(stdout.Contains("path=\"20260823-xpath-core/tasks.xml\"", StringComparison.Ordinal));
        Assert.IsTrue(stdout.Contains("previous_revision=\"9\"", StringComparison.Ordinal));
        Assert.IsTrue(stdout.Contains("revision=\"10\"", StringComparison.Ordinal));
    }

    [TestMethod]
    public void Append_FileHumanFormat_HappyPath_ReturnsHumanOutputWithExitCode0()
    {
        var workspace = CreateWorkspaceCopy();
        var recordFile = Path.Combine(_tempDir, "record.xml");
        File.WriteAllText(recordFile, """
<record
  id="20260823T041500Z-record-human-test"
  kind="discussion"
  status="informational"
  created_at="2026-08-23T04:15:00Z"
  actor="codex">
  <summary>Appended in human format.</summary>
</record>
""");

        var (exitCode, stdout, stderr) = RunCli(
            "append",
            "--workspace-root", workspace,
            "--document", "20260823-xpath-core/tasks.xml",
            "--parent-xpath", "//task[@id='20260823-task-xpath-projection']/records",
            "--expected-revision", "9",
            "--file", recordFile,
            "--format", "human");

        Assert.AreEqual(0, exitCode, $"Stderr: {stderr}");
        Assert.IsTrue(stdout.Contains("Mutation applied (append):", StringComparison.Ordinal));
        Assert.IsTrue(stdout.Contains("20260823-xpath-core/tasks.xml", StringComparison.Ordinal));
        Assert.IsTrue(stdout.Contains("revision 10, previous 9", StringComparison.Ordinal));
    }

    [TestMethod]
    public void Append_Stdin_HappyPath_ReturnsMutationEnvelopeWithExitCode0()
    {
        var workspace = CreateWorkspaceCopy();
        var recordXml = """
<record
  id="20260823T041500Z-record-stdin-test"
  kind="discussion"
  status="informational"
  created_at="2026-08-23T04:15:00Z"
  actor="codex">
  <summary>Appended via stdin option.</summary>
</record>
""";

        var (exitCode, stdout, stderr) = RunCliWithStdin(
            recordXml,
            "append",
            "--workspace-root", workspace,
            "--document", "20260823-xpath-core/tasks.xml",
            "--parent-xpath", "//task[@id=$target_task]/records",
            "--var", "target_task=20260823-task-xpath-projection",
            "--expected-revision", "9",
            "--stdin",
            "--format", "xml");

        Assert.AreEqual(0, exitCode, $"Stderr: {stderr}");
        Assert.IsTrue(stdout.Contains("<mutation command=\"append\" already_applied=\"false\">", StringComparison.Ordinal));
        Assert.IsTrue(stdout.Contains("revision=\"10\"", StringComparison.Ordinal));
    }

    [TestMethod]
    public void Append_CardinalityConflict_ReturnsExitCode4WithDiagnosticsOnStderr()
    {
        var workspace = CreateWorkspaceCopy();
        var recordXml = """
<record
  id="20260823T041500Z-record-card-test"
  kind="discussion"
  status="informational"
  created_at="2026-08-23T04:15:00Z"
  actor="codex">
  <summary>Cardinality test.</summary>
</record>
""";

        var (exitCode, stdout, stderr) = RunCliWithStdin(
            recordXml,
            "append",
            "--workspace-root", workspace,
            "--document", "20260823-xpath-core/tasks.xml",
            "--parent-xpath", "//task[@id='unknown-id']/records",
            "--expected-revision", "9",
            "--stdin",
            "--format", "xml");

        Assert.AreEqual(4, exitCode);
        Assert.IsTrue(stderr.Contains("<diagnostics command=\"append\"", StringComparison.Ordinal));
        Assert.IsTrue(stderr.Contains(DiagnosticCodes.CardinalityConflict, StringComparison.Ordinal));
    }

    [TestMethod]
    public void Append_RevisionConflict_ReturnsExitCode4WithDiagnosticsOnStderr()
    {
        var workspace = CreateWorkspaceCopy();
        var recordXml = """
<record
  id="20260823T041500Z-record-rev-test"
  kind="discussion"
  status="informational"
  created_at="2026-08-23T04:15:00Z"
  actor="codex">
  <summary>Revision test.</summary>
</record>
""";

        var (exitCode, stdout, stderr) = RunCliWithStdin(
            recordXml,
            "append",
            "--workspace-root", workspace,
            "--document", "20260823-xpath-core/tasks.xml",
            "--parent-xpath", "//task[@id='20260823-task-xpath-projection']/records",
            "--expected-revision", "1", // Stale!
            "--stdin",
            "--format", "xml");

        Assert.AreEqual(4, exitCode);
        Assert.IsTrue(stderr.Contains("<diagnostics command=\"append\"", StringComparison.Ordinal));
        Assert.IsTrue(stderr.Contains(DiagnosticCodes.RevisionConflict, StringComparison.Ordinal));
    }

    [TestMethod]
    public void Append_OwnerDecisionRequired_ReturnsExitCode5WithDiagnosticsOnStderr()
    {
        var workspace = CreateWorkspaceCopy();
        var confirmationXml = """
<confirmation
  id="20260823T050000Z-confirm-illegal"
  action="complete"
  decision="accepted"
  actor="owner"
  decided_at="2026-08-23T05:00:00Z">
  <summary>Illegal confirmation.</summary>
</confirmation>
""";

        var (exitCode, stdout, stderr) = RunCliWithStdin(
            confirmationXml,
            "append",
            "--workspace-root", workspace,
            "--document", "20260823-xpath-core/spec.xml",
            "--parent-xpath", "/iteration/confirmations",
            "--expected-revision", "4",
            "--stdin",
            "--format", "xml");

        Assert.AreEqual(5, exitCode, "Protected state mutation must return exit code 5");
        Assert.IsTrue(stderr.Contains("<diagnostics command=\"append\"", StringComparison.Ordinal));
        Assert.IsTrue(stderr.Contains(DiagnosticCodes.OwnerDecisionRequired, StringComparison.Ordinal));
    }

    [TestMethod]
    public void Append_SchemaValidationError_ReturnsExitCode3WithDiagnosticsOnStderr()
    {
        var workspace = CreateWorkspaceCopy();
        var invalidXml = """
<bad-element
  id="20260823T041500Z-bad"
  created_at="2026-08-23T04:15:00Z">
  <bad>data</bad>
</bad-element>
""";

        var (exitCode, stdout, stderr) = RunCliWithStdin(
            invalidXml,
            "append",
            "--workspace-root", workspace,
            "--document", "20260823-xpath-core/tasks.xml",
            "--parent-xpath", "//task[@id='20260823-task-xpath-projection']/records",
            "--expected-revision", "9",
            "--stdin",
            "--format", "xml");

        Assert.AreEqual(3, exitCode, "Schema validation error must return exit code 3");
        Assert.IsTrue(stderr.Contains("<diagnostics command=\"append\"", StringComparison.Ordinal));
        Assert.IsTrue(stderr.Contains(DiagnosticCodes.SchemaValidationError, StringComparison.Ordinal));
    }

    [TestMethod]
    public void Append_RealDisposableWorkspaceLifecycle_Init_CreateIteration_AppendRecord_Validate_Query()
    {
        // 1. Initialize fresh workspace
        var (initCode, initOut, initErr) = RunCli(
            "workspace", "init",
            "--workspace-root", _tempDir,
            "--format", "xml");
        Assert.AreEqual(0, initCode, $"Init failed: {initErr}");

        // 2. Create iteration
        var (createCode, createOut, createErr) = RunCli(
            "iteration", "create",
            "--workspace-root", _tempDir,
            "--id", "20260823-generic-append",
            "--kind", "feature",
            "--format", "xml");
        Assert.AreEqual(0, createCode, $"Iteration create failed: {createErr}");

        // 3. Append a task fixture to tasks.xml
        var taskXml = """
<task
  id="20260823-task-generic-append"
  status="pending"
  created_at="2026-08-23T05:00:00Z"
  updated_at="2026-08-23T05:00:00Z">
  <index>
    <summary>Implement generic append vertical slice.</summary>
    <term key="topic" value="generic-append"/>
  </index>
  <title>Implement generic append</title>
  <objective>Deliver generic append command for DogdouSpec.</objective>
  <rationale>Allow safe atomic appending of records and items.</rationale>
  <scope>
    <repository path=".">
      <include path="src/**"/>
    </repository>
  </scope>
  <origin>
    <ref scope="iteration" target="20260823-req-generic-append" relation="implements"/>
  </origin>
  <constraints/>
  <acceptance>
    <criterion id="20260823-taskaccept-append-slice" status="pending">
      Generic append command succeeds and passes validation.
    </criterion>
  </acceptance>
  <context>
    <summary>Task context summary.</summary>
  </context>
  <records/>
</task>
""";

        var (appendTaskCode, appendTaskOut, appendTaskErr) = RunCliWithStdin(
            taskXml,
            "append",
            "--workspace-root", _tempDir,
            "--document", "20260823-generic-append/tasks.xml",
            "--parent-xpath", "/tasks",
            "--expected-revision", "1",
            "--stdin",
            "--format", "xml");

        Assert.AreEqual(0, appendTaskCode, $"Append task failed: {appendTaskErr}");
        Assert.IsTrue(appendTaskOut.Contains("revision=\"2\"", StringComparison.Ordinal));

        // 4. Append a task-compatible record
        var recordXml = """
<record
  id="20260823T051000Z-record-append-start"
  kind="start"
  status="informational"
  created_at="2026-08-23T05:10:00Z"
  actor="codex">
  <summary>Started implementing generic append vertical slice.</summary>
</record>
""";

        var (appendRecCode, appendRecOut, appendRecErr) = RunCliWithStdin(
            recordXml,
            "append",
            "--workspace-root", _tempDir,
            "--document", "20260823-generic-append/tasks.xml",
            "--parent-xpath", "//task[@id='20260823-task-generic-append']/records",
            "--expected-revision", "2",
            "--stdin",
            "--format", "xml");

        Assert.AreEqual(0, appendRecCode, $"Append record failed: {appendRecErr}");
        Assert.IsTrue(appendRecOut.Contains("<mutation command=\"append\" already_applied=\"false\">", StringComparison.Ordinal));
        Assert.IsTrue(appendRecOut.Contains("previous_revision=\"2\"", StringComparison.Ordinal));
        Assert.IsTrue(appendRecOut.Contains("revision=\"3\"", StringComparison.Ordinal));

        // 5. Validate entire workspace
        var (valCode, valOut, valErr) = RunCli(
            "validate",
            "--workspace-root", _tempDir,
            "--format", "xml");
        Assert.AreEqual(0, valCode, $"Validation failed: {valErr}");
        Assert.IsTrue(valOut.Contains("<validation valid=\"true\"", StringComparison.Ordinal));

        // 6. Query for the appended record
        var (queryRecCode, queryRecOut, queryRecErr) = RunCli(
            "query",
            "--workspace-root", _tempDir,
            "--document", "20260823-generic-append/tasks.xml",
            "--xpath", "//task[@id='20260823-task-generic-append']/records/record[@id='20260823T051000Z-record-append-start']",
            "--format", "xml");
        Assert.AreEqual(0, queryRecCode, $"Query record failed: {queryRecErr}");
        Assert.IsTrue(queryRecOut.Contains("20260823T051000Z-record-append-start", StringComparison.Ordinal));
        Assert.IsTrue(queryRecOut.Contains("Started implementing generic append vertical slice.", StringComparison.Ordinal));
    }
}
