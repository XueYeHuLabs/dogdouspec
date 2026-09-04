using System.Security.Cryptography;
using System.Xml.Linq;
using DogdouSpec.Cli;
using DogdouSpec.Core.Diagnostics;

namespace DogdouSpec.Cli.Tests;

[TestClass]
public sealed class MutationPreflightAndAddressingCliTests
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
        _tempDir = Path.Combine(Path.GetTempPath(), "DogdouSpec_CliPreflightTests_" + Guid.NewGuid().ToString("N"));
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

    private static (int ExitCode, string Stdout, string Stderr) RunCli(params string[] args) =>
        RunCliWithStdin(null, args);

    private static (int ExitCode, string Stdout, string Stderr) RunCliWithStdin(string? stdinInput, params string[] args)
    {
        var originalOut = Console.Out;
        var originalErr = Console.Error;
        var originalIn = Console.In;
        var originalDir = Environment.CurrentDirectory;

        using var outSw = new StringWriter();
        using var errSw = new StringWriter();
        using var inSr = stdinInput != null ? new StringReader(stdinInput) : null;

        try
        {
            Console.SetOut(outSw);
            Console.SetError(errSw);
            if (inSr != null) Console.SetIn(inSr);

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
    public void Addressing_LegacyAndShorthand_QueryAndValidateSucceed()
    {
        var ws = CreateWorkspaceCopy();

        // 1. Query with legacy document path
        var (q1Code, q1Out, q1Err) = RunCli("query", "--workspace-root", ws,
            "--document", "20260823-xpath-core/spec.xml",
            "--xpath", "string(/iteration/@id)", "--format", "human");
        Assert.AreEqual(0, q1Code, q1Err);
        StringAssert.Contains(q1Out, "20260823-xpath-core");

        // 2. Query with shorthand --iteration and --document
        var (q2Code, q2Out, q2Err) = RunCli("query", "--workspace-root", ws,
            "--iteration", "20260823-xpath-core",
            "--document", "spec.xml",
            "--xpath", "string(/iteration/@id)", "--format", "human");
        Assert.AreEqual(0, q2Code, q2Err);
        StringAssert.Contains(q2Out, "20260823-xpath-core");

        // 3. Query root document
        var (q3Code, q3Out, q3Err) = RunCli("query", "--workspace-root", ws,
            "--document", "backlog.xml",
            "--xpath", "string(/backlog/@id)", "--format", "human");
        Assert.AreEqual(0, q3Code, q3Err);
        StringAssert.Contains(q3Out, "20260801-backlog");

        // 4. Validate with legacy document path
        var (v1Code, v1Out, v1Err) = RunCli("validate", "--workspace-root", ws,
            "--document", "20260823-xpath-core/spec.xml", "--format", "xml");
        Assert.AreEqual(0, v1Code, v1Err);
        StringAssert.Contains(v1Out, "valid=\"true\"");
        StringAssert.Contains(v1Out, "document=\"20260823-xpath-core/spec.xml\"");

        // 5. Validate with shorthand --iteration and --document
        var (v2Code, v2Out, v2Err) = RunCli("validate", "--workspace-root", ws,
            "--iteration", "20260823-xpath-core",
            "--document", "spec.xml", "--format", "xml");
        Assert.AreEqual(0, v2Code, v2Err);
        StringAssert.Contains(v2Out, "valid=\"true\"");
        StringAssert.Contains(v2Out, "document=\"20260823-xpath-core/spec.xml\"");

        var (v3Code, v3Out, v3Err) = RunCli("validate", "--workspace-root", ws,
            "--iteration", "20260823-xpath-core",
            "--document", "tasks.xml", "--format", "xml");
        Assert.AreEqual(0, v3Code, v3Err);
        StringAssert.Contains(v3Out, "valid=\"true\"");
        StringAssert.Contains(v3Out, "document=\"20260823-xpath-core/tasks.xml\"");
    }

    [TestMethod]
    public void Addressing_ConflictsAndTraversals_RejectedWithErrors()
    {
        var ws = CreateWorkspaceCopy();

        // 1. Conflict: --iteration with a conflicting document path
        var (c1Code, _, c1Err) = RunCli("query", "--workspace-root", ws,
            "--iteration", "20260823-xpath-core",
            "--document", "other-iter/spec.xml",
            "--xpath", "string(/iteration/@id)");
        Assert.AreNotEqual(0, c1Code);
        StringAssert.Contains(c1Err, "conflicts with specified --iteration");

        var (c2Code, _, c2Err) = RunCli("validate", "--workspace-root", ws,
            "--iteration", "20260823-xpath-core",
            "--document", "other-iter/spec.xml");
        Assert.AreNotEqual(0, c2Code);
        StringAssert.Contains(c2Err, "conflicts with specified --iteration");

        // 2. Conflict: --iteration with root document
        var (c3Code, _, c3Err) = RunCli("validate", "--workspace-root", ws,
            "--iteration", "20260823-xpath-core",
            "--document", "backlog.xml");
        Assert.AreNotEqual(0, c3Code);
        StringAssert.Contains(c3Err, "--iteration cannot be specified with root document");

        // 3. Traversal rejection
        var (t1Code, _, t1Err) = RunCli("query", "--workspace-root", ws,
            "--document", "../escaped.xml",
            "--xpath", "string(/)");
        Assert.AreNotEqual(0, t1Code);
        StringAssert.Contains(t1Err, "PATH_TRAVERSAL_DETECTED");

        var (t2Code, _, t2Err) = RunCli("validate", "--workspace-root", ws,
            "--document", "../escaped.xml");
        Assert.AreNotEqual(0, t2Code);
        StringAssert.Contains(t2Err, "PATH_TRAVERSAL_DETECTED");
    }

    [TestMethod]
    public void MalformedOrMissingRevision_FailsClosedNeverBecomesOne()
    {
        var ws = CreateWorkspaceCopy();
        var specPath = Path.Combine(ws, "20260823-xpath-core", "spec.xml");
        var tasksPath = Path.Combine(ws, "20260823-xpath-core", "tasks.xml");
        var backlogPath = Path.Combine(ws, "backlog.xml");

        // Corrupt spec.xml revision to malformed string
        var specContent = File.ReadAllText(specPath);
        File.WriteAllText(specPath, specContent.Replace("revision=\"4\"", "revision=\"not-a-number\""));

        var (actCode, _, actErr) = RunCli("iteration", "activate",
            "--workspace-root", ws,
            "--iteration", "20260823-xpath-core",
            "--format", "xml");
        Assert.AreNotEqual(0, actCode);
        StringAssert.Contains(actErr, "XML_PARSE_ERROR");

        // Restore spec.xml, corrupt tasks.xml revision to empty
        File.WriteAllText(specPath, specContent);
        var tasksContent = File.ReadAllText(tasksPath);
        File.WriteAllText(tasksPath, tasksContent.Replace("revision=\"9\"", "revision=\"\""));

        var (startCode, _, startErr) = RunCli("task", "start",
            "--workspace-root", ws,
            "--iteration", "20260823-xpath-core",
            "--task", "20260823-task-xpath-projection",
            "--format", "xml");
        Assert.AreNotEqual(0, startCode);
        StringAssert.Contains(startErr, "XML_PARSE_ERROR");

        var (finishCode, _, finishErr) = RunCli("task", "finish",
            "--workspace-root", ws,
            "--iteration", "20260823-xpath-core",
            "--task", "20260823-task-xpath-projection",
            "--format", "xml");
        Assert.AreNotEqual(0, finishCode);
        StringAssert.Contains(finishErr, "XML_PARSE_ERROR");

        // Restore tasks.xml, corrupt backlog.xml revision to missing
        File.WriteAllText(tasksPath, tasksContent);
        var backlogContent = File.ReadAllText(backlogPath);
        File.WriteAllText(backlogPath, backlogContent.Replace("revision=\"1\"", ""));

        var (bCode, _, bErr) = RunCli("backlog", "schedule",
            "--workspace-root", ws,
            "--id", "some-id",
            "--operation-id", "20260825T100000Z-schedule-test",
            "--actor", "tester",
            "--occurred-at", "2026-08-25T10:00:00Z",
            "--format", "xml");
        Assert.AreNotEqual(0, bCode);
        StringAssert.Contains(bErr, "XML_PARSE_ERROR");
    }

    [TestMethod]
    public void Preflight_ZeroWritesAndQuickDryRunPiping_Compatible()
    {
        var ws = CreateWorkspaceCopy();
        var tasksPath = Path.Combine(ws, "20260823-xpath-core", "tasks.xml");
        var originalHash = ComputeFileSha256(tasksPath);
        var originalMtime = File.GetLastWriteTimeUtc(tasksPath);

        // 1. Run task quick --dry-run --format xml to generate the canonical request XML
        var (quickCode, quickOut, quickErr) = RunCli("task", "quick",
            "--workspace-root", ws,
            "--iteration", "20260823-xpath-core",
            "--title", "Pipeable quick task",
            "--scope", "src/Core/",
            "--done-when", "Piping succeeds cleanly.",
            "--why", "Verifying pipe compatibility.",
            "--origin", "20260823-req-iteration-discovery",
            "--dry-run",
            "--format", "xml");
        Assert.AreEqual(0, quickCode, quickErr);
        StringAssert.Contains(quickOut, "<task-add");
        StringAssert.Contains(quickOut, "Pipeable quick task");

        // Verify task quick dry-run did not modify tasks.xml
        Assert.AreEqual(originalHash, ComputeFileSha256(tasksPath));

        // 2. Pipe quick dry-run XML into validate --stdin (Preflight check)
        var (valCode, valOut, valErr) = RunCliWithStdin(quickOut,
            "validate",
            "--workspace-root", ws,
            "--stdin",
            "--iteration", "20260823-xpath-core",
            "--format", "xml");
        Assert.AreEqual(0, valCode, valErr);
        StringAssert.Contains(valOut, "<mutation");
        StringAssert.Contains(valOut, "command=\"task add\"");
        StringAssert.Contains(valOut, "revision=\"10\"");
        StringAssert.Contains(valOut, "previous_revision=\"9\"");

        // Verify validate --stdin preflight did NOT modify tasks.xml on disk
        Assert.AreEqual(originalHash, ComputeFileSha256(tasksPath));
        Assert.AreEqual(originalMtime, File.GetLastWriteTimeUtc(tasksPath));

        // 3. Pipe quick dry-run XML into task add --stdin (Mutation execution)
        var (addCode, addOut, addErr) = RunCliWithStdin(quickOut,
            "task", "add",
            "--workspace-root", ws,
            "--iteration", "20260823-xpath-core",
            "--expected-revision", "9",
            "--stdin",
            "--format", "xml");
        Assert.AreEqual(0, addCode, addErr);
        StringAssert.Contains(addOut, "<mutation");
        StringAssert.Contains(addOut, "command=\"task add\"");
        StringAssert.Contains(addOut, "revision=\"10\"");

        // Verify task add DID modify tasks.xml and revision became 10
        Assert.AreNotEqual(originalHash, ComputeFileSha256(tasksPath));
        var newTasksContent = File.ReadAllText(tasksPath);
        StringAssert.Contains(newTasksContent, "revision=\"10\"");
        StringAssert.Contains(newTasksContent, "Pipeable quick task");
    }

    [TestMethod]
    public void Preflight_QuickStartDryRunXml_PipesUnmodifiedThroughValidateAndTaskAdd()
    {
        var ws = CreateWorkspaceCopy();
        var tasksPath = Path.Combine(ws, "20260823-xpath-core", "tasks.xml");
        var originalHash = ComputeFileSha256(tasksPath);

        var (quickCode, quickOut, quickErr) = RunCli(
            "task", "quick",
            "--workspace-root", ws,
            "--iteration", "20260823-xpath-core",
            "--title", "Pipeable quick start task",
            "--scope", "src/Core/",
            "--done-when", "Started task piping succeeds.",
            "--why", "Verify canonical quick-start composition.",
            "--start",
            "--dry-run",
            "--id", "20260904-task-pipeable-quick-start",
            "--operation-id", "20260904T160000Z-quick-pipeable-start",
            "--format", "xml");
        Assert.AreEqual(0, quickCode, quickErr);
        StringAssert.Contains(quickOut, "status=\"in-progress\"");
        Assert.AreEqual(originalHash, ComputeFileSha256(tasksPath));

        var (validateCode, validateOut, validateErr) = RunCliWithStdin(
            quickOut,
            "validate",
            "--workspace-root", ws,
            "--stdin",
            "--iteration", "20260823-xpath-core",
            "--format", "xml");
        Assert.AreEqual(0, validateCode, validateErr);
        StringAssert.Contains(validateOut, "command=\"task add\"");
        Assert.AreEqual(originalHash, ComputeFileSha256(tasksPath));

        var (addCode, addOut, addErr) = RunCliWithStdin(
            quickOut,
            "task", "add",
            "--workspace-root", ws,
            "--iteration", "20260823-xpath-core",
            "--expected-revision", "9",
            "--stdin",
            "--format", "xml");
        Assert.AreEqual(0, addCode, addErr);
        StringAssert.Contains(addOut, "revision=\"10\"");

        var tasks = XDocument.Load(tasksPath);
        var added = tasks.Root!.Elements("task").Single(t => (string?)t.Attribute("id") == "20260904-task-pipeable-quick-start");
        Assert.AreEqual("in-progress", (string?)added.Attribute("status"));
        Assert.IsNotNull(added.Attribute("started_at"));
    }

    [TestMethod]
    public void ConfirmationMismatches_RejectedFailClosed()
    {
        var ws = CreateWorkspaceCopy();
        var confirmXmlPath = Path.Combine(_tempDir, "confirm.xml");
        File.WriteAllText(confirmXmlPath, """
<?xml version="1.0" encoding="utf-8"?>
<iteration-confirmation
  id="20260823T190000Z-confirm-test"
  iteration="20260823-xpath-core"
  action="activate"
  expected_spec_revision="4"
  expected_tasks_revision="9"
  actor="owner-instruction"
  decided_at="2026-08-23T19:00:00Z">
  <summary>Confirmation mismatch test.</summary>
</iteration-confirmation>
""");

        // 1. Mismatched --iteration option
        var (c1Code, _, c1Err) = RunCli("iteration", "confirm",
            "--workspace-root", ws,
            "--file", confirmXmlPath,
            "--iteration", "20260823-other-iter");
        Assert.AreNotEqual(0, c1Code);
        StringAssert.Contains(c1Err, "disagrees with request attribute");

        // 2. Mismatched --expected-revision option
        var (c2Code, _, c2Err) = RunCli("iteration", "confirm",
            "--workspace-root", ws,
            "--file", confirmXmlPath,
            "--expected-revision", "99");
        Assert.AreNotEqual(0, c2Code);
        StringAssert.Contains(c2Err, "disagrees with request attribute");

        // 3. Mismatched --expected-tasks-revision option
        var (c3Code, _, c3Err) = RunCli("iteration", "confirm",
            "--workspace-root", ws,
            "--file", confirmXmlPath,
            "--expected-tasks-revision", "99");
        Assert.AreNotEqual(0, c3Code);
        StringAssert.Contains(c3Err, "disagrees with request attribute");
    }

    [TestMethod]
    public void ConditionalHelpAndRuntimeContracts_EnforcedAccurately()
    {
        var ws = CreateWorkspaceCopy();

        // 1. Help outputs contain accurate conditional option descriptions
        var (_, quickHelp, _) = RunCli("task", "quick", "--help");
        StringAssert.Contains(quickHelp, "required when --review-required is specified");
        StringAssert.Contains(quickHelp, "requires --agent");

        var (_, taskAddHelp, _) = RunCli("task", "add", "--help");
        StringAssert.Contains(taskAddHelp, "mutually exclusive with --file; exactly one required");
        StringAssert.Contains(taskAddHelp, "mutually exclusive with --stdin; exactly one required");

        var (_, taskReviseHelp, _) = RunCli("task", "revise", "--help");
        StringAssert.Contains(taskReviseHelp, "mutually exclusive with --file; exactly one required if XML input");
        StringAssert.Contains(taskReviseHelp, "mutually exclusive with --stdin; exactly one required if XML input");

        var (_, backlogAddHelp, _) = RunCli("backlog", "add", "--help");
        StringAssert.Contains(backlogAddHelp, "at least one --source-iteration or --source-task required");
        StringAssert.Contains(backlogAddHelp, "mutually exclusive with --review-condition; exactly one required");
        StringAssert.Contains(backlogAddHelp, "mutually exclusive with --target-iteration; exactly one required");
        StringAssert.Contains(backlogAddHelp, "--dry-run");

        var (_, backlogScheduleHelp, _) = RunCli("backlog", "schedule", "--help");
        StringAssert.Contains(backlogScheduleHelp, "(mutating unless --dry-run)");
        StringAssert.Contains(backlogScheduleHelp, "--dry-run");

        var (_, validateHelp, _) = RunCli("validate", "--help");
        StringAssert.Contains(validateHelp, "--request");
        StringAssert.Contains(validateHelp, "mutually exclusive with --stdin and --document");
        StringAssert.Contains(validateHelp, "--expected-revision");
        StringAssert.Contains(validateHelp, "--expected-tasks-revision");

        var (_, queryHelp, _) = RunCli("query", "--help");
        StringAssert.Contains(queryHelp, "--document");
        StringAssert.Contains(queryHelp, "(REQUIRED)");

        foreach (var taskCommand in new[] { "add", "revise", "split", "update", "review" })
        {
            var (_, taskHelp, _) = RunCli("task", taskCommand, "--help");
            StringAssert.Contains(taskHelp, "mutating unless --dry-run");
        }

        var (_, confirmationHelp, _) = RunCli("iteration", "confirm", "--help");
        StringAssert.Contains(confirmationHelp, "(mutating)");
        StringAssert.Contains(confirmationHelp, "--dry-run");
        StringAssert.Contains(confirmationHelp, "validates without writing");

        // 2. Runtime validations enforce conditional constraints
        // task quick --review-required requires --agent
        var (qCode, _, qErr) = RunCli("task", "quick",
            "--workspace-root", ws,
            "--iteration", "20260823-xpath-core",
            "--title", "Unattributed review",
            "--scope", "src/",
            "--done-when", "Done",
            "--why", "Testing",
            "--review-required");
        Assert.AreNotEqual(0, qCode);
        StringAssert.Contains(qErr, "TASK_REVIEW_IMPLEMENTER_UNKNOWN");

        // backlog add requires at least one source
        var (bCode1, _, bErr1) = RunCli("backlog", "add",
            "--workspace-root", ws,
            "--id", "20260825-backlog-no-source",
            "--operation-id", "20260825T110000Z-no-source",
            "--actor", "tester",
            "--occurred-at", "2026-08-25T11:00:00Z",
            "--kind", "feature",
            "--summary", "No source",
            "--statement", "Statement",
            "--rationale", "Rationale",
            "--impact", "Impact",
            "--target-iteration", "20260823-xpath-core");
        Assert.AreNotEqual(0, bCode1);
        StringAssert.Contains(bErr1, "At least one --source-iteration or --source-task is required.");

        // backlog add requires exactly one of target-iteration or review-condition
        var (bCode2, _, bErr2) = RunCli("backlog", "add",
            "--workspace-root", ws,
            "--id", "20260825-backlog-both",
            "--operation-id", "20260825T110100Z-both",
            "--actor", "tester",
            "--occurred-at", "2026-08-25T11:01:00Z",
            "--kind", "feature",
            "--summary", "Both targets",
            "--statement", "Statement",
            "--rationale", "Rationale",
            "--impact", "Impact",
            "--source-iteration", "20260823-xpath-core",
            "--target-iteration", "20260823-xpath-core",
            "--review-condition", "Review condition");
        Assert.AreNotEqual(0, bCode2);
        StringAssert.Contains(bErr2, "Specify exactly one of --target-iteration or --review-condition.");

        // task add cannot specify both --stdin and --file
        var dummyFile = Path.Combine(_tempDir, "dummy.xml");
        File.WriteAllText(dummyFile, "<dummy/>");
        var (tCode, _, tErr) = RunCli("task", "add",
            "--workspace-root", ws,
            "--iteration", "20260823-xpath-core",
            "--expected-revision", "9",
            "--stdin",
            "--file", dummyFile);
        Assert.AreNotEqual(0, tCode);
        StringAssert.Contains(tErr, "Specify either --stdin or --file, not both.");

        // validate cannot specify both --stdin and --request
        var (vCode1, _, vErr1) = RunCli("validate",
            "--workspace-root", ws,
            "--stdin",
            "--request", dummyFile);
        Assert.AreNotEqual(0, vCode1);
        StringAssert.Contains(vErr1, "Specify either --stdin or --request, not both.");

        // validate cannot specify --document with --request
        var (vCode2, _, vErr2) = RunCli("validate",
            "--workspace-root", ws,
            "--document", "spec.xml",
            "--request", dummyFile);
        Assert.AreNotEqual(0, vCode2);
        StringAssert.Contains(vErr2, "Option --document cannot be used with mutation request preflight");
    }
}
