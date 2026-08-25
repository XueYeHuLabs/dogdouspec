using System.Runtime.CompilerServices;
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
