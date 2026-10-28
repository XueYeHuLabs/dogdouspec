using System.Runtime.CompilerServices;
using System.Xml.Linq;
using DogdouSpec.Cli;

namespace DogdouSpec.Cli.Tests;

[TestClass]
public sealed class BacklogCliTests
{
    private static string RepoRoot = null!;
    private string _tempDir = null!;

    [ClassInitialize]
    public static void ClassInitialize(TestContext _)
    {
        RepoRoot = FindRepositoryRootFromSource()
            ?? FindRepositoryRoot(Environment.CurrentDirectory)
            ?? FindRepositoryRoot(AppDomain.CurrentDomain.BaseDirectory)
            ?? string.Empty;
        Assert.IsFalse(string.IsNullOrEmpty(RepoRoot));
    }

    [TestInitialize]
    public void Initialize()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "dogdouspec-backlog-cli-" + Guid.NewGuid().ToString("N"));
        CopyDirectory(Path.Combine(RepoRoot, "docs", "demos", "v1-core", ".dogdouspec"), Path.Combine(_tempDir, ".dogdouspec"));
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }

    [TestMethod]
    public void BacklogCommands_EndToEndLifecycleAndConflicts()
    {
        var itemId = "20260825-backlog-cli-defect";
        var addArgs = new[]
        {
            "backlog", "add", "--id", itemId,
            "--operation-id", "20260825T090000Z-backlog-cli-add", "--actor", "cli-tester",
            "--occurred-at", "2026-08-25T09:00:00Z", "--kind", "defect", "--severity", "p2",
            "--summary", "CLI defect", "--statement", "A CLI defect remains.",
            "--rationale", "It is deferred from current acceptance.", "--impact", "It may break a later workflow.",
            "--source-iteration", "20260823-xpath-core", "--source-task", "20260823-task-xpath-projection",
            "--review-condition", "Review before the next release.", "--expected-revision", "1",
            "--workspace-root", _tempDir, "--format", "xml"
        };
        var (addCode, addOut, addErr) = RunCli(addArgs);
        Assert.AreEqual(0, addCode, addErr);
        StringAssert.Contains(addOut, "previous_revision=\"1\"");
        StringAssert.Contains(addOut, "revision=\"2\"");

        var (replayCode, replayOut, replayErr) = RunCli(addArgs);
        Assert.AreEqual(0, replayCode, replayErr);
        StringAssert.Contains(replayOut, "already_applied=\"true\"");

        var staleArgs = addArgs.ToArray();
        staleArgs[3] = "20260825-backlog-cli-second";
        staleArgs[5] = "20260825T090100Z-backlog-cli-add-second";
        var (staleCode, _, staleErr) = RunCli(staleArgs);
        Assert.AreNotEqual(0, staleCode);
        StringAssert.Contains(staleErr, "REVISION_CONFLICT");

        var (listCode, listOut, listErr) = RunCli(
            "backlog", "list", "--status", "open", "--kind", "defect", "--severity", "p2",
            "--workspace-root", _tempDir, "--format", "xml");
        Assert.AreEqual(0, listCode, listErr);
        StringAssert.Contains(listOut, $"id=\"{itemId}\"");

        var (scheduleCode, scheduleOut, scheduleErr) = RunCli(
            "backlog", "schedule", "--id", itemId,
            "--operation-id", "20260825T090200Z-backlog-cli-schedule", "--actor", "cli-tester",
            "--occurred-at", "2026-08-25T09:02:00Z", "--resolving-task", "20260823-task-xpath-projection",
            "--expected-revision", "2", "--workspace-root", _tempDir, "--format", "xml");
        Assert.AreEqual(0, scheduleCode, scheduleErr);
        StringAssert.Contains(scheduleOut, "revision=\"3\"");

        var (cancelCode, cancelOut, cancelErr) = RunCli(
            "backlog", "cancel", "--id", itemId,
            "--operation-id", "20260825T090300Z-backlog-cli-cancel", "--actor", "cli-tester",
            "--occurred-at", "2026-08-25T09:03:00Z", "--expected-revision", "3",
            "--workspace-root", _tempDir, "--format", "xml");
        Assert.AreEqual(0, cancelCode, cancelErr);
        StringAssert.Contains(cancelOut, "revision=\"4\"");

        var (terminalCode, _, terminalErr) = RunCli(
            "backlog", "complete", "--id", itemId,
            "--operation-id", "20260825T090400Z-backlog-cli-complete", "--actor", "cli-tester",
            "--occurred-at", "2026-08-25T09:04:00Z", "--expected-revision", "4",
            "--workspace-root", _tempDir, "--format", "xml");
        Assert.AreNotEqual(0, terminalCode);
        StringAssert.Contains(terminalErr, "TASK_IMMUTABLE");
    }

    [TestMethod]
    public void BacklogAdd_InvalidReferenceFailsClosed()
    {
        var (code, _, stderr) = RunCli(
            "backlog", "add", "--id", "20260825-backlog-invalid-source",
            "--operation-id", "20260825T091000Z-backlog-invalid-source", "--actor", "cli-tester",
            "--occurred-at", "2026-08-25T09:10:00Z", "--kind", "defect", "--severity", "p1",
            "--summary", "Invalid source", "--statement", "Invalid source item.",
            "--rationale", "Validation drill.", "--impact", "Reference integrity.",
            "--source-task", "20260823-missing-task", "--review-condition", "Review later.",
            "--expected-revision", "1", "--workspace-root", _tempDir, "--format", "xml");
        Assert.AreNotEqual(0, code);
        StringAssert.Contains(stderr, "DANGLING_REFERENCE");
    }

    [TestMethod]
    public void BacklogProgress_DryRunReplayClosureAndFollowUp_RoundTripXmlAndHumanHistory()
    {
        var id = AddProgressItem();
        const string summary = "已完成调研 <方案 A> & 部分验证。\r\n仍需兼容性测试。\r保留意图。";
        var noteArguments = new[]
        {
            "backlog", "record", "--id", id, "--summary", summary,
            "--operation-id", "20260825T100000Z-cli-progress-note", "--actor", "cli-tester",
            "--check-command", "dotnet test", "--check-result", "failed", "--check-exit-code", "1",
            "--resolving-task", "20260823-task-xpath-projection", "--expected-revision", "2",
            "--workspace-root", _tempDir, "--format", "xml"
        };
        var before = File.ReadAllBytes(BacklogPath);
        var preview = RunCli([.. noteArguments, "--dry-run"]);
        Assert.AreEqual(0, preview.ExitCode, preview.Stderr);
        StringAssert.Contains(preview.Stdout, "dry_run=\"true\"");
        CollectionAssert.AreEqual(before, File.ReadAllBytes(BacklogPath));
        var note = RunCli(noteArguments);
        Assert.AreEqual(0, note.ExitCode, note.Stderr);
        var identity = XDocument.Parse(note.Stdout).Root!.Element("backlog-identity")!;
        Assert.AreEqual(id, (string?)identity.Attribute("id"));
        var replay = RunCli(noteArguments);
        Assert.AreEqual(0, replay.ExitCode, replay.Stderr);
        StringAssert.Contains(replay.Stdout, "already_applied=\"true\"");
        var changedArguments = noteArguments.ToArray();
        changedArguments[Array.IndexOf(changedArguments, "--summary") + 1] = "Different note.";
        var conflict = RunCli(changedArguments);
        Assert.AreNotEqual(0, conflict.ExitCode);
        StringAssert.Contains(conflict.Stderr, "IDEMPOTENCY_CONFLICT");

        var records = RunCli("backlog", "records", "--id", id, "--last", "1", "--workspace-root", _tempDir, "--format", "xml");
        Assert.AreEqual(0, records.ExitCode, records.Stderr);
        var history = XDocument.Parse(records.Stdout).Root!;
        Assert.AreEqual("open", (string?)history.Attribute("status"));
        Assert.AreEqual("2", (string?)history.Attribute("total_records"));
        Assert.AreEqual(summary, history.Element("record")!.Element("summary")!.Value);
        Assert.AreEqual("failed", (string?)history.Element("record")!.Element("checks")!.Element("check")!.Attribute("result"));
        var complete = RunCli("backlog", "complete", "--id", id, "--summary", "Caller accepted the available outcome.",
            "--check-result", "failed", "--check-summary", "A remaining check is reported for context.", "--workspace-root", _tempDir, "--format", "xml");
        Assert.AreEqual(0, complete.ExitCode, complete.Stderr);
        var followUp = RunCli("backlog", "record", "--id", id, "--summary", "补充关闭后的说明。", "--workspace-root", _tempDir, "--format", "xml");
        Assert.AreEqual(0, followUp.ExitCode, followUp.Stderr);
        var recent = RunCli("backlog", "records", "--id", id, "--last", "2", "--workspace-root", _tempDir, "--format", "xml");
        var recentXml = XDocument.Parse(recent.Stdout).Root!;
        Assert.AreEqual("completed", (string?)recentXml.Attribute("status"));
        Assert.AreEqual("4", (string?)recentXml.Attribute("total_records"));
        Assert.AreEqual(2, recentXml.Elements("record").Count());
        var human = RunCli("backlog", "records", "--id", id, "--last", "2", "--workspace-root", _tempDir, "--format", "human");
        Assert.AreEqual(0, human.ExitCode, human.Stderr);
        StringAssert.Contains(human.Stdout, "Records: 2 of 4");
        StringAssert.Contains(human.Stdout, "Caller accepted");
        StringAssert.Contains(human.Stdout, "补充关闭后的说明。");
    }

    [TestMethod]
    [DataRow("complete", false)]
    [DataRow("complete", true)]
    [DataRow("cancel", false)]
    [DataRow("cancel", true)]
    public void BacklogClosure_SummariesChecksAndTaskLinksAreOptional(string action, bool withDetails)
    {
        var id = AddProgressItem();
        var arguments = new List<string> { "backlog", action, "--id", id, "--workspace-root", _tempDir, "--format", "xml" };
        if (withDetails) arguments.AddRange(["--summary", "Why this item ended.", "--check-command", "reported-command", "--check-result", "failed", "--check-exit-code", "5"]);
        var result = RunCli([.. arguments]);
        Assert.AreEqual(0, result.ExitCode, result.Stderr);
        var item = XDocument.Load(BacklogPath).Root!.Element("items")!.Element("item")!;
        Assert.AreEqual(action == "complete" ? "completed" : "cancelled", (string?)item.Attribute("status"));
        var record = item.Element("records")!.Elements("record").Last();
        Assert.AreEqual(withDetails ? "Why this item ended." : $"Backlog item {action} transition applied through the public lifecycle helper.", record.Element("summary")!.Value);
        Assert.AreEqual(withDetails, record.Element("checks") != null);
        Assert.IsNull(record.Element("sources"));
    }

    [TestMethod]
    public void BacklogRecords_RemainReadOnlyWithPendingRecovery_AndValidateSelection()
    {
        var id = AddProgressItem();
        var pending = Path.Combine(_tempDir, ".dogdouspec", "_tmp", "tx_pending_backlog_read", "staged");
        Directory.CreateDirectory(pending);
        var before = File.ReadAllBytes(BacklogPath);
        var records = RunCli("backlog", "records", "--id", id, "--workspace-root", _tempDir, "--format", "xml");
        Assert.AreEqual(0, records.ExitCode, records.Stderr);
        CollectionAssert.AreEqual(before, File.ReadAllBytes(BacklogPath));
        Assert.IsTrue(Directory.Exists(pending));
        var invalidLast = RunCli("backlog", "records", "--id", id, "--last", "0", "--workspace-root", _tempDir, "--format", "xml");
        Assert.AreEqual(2, invalidLast.ExitCode);
        var missing = RunCli("backlog", "records", "--id", "20260825-missing-item", "--workspace-root", _tempDir, "--format", "xml");
        Assert.AreNotEqual(0, missing.ExitCode);
        StringAssert.Contains(missing.Stderr, "DANGLING_REFERENCE");
        CollectionAssert.AreEqual(before, File.ReadAllBytes(BacklogPath));
    }

    [TestMethod]
    public void BacklogRecord_InvalidOptionalArgumentsDoNotWrite()
    {
        var id = AddProgressItem();
        var before = File.ReadAllBytes(BacklogPath);
        foreach (var arguments in new[]
        {
            new[] { "backlog", "record", "--id", id },
            new[] { "backlog", "record", "--id", id, "--summary", " " },
            new[] { "backlog", "record", "--id", id, "--summary", "Note", "--check-command", "cmd" },
            new[] { "backlog", "complete", "--id", id, "--check-exit-code", "0", "--check-result", "passed" },
            new[] { "backlog", "cancel", "--id", id, "--summary", "" }
        })
        {
            var result = RunCli([.. arguments, "--workspace-root", _tempDir, "--format", "xml"]);
            Assert.AreEqual(2, result.ExitCode, result.Stderr);
            Assert.AreEqual(string.Empty, result.Stdout);
            CollectionAssert.AreEqual(before, File.ReadAllBytes(BacklogPath));
        }
    }

    private string BacklogPath => Path.Combine(_tempDir, ".dogdouspec", "backlog.xml");

    private string AddProgressItem()
    {
        const string id = "20260825-backlog-progress";
        var result = RunCli("backlog", "add", "--id", id, "--kind", "research", "--summary", "Progress example",
            "--statement", "Investigate a convenience gap.", "--rationale", "Deferred design.", "--impact", "Usability.",
            "--source-iteration", "20260823-xpath-core", "--review-condition", "Review when ready.", "--workspace-root", _tempDir, "--format", "xml");
        Assert.AreEqual(0, result.ExitCode, result.Stderr);
        return id;
    }

    private static (int ExitCode, string Stdout, string Stderr) RunCli(params string[] args)
    {
        var originalOut = Console.Out;
        var originalErr = Console.Error;
        var originalIn = Console.In;
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        using var stdin = new StringReader(string.Empty);
        try
        {
            Console.SetOut(stdout);
            Console.SetError(stderr);
            Console.SetIn(stdin);
            return (Program.Main(args), stdout.ToString(), stderr.ToString());
        }
        finally
        {
            Console.SetOut(originalOut);
            Console.SetError(originalErr);
            Console.SetIn(originalIn);
        }
    }

    private static string? FindRepositoryRootFromSource([CallerFilePath] string sourceFile = "") =>
        FindRepositoryRoot(Path.GetDirectoryName(sourceFile) ?? string.Empty);

    private static string? FindRepositoryRoot(string start)
    {
        if (string.IsNullOrWhiteSpace(start))
        {
            return null;
        }
        for (var current = new DirectoryInfo(Path.GetFullPath(start)); current != null; current = current.Parent)
        {
            if (File.Exists(Path.Combine(current.FullName, "DogdouSpec.slnx")))
            {
                return current.FullName;
            }
        }
        return null;
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var directory in Directory.GetDirectories(source, "*", SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(Path.Combine(destination, Path.GetRelativePath(source, directory)));
        }
        foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(destination, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
        }
    }
}
