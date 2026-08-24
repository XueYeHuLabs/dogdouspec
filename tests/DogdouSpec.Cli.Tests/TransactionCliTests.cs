using System.Xml.Linq;
using DogdouSpec.Cli;
using DogdouSpec.Core.Diagnostics;

namespace DogdouSpec.Cli.Tests;

/// <summary>
/// End-to-end CLI contract tests for 'dogdouspec transaction apply'.
/// Exercises the full CLI stack from argument parsing through output formatting.
/// </summary>
[TestClass]
public sealed class TransactionCliTests
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
        _tempDir = Path.Combine(Path.GetTempPath(), "DogdouSpec_TxCliTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    [TestCleanup]
    public void TearDown()
    {
        if (Directory.Exists(_tempDir))
        {
            try { Directory.Delete(_tempDir, true); }
            catch { /* Ignore cleanup errors */ }
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
            File.Copy(file, Path.Combine(destinationDir, Path.GetFileName(file)), true);
        foreach (var dir in Directory.GetDirectories(sourceDir))
            CopyDirectory(dir, Path.Combine(destinationDir, Path.GetFileName(dir)));
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

    // ─── Help / registration tests ──────────────────────────────────────────────

    [TestMethod]
    public void Help_TransactionApply_ShowsInHelpOutput()
    {
        var (exitCode, stdout, stderr) = RunCli("transaction", "apply", "--help");
        var output = stdout + stderr;
        Assert.IsTrue(output.Contains("transaction apply", StringComparison.OrdinalIgnoreCase) ||
                      output.Contains("apply", StringComparison.OrdinalIgnoreCase),
            "Help output must mention 'apply'.");
        Assert.IsTrue(output.Contains("mutating", StringComparison.OrdinalIgnoreCase),
            "Help output for 'transaction apply' must indicate it is mutating.");
    }

    [TestMethod]
    public void Help_Transaction_ShowsSubcommand()
    {
        var (exitCode, stdout, stderr) = RunCli("transaction", "--help");
        var output = stdout + stderr;
        Assert.IsTrue(output.Contains("apply", StringComparison.OrdinalIgnoreCase),
            "Transaction help must list 'apply' subcommand.");
    }

    // ─── Argument validation (exit 2) ───────────────────────────────────────────

    [TestMethod]
    public void MissingStdinAndFile_ReturnsExitCode2()
    {
        var workspace = CreateWorkspaceCopy();
        var (exitCode, stdout, stderr) = RunCli(
            "transaction", "apply",
            "--workspace-root", workspace,
            "--format", "xml");

        Assert.AreEqual(2, exitCode);
        Assert.IsTrue(stderr.Contains("diagnostics", StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public void BothStdinAndFile_ReturnsExitCode2()
    {
        var workspace = CreateWorkspaceCopy();
        var txFile = Path.Combine(_tempDir, "tx.xml");
        File.WriteAllText(txFile, "<transaction operation_id=\"20260823T000000Z-test\" />");

        var (exitCode, stdout, stderr) = RunCli(
            "transaction", "apply",
            "--workspace-root", workspace,
            "--stdin",
            "--file", txFile,
            "--format", "xml");

        Assert.AreEqual(2, exitCode);
    }

    [TestMethod]
    public void NonExistentFile_ReturnsExitCode2()
    {
        var workspace = CreateWorkspaceCopy();
        var (exitCode, stdout, stderr) = RunCli(
            "transaction", "apply",
            "--workspace-root", workspace,
            "--file", Path.Combine(_tempDir, "doesnotexist.xml"),
            "--format", "xml");

        Assert.AreEqual(2, exitCode);
        Assert.IsTrue(stderr.Contains("diagnostics", StringComparison.OrdinalIgnoreCase));
    }

    // ─── Schema validation errors (exit 2 or 3) ─────────────────────────────────

    [TestMethod]
    public void InvalidXml_ViaFile_ReturnsExitCode2()
    {
        var workspace = CreateWorkspaceCopy();
        var txFile = Path.Combine(_tempDir, "malformed.xml");
        File.WriteAllText(txFile, "<transaction not closed");

        var (exitCode, stdout, stderr) = RunCli(
            "transaction", "apply",
            "--workspace-root", workspace,
            "--file", txFile,
            "--format", "xml");

        Assert.AreEqual(2, exitCode);
        Assert.IsTrue(stderr.Contains("diagnostics", StringComparison.OrdinalIgnoreCase));
    }

    // ─── Successful apply (exit 0) with --format xml ─────────────────────────────

    [TestMethod]
    public void Apply_SetAttribute_ViaFile_XmlFormat_ExitCode0()
    {
        var workspace = CreateWorkspaceCopy();

        var txXml = """
            <?xml version="1.0" encoding="utf-8"?>
            <transaction operation_id="20260823T120001Z-cli-set-attr">
              <document path="20260823-xpath-core/tasks.xml" expected_revision="9">
                <set-attribute select="//task[@id='20260823-task-xpath-projection']" expect="1"
                               name="agent" value="high-priority-cli" />
              </document>
            </transaction>
            """;

        var txFile = Path.Combine(_tempDir, "tx.xml");
        File.WriteAllText(txFile, txXml);

        var (exitCode, stdout, stderr) = RunCli(
            "transaction", "apply",
            "--workspace-root", workspace,
            "--file", txFile,
            "--format", "xml");

        Assert.AreEqual(0, exitCode, $"Expected exit 0. Stderr: {stderr}");
        Assert.IsFalse(string.IsNullOrWhiteSpace(stdout), "Stdout must contain mutation XML");
        Assert.IsTrue(stdout.Contains("<mutation", StringComparison.OrdinalIgnoreCase));
        Assert.IsTrue(stdout.Contains("command=\"transaction apply\"", StringComparison.OrdinalIgnoreCase));
        Assert.IsTrue(stdout.Contains("already_applied=\"false\"", StringComparison.OrdinalIgnoreCase));

        // Verify disk
        var tasksPath = Path.Combine(workspace, "20260823-xpath-core", "tasks.xml");
        var content = File.ReadAllText(tasksPath);
        Assert.IsTrue(content.Contains("revision=\"10\""), "Revision must be 10 after apply");
        Assert.IsTrue(content.Contains("high-priority-cli"), "New attribute value must be written");
    }

    [TestMethod]
    public void Apply_SetAttribute_ViaFile_HumanFormat_ExitCode0()
    {
        var workspace = CreateWorkspaceCopy();

        var txXml = """
            <?xml version="1.0" encoding="utf-8"?>
            <transaction operation_id="20260823T120002Z-cli-human">
              <document path="20260823-xpath-core/tasks.xml" expected_revision="9">
                <set-attribute select="//task[@id='20260823-task-xpath-projection']" expect="1"
                               name="agent" value="human-format-test" />
              </document>
            </transaction>
            """;

        var txFile = Path.Combine(_tempDir, "tx.xml");
        File.WriteAllText(txFile, txXml);

        var (exitCode, stdout, stderr) = RunCli(
            "transaction", "apply",
            "--workspace-root", workspace,
            "--file", txFile,
            "--format", "human");

        Assert.AreEqual(0, exitCode, $"Expected exit 0. Stderr: {stderr}");
        Assert.IsTrue(stdout.Contains("Mutation applied", StringComparison.OrdinalIgnoreCase));
        Assert.IsTrue(stdout.Contains("transaction apply", StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public void Apply_AssertOnlyPass_AlreadyApplied_ExitCode0()
    {
        var workspace = CreateWorkspaceCopy();

        var txXml = """
            <?xml version="1.0" encoding="utf-8"?>
            <transaction operation_id="20260823T120003Z-cli-assert-only">
              <document path="20260823-xpath-core/tasks.xml" expected_revision="9">
                <assert test="count(//task) &gt; 0" />
              </document>
            </transaction>
            """;

        var txFile = Path.Combine(_tempDir, "tx.xml");
        File.WriteAllText(txFile, txXml);

        var (exitCode, stdout, stderr) = RunCli(
            "transaction", "apply",
            "--workspace-root", workspace,
            "--file", txFile,
            "--format", "xml");

        Assert.AreEqual(0, exitCode, $"Expected exit 0. Stderr: {stderr}");
        Assert.IsTrue(stdout.Contains("already_applied=\"true\"", StringComparison.OrdinalIgnoreCase));

        // No revision change on disk
        var tasksPath = Path.Combine(workspace, "20260823-xpath-core", "tasks.xml");
        Assert.IsTrue(File.ReadAllText(tasksPath).Contains("revision=\"9\""));
    }

    // ─── Protected-state rejection (exit 5) ─────────────────────────────────────

    [TestMethod]
    public void Apply_ChangeIterationStatus_ReturnsExitCode5()
    {
        var workspace = CreateWorkspaceCopy();

        var txXml = """
            <?xml version="1.0" encoding="utf-8"?>
            <transaction operation_id="20260823T120010Z-cli-protected">
              <document path="20260823-xpath-core/spec.xml" expected_revision="4">
                <set-attribute select="/iteration" expect="1" name="status" value="completed" />
              </document>
            </transaction>
            """;

        var txFile = Path.Combine(_tempDir, "tx.xml");
        File.WriteAllText(txFile, txXml);

        var (exitCode, stdout, stderr) = RunCli(
            "transaction", "apply",
            "--workspace-root", workspace,
            "--file", txFile,
            "--format", "xml");

        Assert.AreEqual(5, exitCode, $"Protected state must return exit 5. Stderr: {stderr}");
        Assert.IsTrue(stderr.Contains(DiagnosticCodes.OwnerDecisionRequired, StringComparison.OrdinalIgnoreCase));
    }

    // ─── Revision conflict (exit 4) ─────────────────────────────────────────────

    [TestMethod]
    public void Apply_StaleRevision_ReturnsExitCode4()
    {
        var workspace = CreateWorkspaceCopy();

        var txXml = """
            <?xml version="1.0" encoding="utf-8"?>
            <transaction operation_id="20260823T120020Z-cli-stale">
              <document path="20260823-xpath-core/tasks.xml" expected_revision="5">
                <set-attribute select="//task[@id='20260823-task-xpath-projection']" expect="1"
                               name="priority" value="stale" />
              </document>
            </transaction>
            """;

        var txFile = Path.Combine(_tempDir, "tx.xml");
        File.WriteAllText(txFile, txXml);

        var (exitCode, stdout, stderr) = RunCli(
            "transaction", "apply",
            "--workspace-root", workspace,
            "--file", txFile,
            "--format", "xml");

        Assert.AreEqual(4, exitCode);
        Assert.IsTrue(stderr.Contains(DiagnosticCodes.RevisionConflict, StringComparison.OrdinalIgnoreCase));
    }

    // ─── Assert failure (exit 4) ─────────────────────────────────────────────────

    [TestMethod]
    public void Apply_AssertFails_ReturnsExitCode4()
    {
        var workspace = CreateWorkspaceCopy();

        var txXml = """
            <?xml version="1.0" encoding="utf-8"?>
            <transaction operation_id="20260823T120030Z-cli-assert-fail">
              <document path="20260823-xpath-core/tasks.xml" expected_revision="9">
                <assert test="count(//task[@id='nonexistent-task-id']) = 1" />
              </document>
            </transaction>
            """;

        var txFile = Path.Combine(_tempDir, "tx.xml");
        File.WriteAllText(txFile, txXml);

        var (exitCode, stdout, stderr) = RunCli(
            "transaction", "apply",
            "--workspace-root", workspace,
            "--file", txFile,
            "--format", "xml");

        Assert.AreEqual(4, exitCode, $"Assert failure must return exit 4. Stderr: {stderr}");
        Assert.IsTrue(stderr.Contains(DiagnosticCodes.CardinalityConflict, StringComparison.OrdinalIgnoreCase));
    }

    // ─── Disposable public CLI flow ──────────────────────────────────────────────

    [TestMethod]
    public void EndToEnd_UpdateSpecNarrativeAndAppendTask_ThenValidate()
    {
        // This test demonstrates the transaction apply escape hatch:
        // atomically set a spec attribute AND set a task attribute, then validate.
        var workspace = CreateWorkspaceCopy();

        var txXml = """
            <?xml version="1.0" encoding="utf-8"?>
            <transaction operation_id="20260823T120040Z-e2e-combined">
              <document path="20260823-xpath-core/tasks.xml" expected_revision="9">
                <assert test="count(//task[@id='20260823-task-xpath-projection']) = 1" />
                <set-attribute select="//task[@id='20260823-task-xpath-projection']" expect="1"
                               name="agent" value="codex-updated" />
              </document>
            </transaction>
            """;

        var txFile = Path.Combine(_tempDir, "e2e_tx.xml");
        File.WriteAllText(txFile, txXml);

        var (txExitCode, txStdout, txStderr) = RunCli(
            "transaction", "apply",
            "--workspace-root", workspace,
            "--file", txFile,
            "--format", "xml");

        Assert.AreEqual(0, txExitCode, $"Transaction apply failed. Stderr: {txStderr}");
        Assert.IsTrue(txStdout.Contains("command=\"transaction apply\""), "Output must show transaction apply command");

        // Now validate the workspace
        var (valExitCode, valStdout, valStderr) = RunCli(
            "validate",
            "--workspace-root", workspace,
            "--format", "xml");

        Assert.AreEqual(0, valExitCode, $"Validate after transaction failed. Stderr: {valStderr}");

        // Confirm attribute on disk
        var tasksPath = Path.Combine(workspace, "20260823-xpath-core", "tasks.xml");
        Assert.IsTrue(File.ReadAllText(tasksPath).Contains("codex-updated"), "Updated agent value must be on disk");
    }

    [TestMethod]
    public void EndToEnd_MultiDocument_AtomicCommit_WorkspaceValid()
    {
        // Apply a transaction that updates both tasks.xml and backlog.xml atomically.
        var workspace = CreateWorkspaceCopy();

        // Add an item to backlog.xml (backlog rev=1) and set-attribute on tasks.xml (tasks rev=9)
        var txXml = """
            <?xml version="1.0" encoding="utf-8"?>
            <transaction operation_id="20260823T120050Z-e2e-multidoc">
              <document path="20260823-xpath-core/tasks.xml" expected_revision="9">
                <set-attribute select="//task[@id='20260823-task-xpath-projection']" expect="1"
                               name="agent" value="multidoc-test" />
              </document>
              <document path="backlog.xml" expected_revision="1">
                <append-child select="/backlog/items" expect="1">
                  <item id="20260823T120050Z-backlog-e2e-test" status="open" created_at="2026-08-23T12:00:50Z">
                    <index><summary>E2E multi-doc test item.</summary></index>
                    <statement>E2E test backlog item.</statement>
                    <rationale>Exercise a valid multi-document public transaction.</rationale>
                    <impact>Confirms both managed documents commit and validate together.</impact>
                    <source><ref scope="project" target="20260823-task-xpath-projection" relation="derived-from"/></source>
                    <review_condition>Remove after the disposable test workspace is discarded.</review_condition>
                  </item>
                </append-child>
              </document>
            </transaction>
            """;

        var txFile = Path.Combine(_tempDir, "multidoc_tx.xml");
        File.WriteAllText(txFile, txXml);

        var (txExitCode, txStdout, txStderr) = RunCli(
            "transaction", "apply",
            "--workspace-root", workspace,
            "--file", txFile,
            "--format", "xml");

        Assert.AreEqual(0, txExitCode, $"Multi-doc transaction apply failed. Stderr: {txStderr}");

        // Verify both documents updated
        var xTx = XDocument.Parse(txStdout);
        var docPaths = xTx.Root!
            .Elements("document")
            .Select(e => e.Attribute("path")?.Value)
            .ToList();
        Assert.AreEqual(2, docPaths.Count, "Two documents must be in mutation receipt");
        Assert.IsTrue(docPaths.Contains("20260823-xpath-core/tasks.xml", StringComparer.Ordinal));
        Assert.IsTrue(docPaths.Contains("backlog.xml", StringComparer.Ordinal));

        // Validate workspace
        var (valExitCode, valStdout, valStderr) = RunCli(
            "validate",
            "--workspace-root", workspace,
            "--format", "xml");

        Assert.AreEqual(0, valExitCode, $"Validate after multi-doc failed. Stderr: {valStderr}");
    }
}
