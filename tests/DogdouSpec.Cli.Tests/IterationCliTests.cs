using System.Xml.Linq;
using DogdouSpec.Cli;
using DogdouSpec.Core.Diagnostics;

namespace DogdouSpec.Cli.Tests;

[TestClass]
public sealed class IterationCliTests
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
        _tempDir = Path.Combine(Path.GetTempPath(), "DogdouSpec_IterCliTests_" + Guid.NewGuid().ToString("N"));
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

    private static void MakeAllTasksTerminal(string workspace, string iterationId)
    {
        var tasksPath = Path.Combine(workspace, iterationId, "tasks.xml");
        var tasksDoc = XDocument.Load(tasksPath);
        foreach (var task in tasksDoc.Descendants("task"))
        {
            task.SetAttributeValue("status", "done");
            task.SetAttributeValue("started_at", "2026-08-23T03:00:00Z");
            task.SetAttributeValue("completed_at", "2026-08-23T04:00:00Z");
            task.SetAttributeValue("updated_at", "2026-08-23T04:00:00Z");

            var criteria = task.Element("acceptance")?.Elements("criterion").ToList() ?? new List<XElement>();
            foreach (var crit in criteria)
            {
                crit.SetAttributeValue("status", "passed");
            }

            var recordsEl = task.Element("records");
            if (recordsEl == null)
            {
                recordsEl = new XElement("records");
                task.Add(recordsEl);
            }

            // Remove any active findings
            foreach (var rec in recordsEl.Elements("record").ToList())
            {
                if (rec.Attribute("kind")?.Value == "finding" && rec.Attribute("status")?.Value == "active")
                {
                    rec.SetAttributeValue("status", "resolved");
                }
            }

            // Ensure completion record exists covering all criteria
            var hasComp = recordsEl.Elements("record").Any(r => r.Attribute("kind")?.Value == "completion");
            if (!hasComp)
            {
                var compRec = new XElement("record",
                    new XAttribute("id", $"20260823T040000Z-record-{task.Attribute("id")?.Value}-comp"),
                    new XAttribute("kind", "completion"),
                    new XAttribute("status", "informational"),
                    new XAttribute("created_at", "2026-08-23T04:00:00Z"),
                    new XAttribute("actor", "codex"),
                    new XElement("summary", "Task completed."),
                    new XElement("covers", criteria.Select(c => new XElement("ref",
                        new XAttribute("scope", "document"),
                        new XAttribute("target", c.Attribute("id")?.Value ?? string.Empty),
                        new XAttribute("relation", "covers")))));
                recordsEl.Add(compRec);
            }
        }
        tasksDoc.Save(tasksPath);
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

    private static (int ExitCode, string Stdout, string Stderr) RunCliWithStdin(string stdinContent, params string[] args)
    {
        var originalIn = Console.In;
        var originalOut = Console.Out;
        var originalErr = Console.Error;
        var originalDir = Environment.CurrentDirectory;

        using var inSr = new StringReader(stdinContent);
        using var outSw = new StringWriter();
        using var errSw = new StringWriter();

        try
        {
            Console.SetIn(inSr);
            Console.SetOut(outSw);
            Console.SetError(errSw);

            var exitCode = Program.Main(args);
            return (exitCode, outSw.ToString(), errSw.ToString());
        }
        finally
        {
            Console.SetIn(originalIn);
            Console.SetOut(originalOut);
            Console.SetError(originalErr);
            Environment.CurrentDirectory = originalDir;
        }
    }

    [TestMethod]
    public void IterationCreate_HelpOutput_ExplicitlyStatesCommandIsMutating()
    {
        var (exitCode, stdout, _) = RunCli("iteration", "create", "--help");

        Assert.AreEqual(0, exitCode);
        Assert.IsTrue(stdout.Contains("(mutating)", StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public void IterationConfirm_HelpOutput_ExplicitlyStatesCommandIsMutating()
    {
        var (exitCode, stdout, _) = RunCli("iteration", "confirm", "--help");

        Assert.AreEqual(0, exitCode);
        Assert.IsTrue(stdout.Contains("(mutating)", StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public void IterationReadiness_HelpOutput_DisplaysDescriptionAndOptions()
    {
        var (exitCode, stdout, _) = RunCli("iteration", "readiness", "--help");

        Assert.AreEqual(0, exitCode);
        Assert.IsTrue(stdout.Contains("--iteration", StringComparison.Ordinal));
        Assert.IsTrue(stdout.Contains("--phase", StringComparison.Ordinal));
    }

    [TestMethod]
    public void IterationCreate_FeatureIteration_ReturnsExitCode0AndEmitsMutationEnvelope()
    {
        var workspace = CreateWorkspaceCopy();

        var (exitCode, stdout, stderr) = RunCli(
            "iteration", "create",
            "--workspace-root", workspace,
            "--id", "20260824-feature-test",
            "--kind", "feature",
            "--format", "xml");

        Assert.AreEqual(0, exitCode, $"Stderr: {stderr}");
        Assert.IsTrue(stdout.Contains("<mutation command=\"iteration create\"", StringComparison.Ordinal));
        Assert.IsTrue(stdout.Contains("20260824-feature-test/spec.xml", StringComparison.Ordinal));
        Assert.IsTrue(stdout.Contains("20260824-feature-test/tasks.xml", StringComparison.Ordinal));
    }

    [TestMethod]
    public void IterationReadiness_CompletionPhase_ReturnsExitCode0AndEmitsReadinessXml()
    {
        var workspace = CreateWorkspaceCopy();
        MakeAllTasksTerminal(workspace, "20260823-xpath-core");

        var (exitCode, stdout, stderr) = RunCli(
            "iteration", "readiness",
            "--workspace-root", workspace,
            "--iteration", "20260823-xpath-core",
            "--phase", "completion",
            "--format", "xml");

        Assert.AreEqual(0, exitCode, $"Stderr: {stderr}");
        Assert.IsTrue(stdout.Contains("<readiness", StringComparison.Ordinal));
        Assert.IsTrue(stdout.Contains("iteration=\"20260823-xpath-core\"", StringComparison.Ordinal));
        Assert.IsTrue(stdout.Contains("phase=\"completion\"", StringComparison.Ordinal));
        Assert.IsTrue(stdout.Contains("technically_ready=\"true\"", StringComparison.Ordinal));
        Assert.IsTrue(stdout.Contains("owner_confirmation_required=\"true\"", StringComparison.Ordinal));
    }

    [TestMethod]
    public void IterationReadiness_ActivationPhase_HumanFormat_ReturnsExitCode0()
    {
        var workspace = CreateWorkspaceCopy();

        var (exitCode, stdout, stderr) = RunCli(
            "iteration", "readiness",
            "--workspace-root", workspace,
            "--iteration", "20260823-xpath-core",
            "--phase", "activation",
            "--format", "human");

        Assert.AreEqual(0, exitCode, $"Stderr: {stderr}");
        Assert.IsTrue(stdout.Contains("Iteration Readiness: 20260823-xpath-core", StringComparison.Ordinal));
        Assert.IsTrue(stdout.Contains("Phase: activation", StringComparison.Ordinal));
    }

    [TestMethod]
    public void IterationReadiness_NonExistentIteration_ReturnsExitCode2WithDiagnostics()
    {
        var workspace = CreateWorkspaceCopy();

        var (exitCode, stdout, stderr) = RunCli(
            "iteration", "readiness",
            "--workspace-root", workspace,
            "--iteration", "20260899-missing-iter",
            "--phase", "activation",
            "--format", "xml");

        Assert.AreEqual(2, exitCode);
        Assert.IsTrue(stderr.Contains("<diagnostics command=\"iteration readiness\"", StringComparison.Ordinal));
        Assert.IsTrue(stderr.Contains(DiagnosticCodes.DocumentNotFound, StringComparison.Ordinal));
    }

    [TestMethod]
    public void IterationConfirm_MissingBothStdinAndFile_ReturnsExitCode2()
    {
        var workspace = CreateWorkspaceCopy();

        var (exitCode, _, stderr) = RunCli(
            "iteration", "confirm",
            "--workspace-root", workspace,
            "--format", "xml");

        Assert.AreEqual(2, exitCode);
        Assert.IsTrue(stderr.Contains("Either --stdin or --file must be specified.", StringComparison.Ordinal));
    }

    [TestMethod]
    public void IterationConfirm_BothStdinAndFile_ReturnsExitCode2()
    {
        var workspace = CreateWorkspaceCopy();

        var (exitCode, _, stderr) = RunCli(
            "iteration", "confirm",
            "--workspace-root", workspace,
            "--stdin",
            "--file", "somefile.xml",
            "--format", "xml");

        Assert.AreEqual(2, exitCode);
        Assert.IsTrue(stderr.Contains("Specify either --stdin or --file, not both.", StringComparison.Ordinal));
    }

    [TestMethod]
    public void IterationConfirm_Activation_ViaStdin_Succeeds()
    {
        var workspace = CreateWorkspaceCopy();
        var iterId = "20260824-feature-cli";
        var (createExit, _, _) = RunCli(
            "iteration", "create",
            "--workspace-root", workspace,
            "--id", iterId,
            "--kind", "feature",
            "--criterion", "CLI activation criterion defined.");
        Assert.AreEqual(0, createExit);

        var specDoc = XDocument.Load(Path.Combine(workspace, iterId, "spec.xml"));
        var updatedAtStr = specDoc.Root?.Attribute("updated_at")?.Value ?? "2026-08-24T12:00:00Z";

        var requestXml = $@"<?xml version=""1.0"" encoding=""utf-8""?>
<iteration-confirmation
  id=""20260824T120000Z-confirm-activate""
  iteration=""{iterId}""
  action=""activate""
  expected_spec_revision=""1""
  actor=""lead""
  decided_at=""{updatedAtStr}"">
  <summary>Owner approved iteration plan.</summary>
  <requirements>
    <requirement target=""20260824-req-feature-cli"" decision=""approved""/>
  </requirements>
</iteration-confirmation>";

        var (confirmExit, confirmOut, confirmErr) = RunCliWithStdin(
            requestXml,
            "iteration", "confirm",
            "--workspace-root", workspace,
            "--stdin",
            "--format", "xml");

        Assert.AreEqual(0, confirmExit, $"Confirm stderr: {confirmErr}");
        Assert.IsTrue(confirmOut.Contains("<mutation command=\"iteration confirm\"", StringComparison.Ordinal));
    }

    [TestMethod]
    public void IterationConfirm_Completion_ViaFile_AndIdempotencyRetry_Succeeds()
    {
        var workspace = CreateWorkspaceCopy();
        MakeAllTasksTerminal(workspace, "20260823-xpath-core");

        var requestXml = @"<?xml version=""1.0"" encoding=""utf-8""?>
<iteration-confirmation
  id=""20260824T140000Z-confirm-complete""
  iteration=""20260823-xpath-core""
  action=""complete""
  expected_spec_revision=""4""
  expected_tasks_revision=""9""
  actor=""owner""
  decided_at=""2026-08-24T14:00:00Z"">
  <summary>Owner validated XPath core implementation and accepted all criteria.</summary>
  <acceptance>
    <criterion target=""20260823-accept-directory-overview"" decision=""accepted""/>
    <criterion target=""20260823-accept-resume-task"" decision=""accepted""/>
    <criterion target=""20260823-accept-integrated-verification"" decision=""accepted""/>
    <criterion target=""20260823-accept-no-truncation"" decision=""accepted""/>
    <criterion target=""20260823-accept-structured-reasoning"" decision=""accepted""/>
    <criterion target=""20260823-accept-template-append"" decision=""accepted""/>
  </acceptance>
</iteration-confirmation>";

        var tempReqFile = Path.Combine(_tempDir, "confirm_req.xml");
        File.WriteAllText(tempReqFile, requestXml);

        // 1. Initial confirm via --file
        var (confirmExit, confirmOut, confirmErr) = RunCli(
            "iteration", "confirm",
            "--workspace-root", workspace,
            "--file", tempReqFile,
            "--format", "xml");

        Assert.AreEqual(0, confirmExit, $"Confirm stderr: {confirmErr}");
        Assert.IsTrue(confirmOut.Contains("<mutation command=\"iteration confirm\"", StringComparison.Ordinal));

        // 2. Retry exact confirmation -> exit 0 with already_applied="true"
        var (retryExit, retryOut, retryErr) = RunCli(
            "iteration", "confirm",
            "--workspace-root", workspace,
            "--file", tempReqFile,
            "--format", "xml");

        Assert.AreEqual(0, retryExit, $"Retry stderr: {retryErr}");
        Assert.IsTrue(retryOut.Contains("already_applied=\"true\"", StringComparison.Ordinal));

        // 3. Retry same confirmation ID with conflicting summary -> exit 4
        var conflictReqFile = Path.Combine(_tempDir, "conflict_req.xml");
        File.WriteAllText(conflictReqFile, requestXml.Replace("Owner validated XPath core implementation", "Conflicting text"));

        var (conflictExit, _, conflictErr) = RunCli(
            "iteration", "confirm",
            "--workspace-root", workspace,
            "--file", conflictReqFile,
            "--format", "xml");

        Assert.AreEqual(4, conflictExit);
        Assert.IsTrue(conflictErr.Contains(DiagnosticCodes.IdempotencyConflict, StringComparison.Ordinal));
    }

    [TestMethod]
    public void IterationConfirm_IdempotencyConflict_OnStateDrift_ReturnsExitCode4()
    {
        // Regression test for reproduction 1 via CLI
        var workspace = CreateWorkspaceCopy();

        // 1. Add design decision (rev 4 -> 5)
        var firstXml = @"<?xml version=""1.0"" encoding=""utf-8""?>
<iteration-confirmation
  id=""20260825T100000Z-confirm-design-cli""
  iteration=""20260823-xpath-core""
  action=""accept-design-change""
  expected_spec_revision=""4""
  expected_tasks_revision=""9""
  actor=""architect""
  decided_at=""2026-08-25T10:00:00Z"">
  <summary>First design decision added.</summary>
  <new_design_decision id=""20260825-dec-cli"" status=""proposed"">
    <index>
      <summary>CLI design decision.</summary>
      <term key=""kind"" value=""decision""/>
    </index>
    <rationale>Initial CLI rationale.</rationale>
  </new_design_decision>
  <design>
    <decision target=""20260825-dec-cli"" decision=""accepted""/>
  </design>
</iteration-confirmation>";

        var (firstExit, _, _) = RunCliWithStdin(firstXml, "iteration", "confirm", "--workspace-root", workspace, "--stdin", "--format", "xml");
        Assert.AreEqual(0, firstExit);

        // 2. Supersede design decision (rev 5 -> 6)
        var secondXml = @"<?xml version=""1.0"" encoding=""utf-8""?>
<iteration-confirmation
  id=""20260825T110000Z-confirm-design-supersede-cli""
  iteration=""20260823-xpath-core""
  action=""accept-design-change""
  expected_spec_revision=""5""
  expected_tasks_revision=""9""
  actor=""architect""
  decided_at=""2026-08-25T11:00:00Z"">
  <summary>Superseding design decision.</summary>
  <design>
    <decision target=""20260825-dec-cli"" decision=""superseded""/>
  </design>
</iteration-confirmation>";

        var (secondExit, _, _) = RunCliWithStdin(secondXml, "iteration", "confirm", "--workspace-root", workspace, "--stdin", "--format", "xml");
        Assert.AreEqual(0, secondExit);

        // 3. Replay first confirmation ID with expected_spec_revision=6 -> exit 4
        var replayXml = firstXml.Replace("expected_spec_revision=\"4\"", "expected_spec_revision=\"6\"");
        var (replayExit, _, replayErr) = RunCliWithStdin(replayXml, "iteration", "confirm", "--workspace-root", workspace, "--stdin", "--format", "xml");
        Assert.AreEqual(4, replayExit);
        Assert.IsTrue(replayErr.Contains(DiagnosticCodes.IdempotencyConflict, StringComparison.Ordinal));
    }

    [TestMethod]
    public void IterationConfirm_NonProposedNewDesign_ReturnsExitCode2WithDiagnostics()
    {
        // Regression test for reproduction 2 via CLI
        var workspace = CreateWorkspaceCopy();

        var invalidXml = @"<?xml version=""1.0"" encoding=""utf-8""?>
<iteration-confirmation
  id=""20260825T100000Z-confirm-design-invalid""
  iteration=""20260823-xpath-core""
  action=""accept-design-change""
  expected_spec_revision=""4""
  expected_tasks_revision=""9""
  actor=""architect""
  decided_at=""2026-08-25T10:00:00Z"">
  <summary>Invalid embedded status in new_design_decision.</summary>
  <new_design_decision id=""20260825-dec-cli"" status=""accepted"">
    <index>
      <summary>CLI design decision.</summary>
      <term key=""kind"" value=""decision""/>
    </index>
    <rationale>Initial CLI rationale.</rationale>
  </new_design_decision>
  <design>
    <decision target=""20260825-dec-cli"" decision=""accepted""/>
  </design>
</iteration-confirmation>";

        var (exitCode, _, stderr) = RunCliWithStdin(invalidXml, "iteration", "confirm", "--workspace-root", workspace, "--stdin", "--format", "xml");
        Assert.AreEqual(2, exitCode);
        Assert.IsTrue(stderr.Contains(DiagnosticCodes.InvalidArgument, StringComparison.Ordinal));
    }

    [TestMethod]
    public void IterationList_XmlFormat_ReturnsExitCode0AndEmitsValidXml()
    {
        var workspace = CreateWorkspaceCopy();

        var (exitCode, stdout, stderr) = RunCli(
            "iteration", "list",
            "--workspace-root", workspace,
            "--format", "xml");

        Assert.AreEqual(0, exitCode, $"Stderr: {stderr}");
        Assert.IsTrue(stdout.Contains("<iterations", StringComparison.Ordinal));
        Assert.IsTrue(stdout.Contains("id=\"20260823-xpath-core\"", StringComparison.Ordinal));
        Assert.IsTrue(stdout.Contains("status=\"active\"", StringComparison.Ordinal));
        Assert.IsTrue(stdout.Contains("kind=\"feature\"", StringComparison.Ordinal));
        Assert.IsTrue(stdout.Contains("created_at=\"", StringComparison.Ordinal));
        Assert.IsTrue(stdout.Contains("<index>", StringComparison.Ordinal));
    }

    [TestMethod]
    public void IterationList_HumanFormat_ReturnsHumanOutputWithExitCode0()
    {
        var workspace = CreateWorkspaceCopy();

        var (exitCode, stdout, stderr) = RunCli(
            "iteration", "list",
            "--workspace-root", workspace,
            "--format", "human");

        Assert.AreEqual(0, exitCode, $"Stderr: {stderr}");
        Assert.IsTrue(stdout.Contains("Iterations in workspace:", StringComparison.Ordinal));
        Assert.IsTrue(stdout.Contains("20260823-xpath-core", StringComparison.Ordinal));
        Assert.IsTrue(stdout.Contains("created:", StringComparison.Ordinal));
    }

    [TestMethod]
    public void IterationCreate_TimestampIdGrammar_Cli_Succeeds()
    {
        var workspace = CreateWorkspaceCopy();

        var (exitCode, stdout, stderr) = RunCli(
            "iteration", "create",
            "--id", "20260825T143000Z-cli-ts",
            "--kind", "feature",
            "--workspace-root", workspace,
            "--format", "xml");

        Assert.AreEqual(0, exitCode, $"Stderr: {stderr}");
        Assert.IsTrue(stdout.Contains("<mutation command=\"iteration create\"", StringComparison.Ordinal));
        Assert.IsTrue(stdout.Contains("20260825T143000Z-cli-ts/spec.xml", StringComparison.Ordinal));
    }

    [TestMethod]
    public void IterationCreate_InvalidNearMissIds_Cli_FailsWithExitCode2()
    {
        var workspace = CreateWorkspaceCopy();

        var invalidIds = new[]
        {
            "20260825T14300Z-short",
            "20260825t143000z-lower",
            "20260825T143000Z-UPPER",
            "20260825-slug-",
            "-leading-dash"
        };

        foreach (var badId in invalidIds)
        {
            var (exitCode, stdout, stderr) = RunCli(
                "iteration", "create",
                "--id", badId,
                "--kind", "feature",
                "--workspace-root", workspace,
                "--format", "xml");

            Assert.AreEqual(2, exitCode, $"ID '{badId}' should have returned exit code 2");
            Assert.IsTrue(stderr.Contains(DiagnosticCodes.InvalidArgument, StringComparison.Ordinal));
        }
    }

    [TestMethod]
    public void IterationLifecycle_EndToEnd_TimestampId_Cli_Succeeds()
    {
        var workspace = CreateWorkspaceCopy();
        var iterId = "20260825T143000Z-timestamp-cli";

        // 1. iteration create
        var (createExit, createOut, createErr) = RunCli(
            "iteration", "create",
            "--id", iterId,
            "--kind", "feature",
            "--criterion", "CLI timestamp criterion defined.",
            "--workspace-root", workspace,
            "--format", "xml");
        Assert.AreEqual(0, createExit, $"Create stderr: {createErr}");
        Assert.IsTrue(createOut.Contains("<mutation command=\"iteration create\"", StringComparison.Ordinal));

        // 2. validate workspace
        var (valExit, valOut, valErr) = RunCli(
            "validate",
            "--workspace-root", workspace,
            "--format", "xml");
        Assert.AreEqual(0, valExit, $"Validate stderr: {valErr}");
        Assert.IsTrue(valOut.Contains("<validation valid=\"true\"", StringComparison.Ordinal));

        // 3. iteration list
        var (listExit, listOut, listErr) = RunCli(
            "iteration", "list",
            "--workspace-root", workspace,
            "--format", "xml");
        Assert.AreEqual(0, listExit, $"List stderr: {listErr}");
        Assert.IsTrue(listOut.Contains($"id=\"{iterId}\"", StringComparison.Ordinal));

        // 4. iteration readiness
        var (readyExit, readyOut, readyErr) = RunCli(
            "iteration", "readiness",
            "--iteration", iterId,
            "--phase", "activation",
            "--workspace-root", workspace,
            "--format", "xml");
        Assert.AreEqual(0, readyExit, $"Readiness stderr: {readyErr}");
        Assert.IsTrue(readyOut.Contains("<readiness", StringComparison.Ordinal));

        // 5. iteration confirm activate
        var specDoc = XDocument.Load(Path.Combine(workspace, iterId, "spec.xml"));
        var updatedAtStr = specDoc.Root?.Attribute("updated_at")?.Value ?? "2026-08-25T14:35:00Z";

        var confirmXml = $"""
<iteration-confirmation
  id="20260825T143500Z-confirm-activate"
  iteration="{iterId}"
  action="activate"
  expected_spec_revision="1"
  actor="owner"
  decided_at="{updatedAtStr}">
  <summary>Owner activated timestamp iteration via CLI.</summary>
  <requirements>
    <requirement target="20260825T143000Z-req-timestamp-cli" decision="approved"/>
  </requirements>
  <acceptance>
    <criterion target="20260825T143000Z-crit-timestamp-cli" decision="accepted"/>
  </acceptance>
</iteration-confirmation>
""";
        var (confExit, confOut, confErr) = RunCliWithStdin(
            confirmXml,
            "iteration", "confirm",
            "--workspace-root", workspace,
            "--stdin",
            "--format", "xml");
        Assert.AreEqual(0, confExit, $"Confirm stderr: {confErr}");
        Assert.IsTrue(confOut.Contains("<mutation command=\"iteration confirm\"", StringComparison.Ordinal));
    }

    [TestMethod]
    public void IterationList_MalformedCandidateDir_ReturnsExitCode3WithDiagnosticsOnStderr()
    {
        var workspace = CreateWorkspaceCopy();

        // Add a malformed candidate dir
        var malformedDir = Path.Combine(workspace, "20260825-broken");
        Directory.CreateDirectory(malformedDir);
        File.WriteAllText(Path.Combine(malformedDir, "spec.xml"), "<iteration id=\"20260825-wrong-id\" schema_version=\"1.0\" revision=\"1\" kind=\"feature\" status=\"draft\" created_at=\"2026-08-25T00:00:00Z\" updated_at=\"2026-08-25T00:00:00Z\"><index><summary>Test</summary></index><product><objective>Test</objective><deliverables><deliverable id=\"20260825-deliv-test\"><index><summary>Test</summary></index><description>Test</description></deliverable></deliverables><scope><included/><excluded/></scope><requirements><requirement id=\"20260825-req-test\" status=\"proposed\"><index><summary>Test</summary></index><statement>Test</statement><rationale>Test</rationale></requirement></requirements><acceptance><criterion id=\"20260825-crit-test\" decision=\"pending\">Test</criterion></acceptance></product><confirmations/></iteration>");
        File.WriteAllText(Path.Combine(malformedDir, "tasks.xml"), "<tasks id=\"20260825-tasks-test\" iteration=\"20260825-wrong-id\" schema_version=\"1.0\" revision=\"1\"><index><summary>Test</summary></index></tasks>");

        var (exitCode, stdout, stderr) = RunCli(
            "iteration", "list",
            "--workspace-root", workspace,
            "--format", "xml");

        Assert.AreEqual(3, exitCode);
        Assert.IsTrue(stderr.Contains("<diagnostics command=\"iteration list\"", StringComparison.Ordinal));
        Assert.IsTrue(stderr.Contains(DiagnosticCodes.IterationIdMismatch, StringComparison.Ordinal));
    }

    [TestMethod]
    public void FullWorkflow_InitCreateBothValidateListQuery_SucceedsEndToEnd()
    {
        var projDir = Path.Combine(_tempDir, "fresh_project");
        Directory.CreateDirectory(projDir);

        // 1. Workspace init
        var (initExit, initOut, initErr) = RunCli("workspace", "init", "--workspace-root", projDir, "--format", "xml");
        Assert.AreEqual(0, initExit, $"Init stderr: {initErr}");
        Assert.IsTrue(initOut.Contains("initialized=\"true\"", StringComparison.Ordinal));

        // 2. Create feature iteration
        var (featExit, featOut, featErr) = RunCli(
            "iteration", "create",
            "--workspace-root", projDir,
            "--id", "20260823-feature-alpha",
            "--kind", "feature",
            "--format", "xml");
        Assert.AreEqual(0, featExit, $"Feature create stderr: {featErr}");
        Assert.IsTrue(featOut.Contains("<mutation command=\"iteration create\"", StringComparison.Ordinal));
        Assert.IsTrue(featOut.Contains("20260823-feature-alpha/spec.xml", StringComparison.Ordinal));

        // 3. Create research iteration
        var (resExit, resOut, resErr) = RunCli(
            "iteration", "create",
            "--workspace-root", projDir,
            "--id", "20260824-research-beta",
            "--kind", "research",
            "--format", "xml");
        Assert.AreEqual(0, resExit, $"Research create stderr: {resErr}");
        Assert.IsTrue(resOut.Contains("<mutation command=\"iteration create\"", StringComparison.Ordinal));
        Assert.IsTrue(resOut.Contains("20260824-research-beta/spec.xml", StringComparison.Ordinal));

        // 4. Validate whole workspace
        var (valExit, valOut, valErr) = RunCli("validate", "--workspace-root", projDir, "--format", "xml");
        Assert.AreEqual(0, valExit, $"Validate stderr: {valErr}");
        Assert.IsTrue(valOut.Contains("<validation valid=\"true\"", StringComparison.Ordinal));

        // 5. List iterations in date order
        var (listExit, listOut, listErr) = RunCli("iteration", "list", "--workspace-root", projDir, "--format", "xml");
        Assert.AreEqual(0, listExit, $"List stderr: {listErr}");
        Assert.IsTrue(listOut.Contains("id=\"20260823-feature-alpha\"", StringComparison.Ordinal));
        Assert.IsTrue(listOut.Contains("id=\"20260824-research-beta\"", StringComparison.Ordinal));
        var alphaIdx = listOut.IndexOf("20260823-feature-alpha", StringComparison.Ordinal);
        var betaIdx = listOut.IndexOf("20260824-research-beta", StringComparison.Ordinal);
        Assert.IsTrue(alphaIdx < betaIdx, "Iterations must be ordered in ascending normalized date order");

        // 6. Query compact index
        var (queryExit, queryOut, queryErr) = RunCli(
            "query",
            "--workspace-root", projDir,
            "--document", "20260823-feature-alpha/spec.xml",
            "--xpath", "/iteration/index/summary",
            "--format", "xml");
        Assert.AreEqual(0, queryExit, $"Query stderr: {queryErr}");
        Assert.IsTrue(queryOut.Contains("<summary>", StringComparison.Ordinal));

        // 7. Search project for elements with @id
        var (searchExit, searchOut, searchErr) = RunCli(
            "search",
            "--workspace-root", projDir,
            "--scope", "project",
            "--xpath", "//*[@id]",
            "--format", "xml");
        Assert.AreEqual(0, searchExit, $"Search stderr: {searchErr}");
        Assert.IsTrue(searchOut.Contains("20260823-feature-alpha/spec.xml", StringComparison.Ordinal));
        Assert.IsTrue(searchOut.Contains("20260824-research-beta/spec.xml", StringComparison.Ordinal));
    }

    [TestMethod]
    public void IterationCreate_Activate_WithoutCriterion_Cli_FailsAtomically()
    {
        var workspace = CreateWorkspaceCopy();
        var iterId = "20260827-no-crit-cli";

        var (exitCode, stdout, stderr) = RunCli(
            "iteration", "create",
            "--id", iterId,
            "--kind", "feature",
            "--activate",
            "--workspace-root", workspace,
            "--format", "xml");

        Assert.AreEqual(5, exitCode);
        Assert.IsTrue(stderr.Contains(DiagnosticCodes.CriterionUndefined, StringComparison.Ordinal));
        Assert.IsFalse(Directory.Exists(Path.Combine(workspace, iterId)));
    }

    [TestMethod]
    public void IterationCriterion_DefineAndAdd_Cli_Succeeds()
    {
        var workspace = CreateWorkspaceCopy();
        var iterId = "20260827-crit-porcelain";

        // Create draft iteration
        var (cExit, _, cErr) = RunCli(
            "iteration", "create",
            "--id", iterId,
            "--kind", "feature",
            "--workspace-root", workspace);
        Assert.AreEqual(0, cExit, cErr);

        // 1. Define replacement criterion via CLI
        var (defExit, defOut, defErr) = RunCli(
            "iteration", "criterion", "define",
            "--iteration", iterId,
            "--text", "Defined criterion via CLI",
            "--workspace-root", workspace,
            "--format", "xml");
        Assert.AreEqual(0, defExit, $"Define stderr: {defErr}");
        Assert.IsTrue(defOut.Contains("<mutation command=\"iteration criterion\"", StringComparison.Ordinal));

        var specDoc1 = XDocument.Load(Path.Combine(workspace, iterId, "spec.xml"));
        Assert.AreEqual("2", specDoc1.Root?.Attribute("revision")?.Value);
        var crit1 = specDoc1.Descendants("criterion").ToList();
        Assert.AreEqual(1, crit1.Count);
        Assert.AreEqual("Defined criterion via CLI", crit1[0].Value);

        // 2. Add new criterion via CLI
        var (addExit, addOut, addErr) = RunCli(
            "iteration", "criterion", "add",
            "--iteration", iterId,
            "--text", "Added criterion via CLI",
            "--workspace-root", workspace,
            "--format", "xml");
        Assert.AreEqual(0, addExit, $"Add stderr: {addErr}");
        Assert.IsTrue(addOut.Contains("<mutation command=\"iteration criterion\"", StringComparison.Ordinal));

        var specDoc2 = XDocument.Load(Path.Combine(workspace, iterId, "spec.xml"));
        Assert.AreEqual("3", specDoc2.Root?.Attribute("revision")?.Value);
        var crit2 = specDoc2.Descendants("criterion").ToList();
        Assert.AreEqual(2, crit2.Count);
        Assert.AreEqual("20260827-crit-crit-porcelain-2", crit2[1].Attribute("id")?.Value);
        Assert.AreEqual("Added criterion via CLI", crit2[1].Value);

        // 3. Set existing criterion via CLI
        var (setExit, setOut, setErr) = RunCli(
            "iteration", "criterion", "set",
            "--iteration", iterId,
            "--criterion-id", "20260827-crit-crit-porcelain",
            "--text", "Updated criterion via set CLI",
            "--workspace-root", workspace,
            "--format", "xml");
        Assert.AreEqual(0, setExit, $"Set stderr: {setErr}");
        Assert.IsTrue(setOut.Contains("<mutation command=\"iteration criterion\"", StringComparison.Ordinal));

        var specDoc3 = XDocument.Load(Path.Combine(workspace, iterId, "spec.xml"));
        Assert.AreEqual("4", specDoc3.Root?.Attribute("revision")?.Value);
        Assert.AreEqual("Updated criterion via set CLI", specDoc3.Descendants("criterion").First().Value);
    }

    [TestMethod]
    public void IterationCriterion_RepeatableCriterionOption_TokensDoNotConsumeFollowingOptions()
    {
        var workspace = CreateWorkspaceCopy();
        var iterId = "20260827-multi-token";

        var (exitCode, stdout, stderr) = RunCli(
            "iteration", "create",
            "--id", iterId,
            "--kind", "feature",
            "--criterion", "First criteria token.",
            "--criterion", "Second criteria token.",
            "--activate",
            "--workspace-root", workspace,
            "--format", "xml");

        Assert.AreEqual(0, exitCode, $"Create stderr: {stderr}");
        Assert.IsTrue(stdout.Contains("<mutation command=\"iteration create\"", StringComparison.Ordinal));

        var specDoc = XDocument.Load(Path.Combine(workspace, iterId, "spec.xml"));
        Assert.AreEqual("active", specDoc.Root?.Attribute("status")?.Value);
        var criteria = specDoc.Descendants("criterion").ToList();
        Assert.AreEqual(2, criteria.Count);
        Assert.AreEqual("20260827-crit-multi-token-1", criteria[0].Attribute("id")?.Value);
        Assert.AreEqual("First criteria token.", criteria[0].Value);
        Assert.AreEqual("20260827-crit-multi-token-2", criteria[1].Attribute("id")?.Value);
        Assert.AreEqual("Second criteria token.", criteria[1].Value);
    }
}
