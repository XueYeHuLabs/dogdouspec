using System.Globalization;
using System.Xml.Linq;
using DogdouSpec.Cli;
using DogdouSpec.Core.Diagnostics;

namespace DogdouSpec.Cli.Tests;

[TestClass]
public sealed class TaskChangeWorkflowCliTests
{
    private static string RepoRoot = null!;
    private string _tempDir = null!;

    [ClassInitialize]
    public static void ClassInit(TestContext context)
    {
        foreach (var startPath in new[] { Environment.CurrentDirectory, AppDomain.CurrentDomain.BaseDirectory })
        {
            var current = new DirectoryInfo(startPath);
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

            if (!string.IsNullOrEmpty(RepoRoot))
            {
                break;
            }
        }

        Assert.IsNotNull(RepoRoot, "Repository root could not be located.");
    }

    [TestInitialize]
    public void SetUp()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "DogdouSpec_WorkflowCliTests_" + Guid.NewGuid().ToString("N"));
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
            catch { }
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
    public void TaskAdd_Cli_HappyPath_Succeeds()
    {
        CreateWorkspaceCopy();
        var iterId = "20260824-cli-feature";
        var (cCode, _, cErr) = RunCli("iteration", "create", "--id", iterId, "--kind", "feature", "--workspace-root", _tempDir);
        Assert.AreEqual(0, cCode, $"Create iter failed: {cErr}");

        var addXml = $"""
<task-add
  id="20260824T100000Z-taskadd-cli-01"
  actor="codex"
  occurred_at="2026-08-24T10:00:00Z">
  <task
    id="20260824-task-cli-added"
    status="pending"
    created_at="2026-08-24T10:00:00Z"
    updated_at="2026-08-24T10:00:00Z">
    <index>
      <summary>CLI Added Task Summary.</summary>
    </index>
    <title>CLI Added Task</title>
    <objective>CLI added objective.</objective>
    <rationale>Testing CLI add.</rationale>
    <scope>
      <repository path="src/cli.cs"/>
    </scope>
    <origin>
      <ref scope="iteration" target="20260824-req-cli-feature" relation="implements"/>
    </origin>
    <constraints/>
    <acceptance>
      <criterion id="20260824-crit-cli-added" status="pending">
        Works from CLI.
      </criterion>
    </acceptance>
    <context>
      <summary>Initial context.</summary>
    </context>
    <records/>
  </task>
</task-add>
""";

        var (exitCode, stdout, stderr) = RunCliWithStdin(
            addXml,
            "task", "add",
            "--iteration", iterId,
            "--expected-revision", "1",
            "--stdin",
            "--workspace-root", _tempDir,
            "--format", "xml");

        Assert.AreEqual(0, exitCode, $"Stderr: {stderr}");
        Assert.IsTrue(stdout.Contains("command=\"task add\""));
    }

    [TestMethod]
    public void TaskQuick_Cli_PendingDryRunAndAtomicStart_ArePubliclyObservable()
    {
        CreateWorkspaceCopy();
        var iterId = "20260824-cli-quick";
        var (createCode, _, createError) = RunCli("iteration", "create", "--id", iterId, "--kind", "feature", "--criterion", "Quick task criteria defined.", "--workspace-root", _tempDir);
        Assert.AreEqual(0, createCode, createError);

        var (dryCode, dryOut, dryErr) = RunCli("task", "quick", "--title", "Preview quick", "--scope", "src/**", "--done-when", "preview works", "--why", "test preview", "--iteration", iterId, "--dry-run", "--id", "20260824-task-cli-quick-preview", "--operation-id", "20260824T121000Z-quick-preview", "--workspace-root", _tempDir, "--format", "xml");
        Assert.AreEqual(0, dryCode, dryErr);
        Assert.IsTrue(dryOut.Contains("relation=\"supports\""));
        var tasksPath = Path.Combine(_tempDir, ".dogdouspec", iterId, "tasks.xml");
        Assert.AreEqual("1", XDocument.Load(tasksPath).Root!.Attribute("revision")!.Value);
        var (humanDryCode, humanDryOut, humanDryErr) = RunCli("task", "quick", "--title", "Preview quick", "--scope", "src/**", "--done-when", "preview works", "--why", "test preview", "--iteration", iterId, "--dry-run", "--id", "20260824-task-cli-quick-preview", "--operation-id", "20260824T121000Z-quick-preview", "--workspace-root", _tempDir, "--format", "human");
        Assert.AreEqual(0, humanDryCode, humanDryErr);
        Assert.IsTrue(humanDryOut.StartsWith("Quick task preview:", StringComparison.Ordinal));
        Assert.IsFalse(humanDryOut.Contains("<task-add", StringComparison.Ordinal));
        var (invalidIterationCode, _, invalidIterationErr) = RunCli("task", "quick", "--title", "Bad iteration", "--scope", "src/**", "--done-when", "no", "--why", "no", "--iteration", "invalid-iteration-id", "--dry-run", "--workspace-root", _tempDir, "--format", "xml");
        Assert.AreEqual(2, invalidIterationCode);
        Assert.IsTrue(invalidIterationErr.Contains(DiagnosticCodes.InvalidArgument) || invalidIterationErr.Contains(DiagnosticCodes.InvalidIdGrammar));

        var specDoc = XDocument.Load(Path.Combine(_tempDir, ".dogdouspec", iterId, "spec.xml"));
        var updatedAtStr = specDoc.Root?.Attribute("updated_at")?.Value ?? "2026-08-24T12:09:59Z";
        var activation = $"""<iteration-confirmation id="20260824T120959Z-confirm-quick" iteration="{iterId}" action="activate" expected_spec_revision="1" expected_tasks_revision="1" actor="owner" decided_at="{updatedAtStr}"><summary>Activate quick test.</summary><requirements><requirement target="20260824-req-cli-quick" decision="approved"/></requirements></iteration-confirmation>""";
        var (activateCode, _, activateErr) = RunCliWithStdin(activation, "iteration", "confirm", "--stdin", "--workspace-root", _tempDir, "--format", "xml");
        Assert.AreEqual(0, activateCode, activateErr);
        var args = new[] { "task", "quick", "--title", "Start quick", "--scope", "src/**", "--done-when", "started work is tracked", "--why", "test atomic start", "--iteration", iterId, "--start", "--id", "20260824-task-cli-quick-start", "--operation-id", "20260824T121001Z-quick-start", "--workspace-root", _tempDir, "--format", "xml" };
        var (startCode, startOut, startErr) = RunCli(args);
        Assert.AreEqual(0, startCode, startErr);
        Assert.IsTrue(startOut.Contains("command=\"task quick\""));
        var started = XDocument.Load(tasksPath).Root!.Elements("task").Single();
        Assert.AreEqual("in-progress", started.Attribute("status")!.Value);
        Assert.AreEqual(started.Attribute("created_at")!.Value, started.Attribute("started_at")!.Value);
        Assert.IsTrue(started.Element("records")!.Elements("record").Any(r => r.Attribute("kind")!.Value == "start"));

        var (pendingCode, pendingOut, pendingErr) = RunCli("task", "quick", "--title", "Pending quick", "--scope", "src/**", "--done-when", "pending work is tracked", "--why", "test pending", "--iteration", iterId, "--expected-revision", "2", "--id", "20260824-task-cli-quick-pending", "--operation-id", "20260824T121002Z-quick-pending", "--workspace-root", _tempDir, "--format", "xml");
        Assert.AreEqual(0, pendingCode, pendingErr);
        Assert.IsTrue(pendingOut.Contains("command=\"task quick\""));
        var task = XDocument.Load(tasksPath).Root!.Elements("task").Single(t => t.Attribute("id")!.Value == "20260824-task-cli-quick-pending");
        Assert.AreEqual("pending", task.Attribute("status")!.Value);
        Assert.AreEqual("supports", task.Element("origin")!.Element("ref")!.Attribute("relation")!.Value);

        var (replayCode, replayOut, replayErr) = RunCli("task", "quick", "--title", "Pending quick", "--scope", "src/**", "--done-when", "pending work is tracked", "--why", "test pending", "--iteration", iterId, "--expected-revision", "2", "--id", "20260824-task-cli-quick-pending", "--operation-id", "20260824T121002Z-quick-pending", "--workspace-root", _tempDir, "--format", "xml");
        Assert.AreEqual(0, replayCode, replayErr);
        Assert.IsTrue(replayOut.Contains("already_applied=\"true\""));

        var (explicitCode, explicitOut, explicitErr) = RunCli("task", "quick", "--title", "Requirement quick", "--scope", "src/**", "--done-when", "requirement work is tracked", "--why", "exercise confirmation-bearing write", "--origin", "20260824-req-cli-quick", "--iteration", iterId, "--expected-revision", "3", "--start", "--id", "20260824-task-cli-quick-requirement", "--operation-id", "20260824T121003Z-quick-requirement", "--workspace-root", _tempDir, "--format", "xml");
        Assert.AreEqual(0, explicitCode, explicitErr);
        Assert.IsTrue(explicitOut.Contains("command=\"task quick\""));
        var explicitTask = XDocument.Load(tasksPath).Root!.Elements("task").Single(t => t.Attribute("id")!.Value == "20260824-task-cli-quick-requirement");
        Assert.AreEqual("in-progress", explicitTask.Attribute("status")!.Value);
        Assert.AreEqual("4", XDocument.Load(tasksPath).Root!.Attribute("revision")!.Value);
    }

    [TestMethod]
    public void RequirementPropose_Cli_HappyPath_Succeeds()
    {
        CreateWorkspaceCopy();
        var iterId = "20260824-cli-feature-2";
        var (cCode, _, cErr) = RunCli("iteration", "create", "--id", iterId, "--kind", "feature", "--workspace-root", _tempDir);
        Assert.AreEqual(0, cCode, $"Create iter failed: {cErr}");

        var specDoc = XDocument.Load(Path.Combine(_tempDir, ".dogdouspec", iterId, "spec.xml"));
        var updatedAtStr = specDoc.Root?.Attribute("updated_at")?.Value ?? "2026-08-24T10:10:00Z";

        var propXml = $"""
<requirement-propose
  id="20260824T101000Z-reqprop-cli-01"
  actor="codex"
  occurred_at="{updatedAtStr}">
  <requirement id="20260824-req-cli-proposed" status="proposed">
    <index>
      <summary>CLI Proposed Requirement Summary.</summary>
    </index>
    <statement>Requirement proposed via CLI.</statement>
    <rationale>Testing CLI command.</rationale>
  </requirement>
</requirement-propose>
""";

        var (exitCode, stdout, stderr) = RunCliWithStdin(
            propXml,
            "requirement", "propose",
            "--iteration", iterId,
            "--expected-revision", "1",
            "--stdin",
            "--workspace-root", _tempDir,
            "--format", "xml");

        Assert.AreEqual(0, exitCode, $"Stderr: {stderr}");
        Assert.IsTrue(stdout.Contains("command=\"requirement propose\""));
    }

    [TestMethod]
    public void ChangeProposeAndApply_Cli_EndToEndLifecycle_Succeeds()
    {
        CreateWorkspaceCopy();
        var iterId = "20260824-cli-e2e-change";
        var (cCode, _, cErr) = RunCli("iteration", "create", "--id", iterId, "--kind", "feature", "--criterion", "E2E change criteria defined.", "--workspace-root", _tempDir);
        Assert.AreEqual(0, cCode, $"Create iter failed: {cErr}");

        var specDoc = XDocument.Load(Path.Combine(_tempDir, ".dogdouspec", iterId, "spec.xml"));
        var t0 = DateTime.Parse(specDoc.Root?.Attribute("updated_at")?.Value ?? "2026-08-24T10:19:50Z", CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal);
        var t0Iso = t0.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
        var t1Iso = t0.AddSeconds(10).ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
        var t2Iso = t0.AddSeconds(20).ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
        var t3Iso = t0.AddSeconds(30).ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
        var t4Iso = t0.AddSeconds(40).ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
        var t5Iso = t0.AddSeconds(50).ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
        var t6Iso = t0.AddSeconds(60).ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
        var t7Iso = t0.AddSeconds(70).ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
        var t8Iso = t0.AddSeconds(80).ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);

        var activateXml = $"""
<iteration-confirmation id="20260824T101950Z-conf-activate-e2e" iteration="{iterId}" action="activate" expected_spec_revision="1" expected_tasks_revision="1" actor="owner" decided_at="{t0Iso}">
  <summary>Owner activated the baseline product scope.</summary>
  <requirements><requirement target="20260824-req-cli-e2e-change" decision="approved"/></requirements>
  <acceptance><criterion target="20260824-crit-cli-e2e-change" decision="accepted"/></acceptance>
</iteration-confirmation>
""";
        var (activateCode, _, activateErr) = RunCliWithStdin(activateXml, "iteration", "confirm", "--stdin", "--workspace-root", _tempDir, "--format", "xml");
        Assert.AreEqual(0, activateCode, $"Owner activation failed: {activateErr}");

        // 1. Add base task
        var addXml = $"""
<task-add
  id="20260824T102000Z-taskadd-e2e"
  actor="codex"
  occurred_at="{t1Iso}">
  <task
    id="20260824-task-to-replan"
    status="pending"
    created_at="{t1Iso}"
    updated_at="{t1Iso}">
    <index>
      <summary>Task To Replan Summary.</summary>
    </index>
    <title>Task To Replan</title>
    <objective>Initial objective.</objective>
    <rationale>Initial task.</rationale>
    <scope>
      <repository path="src/replan.cs"/>
    </scope>
    <origin>
      <ref scope="iteration" target="20260824-req-cli-e2e-change" relation="implements"/>
    </origin>
    <constraints/>
    <acceptance>
      <criterion id="20260824-crit-replan" status="pending">
        Initial criterion.
      </criterion>
    </acceptance>
    <context>
      <summary>Initial context.</summary>
    </context>
    <records/>
  </task>
</task-add>
""";
        var (addCode, _, addErr) = RunCliWithStdin(
            addXml,
            "task", "add",
            "--iteration", iterId,
            "--expected-revision", "1",
            "--stdin",
            "--workspace-root", _tempDir);
        Assert.AreEqual(0, addCode, $"Task add failed: {addErr}");

        var startXml = $"""
<task-update id="20260824T102030Z-taskstart-e2e" transition="start" actor="codex" occurred_at="{t2Iso}">
  <records><record id="20260824T102030Z-rec-start-e2e" kind="start" status="informational" created_at="{t2Iso}" actor="codex"><summary>Started implementation.</summary></record></records>
</task-update>
""";
        var (startCode, _, startErr) = RunCliWithStdin(startXml, "task", "update", "--iteration", iterId, "--task", "20260824-task-to-replan", "--expected-revision", "2", "--stdin", "--workspace-root", _tempDir, "--format", "xml");
        Assert.AreEqual(0, startCode, $"Task start failed: {startErr}");

        // 2. Change Propose
        var cpXml = $"""
<change-propose
  id="20260824T102100Z-changeprop-e2e"
  actor="codex"
  occurred_at="{t3Iso}">
  <summary>Change proposal discovered in CLI test.</summary>
  <finding_record task="20260824-task-to-replan">
    <record
      id="20260824T102100Z-rec-replan-finding"
      kind="finding"
      status="active"
      created_at="{t3Iso}"
      actor="codex">
      <summary>Need architecture split.</summary>
    </record>
  </finding_record>
  <freeze_tasks>
    <task target="20260824-task-to-replan" reason="Blocked for replanning."/>
  </freeze_tasks>
  <proposed_requirements>
    <requirement id="20260824-req-split-arch" status="proposed">
      <index>
        <summary>Split Architecture Requirement Summary.</summary>
      </index>
      <statement>The architecture must be modular.</statement>
      <rationale>Discovered mid-iteration.</rationale>
      <sources><ref scope="document" target="20260824-req-cli-e2e-change" relation="supersedes"/></sources>
    </requirement>
  </proposed_requirements>
</change-propose>
""";

        var (cpCode, cpOut, cpErr) = RunCliWithStdin(
            cpXml,
            "change", "propose",
            "--iteration", iterId,
            "--expected-spec-revision", "2",
            "--expected-tasks-revision", "3",
            "--stdin",
            "--workspace-root", _tempDir,
            "--format", "xml");
        Assert.AreEqual(0, cpCode, $"Change propose failed: {cpErr}");
        Assert.IsTrue(cpOut.Contains("command=\"change propose\""));

        var (cpReplayCode, cpReplayOut, cpReplayErr) = RunCliWithStdin(cpXml, "change", "propose", "--iteration", iterId, "--expected-spec-revision", "2", "--expected-tasks-revision", "3", "--stdin", "--workspace-root", _tempDir, "--format", "xml");
        Assert.AreEqual(0, cpReplayCode, $"Immediate change-propose replay failed: {cpReplayErr}");
        Assert.IsTrue(cpReplayOut.Contains("already_applied=\"true\""));
        var (cpDivergentCode, _, _) = RunCliWithStdin(cpXml.Replace("Need architecture split.", "A different finding payload."), "change", "propose", "--iteration", iterId, "--expected-spec-revision", "2", "--expected-tasks-revision", "3", "--stdin", "--workspace-root", _tempDir, "--format", "xml");
        Assert.AreEqual(4, cpDivergentCode, "Divergent change-propose replay must fail.");
        var (cpReasonCode, _, _) = RunCliWithStdin(cpXml.Replace("Blocked for replanning.", "Different freeze reason."), "change", "propose", "--iteration", iterId, "--expected-spec-revision", "2", "--expected-tasks-revision", "3", "--stdin", "--workspace-root", _tempDir, "--format", "xml");
        Assert.AreEqual(4, cpReasonCode, "Changed freeze reason must conflict with the durable request fingerprint.");
        var omittedRequirements = XDocument.Parse(cpXml);
        omittedRequirements.Root!.Element("proposed_requirements")!.Remove();
        var (cpOmittedCode, _, _) = RunCliWithStdin(omittedRequirements.ToString(), "change", "propose", "--iteration", iterId, "--expected-spec-revision", "2", "--expected-tasks-revision", "3", "--stdin", "--workspace-root", _tempDir, "--format", "xml");
        Assert.AreEqual(4, cpOmittedCode, "Omitting previously proposed requirements must conflict with the durable request fingerprint.");
        var proposalTasks = XDocument.Load(Path.Combine(_tempDir, ".dogdouspec", iterId, "tasks.xml"));
        Assert.IsTrue(proposalTasks.Descendants("record").Any(r => (string?)r.Attribute("id") == "20260824T102100Z-changeprop-e2e-freeze-1" && r.Element("impact")?.Value == "Blocked for replanning."), "Freeze reason must remain queryable as a task record.");

        // 3. Execution is frozen before the owner confirms replanning. The
        // failed public command must leave tasks.xml byte-identical.
        var tasksPath = Path.Combine(_tempDir, ".dogdouspec", iterId, "tasks.xml");
        var beforeFrozenAttempt = File.ReadAllBytes(tasksPath);
        var frozenXml = $"""
<task-update id="20260824T102120Z-taskstart-frozen" transition="start" actor="codex" occurred_at="{t4Iso}">
  <records><record id="20260824T102120Z-rec-start-frozen" kind="discussion" status="informational" created_at="{t4Iso}" actor="codex"><summary>Attempt to start a change-frozen task.</summary></record></records>
</task-update>
""";
        var (frozenCode, _, frozenErr) = RunCliWithStdin(frozenXml, "task", "update", "--iteration", iterId, "--task", "20260824-task-to-replan", "--expected-revision", "4", "--stdin", "--workspace-root", _tempDir, "--format", "xml");
        Assert.AreEqual(4, frozenCode, $"Frozen transition should fail without mutation: {frozenErr}");
        CollectionAssert.AreEqual(beforeFrozenAttempt, File.ReadAllBytes(tasksPath));

        var replanXml = $"""
<iteration-confirmation id="20260824T102130Z-conf-replan" iteration="{iterId}" action="replan" expected_spec_revision="3" expected_tasks_revision="4" actor="owner" decided_at="{t5Iso}">
  <summary>Owner accepted requirement replacement and confirmed replanning.</summary>
  <requirements>
    <requirement target="20260824-req-cli-e2e-change" decision="superseded"/>
    <requirement target="20260824-req-split-arch" decision="approved"/>
  </requirements>
</iteration-confirmation>
""";
        var (replanCode, _, replanErr) = RunCliWithStdin(replanXml, "iteration", "confirm", "--stdin", "--workspace-root", _tempDir, "--format", "xml");
        Assert.AreEqual(0, replanCode, $"Owner replanning confirmation failed: {replanErr}");

        // 4. Change Apply during replanning
        var caXml = $"""
<change-apply
  id="20260824T102200Z-changeapply-e2e"
  actor="codex"
  occurred_at="{t6Iso}">
  <summary>Applying change adjustments.</summary>
  <resolve_findings>
    <finding task="20260824-task-to-replan" target="20260824T102100Z-rec-replan-finding"/>
  </resolve_findings>
  <task_dispositions>
    <task target="20260824-task-to-replan" transition="supersede" rationale="Superseded by modular successor.">
      <record
        id="20260824T102200Z-rec-disp"
        kind="discussion"
        status="informational"
        created_at="{t6Iso}"
        actor="codex">
        <summary>Task superseded during replanning.</summary>
      </record>
    </task>
  </task_dispositions>
  <add_tasks>
    <task
      id="20260824-task-modular-successor"
      status="pending"
      created_at="{t6Iso}"
      updated_at="{t6Iso}">
      <index>
        <summary>Modular Successor Task Summary.</summary>
      </index>
      <title>Modular Successor Task</title>
      <objective>Modular successor objective.</objective>
      <rationale>Implements new architecture.</rationale>
      <scope>
        <repository path="src/modular.cs"/>
      </scope>
      <origin>
        <ref scope="iteration" target="20260824-req-split-arch" relation="implements"/>
      </origin>
      <constraints/>
      <acceptance>
        <criterion id="20260824-crit-modular" status="pending">
          Modular architecture tests pass.
        </criterion>
      </acceptance>
      <context>
        <summary>Context for modular successor.</summary>
      </context>
      <records/>
    </task>
  </add_tasks>
</change-apply>
""";

        var (caCode, caOut, caErr) = RunCliWithStdin(
            caXml,
            "change", "apply",
            "--iteration", iterId,
            "--expected-spec-revision", "4",
            "--expected-tasks-revision", "4",
            "--stdin",
            "--workspace-root", _tempDir,
            "--format", "xml");

        Assert.AreEqual(0, caCode, $"Change apply failed: {caErr}");
        Assert.IsTrue(caOut.Contains("command=\"change apply\""));

        var (caReplayCode, caReplayOut, caReplayErr) = RunCliWithStdin(caXml, "change", "apply", "--iteration", iterId, "--expected-spec-revision", "4", "--expected-tasks-revision", "4", "--stdin", "--workspace-root", _tempDir, "--format", "xml");
        Assert.AreEqual(0, caReplayCode, $"Immediate change-apply replay failed: {caReplayErr}");
        Assert.IsTrue(caReplayOut.Contains("already_applied=\"true\""));
        var (caDivergentCode, _, _) = RunCliWithStdin(caXml.Replace("Applying change adjustments.", "Different change application."), "change", "apply", "--iteration", iterId, "--expected-spec-revision", "4", "--expected-tasks-revision", "4", "--stdin", "--workspace-root", _tempDir, "--format", "xml");
        Assert.AreEqual(4, caDivergentCode, "Divergent change-apply replay must fail.");
        var (caRationaleCode, _, _) = RunCliWithStdin(caXml.Replace("Superseded by modular successor.", "Different disposition rationale."), "change", "apply", "--iteration", iterId, "--expected-spec-revision", "4", "--expected-tasks-revision", "4", "--stdin", "--workspace-root", _tempDir, "--format", "xml");
        Assert.AreEqual(4, caRationaleCode, "Changed disposition rationale must conflict with the durable request fingerprint.");

        var (readinessCode, readinessOut, readinessErr) = RunCli(
            "iteration", "readiness",
            "--iteration", iterId,
            "--phase", "activation",
            "--workspace-root", _tempDir,
            "--format", "xml");
        Assert.AreEqual(0, readinessCode, $"Replanning readiness failed: {readinessErr}");
        Assert.IsTrue(readinessOut.Contains("technically_ready=\"true\"", StringComparison.Ordinal));
        Assert.IsTrue(readinessOut.Contains("<required_action action=\"continue\"", StringComparison.Ordinal));

        var continueXml = $"""
<iteration-confirmation id="20260824T102230Z-conf-continue" iteration="{iterId}" action="continue" expected_spec_revision="4" expected_tasks_revision="5" actor="owner" decided_at="{t7Iso}">
  <summary>Owner reviewed replacement coverage and resumed execution.</summary>
</iteration-confirmation>
""";
        var (continueCode, _, continueErr) = RunCliWithStdin(continueXml, "iteration", "confirm", "--stdin", "--workspace-root", _tempDir, "--format", "xml");
        Assert.AreEqual(0, continueCode, $"Owner continuation confirmation failed: {continueErr}");

        var successorStartXml = $"""
<task-update id="20260824T102240Z-taskstart-successor" transition="start" actor="codex" occurred_at="{t8Iso}">
  <records><record id="20260824T102240Z-rec-start-successor" kind="start" status="informational" created_at="{t8Iso}" actor="codex"><summary>Started successor implementation.</summary></record></records>
</task-update>
""";
        var (successorCode, successorOut, successorErr) = RunCliWithStdin(successorStartXml, "task", "update", "--iteration", iterId, "--task", "20260824-task-modular-successor", "--expected-revision", "5", "--stdin", "--workspace-root", _tempDir, "--format", "xml");
        Assert.AreEqual(0, successorCode, $"Successor task start failed: {successorErr}");
        Assert.IsTrue(successorOut.Contains("command=\"task update\""));
    }
}
