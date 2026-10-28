using System.Xml.Linq;
using DogdouSpec.Core.Backlog;
using DogdouSpec.Core.Diagnostics;
using DogdouSpec.Core.Formatting;
using DogdouSpec.Core.Transactions;

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

        AssertDryRunReplayBlocked(
            "backlog add",
            () => BacklogLifecycle.Add(_tempDir, 1, create, dryRun: true));

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

        AssertDryRunReplayBlocked(
            "backlog transition",
            () => BacklogLifecycle.Schedule(_tempDir, 2, schedule, dryRun: true));

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

    private void AssertDryRunReplayBlocked(
        string family,
        Func<(bool Success, MutationEnvelope? Envelope, IReadOnlyList<Diagnostic> Diagnostics)> replay)
    {
        var before = Directory.GetFiles(_tempDir, "*.xml", SearchOption.AllDirectories)
            .Where(path => !path.Contains(Path.DirectorySeparatorChar + "_tmp" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            .ToDictionary(
                path => Path.GetRelativePath(_tempDir, path),
                File.ReadAllBytes,
                StringComparer.OrdinalIgnoreCase);
        var pendingRoot = Path.Combine(_tempDir, "_tmp", "tx_pending_" + family.Replace(' ', '_'));
        var pendingDirectory = Path.Combine(pendingRoot, "staged");
        Directory.CreateDirectory(pendingDirectory);

        var result = replay();

        Assert.IsFalse(result.Success, $"{family} exact dry-run replay must fail closed while recovery is pending.");
        Assert.IsTrue(result.Diagnostics.Any(d => d.Code == DiagnosticCodes.RecoveryFailed), Join(result.Diagnostics));
        Assert.IsTrue(Directory.Exists(pendingDirectory), $"{family} dry-run must preserve pending recovery artifacts.");
        foreach (var (relativePath, originalBytes) in before)
        {
            CollectionAssert.AreEqual(originalBytes, File.ReadAllBytes(Path.Combine(_tempDir, relativePath)));
        }
        Directory.Delete(pendingRoot, recursive: true);
    }

    [TestMethod]
    public void ProgressRecords_ReplayAndFollowUpWithoutChangingLifecycleOrTaskEvidence()
    {
        var input = CreateInput();
        Assert.IsTrue(BacklogLifecycle.Add(_tempDir, 1, input).Success);
        var tasksPath = Path.Combine(_tempDir, "20260823-xpath-core", "tasks.xml");
        var tasksBefore = File.ReadAllBytes(tasksPath);
        var note = new BacklogTransitionInput(input.Id, "20260825T080100Z-backlog-note", "tester",
            DateTimeOffset.Parse("2026-08-25T08:01:00Z", System.Globalization.CultureInfo.InvariantCulture),
            "20260823-task-xpath-projection", "Partial progress <XML> & 中文\r\nMore work remains.\rKeep intent.",
            [new BacklogCheck("command", "failed", "An incomplete check is reported, not a closure decision.", "dotnet test", 1)]);
        var recorded = BacklogLifecycle.Record(_tempDir, 2, note);
        Assert.IsTrue(recorded.Success, Join(recorded.Diagnostics));
        var path = Path.Combine(_tempDir, "backlog.xml");
        var bytes = File.ReadAllBytes(path);
        var replay = BacklogLifecycle.Record(_tempDir, 2, note);
        Assert.IsTrue(replay.Success, Join(replay.Diagnostics));
        Assert.IsTrue(replay.Envelope!.AlreadyApplied);
        CollectionAssert.AreEqual(bytes, File.ReadAllBytes(path));
        Assert.IsFalse(BacklogLifecycle.Record(_tempDir, 3, note with { Summary = "Changed intent" }).Success);
        Assert.IsFalse(BacklogLifecycle.Record(_tempDir, 3, note with
        {
            Checks = [new BacklogCheck("command", "passed", "Different evidence.", "dotnet test", 0)]
        }).Success);

        var history = BacklogLifecycle.Records(_tempDir, input.Id, 1);
        Assert.IsTrue(history.Success, Join(history.Diagnostics));
        Assert.AreEqual("open", history.Result!.Status);
        Assert.AreEqual(2, history.Result.TotalRecords);
        Assert.AreEqual(note.Summary, history.Result.Records[0].Element("summary")!.Value);
        Assert.AreEqual("related-to", (string?)history.Result.Records[0].Element("sources")!.Element("ref")!.Attribute("relation"));
        var xml = XDocument.Parse(history.Result.Format(OutputFormat.Xml));
        Assert.AreEqual(note.Summary, xml.Root!.Element("record")!.Element("summary")!.Value);
        StringAssert.Contains(history.Result.Format(OutputFormat.Human), "Exit code: 1");

        var complete = note with { OperationId = "20260825T080200Z-backlog-note-complete", OccurredAt = note.OccurredAt.AddMinutes(1), Summary = "Caller chose completion.", ResolvingTask = null };
        var completed = BacklogLifecycle.Complete(_tempDir, 3, complete);
        Assert.IsTrue(completed.Success, Join(completed.Diagnostics));
        var followUp = note with { OperationId = "20260825T080130Z-backlog-follow-up", OccurredAt = note.OccurredAt.AddSeconds(30), Summary = "Historical follow-up after closure.", Checks = null };
        var followedUp = BacklogLifecycle.Record(_tempDir, 4, followUp);
        Assert.IsTrue(followedUp.Success, Join(followedUp.Diagnostics));
        var recent = BacklogLifecycle.Records(_tempDir, input.Id, 2).Result!;
        Assert.AreEqual("completed", recent.Status);
        Assert.AreEqual(4, recent.TotalRecords);
        CollectionAssert.AreEqual(new[] { complete.Summary, followUp.Summary }, recent.Records.Select(record => record.Element("summary")!.Value).ToArray());
        var item = XDocument.Load(path).Root!.Element("items")!.Element("item")!;
        Assert.AreEqual("2026-08-25T08:02:00Z", (string?)item.Attribute("updated_at"));
        CollectionAssert.AreEqual(tasksBefore, File.ReadAllBytes(tasksPath));
    }

    [TestMethod]
    public void LegacyCompletion_RetainsReleased105FingerprintAndReplay()
    {
        var input = CreateInput();
        Assert.IsTrue(BacklogLifecycle.Add(_tempDir, 1, input).Success);
        var complete = new BacklogTransitionInput(input.Id, "20260825T080200Z-backlog-complete", "tester",
            DateTimeOffset.Parse("2026-08-25T08:02:00Z", System.Globalization.CultureInfo.InvariantCulture), "20260823-task-xpath-projection");
        var completed = BacklogLifecycle.Complete(_tempDir, 2, complete);
        Assert.IsTrue(completed.Success, Join(completed.Diagnostics));
        var record = BacklogLifecycle.Records(_tempDir, input.Id, 1).Result!.Records[0];
        // Captured from the installed 1.0.5 binary using these exact legacy arguments.
        Assert.AreEqual("b341216ff12276a436097f942a00df6991d624719887825cb6129f22d8f0cf1e",
            (string?)record.Element("index")!.Elements("term").Single(term => (string?)term.Attribute("key") == "request-sha256").Attribute("value"));
        Assert.IsTrue(BacklogLifecycle.Complete(_tempDir, 2, complete).Envelope!.AlreadyApplied);
    }

    [TestMethod]
    public void ClosureSummary_FingerprintsDistinguishCarriageReturns_AndChecksAreOptional()
    {
        var input = CreateInput();
        Assert.IsTrue(BacklogLifecycle.Add(_tempDir, 1, input).Success);
        var close = new BacklogTransitionInput(input.Id, "20260825T080100Z-backlog-summary-cancel", "tester",
            input.OccurredAt.AddMinutes(1), null, "Reason\r\nOutcome\rEnd");
        var cancelled = BacklogLifecycle.Cancel(_tempDir, 2, close);
        Assert.IsTrue(cancelled.Success, Join(cancelled.Diagnostics));
        var replay = BacklogLifecycle.Cancel(_tempDir, 2, close);
        Assert.IsTrue(replay.Success, Join(replay.Diagnostics));
        var changed = BacklogLifecycle.Cancel(_tempDir, 3, close with { Summary = close.Summary!.Replace("\r", "", StringComparison.Ordinal) });
        Assert.IsFalse(changed.Success);
        Assert.IsTrue(changed.Diagnostics.Any(diagnostic => diagnostic.Code == DiagnosticCodes.IdempotencyConflict));
        var note = close with { OperationId = "20260825T080200Z-backlog-cancel-followup", OccurredAt = close.OccurredAt.AddMinutes(1), Summary = "Additional explanation." };
        Assert.IsTrue(BacklogLifecycle.Record(_tempDir, 3, note).Success);
        Assert.AreEqual("cancelled", BacklogLifecycle.Records(_tempDir, input.Id).Result!.Status);
    }

    [TestMethod]
    public void RecordDryRunAndRead_AreNonMutating_AndRespectRecoveryPreview()
    {
        var input = CreateInput();
        Assert.IsTrue(BacklogLifecycle.Add(_tempDir, 1, input).Success);
        var note = new BacklogTransitionInput(input.Id, "20260825T080100Z-backlog-dry-note", "tester", input.OccurredAt.AddMinutes(1), null, "Preview progress.");
        var path = Path.Combine(_tempDir, "backlog.xml");
        var before = File.ReadAllBytes(path);
        var preview = BacklogLifecycle.Record(_tempDir, 2, note, dryRun: true);
        Assert.IsTrue(preview.Success, Join(preview.Diagnostics));
        StringAssert.Contains(preview.Envelope!.ToXmlString(), "dry_run=\"true\"");
        CollectionAssert.AreEqual(before, File.ReadAllBytes(path));
        Assert.IsTrue(BacklogLifecycle.Record(_tempDir, 2, note).Success);
        AssertDryRunReplayBlocked("backlog record", () => BacklogLifecycle.Record(_tempDir, 2, note, dryRun: true));
        before = File.ReadAllBytes(path);
        Assert.IsTrue(BacklogLifecycle.Records(_tempDir, input.Id).Success);
        CollectionAssert.AreEqual(before, File.ReadAllBytes(path));
        var stale = BacklogLifecycle.Record(_tempDir, 2, note with { OperationId = "20260825T080200Z-backlog-stale-note" });
        Assert.IsFalse(stale.Success);
        Assert.IsTrue(stale.Diagnostics.Any(diagnostic => diagnostic.Code == DiagnosticCodes.RevisionConflict));
    }

    [TestMethod]
    public void RecordValidation_RejectsMalformedInputsWithoutChangingTheBacklog()
    {
        var input = CreateInput();
        Assert.IsTrue(BacklogLifecycle.Add(_tempDir, 1, input).Success);
        var note = new BacklogTransitionInput(input.Id, "20260825T080100Z-backlog-invalid-note", "tester", input.OccurredAt.AddMinutes(1), null, "Valid note.");
        var path = Path.Combine(_tempDir, "backlog.xml");
        var before = File.ReadAllBytes(path);
        foreach (var invalid in new[]
        {
            note with { Summary = null }, note with { Summary = " " }, note with { Summary = "invalid\0text" },
            note with { Checks = [new BacklogCheck("command", "unknown", "Invalid result.")] },
            note with { Checks = [new BacklogCheck("command", "passed", "Missing command.", ExitCode: 0)] },
            note with { ResolvingTask = "20260823-missing-task" }, note with { Id = "20260825-missing-backlog" }
        })
        {
            Assert.IsFalse(BacklogLifecycle.Record(_tempDir, 2, invalid).Success);
            CollectionAssert.AreEqual(before, File.ReadAllBytes(path));
        }
        Assert.IsFalse(BacklogLifecycle.Records(_tempDir, input.Id, 0).Success);
        Assert.IsFalse(BacklogLifecycle.Records(_tempDir, "20260825-missing-backlog").Success);
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
