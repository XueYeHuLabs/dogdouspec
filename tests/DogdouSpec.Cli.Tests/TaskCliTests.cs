using System.Xml.Linq;
using DogdouSpec.Cli;
using DogdouSpec.Core.Diagnostics;

namespace DogdouSpec.Cli.Tests;

[TestClass]
public sealed class TaskCliTests
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
        _tempDir = Path.Combine(Path.GetTempPath(), "DogdouSpec_TaskCliTests_" + Guid.NewGuid().ToString("N"));
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
    public void TaskHelp_DisplaysOptionsAndSubcommands()
    {
        var (exitCode, stdout, stderr) = RunCli("task", "--help");
        Assert.AreEqual(0, exitCode, $"Stderr: {stderr}");
        Assert.IsTrue(stdout.Contains("update", StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public void TaskUpdate_HelpOutput_ExplicitlyStatesCommandIsMutating()
    {
        var (exitCode, stdout, stderr) = RunCli("task", "update", "--help");
        Assert.AreEqual(0, exitCode, $"Stderr: {stderr}");
        Assert.IsTrue(stdout.Contains("mutating", StringComparison.OrdinalIgnoreCase), "Help text must indicate command is mutating");
    }

    [TestMethod]
    public void TaskUpdate_StdinAndFileBothSpecified_ReturnsExit2()
    {
        var workspace = CreateWorkspaceCopy();
        var (exitCode, stdout, stderr) = RunCli(
            "task", "update",
            "--iteration", "20260823-xpath-core",
            "--task", "20260823-task-task-history",
            "--expected-revision", "9",
            "--stdin",
            "--file", "dummy.xml",
            "--workspace-root", _tempDir);

        Assert.AreEqual(2, exitCode);
        Assert.IsTrue(stderr.Contains("INVALID_ARGUMENT", StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public void TaskUpdate_NeitherStdinNorFileSpecified_ReturnsExit2()
    {
        var workspace = CreateWorkspaceCopy();
        var (exitCode, stdout, stderr) = RunCli(
            "task", "update",
            "--iteration", "20260823-xpath-core",
            "--task", "20260823-task-task-history",
            "--expected-revision", "9",
            "--workspace-root", _tempDir);

        Assert.AreEqual(2, exitCode);
        Assert.IsTrue(stderr.Contains("INVALID_ARGUMENT", StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public void TaskUpdate_StdinHappyPath_SucceedsAndReturnsMutationEnvelope()
    {
        var workspace = CreateWorkspaceCopy();
        var requestXml = """
<task-update
  id="20260823T080000Z-update-cli-stdin"
  transition="start"
  actor="codex"
  occurred_at="2026-08-23T08:00:00Z">
  <records>
    <record
      id="20260823T080000Z-record-cli-stdin"
      kind="start"
      status="informational"
      created_at="2026-08-23T08:00:00Z"
      actor="codex">
      <summary>Starting task history work via CLI stdin.</summary>
    </record>
  </records>
</task-update>
""";

        var (exitCode, stdout, stderr) = RunCliWithStdin(
            requestXml,
            "task", "update",
            "--iteration", "20260823-xpath-core",
            "--task", "20260823-task-task-history",
            "--expected-revision", "9",
            "--stdin",
            "--workspace-root", _tempDir,
            "--format", "xml");

        Assert.AreEqual(0, exitCode, $"Stderr: {stderr}");
        Assert.IsTrue(stdout.Contains("<mutation"));
        Assert.IsTrue(stdout.Contains("command=\"task update\""));
        Assert.IsTrue(stdout.Contains("already_applied=\"false\""));
        Assert.IsTrue(stdout.Contains("revision=\"10\""));
    }

    [TestMethod]
    public void TaskUpdate_FileHappyPath_SucceedsAndReturnsMutationEnvelope()
    {
        var workspace = CreateWorkspaceCopy();
        var requestXml = """
<task-update
  id="20260823T080100Z-update-cli-start"
  transition="start"
  actor="codex"
  occurred_at="2026-08-23T08:01:00Z">
  <records>
    <record
      id="20260823T080100Z-record-cli-start"
      kind="start"
      status="informational"
      created_at="2026-08-23T08:01:00Z"
      actor="codex">
      <summary>Starting task history work via CLI file.</summary>
    </record>
  </records>
</task-update>
""";

        var reqFile = Path.Combine(_tempDir, "request.xml");
        File.WriteAllText(reqFile, requestXml);

        var (exitCode, stdout, stderr) = RunCli(
            "task", "update",
            "--iteration", "20260823-xpath-core",
            "--task", "20260823-task-task-history",
            "--expected-revision", "9",
            "--file", reqFile,
            "--workspace-root", _tempDir,
            "--format", "xml");

        Assert.AreEqual(0, exitCode, $"Stderr: {stderr}");
        Assert.IsTrue(stdout.Contains("<mutation"));
        Assert.IsTrue(stdout.Contains("command=\"task update\""));
        Assert.IsTrue(stdout.Contains("already_applied=\"false\""));
        Assert.IsTrue(stdout.Contains("revision=\"10\""));
    }

    [TestMethod]
    public void TaskUpdate_HumanFormat_ReturnsHumanMessage()
    {
        var workspace = CreateWorkspaceCopy();
        var requestXml = """
<task-update
  id="20260823T081000Z-update-cli-human"
  transition="start"
  actor="codex"
  occurred_at="2026-08-23T08:10:00Z">
  <records>
    <record
      id="20260823T081000Z-record-cli-human"
      kind="start"
      status="informational"
      created_at="2026-08-23T08:10:00Z"
      actor="codex">
      <summary>Starting task history work via CLI human.</summary>
    </record>
  </records>
</task-update>
""";

        var reqFile = Path.Combine(_tempDir, "request_human.xml");
        File.WriteAllText(reqFile, requestXml);

        var (exitCode, stdout, stderr) = RunCli(
            "task", "update",
            "--iteration", "20260823-xpath-core",
            "--task", "20260823-task-task-history",
            "--expected-revision", "9",
            "--file", reqFile,
            "--workspace-root", _tempDir,
            "--format", "human");

        Assert.AreEqual(0, exitCode, $"Stderr: {stderr}");
        Assert.IsTrue(stdout.Contains("Mutation applied (task update)"));
        Assert.IsTrue(stdout.Contains("20260823-xpath-core/tasks.xml"));
    }

    [TestMethod]
    public void TaskUpdate_StaleRevision_ReturnsExit4()
    {
        var workspace = CreateWorkspaceCopy();
        var requestXml = """
<task-update
  id="20260823T082000Z-update-stale"
  transition="start"
  actor="codex"
  occurred_at="2026-08-23T08:20:00Z">
  <records>
    <record
      id="20260823T082000Z-record-stale"
      kind="start"
      status="informational"
      created_at="2026-08-23T08:20:00Z"
      actor="codex">
      <summary>Stale revision request.</summary>
    </record>
  </records>
</task-update>
""";

        var reqFile = Path.Combine(_tempDir, "request_stale.xml");
        File.WriteAllText(reqFile, requestXml);

        var (exitCode, stdout, stderr) = RunCli(
            "task", "update",
            "--iteration", "20260823-xpath-core",
            "--task", "20260823-task-task-history",
            "--expected-revision", "99",
            "--file", reqFile,
            "--workspace-root", _tempDir,
            "--format", "xml");

        Assert.AreEqual(4, exitCode);
        Assert.IsTrue(stderr.Contains("REVISION_CONFLICT"));
    }

    [TestMethod]
    public void TaskUpdate_IdempotentRetry_ReturnsAlreadyAppliedTrue()
    {
        var workspace = CreateWorkspaceCopy();
        var requestXml = """
<task-update
  id="20260823T083000Z-update-retry"
  transition="start"
  actor="codex"
  occurred_at="2026-08-23T08:30:00Z">
  <records>
    <record
      id="20260823T083000Z-record-retry"
      kind="start"
      status="informational"
      created_at="2026-08-23T08:30:00Z"
      actor="codex">
      <summary>Idempotent retry record.</summary>
    </record>
  </records>
</task-update>
""";

        var reqFile = Path.Combine(_tempDir, "request_retry.xml");
        File.WriteAllText(reqFile, requestXml);

        // 1. First run
        var (exitCode1, _, stderr1) = RunCli(
            "task", "update",
            "--iteration", "20260823-xpath-core",
            "--task", "20260823-task-task-history",
            "--expected-revision", "9",
            "--file", reqFile,
            "--workspace-root", _tempDir);
        Assert.AreEqual(0, exitCode1, $"Stderr: {stderr1}");

        // 2. Retry with pre-commit expected revision (9)
        var (exitCode2, stdout2, stderr2) = RunCli(
            "task", "update",
            "--iteration", "20260823-xpath-core",
            "--task", "20260823-task-task-history",
            "--expected-revision", "9",
            "--file", reqFile,
            "--workspace-root", _tempDir,
            "--format", "xml");

        Assert.AreEqual(0, exitCode2, $"Stderr: {stderr2}");
        Assert.IsTrue(stdout2.Contains("already_applied=\"true\""));
    }

    [TestMethod]
    public void TaskUpdate_EndToEndDisposableWorkflow_Succeeds()
    {
        var e2eDir = Path.Combine(_tempDir, "e2e_workflow");
        Directory.CreateDirectory(e2eDir);

        // 1. Initialize workspace
        var (initCode, _, initErr) = RunCli("workspace", "init", "--workspace-root", e2eDir);
        Assert.AreEqual(0, initCode, $"Init failed: {initErr}");

        // 2. Create iteration
        var (createCode, _, createErr) = RunCli(
            "iteration", "create",
            "--id", "20260824-e2e-feature",
            "--kind", "feature",
            "--workspace-root", e2eDir);
        Assert.AreEqual(0, createCode, $"Create iteration failed: {createErr}");

        // 3. Append a task into tasks.xml
        var taskFragment = """
<task
  id="20260824-task-e2e-impl"
  status="pending"
  created_at="2026-08-24T00:00:00Z"
  updated_at="2026-08-24T00:00:00Z"
  agent="codex">
  <index>
    <summary>E2E task summary.</summary>
    <term key="topic" value="e2e"/>
  </index>
  <title>E2E Implementation Task</title>
  <objective>Implement feature completely.</objective>
  <rationale>Testing end to end lifecycle.</rationale>
  <scope>
    <repository path=".">
      <include path="src/**"/>
    </repository>
  </scope>
  <origin>
    <ref scope="iteration" target="20260824-req-e2e-feature" relation="implements"/>
  </origin>
  <constraints/>
  <acceptance>
    <criterion id="20260824-taskaccept-e2e-criterion" status="pending">
      Feature works as expected.
    </criterion>
  </acceptance>
  <context>
    <summary>Initial context.</summary>
  </context>
  <records/>
</task>
""";
        var (appendCode, _, appendErr) = RunCliWithStdin(
            taskFragment,
            "append",
            "--document", "20260824-e2e-feature/tasks.xml",
            "--parent-xpath", "/tasks",
            "--expected-revision", "1",
            "--stdin",
            "--workspace-root", e2eDir);
        Assert.AreEqual(0, appendCode, $"Append task failed: {appendErr}");

        // 4. Start the task via task update
        var startReq = """
<task-update
  id="20260824T000500Z-update-e2e-start"
  transition="start"
  actor="codex"
  occurred_at="2026-08-24T00:05:00Z">
  <records>
    <record
      id="20260824T000500Z-record-e2e-start"
      kind="start"
      status="informational"
      created_at="2026-08-24T00:05:00Z"
      actor="codex">
      <summary>Starting task.</summary>
    </record>
  </records>
</task-update>
""";
        var startFile = Path.Combine(e2eDir, "start.xml");
        File.WriteAllText(startFile, startReq);

        var (startCode, _, startErr) = RunCli(
            "task", "update",
            "--iteration", "20260824-e2e-feature",
            "--task", "20260824-task-e2e-impl",
            "--expected-revision", "2",
            "--file", startFile,
            "--workspace-root", e2eDir);
        Assert.AreEqual(0, startCode, $"Start task failed: {startErr}");

        // 5. Verify the task with criteria passed
        var verifyReq = """
<task-update
  id="20260824T001000Z-update-e2e-verify"
  transition="verify"
  actor="codex"
  occurred_at="2026-08-24T00:10:00Z">
  <acceptance>
    <criterion target="20260824-taskaccept-e2e-criterion" result="passed"/>
  </acceptance>
  <context_update>
    <summary>Context updated during verification.</summary>
  </context_update>
  <records>
    <record
      id="20260824T001000Z-record-e2e-verification"
      kind="verification"
      status="informational"
      created_at="2026-08-24T00:10:00Z"
      actor="codex">
      <summary>Verification criteria checked.</summary>
      <covers>
        <ref scope="document" target="20260824-taskaccept-e2e-criterion" relation="covers"/>
      </covers>
    </record>
  </records>
</task-update>
""";
        var verifyFile = Path.Combine(e2eDir, "verify.xml");
        File.WriteAllText(verifyFile, verifyReq);

        var (verifyCode, _, verifyErr) = RunCli(
            "task", "update",
            "--iteration", "20260824-e2e-feature",
            "--task", "20260824-task-e2e-impl",
            "--expected-revision", "3",
            "--file", verifyFile,
            "--workspace-root", e2eDir);
        Assert.AreEqual(0, verifyCode, $"Verify task failed: {verifyErr}");

        // 6. Complete the task
        var completeReq = """
<task-update
  id="20260824T001500Z-update-e2e-complete"
  transition="complete"
  actor="codex"
  occurred_at="2026-08-24T00:15:00Z">
  <records>
    <record
      id="20260824T001500Z-record-e2e-complete"
      kind="completion"
      status="informational"
      created_at="2026-08-24T00:15:00Z"
      actor="codex">
      <summary>Completed task successfully.</summary>
      <covers>
        <ref scope="document" target="20260824-taskaccept-e2e-criterion" relation="covers"/>
      </covers>
    </record>
  </records>
</task-update>
""";
        var completeFile = Path.Combine(e2eDir, "complete.xml");
        File.WriteAllText(completeFile, completeReq);

        var (completeCode, _, completeErr) = RunCli(
            "task", "update",
            "--iteration", "20260824-e2e-feature",
            "--task", "20260824-task-e2e-impl",
            "--expected-revision", "4",
            "--file", completeFile,
            "--workspace-root", e2eDir);
        Assert.AreEqual(0, completeCode, $"Complete task failed: {completeErr}");

        // 7. Validate whole workspace
        var (validateCode, _, validateErr) = RunCli("validate", "--workspace-root", e2eDir);
        Assert.AreEqual(0, validateCode, $"Validate workspace failed: {validateErr}");
    }

    [TestMethod]
    public void TaskUpdate_DuplicateAcceptanceTargetCli_ReturnsExit4()
    {
        var workspace = CreateWorkspaceCopy();
        var reqXml = """
<task-update
  id="20260823T084000Z-update-cli-dup-accept"
  actor="codex"
  occurred_at="2026-08-23T08:40:00Z">
  <acceptance>
    <criterion target="20260823-taskaccept-filter-members" result="passed"/>
    <criterion target="20260823-taskaccept-filter-members" result="passed"/>
  </acceptance>
  <records>
    <record
      id="20260823T084000Z-record-cli-dup"
      kind="discussion"
      status="informational"
      created_at="2026-08-23T08:40:00Z"
      actor="codex">
      <summary>Duplicate target.</summary>
    </record>
  </records>
</task-update>
""";

        var (exitCode, _, stderr) = RunCliWithStdin(
            reqXml,
            "task", "update",
            "--iteration", "20260823-xpath-core",
            "--task", "20260823-task-xpath-projection",
            "--expected-revision", "9",
            "--stdin",
            "--workspace-root", _tempDir,
            "--format", "xml");

        Assert.AreEqual(4, exitCode);
        Assert.IsTrue(stderr.Contains("IDEMPOTENCY_CONFLICT"));
    }

    [TestMethod]
    public void TaskUpdate_DuplicateResolveTargetCli_ReturnsExit4()
    {
        var workspace = CreateWorkspaceCopy();
        var reqXml = """
<task-update
  id="20260823T084100Z-update-cli-dup-resolve"
  actor="codex"
  occurred_at="2026-08-23T08:41:00Z">
  <resolve-records>
    <record target="20260823T040000Z-record-projection-attempt"/>
    <record target="20260823T040000Z-record-projection-attempt"/>
  </resolve-records>
  <records>
    <record
      id="20260823T084100Z-record-cli-dup-r"
      kind="discussion"
      status="informational"
      created_at="2026-08-23T08:41:00Z"
      actor="codex">
      <summary>Duplicate resolve.</summary>
    </record>
  </records>
</task-update>
""";

        var (exitCode, _, stderr) = RunCliWithStdin(
            reqXml,
            "task", "update",
            "--iteration", "20260823-xpath-core",
            "--task", "20260823-task-xpath-projection",
            "--expected-revision", "9",
            "--stdin",
            "--workspace-root", _tempDir,
            "--format", "xml");

        Assert.AreEqual(4, exitCode);
        Assert.IsTrue(stderr.Contains("IDEMPOTENCY_CONFLICT"));
    }

    [TestMethod]
    public void TaskUpdate_ResolveNonActiveRecordCli_ReturnsExit4()
    {
        var workspace = CreateWorkspaceCopy();
        var reqXml = """
<task-update
  id="20260823T084200Z-update-cli-bad-res"
  actor="codex"
  occurred_at="2026-08-23T08:42:00Z">
  <resolve-records>
    <record target="20260823T031500Z-record-projection-start"/>
  </resolve-records>
  <records>
    <record
      id="20260823T084200Z-record-cli-bad-res"
      kind="discussion"
      status="informational"
      created_at="2026-08-23T08:42:00Z"
      actor="codex">
      <summary>Bad resolve target.</summary>
    </record>
  </records>
</task-update>
""";

        var (exitCode, _, stderr) = RunCliWithStdin(
            reqXml,
            "task", "update",
            "--iteration", "20260823-xpath-core",
            "--task", "20260823-task-xpath-projection",
            "--expected-revision", "9",
            "--stdin",
            "--workspace-root", _tempDir,
            "--format", "xml");

        Assert.AreEqual(4, exitCode);
        Assert.IsTrue(stderr.Contains("IDEMPOTENCY_CONFLICT"));
    }

    [TestMethod]
    public void TaskUpdate_NonUtcTimestampCli_ReturnsExit2()
    {
        var workspace = CreateWorkspaceCopy();
        var reqXml = """
<task-update
  id="20260823T084300Z-update-cli-bad-time"
  actor="codex"
  occurred_at="2026-08-23T08:43:00+08:00">
  <records>
    <record
      id="20260823T084300Z-record-cli-bad-time"
      kind="discussion"
      status="informational"
      created_at="2026-08-23T08:43:00Z"
      actor="codex">
      <summary>Non-UTC timestamp.</summary>
    </record>
  </records>
</task-update>
""";

        var (exitCode, _, stderr) = RunCliWithStdin(
            reqXml,
            "task", "update",
            "--iteration", "20260823-xpath-core",
            "--task", "20260823-task-xpath-projection",
            "--expected-revision", "9",
            "--stdin",
            "--workspace-root", _tempDir,
            "--format", "xml");

        Assert.AreEqual(2, exitCode);
        Assert.IsTrue(stderr.Contains("INVALID_ARGUMENT"));
    }
}
