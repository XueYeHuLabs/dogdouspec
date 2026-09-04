using System.Xml;
using System.Xml.Linq;
using DogdouSpec.Core.Backlog;
using DogdouSpec.Core.Diagnostics;
using DogdouSpec.Core.Knowledge;
using DogdouSpec.Core.Transactions;
using DogdouSpec.Core.Validation;
using DogdouSpec.Core.Workspace;

namespace DogdouSpec.Core.Tests;

[TestClass]
public sealed class KnowledgeLifecycleCoreTests
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
        _tempDir = Path.Combine(Path.GetTempPath(), "dogdouspec-knowledge-core-" + Guid.NewGuid().ToString("N"));
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
    public void Lifecycle_AddReplayAndReceiptIntegrity()
    {
        var knowledgePath = Path.Combine(_tempDir, "knowledge.xml");
        var initialBytes = File.ReadAllBytes(knowledgePath);
        var create = CreateInput();

        // Initial add: revision 2 -> 3
        var (added, addEnvelope, addDiagnostics) = KnowledgeLifecycle.Add(_tempDir, 2, create);
        Assert.IsTrue(added, Join(addDiagnostics));
        Assert.IsNotNull(addEnvelope);
        Assert.AreEqual("knowledge add", addEnvelope.Command);
        Assert.IsFalse(addEnvelope.AlreadyApplied);
        Assert.AreEqual(3, addEnvelope.Documents.Single().Revision);

        // Verify persisted document, structure, provenance, records, and LF line endings
        var rawText = File.ReadAllText(knowledgePath);
        Assert.IsFalse(rawText.Contains("\r\n"), "knowledge.xml must use LF line endings.");
        Assert.IsTrue(rawText.EndsWith('\n'), "knowledge.xml must end with a newline.");

        var doc = XDocument.Load(knowledgePath);
        Assert.AreEqual("3", (string?)doc.Root?.Attribute("revision"));
        var entry = doc.Root?.Elements("entry")
            .FirstOrDefault(e => string.Equals((string?)e.Attribute("id"), create.Id, StringComparison.Ordinal));
        Assert.IsNotNull(entry);
        Assert.AreEqual("proposed", (string?)entry.Attribute("status"));
        Assert.IsNotNull(entry.Attribute("created_at"));
        Assert.IsNotNull(entry.Attribute("updated_at"));

        var index = entry.Element("index");
        Assert.IsNotNull(index);
        Assert.AreEqual(create.Summary, index.Element("summary")?.Value);
        var topicTerm = index.Elements("term").FirstOrDefault(t => (string?)t.Attribute("key") == "topic");
        Assert.IsNotNull(topicTerm);
        Assert.AreEqual(create.Topic, (string?)topicTerm.Attribute("value"));

        Assert.AreEqual(create.Statement, entry.Element("statement")?.Value);
        Assert.AreEqual(create.Rationale, entry.Element("rationale")?.Value);

        var sources = entry.Element("sources");
        Assert.IsNotNull(sources);
        var sourceRefs = sources.Elements("ref").ToList();
        Assert.AreEqual(2, sourceRefs.Count);
        Assert.IsTrue(sourceRefs.Any(r => (string?)r.Attribute("scope") == "project" &&
                                          (string?)r.Attribute("target") == "20260823-xpath-core" &&
                                          (string?)r.Attribute("relation") == "derived-from"));
        Assert.IsTrue(sourceRefs.Any(r => (string?)r.Attribute("scope") == "project" &&
                                          (string?)r.Attribute("target") == "20260823-task-xpath-projection" &&
                                          (string?)r.Attribute("relation") == "derived-from"));

        var records = entry.Element("records");
        Assert.IsNotNull(records);
        var record = records.Element("record");
        Assert.IsNotNull(record);
        Assert.AreEqual(create.OperationId + "-receipt", (string?)record.Attribute("id"));
        Assert.AreEqual("discussion", (string?)record.Attribute("kind"));
        Assert.AreEqual("informational", (string?)record.Attribute("status"));
        Assert.AreEqual(create.Actor, (string?)record.Attribute("actor"));
        Assert.AreEqual(create.OperationId, (string?)record.Attribute("operation_id"));
        Assert.IsNull(record.Element("index")?.Elements("term")
            .FirstOrDefault(t => (string?)t.Attribute("key") == "operation-id"),
            "Redundant operation-id index term must be omitted.");
        Assert.AreEqual("add", (string?)record.Element("index")?.Elements("term")
            .FirstOrDefault(t => (string?)t.Attribute("key") == "action")?.Attribute("value"));
        Assert.IsNotNull((string?)record.Element("index")?.Elements("term")
            .FirstOrDefault(t => (string?)t.Attribute("key") == "request-sha256")?.Attribute("value"));

        var afterAddBytes = File.ReadAllBytes(knowledgePath);

        // Exact replay with current revision (3)
        var (replayedCur, replayCurEnv, replayCurDiags) = KnowledgeLifecycle.Add(_tempDir, 3, create);
        Assert.IsTrue(replayedCur, Join(replayCurDiags));
        Assert.IsNotNull(replayCurEnv);
        Assert.IsTrue(replayCurEnv.AlreadyApplied);
        Assert.AreEqual(3, replayCurEnv.Documents.Single().Revision);
        CollectionAssert.AreEqual(afterAddBytes, File.ReadAllBytes(knowledgePath), "Replay with current revision must be byte-identical.");

        // Exact replay with current-1 revision (2)
        var (replayedPrev, replayPrevEnv, replayPrevDiags) = KnowledgeLifecycle.Add(_tempDir, 2, create);
        Assert.IsTrue(replayedPrev, Join(replayPrevDiags));
        Assert.IsNotNull(replayPrevEnv);
        Assert.IsTrue(replayPrevEnv.AlreadyApplied);
        Assert.AreEqual(3, replayPrevEnv.Documents.Single().Revision);
        CollectionAssert.AreEqual(afterAddBytes, File.ReadAllBytes(knowledgePath), "Replay with current-1 revision must be byte-identical.");

        // Idempotency conflict: divergent payload (changed Summary)
        var changedPayload = create with { Summary = "Divergent summary" };
        var (divSuccess, _, divDiags) = KnowledgeLifecycle.Add(_tempDir, 3, changedPayload);
        Assert.IsFalse(divSuccess);
        Assert.IsTrue(divDiags.Any(d => d.Code == DiagnosticCodes.IdempotencyConflict));

        // Idempotency conflict: reused operation ID for different entry ID
        var diffEntry = create with { Id = "20260905-knowledge-different" };
        var (diffSuccess, _, diffDiags) = KnowledgeLifecycle.Add(_tempDir, 3, diffEntry);
        Assert.IsFalse(diffSuccess);
        Assert.IsTrue(diffDiags.Any(d => d.Code == DiagnosticCodes.IdempotencyConflict));

        // Idempotency conflict: revision outside current and current-1
        var (badRevSuccess, _, badRevDiags) = KnowledgeLifecycle.Add(_tempDir, 1, create);
        Assert.IsFalse(badRevSuccess);
        Assert.IsTrue(badRevDiags.Any(d => d.Code == DiagnosticCodes.IdempotencyConflict));
    }

    [TestMethod]
    public void Lifecycle_DuplicateIdAndReusedReceiptConflict()
    {
        var input = CreateInput();
        var (added, _, addDiags) = KnowledgeLifecycle.Add(_tempDir, 2, input);
        Assert.IsTrue(added, Join(addDiags));

        // Duplicate entry ID with a new operation ID
        var duplicate = input with { OperationId = "20260905T080100Z-knowledge-add-second" };
        var (dupSuccess, _, dupDiags) = KnowledgeLifecycle.Add(_tempDir, 3, duplicate);
        Assert.IsFalse(dupSuccess);
        Assert.IsTrue(dupDiags.Any(d => d.Code == DiagnosticCodes.DuplicateId));

        // Duplicate receipt in XML causes replay to report idempotency conflict
        var path = Path.Combine(_tempDir, "knowledge.xml");
        var document = XDocument.Load(path);
        var entry = document.Root!.Elements("entry").First(e => (string?)e.Attribute("id") == input.Id);
        var records = entry.Element("records")!;
        var duplicateRecord = new XElement(records.Element("record")!);
        duplicateRecord.SetAttributeValue("id", "20260905T080200Z-knowledge-duplicate-receipt");
        records.Add(duplicateRecord);
        document.Save(path);

        var (reusedSuccess, _, reusedDiags) = KnowledgeLifecycle.Add(_tempDir, 3, input);
        Assert.IsFalse(reusedSuccess);
        Assert.IsTrue(reusedDiags.Any(d => d.Code == DiagnosticCodes.IdempotencyConflict));
    }

    [TestMethod]
    public void Lifecycle_StaleRevision_ReturnsRevisionConflict()
    {
        var input = CreateInput();
        var (added, _, addDiags) = KnowledgeLifecycle.Add(_tempDir, 2, input);
        Assert.IsTrue(added, Join(addDiags));

        // Unrelated new operation with stale expected revision 2 (current is 3)
        var stale = CreateInput() with
        {
            Id = "20260905-knowledge-second",
            OperationId = "20260905T080300Z-knowledge-add-second"
        };
        var (staleSuccess, _, staleDiags) = KnowledgeLifecycle.Add(_tempDir, 2, stale);
        Assert.IsFalse(staleSuccess);
        Assert.IsTrue(staleDiags.Any(d => d.Code == DiagnosticCodes.RevisionConflict));
    }

    [TestMethod]
    public void Lifecycle_InvalidReferences_RejectDuplicateDanglingAmbiguousWrongType()
    {
        // Dangling source iteration
        var danglingIter = CreateInput() with
        {
            SourceIterations = new[] { "20260823-missing-iteration" }
        };
        var (iterSuccess, _, iterDiags) = KnowledgeLifecycle.Add(_tempDir, 2, danglingIter);
        Assert.IsFalse(iterSuccess);
        Assert.IsTrue(iterDiags.Any(d => d.Code == DiagnosticCodes.DanglingReference));

        // Dangling source task
        var danglingTask = CreateInput() with
        {
            SourceTasks = new[] { "20260823-missing-task" }
        };
        var (taskSuccess, _, taskDiags) = KnowledgeLifecycle.Add(_tempDir, 2, danglingTask);
        Assert.IsFalse(taskSuccess);
        Assert.IsTrue(taskDiags.Any(d => d.Code == DiagnosticCodes.DanglingReference));

        // Wrong reference target type: source iteration pointing to a task
        var wrongTypeIter = CreateInput() with
        {
            SourceIterations = new[] { "20260823-task-xpath-projection" },
            SourceTasks = Array.Empty<string>()
        };
        var (wtIterSuccess, _, wtIterDiags) = KnowledgeLifecycle.Add(_tempDir, 2, wrongTypeIter);
        Assert.IsFalse(wtIterSuccess);
        Assert.IsTrue(wtIterDiags.Any(d => d.Code == DiagnosticCodes.InvalidReferenceTargetType));

        // Wrong reference target type: source task pointing to an iteration
        var wrongTypeTask = CreateInput() with
        {
            SourceIterations = Array.Empty<string>(),
            SourceTasks = new[] { "20260823-xpath-core" }
        };
        var (wtTaskSuccess, _, wtTaskDiags) = KnowledgeLifecycle.Add(_tempDir, 2, wrongTypeTask);
        Assert.IsFalse(wtTaskSuccess);
        Assert.IsTrue(wtTaskDiags.Any(d => d.Code == DiagnosticCodes.InvalidReferenceTargetType));

        // Duplicate source references
        var duplicateSources = CreateInput() with
        {
            SourceIterations = new[] { "20260823-xpath-core", "20260823-xpath-core" }
        };
        var (dupSrcSuccess, _, dupSrcDiags) = KnowledgeLifecycle.Add(_tempDir, 2, duplicateSources);
        Assert.IsFalse(dupSrcSuccess);
        Assert.IsTrue(dupSrcDiags.Any(d => d.Code == DiagnosticCodes.InvalidArgument));

        // Invalid ID grammar in source reference
        var invalidIdRef = CreateInput() with
        {
            SourceIterations = new[] { "not-a-time-first-id" }
        };
        var (invIdSuccess, _, invIdDiags) = KnowledgeLifecycle.Add(_tempDir, 2, invalidIdRef);
        Assert.IsFalse(invIdSuccess);
        Assert.IsTrue(invIdDiags.Any(d => d.Code == DiagnosticCodes.InvalidIdGrammar));

        // No sources provided
        var noSources = CreateInput() with
        {
            SourceIterations = Array.Empty<string>(),
            SourceTasks = Array.Empty<string>()
        };
        var (noSrcSuccess, _, noSrcDiags) = KnowledgeLifecycle.Add(_tempDir, 2, noSources);
        Assert.IsFalse(noSrcSuccess);
        Assert.IsTrue(noSrcDiags.Any(d => d.Code == DiagnosticCodes.InvalidArgument));

        // Blank topic
        var blankTopic = CreateInput() with { Topic = "   " };
        var (blankTopicSuccess, _, blankTopicDiags) = KnowledgeLifecycle.Add(_tempDir, 2, blankTopic);
        Assert.IsFalse(blankTopicSuccess);
        Assert.IsTrue(blankTopicDiags.Any(d => d.Code == DiagnosticCodes.InvalidArgument));

        // Invalid topic grammar (e.g. contains space)
        var invalidTopic = CreateInput() with { Topic = "has space" };
        var (invTopicSuccess, _, invTopicDiags) = KnowledgeLifecycle.Add(_tempDir, 2, invalidTopic);
        Assert.IsFalse(invTopicSuccess);
        Assert.IsTrue(invTopicDiags.Any(d => d.Code == DiagnosticCodes.InvalidArgument));
    }

    [TestMethod]
    public void Lifecycle_DryRunAndPendingRecovery()
    {
        var input = CreateInput();
        var knowledgePath = Path.Combine(_tempDir, "knowledge.xml");
        var beforeBytes = File.ReadAllBytes(knowledgePath);

        // Dry-run should succeed and preview prospective revision, but perform zero writes
        var (drySuccess, dryEnv, dryDiags) = KnowledgeLifecycle.Add(_tempDir, 2, input, dryRun: true);
        Assert.IsTrue(drySuccess, Join(dryDiags));
        Assert.IsNotNull(dryEnv);
        Assert.AreEqual(3, dryEnv.Documents.Single().Revision);
        CollectionAssert.AreEqual(beforeBytes, File.ReadAllBytes(knowledgePath), "Dry-run must perform zero writes.");

        // Now actually add
        var (added, _, addDiags) = KnowledgeLifecycle.Add(_tempDir, 2, input);
        Assert.IsTrue(added, Join(addDiags));

        // Exact dry-run replay while recovery is pending must fail closed
        AssertDryRunReplayBlocked("knowledge add", () => KnowledgeLifecycle.Add(_tempDir, 3, input, dryRun: true));
    }

    [TestMethod]
    public void Lifecycle_List_DeterministicSortingAndFiltering()
    {
        // Initial list has 1 entry from demo: 20260801-knowledge-xml-authority (verified, topic xml-authority)
        var (listInitSuccess, listInitResult, listInitDiags) = KnowledgeLifecycle.List(_tempDir);
        Assert.IsTrue(listInitSuccess, Join(listInitDiags));
        Assert.IsNotNull(listInitResult);
        Assert.AreEqual(2, listInitResult.Revision);
        Assert.AreEqual(1, listInitResult.Items.Count);
        Assert.AreEqual("20260801-knowledge-xml-authority", listInitResult.Items[0].Id);
        Assert.AreEqual("verified", listInitResult.Items[0].Status);
        Assert.AreEqual("xml-authority", listInitResult.Items[0].Topic);

        // Add proposed entry
        var input = CreateInput();
        var (added, _, addDiags) = KnowledgeLifecycle.Add(_tempDir, 2, input);
        Assert.IsTrue(added, Join(addDiags));

        // List all: sorted deterministically by ordinal ID
        var (listAllSuccess, listAllResult, listAllDiags) = KnowledgeLifecycle.List(_tempDir);
        Assert.IsTrue(listAllSuccess, Join(listAllDiags));
        Assert.AreEqual(3, listAllResult!.Revision);
        Assert.AreEqual(2, listAllResult.Items.Count);
        Assert.AreEqual("20260801-knowledge-xml-authority", listAllResult.Items[0].Id);
        Assert.AreEqual(input.Id, listAllResult.Items[1].Id);

        // Filter by status: proposed
        var (listPropSuccess, listPropResult, _) = KnowledgeLifecycle.List(_tempDir, status: "proposed");
        Assert.IsTrue(listPropSuccess);
        Assert.AreEqual(1, listPropResult!.Items.Count);
        Assert.AreEqual(input.Id, listPropResult.Items[0].Id);

        // Filter by status: verified
        var (listVerSuccess, listVerResult, _) = KnowledgeLifecycle.List(_tempDir, status: "verified");
        Assert.IsTrue(listVerSuccess);
        Assert.AreEqual(1, listVerResult!.Items.Count);
        Assert.AreEqual("20260801-knowledge-xml-authority", listVerResult.Items[0].Id);

        // Filter by topic: architectural-facts
        var (listTopSuccess, listTopResult, _) = KnowledgeLifecycle.List(_tempDir, topic: input.Topic);
        Assert.IsTrue(listTopSuccess);
        Assert.AreEqual(1, listTopResult!.Items.Count);
        Assert.AreEqual(input.Id, listTopResult.Items[0].Id);

        // Filter by non-matching topic: empty list succeeds
        var (listNoneSuccess, listNoneResult, _) = KnowledgeLifecycle.List(_tempDir, topic: "non-existent");
        Assert.IsTrue(listNoneSuccess);
        Assert.AreEqual(0, listNoneResult!.Items.Count);

        // Invalid status returns error
        var (listInvSuccess, _, listInvDiags) = KnowledgeLifecycle.List(_tempDir, status: "invalid-status");
        Assert.IsFalse(listInvSuccess);
        Assert.IsTrue(listInvDiags.Any(d => d.Code == DiagnosticCodes.InvalidArgument));
    }

    [TestMethod]
    public void Lifecycle_CrossDocumentOperationIdCollision_RejectsWithZeroWrites_AndPreservesExactReplay()
    {
        var knowledgePath = Path.Combine(_tempDir, "knowledge.xml");
        var tasksPath = Path.Combine(_tempDir, "20260823-xpath-core", "tasks.xml");

        // 1. Plant an operation_id outside knowledge.xml in tasks.xml
        const string plantedTaskOpId = "20260905T082000Z-task-planted-op";
        var tasksDoc = XDocument.Load(tasksPath);
        var taskRecord = tasksDoc.Descendants("record").First();
        taskRecord.SetAttributeValue("operation_id", plantedTaskOpId);
        tasksDoc.Save(tasksPath);

        var beforeBytes = File.ReadAllBytes(knowledgePath);

        // Attempt to create knowledge entry with planted task operation ID
        var collidingWithTask = CreateInput() with
        {
            Id = "20260905-knowledge-task-collision",
            OperationId = plantedTaskOpId
        };
        var (taskCollSuccess, taskCollEnvelope, taskCollDiags) = KnowledgeLifecycle.Add(_tempDir, 2, collidingWithTask);
        Assert.IsFalse(taskCollSuccess);
        Assert.IsNull(taskCollEnvelope);
        Assert.IsTrue(taskCollDiags.Any(d => d.Code == DiagnosticCodes.IdempotencyConflict), Join(taskCollDiags));
        CollectionAssert.AreEqual(beforeBytes, File.ReadAllBytes(knowledgePath),
            "Cross-document collision with planted task operation ID must perform zero knowledge.xml writes.");

        // 2. Also reuse an operation ID from backlog.xml created via real BacklogLifecycle
        const string backlogOpId = "20260905T082100Z-backlog-real-op";
        var backlogInput = new BacklogCreateInput(
            "20260905-backlog-collision-source",
            backlogOpId,
            "tester",
            new DateTimeOffset(2026, 9, 5, 8, 21, 0, TimeSpan.Zero),
            "defect",
            "p1",
            "Backlog item for collision test",
            "A statement.",
            "A rationale.",
            "An impact.",
            SourceIterations,
            SourceTasks,
            TargetIteration: "20260823-xpath-core",
            ReviewCondition: null);
        var (backlogAdded, _, backlogDiags) = BacklogLifecycle.Add(_tempDir, 1, backlogInput);
        Assert.IsTrue(backlogAdded, Join(backlogDiags));

        var knowledgeBeforeBacklog = File.ReadAllBytes(knowledgePath);
        var collidingWithBacklog = CreateInput() with
        {
            Id = "20260905-knowledge-backlog-collision",
            OperationId = backlogOpId
        };
        var (backlogCollSuccess, backlogCollEnvelope, backlogCollDiags) = KnowledgeLifecycle.Add(_tempDir, 2, collidingWithBacklog);
        Assert.IsFalse(backlogCollSuccess);
        Assert.IsNull(backlogCollEnvelope);
        Assert.IsTrue(backlogCollDiags.Any(d => d.Code == DiagnosticCodes.IdempotencyConflict), Join(backlogCollDiags));
        CollectionAssert.AreEqual(knowledgeBeforeBacklog, File.ReadAllBytes(knowledgePath),
            "Cross-document collision with backlog operation ID must perform zero knowledge.xml writes.");

        // 3. Now perform a valid knowledge add with a fresh operation ID
        var validKnowledge = CreateInput() with
        {
            Id = "20260905-knowledge-valid",
            OperationId = "20260905T082200Z-knowledge-valid-op"
        };
        var (validAdded, validEnvelope, validDiags) = KnowledgeLifecycle.Add(_tempDir, 2, validKnowledge);
        Assert.IsTrue(validAdded, Join(validDiags));
        Assert.IsNotNull(validEnvelope);
        Assert.AreEqual(3, validEnvelope.Documents.Single().Revision);

        var doc = XDocument.Load(knowledgePath);
        var entry = doc.Root?.Elements("entry").First(e => (string?)e.Attribute("id") == validKnowledge.Id);
        var record = entry?.Element("records")?.Element("record");
        Assert.IsNotNull(record);
        Assert.AreEqual(validKnowledge.OperationId, (string?)record.Attribute("operation_id"));
        Assert.IsNull(record.Element("index")?.Elements("term").FirstOrDefault(t => (string?)t.Attribute("key") == "operation-id"));

        var afterValidBytes = File.ReadAllBytes(knowledgePath);

        // 4. Exact replay still works cleanly (current and current-1)
        var (replayCurSuccess, replayCurEnv, replayCurDiags) = KnowledgeLifecycle.Add(_tempDir, 3, validKnowledge);
        Assert.IsTrue(replayCurSuccess, Join(replayCurDiags));
        Assert.IsNotNull(replayCurEnv);
        Assert.IsTrue(replayCurEnv.AlreadyApplied);
        Assert.AreEqual(3, replayCurEnv.Documents.Single().Revision);
        CollectionAssert.AreEqual(afterValidBytes, File.ReadAllBytes(knowledgePath),
            "Exact replay with current revision must perform zero writes.");

        var (replayPrevSuccess, replayPrevEnv, replayPrevDiags) = KnowledgeLifecycle.Add(_tempDir, 2, validKnowledge);
        Assert.IsTrue(replayPrevSuccess, Join(replayPrevDiags));
        Assert.IsNotNull(replayPrevEnv);
        Assert.IsTrue(replayPrevEnv.AlreadyApplied);
        Assert.AreEqual(3, replayPrevEnv.Documents.Single().Revision);
        CollectionAssert.AreEqual(afterValidBytes, File.ReadAllBytes(knowledgePath),
            "Exact replay with previous revision must perform zero writes.");

        // 5. Another knowledge entry attempting to reuse the knowledge operation ID fails with IDEMPOTENCY_CONFLICT and zero writes
        var collidingWithKnowledge = CreateInput() with
        {
            Id = "20260905-knowledge-another-reuse-op",
            OperationId = validKnowledge.OperationId
        };
        var (kCollSuccess, kCollEnvelope, kCollDiags) = KnowledgeLifecycle.Add(_tempDir, 3, collidingWithKnowledge);
        Assert.IsFalse(kCollSuccess);
        Assert.IsNull(kCollEnvelope);
        Assert.IsTrue(kCollDiags.Any(d => d.Code == DiagnosticCodes.IdempotencyConflict), Join(kCollDiags));
        CollectionAssert.AreEqual(afterValidBytes, File.ReadAllBytes(knowledgePath),
            "Reusing knowledge operation ID for another entry must perform zero writes.");

        // 6. Verify knowledge receipt is indexed in ProjectSemanticIndex.OperationReceiptsById
        var (discOk, documents, _) = WorkspaceDiscovery.EnumerateDocuments(_tempDir);
        Assert.IsTrue(discOk);
        var loaded = documents.Select(d =>
        {
            using var stream = File.OpenRead(d.FullPath);
            using var reader = XmlReader.Create(stream);
            return (d, XDocument.Load(reader));
        }).ToList();
        var semanticIndex = ProjectSemanticIndex.Build(loaded);
        Assert.IsTrue(semanticIndex.OperationReceiptsById.ContainsKey(validKnowledge.OperationId),
            "Knowledge receipt must be present in ProjectSemanticIndex.OperationReceiptsById.");
        var indexedReceipt = semanticIndex.OperationReceiptsById[validKnowledge.OperationId].Single();
        Assert.AreEqual("knowledge.xml", indexedReceipt.Document.RelativePath);
        Assert.AreEqual("record", indexedReceipt.ElementName);
        Assert.AreEqual(validKnowledge.OperationId + "-receipt", indexedReceipt.RecordId);
    }

    private void AssertDryRunReplayBlocked(
        string family,
        Func<(bool Success, MutationEnvelope? Envelope, IReadOnlyList<Diagnostic> Diagnostics)> replay)
    {
        var knowledgePath = Path.Combine(_tempDir, "knowledge.xml");
        var beforeBytes = File.ReadAllBytes(knowledgePath);
        var pendingRoot = Path.Combine(_tempDir, "_tmp", "tx_pending_" + family.Replace(' ', '_'));
        var pendingDirectory = Path.Combine(pendingRoot, "staged");
        Directory.CreateDirectory(pendingDirectory);

        var result = replay();

        Assert.IsFalse(result.Success, $"{family} exact dry-run replay must fail closed while recovery is pending.");
        Assert.IsTrue(result.Diagnostics.Any(d => d.Code == DiagnosticCodes.RecoveryFailed), Join(result.Diagnostics));
        Assert.IsTrue(Directory.Exists(pendingDirectory), $"{family} dry-run must preserve pending recovery artifacts.");
        CollectionAssert.AreEqual(beforeBytes, File.ReadAllBytes(knowledgePath));
        Directory.Delete(pendingRoot, recursive: true);
    }

    private static KnowledgeCreateInput CreateInput() => new(
        "20260905-knowledge-sample-rule",
        "20260905T080000Z-knowledge-add-sample",
        "tester",
        new DateTimeOffset(2026, 9, 5, 8, 0, 0, TimeSpan.Zero),
        "architectural-facts",
        "Sample architectural knowledge summary",
        "DogdouSpec knowledge documents retain long-lived reusable architectural facts.",
        "Reusable facts outlive individual iterations and provide cross-iteration context.",
        SourceIterations,
        SourceTasks);

    private static string Join(IReadOnlyList<Diagnostic> diagnostics) =>
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