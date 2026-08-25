using System.Xml.Linq;
using DogdouSpec.Core.Backlog;
using DogdouSpec.Core.Diagnostics;

namespace DogdouSpec.Core.Tests;

[TestClass]
public sealed class BacklogLifecycleCoreTests
{
    private static string RepoRoot = null!;
    private static readonly string[] SourceIterations = new[] { "20260823-xpath-core" };
    private static readonly string[] SourceTasks = new[] { "20260823-task-xpath-projection" };
    private string _tempDir = null!;

    [ClassInitialize]
    public static void ClassInitialize(TestContext _)
    {
        foreach (var startPath in new[] { Environment.CurrentDirectory, AppDomain.CurrentDomain.BaseDirectory })
        {
            var current = new DirectoryInfo(startPath);
            while (current != null)
            {
                if (File.Exists(Path.Combine(current.FullName, "DogdouSpec.slnx")))
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
        Assert.IsTrue(File.Exists(Path.Combine(RepoRoot, "DogdouSpec.slnx")));
    }

    [TestInitialize]
    public void Initialize()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "dogdouspec-backlog-core-" + Guid.NewGuid().ToString("N"));
        CopyDirectory(Path.Combine(RepoRoot, "docs", "demos", "v1-core", ".dogdouspec"), _tempDir);
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
    public void Lifecycle_DefectAddReplayScheduleCompleteAndTerminalImmutability()
    {
        var tasksPath = Path.Combine(_tempDir, "20260823-xpath-core", "tasks.xml");
        var tasksBefore = File.ReadAllBytes(tasksPath);
        var create = CreateInput();

        var (added, addEnvelope, addDiagnostics) = BacklogLifecycle.Add(_tempDir, 1, create);
        Assert.IsTrue(added, Join(addDiagnostics));
        Assert.AreEqual(2, addEnvelope!.Documents.Single().Revision);

        var (replayed, replayEnvelope, replayDiagnostics) = BacklogLifecycle.Add(_tempDir, 1, create);
        Assert.IsTrue(replayed, Join(replayDiagnostics));
        Assert.IsTrue(replayEnvelope!.AlreadyApplied);
        Assert.AreEqual(2, replayEnvelope.Documents.Single().Revision);

        var changedReplay = create with { Summary = "Changed replay semantics" };
        var (changedSuccess, _, changedDiagnostics) = BacklogLifecycle.Add(_tempDir, 1, changedReplay);
        Assert.IsFalse(changedSuccess);
        Assert.IsTrue(changedDiagnostics.Any(d => d.Code == DiagnosticCodes.IdempotencyConflict));

        var schedule = new BacklogTransitionInput(
            create.Id, "20260825T080100Z-backlog-schedule", "tester",
            new DateTimeOffset(2026, 8, 25, 8, 1, 0, TimeSpan.Zero), "20260823-task-xpath-projection");
        var (scheduled, _, scheduleDiagnostics) = BacklogLifecycle.Schedule(_tempDir, 2, schedule);
        Assert.IsTrue(scheduled, Join(scheduleDiagnostics));
        CollectionAssert.AreEqual(tasksBefore, File.ReadAllBytes(tasksPath), "Backlog transitions must not mutate tasks.xml.");

        var complete = new BacklogTransitionInput(
            create.Id, "20260825T080200Z-backlog-complete", "tester",
            new DateTimeOffset(2026, 8, 25, 8, 2, 0, TimeSpan.Zero), "20260823-task-xpath-projection");
        var (completed, _, completeDiagnostics) = BacklogLifecycle.Complete(_tempDir, 3, complete);
        Assert.IsTrue(completed, Join(completeDiagnostics));

        var (listed, result, listDiagnostics) = BacklogLifecycle.List(_tempDir, status: "completed", kind: "defect", severity: "p1");
        Assert.IsTrue(listed, Join(listDiagnostics));
        Assert.AreEqual(1, result!.Items.Count);
        Assert.AreEqual(create.Id, result.Items[0].Id);

        var cancel = new BacklogTransitionInput(
            create.Id, "20260825T080300Z-backlog-cancel", "tester",
            new DateTimeOffset(2026, 8, 25, 8, 3, 0, TimeSpan.Zero), null);
        var (cancelled, _, cancelDiagnostics) = BacklogLifecycle.Cancel(_tempDir, 4, cancel);
        Assert.IsFalse(cancelled);
        Assert.IsTrue(cancelDiagnostics.Any(d => d.Code == DiagnosticCodes.TaskImmutable));

        var doc = XDocument.Load(Path.Combine(_tempDir, "backlog.xml"));
        var item = doc.Root!.Element("items")!.Element("item")!;
        Assert.AreEqual("completed", (string?)item.Attribute("status"));
        Assert.IsTrue(item.Descendants("ref").Any(r =>
            (string?)r.Attribute("relation") == "resolved-by" &&
            (string?)r.Attribute("target") == "20260823-task-xpath-projection"));
    }

    [TestMethod]
    public void Lifecycle_RejectsInvalidDefectReferencesTargetAndRevision()
    {
        var invalidSeverity = CreateInput() with { Severity = "critical" };
        var (severitySuccess, _, severityDiagnostics) = BacklogLifecycle.Add(_tempDir, 1, invalidSeverity);
        Assert.IsFalse(severitySuccess);
        Assert.IsTrue(severityDiagnostics.Any(d => d.Code == DiagnosticCodes.InvalidArgument));

        var invalidSource = CreateInput() with
        {
            SourceIterations = Array.Empty<string>(),
            SourceTasks = new[] { "20260823-xpath-core" }
        };
        var (sourceSuccess, _, sourceDiagnostics) = BacklogLifecycle.Add(_tempDir, 1, invalidSource);
        Assert.IsFalse(sourceSuccess);
        Assert.IsTrue(sourceDiagnostics.Any(d => d.Code == DiagnosticCodes.InvalidReferenceTargetType));

        var invalidTarget = CreateInput() with { TargetIteration = "20260823-task-xpath-projection" };
        var (targetSuccess, _, targetDiagnostics) = BacklogLifecycle.Add(_tempDir, 1, invalidTarget);
        Assert.IsFalse(targetSuccess);
        Assert.IsTrue(targetDiagnostics.Any(d => d.Code == DiagnosticCodes.InvalidReferenceTargetType));

        var (added, _, addDiagnostics) = BacklogLifecycle.Add(_tempDir, 1, CreateInput());
        Assert.IsTrue(added, Join(addDiagnostics));
        var stale = CreateInput() with
        {
            Id = "20260825-backlog-second-defect",
            OperationId = "20260825T080500Z-backlog-add-second"
        };
        var (staleSuccess, _, staleDiagnostics) = BacklogLifecycle.Add(_tempDir, 1, stale);
        Assert.IsFalse(staleSuccess);
        Assert.IsTrue(staleDiagnostics.Any(d => d.Code == DiagnosticCodes.RevisionConflict));
    }

    [TestMethod]
    public void Lifecycle_DuplicateReplayReceiptsReturnDeterministicConflict()
    {
        var input = CreateInput();
        var (added, _, addDiagnostics) = BacklogLifecycle.Add(_tempDir, 1, input);
        Assert.IsTrue(added, Join(addDiagnostics));

        var path = Path.Combine(_tempDir, "backlog.xml");
        var document = XDocument.Load(path);
        var records = document.Root!.Element("items")!.Element("item")!.Element("records")!;
        var duplicate = new XElement(records.Element("record")!);
        duplicate.SetAttributeValue("id", "20260825T080000Z-backlog-add-duplicate-receipt");
        records.Add(duplicate);
        document.Save(path);

        var (success, _, diagnostics) = BacklogLifecycle.Add(_tempDir, 2, input);
        Assert.IsFalse(success);
        Assert.IsTrue(diagnostics.Any(d => d.Code == DiagnosticCodes.IdempotencyConflict));
    }

    private static BacklogCreateInput CreateInput() => new(
        "20260825-backlog-dogfood-defect",
        "20260825T080000Z-backlog-add-dogfood-defect",
        "tester",
        new DateTimeOffset(2026, 8, 25, 8, 0, 0, TimeSpan.Zero),
        "defect",
        "p1",
        "Dogfood defect",
        "A reproducible dogfood defect remains.",
        "It is non-blocking for the current acceptance boundary.",
        "The defect can break a later project workflow.",
        SourceIterations,
        SourceTasks,
        "20260823-xpath-core",
        null);

    private static string Join(IReadOnlyList<DogdouSpec.Core.Diagnostics.Diagnostic> diagnostics) =>
        string.Join("; ", diagnostics.Select(d => d.Code + ": " + d.Message));

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
