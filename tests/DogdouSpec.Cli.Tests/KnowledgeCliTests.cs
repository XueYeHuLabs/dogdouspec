using System.Globalization;
using System.Runtime.CompilerServices;
using System.Xml.Linq;
using DogdouSpec.Cli;
using DogdouSpec.Cli.Commands;

namespace DogdouSpec.Cli.Tests;

[TestClass]
public sealed class KnowledgeCliTests
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
        _tempDir = Path.Combine(Path.GetTempPath(), "dogdouspec-knowledge-cli-" + Guid.NewGuid().ToString("N"));
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
    public void KnowledgeCli_HelpOutput_AllCommands()
    {
        var (rootCode, rootOut, _) = RunCli("knowledge", "--help");
        Assert.AreEqual(0, rootCode);
        StringAssert.Contains(rootOut, "add");
        StringAssert.Contains(rootOut, "list");

        var (addCode, addOut, _) = RunCli("knowledge", "add", "--help");
        Assert.AreEqual(0, addCode);
        StringAssert.Contains(addOut, "--id");
        StringAssert.Contains(addOut, "--operation-id");
        StringAssert.Contains(addOut, "--actor");
        StringAssert.Contains(addOut, "--occurred-at");
        StringAssert.Contains(addOut, "--topic");
        StringAssert.Contains(addOut, "--summary");
        StringAssert.Contains(addOut, "--statement");
        StringAssert.Contains(addOut, "--rationale");
        StringAssert.Contains(addOut, "--source-iteration");
        StringAssert.Contains(addOut, "--source-task");
        StringAssert.Contains(addOut, "--expected-revision");
        StringAssert.Contains(addOut, "--dry-run");
        StringAssert.Contains(addOut, "--workspace-root");
        StringAssert.Contains(addOut, "--format");

        var (listCode, listOut, _) = RunCli("knowledge", "list", "--help");
        Assert.AreEqual(0, listCode);
        StringAssert.Contains(listOut, "--status");
        StringAssert.Contains(listOut, "--topic");
        StringAssert.Contains(listOut, "--workspace-root");
        StringAssert.Contains(listOut, "--format");
    }

    [TestMethod]
    public void KnowledgeCli_EndToEndLifecycle_AddReplayConflictsListAndAutoRevision()
    {
        var entryId = "20260905-knowledge-cli-rule";
        var addArgs = new[]
        {
            "knowledge", "add",
            "--id", entryId,
            "--operation-id", "20260905T090000Z-knowledge-cli-add",
            "--actor", "cli-tester",
            "--occurred-at", "2026-09-05T09:00:00Z",
            "--topic", "cli-conventions",
            "--summary", "CLI knowledge entry summary",
            "--statement", "CLI commands require explicit provenance.",
            "--rationale", "Preserving execution provenance ensures replayability.",
            "--source-iteration", "20260823-xpath-core",
            "--source-task", "20260823-task-xpath-projection",
            "--expected-revision", "2",
            "--workspace-root", _tempDir,
            "--format", "xml"
        };

        // 1. Initial add in XML format
        var (addCode, addOut, addErr) = RunCli(addArgs);
        Assert.AreEqual(0, addCode, addErr);
        StringAssert.Contains(addOut, "command=\"knowledge add\"");
        StringAssert.Contains(addOut, "already_applied=\"false\"");
        StringAssert.Contains(addOut, "previous_revision=\"2\"");
        StringAssert.Contains(addOut, "revision=\"3\"");

        var kDoc = XDocument.Load(Path.Combine(_tempDir, ".dogdouspec", "knowledge.xml"));
        var kEntry = kDoc.Root?.Elements("entry").FirstOrDefault(e => (string?)e.Attribute("id") == entryId);
        var kRecord = kEntry?.Element("records")?.Element("record");
        Assert.IsNotNull(kRecord);
        Assert.AreEqual("20260905T090000Z-knowledge-cli-add", (string?)kRecord.Attribute("operation_id"));
        Assert.IsNull(kRecord.Element("index")?.Elements("term").FirstOrDefault(t => (string?)t.Attribute("key") == "operation-id"));

        // 2. Exact replay with current revision (3)
        var replayArgsCurrent = addArgs.ToArray();
        replayArgsCurrent[23] = "3";
        var (replayCurCode, replayCurOut, replayCurErr) = RunCli(replayArgsCurrent);
        Assert.AreEqual(0, replayCurCode, replayCurErr);
        StringAssert.Contains(replayCurOut, "already_applied=\"true\"");
        StringAssert.Contains(replayCurOut, "revision=\"3\"");

        // 3. Exact replay with current-1 revision (2)
        var (replayPrevCode, replayPrevOut, replayPrevErr) = RunCli(addArgs);
        Assert.AreEqual(0, replayPrevCode, replayPrevErr);
        StringAssert.Contains(replayPrevOut, "already_applied=\"true\"");
        StringAssert.Contains(replayPrevOut, "revision=\"3\"");

        // 4. Stale revision conflict: new operation ID with stale expected revision 2
        var staleArgs = addArgs.ToArray();
        staleArgs[3] = "20260905-knowledge-cli-second";
        staleArgs[5] = "20260905T090100Z-knowledge-cli-second";
        var (staleCode, _, staleErr) = RunCli(staleArgs);
        Assert.AreNotEqual(0, staleCode);
        StringAssert.Contains(staleErr, "REVISION_CONFLICT");

        // 5. Idempotency conflict: same operation ID with divergent payload
        var divArgs = replayArgsCurrent.ToArray();
        divArgs[13] = "Divergent summary content";
        var (divCode, _, divErr) = RunCli(divArgs);
        Assert.AreNotEqual(0, divCode);
        StringAssert.Contains(divErr, "IDEMPOTENCY_CONFLICT");

        // 6. Auto-revision add: omit --expected-revision
        var autoRevArgs = new[]
        {
            "knowledge", "add",
            "--id", "20260905-knowledge-cli-autorev",
            "--operation-id", "20260905T090200Z-knowledge-cli-autorev",
            "--actor", "cli-tester",
            "--occurred-at", "2026-09-05T09:02:00Z",
            "--topic", "cli-autorev",
            "--summary", "Auto revision summary",
            "--statement", "Auto revision fact.",
            "--rationale", "Auto revision rationale.",
            "--source-iteration", "20260823-xpath-core",
            "--workspace-root", _tempDir,
            "--format", "human"
        };
        var (autoRevCode, autoRevOut, autoRevErr) = RunCli(autoRevArgs);
        Assert.AreEqual(0, autoRevCode, autoRevErr);
        StringAssert.Contains(autoRevOut, "Mutation applied (knowledge add):");
        StringAssert.Contains(autoRevOut, "revision 4");

        // 7. List in XML format
        var (listXmlCode, listXmlOut, listXmlErr) = RunCli(
            "knowledge", "list",
            "--workspace-root", _tempDir,
            "--format", "xml");
        Assert.AreEqual(0, listXmlCode, listXmlErr);
        StringAssert.Contains(listXmlOut, "<knowledge-list revision=\"4\">");
        StringAssert.Contains(listXmlOut, $"id=\"{entryId}\"");
        StringAssert.Contains(listXmlOut, "id=\"20260905-knowledge-cli-autorev\"");

        // 8. List in Human format
        var (listHumCode, listHumOut, listHumErr) = RunCli(
            "knowledge", "list",
            "--workspace-root", _tempDir,
            "--format", "human");
        Assert.AreEqual(0, listHumCode, listHumErr);
        StringAssert.Contains(listHumOut, "Knowledge (Revision: 4)");
        StringAssert.Contains(listHumOut, entryId);

        // 9. Filter by status
        var (filterPropCode, filterPropOut, _) = RunCli(
            "knowledge", "list",
            "--status", "proposed",
            "--workspace-root", _tempDir,
            "--format", "xml");
        Assert.AreEqual(0, filterPropCode);
        StringAssert.Contains(filterPropOut, $"id=\"{entryId}\"");

        var (filterVerCode, filterVerOut, _) = RunCli(
            "knowledge", "list",
            "--status", "verified",
            "--workspace-root", _tempDir,
            "--format", "xml");
        Assert.AreEqual(0, filterVerCode);
        StringAssert.Contains(filterVerOut, "id=\"20260801-knowledge-xml-authority\"");
        Assert.IsFalse(filterVerOut.Contains(entryId, StringComparison.Ordinal));

        // 10. Filter by topic
        var (filterTopCode, filterTopOut, _) = RunCli(
            "knowledge", "list",
            "--topic", "cli-conventions",
            "--workspace-root", _tempDir,
            "--format", "xml");
        Assert.AreEqual(0, filterTopCode);
        StringAssert.Contains(filterTopOut, $"id=\"{entryId}\"");

        // 11. Empty list result succeeds
        var (filterNoneCode, filterNoneOut, _) = RunCli(
            "knowledge", "list",
            "--topic", "non-existent-topic",
            "--workspace-root", _tempDir,
            "--format", "xml");
        Assert.AreEqual(0, filterNoneCode);
        StringAssert.Contains(filterNoneOut, "<knowledge-list revision=\"4\"");
        Assert.IsFalse(filterNoneOut.Contains("<entry", StringComparison.Ordinal));
    }

    [TestMethod]
    public void KnowledgeCli_InvalidTimestampAndReferences_FailsClosed()
    {
        // Invalid timestamp
        var (timeCode, _, timeErr) = RunCli(
            "knowledge", "add",
            "--id", "20260905-knowledge-cli-badtime",
            "--operation-id", "20260905T091000Z-knowledge-cli-badtime",
            "--actor", "cli-tester",
            "--occurred-at", "not-a-valid-timestamp",
            "--topic", "test",
            "--summary", "Bad time summary",
            "--statement", "Statement.",
            "--rationale", "Rationale.",
            "--source-iteration", "20260823-xpath-core",
            "--workspace-root", _tempDir,
            "--format", "xml");
        Assert.AreNotEqual(0, timeCode);
        StringAssert.Contains(timeErr, "INVALID_ARGUMENT");

        // Dangling reference
        var (dangCode, _, dangErr) = RunCli(
            "knowledge", "add",
            "--id", "20260905-knowledge-cli-dangling",
            "--operation-id", "20260905T091100Z-knowledge-cli-dangling",
            "--actor", "cli-tester",
            "--occurred-at", "2026-09-05T09:11:00Z",
            "--topic", "test",
            "--summary", "Dangling summary",
            "--statement", "Statement.",
            "--rationale", "Rationale.",
            "--source-task", "20260823-missing-task",
            "--workspace-root", _tempDir,
            "--format", "xml");
        Assert.AreNotEqual(0, dangCode);
        StringAssert.Contains(dangErr, "DANGLING_REFERENCE");

        // Wrong target type
        var (wtCode, _, wtErr) = RunCli(
            "knowledge", "add",
            "--id", "20260905-knowledge-cli-wrongtype",
            "--operation-id", "20260905T091200Z-knowledge-cli-wrongtype",
            "--actor", "cli-tester",
            "--occurred-at", "2026-09-05T09:12:00Z",
            "--topic", "test",
            "--summary", "Wrong type summary",
            "--statement", "Statement.",
            "--rationale", "Rationale.",
            "--source-iteration", "20260823-task-xpath-projection",
            "--workspace-root", _tempDir,
            "--format", "xml");
        Assert.AreNotEqual(0, wtCode);
        StringAssert.Contains(wtErr, "INVALID_REFERENCE_TARGET_TYPE");

        // Dry-run preview
        var knowledgePath = Path.Combine(_tempDir, ".dogdouspec", "knowledge.xml");
        var beforeBytes = File.ReadAllBytes(knowledgePath);
        var (dryCode, dryOut, dryErr) = RunCli(
            "knowledge", "add",
            "--id", "20260905-knowledge-cli-dryrun",
            "--operation-id", "20260905T091300Z-knowledge-cli-dryrun",
            "--actor", "cli-tester",
            "--occurred-at", "2026-09-05T09:13:00Z",
            "--topic", "test",
            "--summary", "Dry run summary",
            "--statement", "Statement.",
            "--rationale", "Rationale.",
            "--source-iteration", "20260823-xpath-core",
            "--dry-run",
            "--workspace-root", _tempDir,
            "--format", "xml");
        Assert.AreEqual(0, dryCode, dryErr);
        StringAssert.Contains(dryOut, "revision=\"3\"");
        CollectionAssert.AreEqual(beforeBytes, File.ReadAllBytes(knowledgePath), "Dry run must perform zero writes.");
    }

    [TestMethod]
    public void KnowledgeCli_CrossDocumentOperationIdCollision_RejectsWithZeroWrites()
    {
        // 1. Plant operation_id in tasks.xml
        var tasksPath = Path.Combine(_tempDir, ".dogdouspec", "20260823-xpath-core", "tasks.xml");
        var tasksDoc = XDocument.Load(tasksPath);
        var taskRecord = tasksDoc.Descendants("record").First();
        const string plantedOpId = "20260905T093000Z-cli-task-collision";
        taskRecord.SetAttributeValue("operation_id", plantedOpId);
        tasksDoc.Save(tasksPath);

        var knowledgePath = Path.Combine(_tempDir, ".dogdouspec", "knowledge.xml");
        var beforeBytes = File.ReadAllBytes(knowledgePath);

        // 2. Attempt knowledge add with that operation ID
        var (collCode, _, collErr) = RunCli(
            "knowledge", "add",
            "--id", "20260905-knowledge-cli-collision",
            "--operation-id", plantedOpId,
            "--actor", "cli-tester",
            "--occurred-at", "2026-09-05T09:30:00Z",
            "--topic", "cli-test",
            "--summary", "Colliding summary",
            "--statement", "Statement.",
            "--rationale", "Rationale.",
            "--source-iteration", "20260823-xpath-core",
            "--expected-revision", "2",
            "--workspace-root", _tempDir,
            "--format", "xml");

        Assert.AreNotEqual(0, collCode);
        StringAssert.Contains(collErr, "IDEMPOTENCY_CONFLICT");
        CollectionAssert.AreEqual(beforeBytes, File.ReadAllBytes(knowledgePath),
            "Cross-document operation ID collision must perform zero knowledge.xml writes.");
    }

    [TestMethod]
    public void IterationCompletion_Guidance_InitialAndReplay_XmlAndHuman_AndAbsence()
    {
        var iterId = "20260905-iteration-complete-guidance";

        // Setup an iteration with one criterion and task, and complete the task
        var createExit = Program.Main(new[]
        {
            "iteration", "create",
            "--id", iterId,
            "--kind", "feature",
            "--activate",
            "--criterion", "Initial completion criteria.",
            "--workspace-root", _tempDir
        });
        Assert.AreEqual(0, createExit);

        var quickExit = Program.Main(new[]
        {
            "task", "quick",
            "--iteration", iterId,
            "--title", "Completion test task",
            "--scope", ".",
            "--done-when", "Task finished",
            "--why", "For testing completion guidance",
            "--workspace-root", _tempDir
        });
        Assert.AreEqual(0, quickExit);

        var tasksPath = Path.Combine(_tempDir, ".dogdouspec", iterId, "tasks.xml");
        var tasksDoc = XDocument.Load(tasksPath);
        var taskId = (string)tasksDoc.Descendants("task").First().Attribute("id")!;

        var finishExit = Program.Main(new[]
        {
            "task", "finish",
            "--iteration", iterId,
            "--task", taskId,
            "--workspace-root", _tempDir
        });
        Assert.AreEqual(0, finishExit);

        // 1. Initial iteration complete in XML format: must emit guidance in a single well-formed XML document
        var (compCode, compOut, compErr) = RunCli(
            "iteration", "complete",
            "--iteration", iterId,
            "--accept-all",
            "--workspace-root", _tempDir,
            "--format", "xml");
        Assert.AreEqual(0, compCode, compErr);
        StringAssert.Contains(compOut, IterationCommand.KnowledgeGuidanceMessage);
        StringAssert.Contains(compOut, "<guidance>");
        var parsedXml = XDocument.Parse(compOut);
        Assert.AreEqual("mutation", parsedXml.Root?.Name.LocalName);
        Assert.AreEqual(IterationCommand.KnowledgeGuidanceMessage, parsedXml.Root?.Element("guidance")?.Value);

        // 2. Exact replay of iteration complete in XML format: must emit guidance in a single well-formed XML document
        var (replayCode, replayOut, replayErr) = RunCli(
            "iteration", "complete",
            "--iteration", iterId,
            "--accept-all",
            "--workspace-root", _tempDir,
            "--format", "xml");
        Assert.AreEqual(0, replayCode, replayErr);
        StringAssert.Contains(replayOut, IterationCommand.KnowledgeGuidanceMessage);
        var parsedReplayXml = XDocument.Parse(replayOut);
        Assert.AreEqual("mutation", parsedReplayXml.Root?.Name.LocalName);
        Assert.AreEqual("true", (string?)parsedReplayXml.Root?.Attribute("already_applied"));
        Assert.AreEqual(IterationCommand.KnowledgeGuidanceMessage, parsedReplayXml.Root?.Element("guidance")?.Value);

        // 3. Exact replay of iteration complete in Human format: must include guidance text
        var (replayHumCode, replayHumOut, replayHumErr) = RunCli(
            "iteration", "complete",
            "--iteration", iterId,
            "--accept-all",
            "--workspace-root", _tempDir,
            "--format", "human");
        Assert.AreEqual(0, replayHumCode, replayHumErr);
        StringAssert.Contains(replayHumOut, "Guidance: " + IterationCommand.KnowledgeGuidanceMessage);

        // 4. Raw iteration confirm with action="complete": initial + replay
        var iterId2 = "20260906-iteration-confirm-raw-guidance";
        Program.Main(new[]
        {
            "iteration", "create",
            "--id", iterId2,
            "--kind", "feature",
            "--activate",
            "--criterion", "Confirm raw completion criteria.",
            "--workspace-root", _tempDir
        });
        Program.Main(new[]
        {
            "task", "quick",
            "--iteration", iterId2,
            "--title", "Raw confirm task",
            "--scope", ".",
            "--done-when", "Task finished",
            "--why", "For testing raw confirm",
            "--workspace-root", _tempDir
        });
        var tasksPath2 = Path.Combine(_tempDir, ".dogdouspec", iterId2, "tasks.xml");
        var tasksDoc2 = XDocument.Load(tasksPath2);
        var taskId2 = (string)tasksDoc2.Descendants("task").First().Attribute("id")!;
        Program.Main(new[] { "task", "finish", "--iteration", iterId2, "--task", taskId2, "--workspace-root", _tempDir });

        var specPath2 = Path.Combine(_tempDir, ".dogdouspec", iterId2, "spec.xml");
        var specDoc2 = XDocument.Load(specPath2);
        var critId2 = (string)specDoc2.Descendants("criterion").First().Attribute("id")!;
        var specRev2 = (string)specDoc2.Root!.Attribute("revision")!;

        var tasksAfterFinish = XDocument.Load(tasksPath2);
        var tasksRev2 = (string)tasksAfterFinish.Root!.Attribute("revision")!;

        var confirmId = "20260906T100000Z-confirm-raw-complete";
        var confirmXml = $@"<?xml version=""1.0"" encoding=""utf-8""?>
<iteration-confirmation
  id=""{confirmId}""
  iteration=""{iterId2}""
  action=""complete""
  expected_spec_revision=""{specRev2}""
  expected_tasks_revision=""{tasksRev2}""
  actor=""owner""
  decided_at=""2026-09-06T10:00:00Z"">
  <summary>Raw confirmation completion.</summary>
  <acceptance>
    <criterion target=""{critId2}"" decision=""accepted""/>
  </acceptance>
</iteration-confirmation>";
        var confirmFilePath = Path.Combine(_tempDir, "confirm-request.xml");
        File.WriteAllText(confirmFilePath, confirmXml);

        // 4a. Dry-run raw complete: must NOT emit guidance
        var (dryRawCode, dryRawOut, _) = RunCli(
            "iteration", "confirm",
            "--file", confirmFilePath,
            "--dry-run",
            "--workspace-root", _tempDir,
            "--format", "xml");
        Assert.AreEqual(0, dryRawCode);
        Assert.IsFalse(dryRawOut.Contains(IterationCommand.KnowledgeGuidanceMessage, StringComparison.Ordinal),
            "Dry-run confirm complete must not emit guidance.");

        // 4b. Real raw complete: must emit guidance in a single well-formed XML document
        var (rawCode, rawOut, rawErr) = RunCli(
            "iteration", "confirm",
            "--file", confirmFilePath,
            "--workspace-root", _tempDir,
            "--format", "xml");
        Assert.AreEqual(0, rawCode, rawErr);
        StringAssert.Contains(rawOut, IterationCommand.KnowledgeGuidanceMessage);
        var parsedRawXml = XDocument.Parse(rawOut);
        Assert.AreEqual("mutation", parsedRawXml.Root?.Name.LocalName);
        Assert.AreEqual(IterationCommand.KnowledgeGuidanceMessage, parsedRawXml.Root?.Element("guidance")?.Value);

        // 4c. Raw complete exact replay: must emit guidance
        var (rawReplayCode, rawReplayOut, rawReplayErr) = RunCli(
            "iteration", "confirm",
            "--file", confirmFilePath,
            "--workspace-root", _tempDir,
            "--format", "xml");
        Assert.AreEqual(0, rawReplayCode, rawReplayErr);
        StringAssert.Contains(rawReplayOut, IterationCommand.KnowledgeGuidanceMessage);
        var parsedRawReplayXml = XDocument.Parse(rawReplayOut);
        Assert.AreEqual("true", (string?)parsedRawReplayXml.Root?.Attribute("already_applied"));
        Assert.AreEqual(IterationCommand.KnowledgeGuidanceMessage, parsedRawReplayXml.Root?.Element("guidance")?.Value);

        // 5. Non-complete actions: activate must NOT emit guidance
        var iterId3 = "20260905-iteration-activate-guidance";
        var (actCreateCode, actCreateOut, _) = RunCli(
            "iteration", "create",
            "--id", iterId3,
            "--kind", "feature",
            "--criterion", "Activate guidance check.",
            "--workspace-root", _tempDir,
            "--format", "xml");
        Assert.AreEqual(0, actCreateCode);

        var (actCode, actOut, actErr) = RunCli(
            "iteration", "activate",
            "--iteration", iterId3,
            "--workspace-root", _tempDir,
            "--format", "xml");
        Assert.AreEqual(0, actCode, actErr);
        Assert.IsFalse(actOut.Contains(IterationCommand.KnowledgeGuidanceMessage, StringComparison.Ordinal),
            "Activate must not emit completion guidance.");

        // 6. Failure must NOT emit guidance
        var (failCode, failOut, failErr) = RunCli(
            "iteration", "complete",
            "--iteration", "non-existent-iteration",
            "--workspace-root", _tempDir,
            "--format", "xml");
        Assert.AreNotEqual(0, failCode);
        Assert.IsFalse(failOut.Contains(IterationCommand.KnowledgeGuidanceMessage, StringComparison.Ordinal),
            "Failed complete must not emit completion guidance.");
        Assert.IsFalse(failErr.Contains(IterationCommand.KnowledgeGuidanceMessage, StringComparison.Ordinal),
            "Failed complete error must not emit completion guidance.");
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