using System.Globalization;
using System.Xml.Linq;
using DogdouSpec.Core.Workspace;

namespace DogdouSpec.Cli.Tests;

[TestClass]
public sealed class TaskReviewExperienceCliTests
{
    private string _root = null!;
    private const string Iteration = "20261028-review-experience-test";

    [TestInitialize]
    public void Initialize()
    {
        _root = Path.Combine(Path.GetTempPath(), "dogdouspec-review-experience-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        WorkspaceInitializer.Initialize(_root, _root);
        Succeed("iteration", "create", "--id", Iteration, "--kind", "feature", "--criterion", "Review convenience works.", "--activate");
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    [TestMethod]
    [DataRow("pending")]
    [DataRow("in-progress")]
    [DataRow("verification")]
    [DataRow("blocked")]
    [DataRow("done")]
    [DataRow("cancelled")]
    [DataRow("transferred")]
    [DataRow("superseded")]
    public void ReviewRecords_WorkAcrossTaskStates_WithoutChangingTaskStatus(string status)
    {
        var id = CreateTask();
        if (status is "in-progress" or "verification" or "blocked" or "done") Succeed("task", "start", "--task", id);
        if (status == "verification") Succeed("task", "verify", "--task", id);
        if (status == "done") Succeed("task", "finish", "--task", id);
        if (status == "blocked") Succeed("task", "block", "--task", id, "--summary", "Waiting for context.");
        if (status is "cancelled" or "transferred" or "superseded")
        {
            var at = DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
            var transition = status == "cancelled" ? "cancel" : status == "transferred" ? "transfer" : "supersede";
            var operation = "20261028T100000Z-test-disposition";
            var request = new XElement("task-update", new XAttribute("id", operation), new XAttribute("actor", "tester"),
                new XAttribute("occurred_at", at), new XAttribute("transition", transition),
                new XElement("records", new XElement("record", new XAttribute("id", operation + "-record"),
                    new XAttribute("kind", "decision"), new XAttribute("status", "informational"), new XAttribute("actor", "tester"),
                    new XAttribute("created_at", at), new XAttribute("operation_id", operation), new XElement("summary", "Disposition for review follow-up test."))));
            var path = Path.Combine(_root, "disposition.xml");
            File.WriteAllText(path, request.ToString());
            Succeed("task", "update", "--task", id, "--expected-revision", Tasks().Root!.Attribute("revision")!.Value, "--file", path);
        }
        var before = Task(id);
        const string summary = "修复建议 <A> & <B>。\r\n保留换行。\r补充";
        var note = Succeed("task", "review", "record", "--task", id, "--result", "changes-requested", "--summary", summary);
        var receipt = XDocument.Parse(note).Root!;
        Assert.IsNotNull(receipt.Element("review-identity"));
        var after = Task(id);
        Assert.AreEqual(status, (string?)after.Attribute("status"));
        Assert.IsTrue(XNode.DeepEquals(before.Element("acceptance"), after.Element("acceptance")));
        Assert.IsTrue(XNode.DeepEquals(before.Element("review"), after.Element("review")));
        if (status is "done" or "cancelled" or "transferred" or "superseded")
            CollectionAssert.AreEqual(before.Attributes().Select(a => a.ToString()).ToArray(), after.Attributes().Select(a => a.ToString()).ToArray());
        var history = XDocument.Parse(Succeed("task", "review", "records", "--task", id)).Root!;
        Assert.AreEqual(summary, history.Element("review")!.Element("record")!.Element("summary")!.Value);
        Assert.AreEqual("record", (string?)history.Element("review")!.Attribute("source"));
        Succeed("validate");
    }

    [TestMethod]
    public void ReviewRecord_PreviewReplayAndStatus_HaveXmlAndHumanOutput()
    {
        var id = CreateTask();
        var arguments = new[] { "task", "review", "record", "--task", id, "--summary", "Independent check passed.",
            "--result", "approved", "--actor", "gemini-4-argon-high", "--operation-id", "20261028T090000Z-cli-review-note", "--occurred-at", DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture) };
        var bytes = File.ReadAllBytes(TasksPath);
        var preview = Run([.. arguments, "--dry-run"]);
        Assert.AreEqual(0, preview.Exit, preview.Error);
        StringAssert.Contains(preview.Output, "dry_run=\"true\"");
        var humanPreview = Run([.. arguments, "--dry-run", "--format", "human"]);
        Assert.AreEqual(0, humanPreview.Exit, humanPreview.Error);
        StringAssert.Contains(humanPreview.Output, "Preview only;");
        Assert.IsFalse(humanPreview.Output.Contains("Saved as", StringComparison.Ordinal));
        CollectionAssert.AreEqual(bytes, File.ReadAllBytes(TasksPath));
        var applied = Run(arguments);
        Assert.AreEqual(0, applied.Exit, applied.Error);
        var replay = Run(arguments);
        Assert.AreEqual(0, replay.Exit, replay.Error);
        StringAssert.Contains(replay.Output, "already_applied=\"true\"");
        var status = XDocument.Parse(Succeed("task", "review", "status", "--task", id)).Root!;
        Assert.AreEqual("false", (string?)status.Element("configuration")!.Attribute("required"));
        Assert.AreEqual("approved", (string?)status.Element("latest-record")!.Attribute("result"));
        Assert.AreEqual("false", (string?)status.Element("submission")!.Attribute("present"));
        var human = Run("task", "review", "status", "--task", id, "--format", "human");
        Assert.AreEqual(0, human.Exit, human.Error);
        StringAssert.Contains(human.Output, "Independent check passed.");
        StringAssert.Contains(human.Output, "task review record");
        var humanHistory = Run("task", "review", "records", "--task", id, "--format", "human");
        Assert.AreEqual(0, humanHistory.Exit, humanHistory.Error);
        StringAssert.Contains(humanHistory.Output, "[record] approved");
    }

    [TestMethod]
    public void FormalReviewGuidance_ExplainsUnsupportedCommands_AndNotesDoNotApproveRequiredReview()
    {
        var ungated = CreateTask();
        var before = File.ReadAllBytes(TasksPath);
        var unsupported = Run("task", "review", "approve", "--task", ungated);
        Assert.AreNotEqual(0, unsupported.Exit);
        StringAssert.Contains(unsupported.Error, "TASK_REVIEW_REQUIRED");
        StringAssert.Contains(unsupported.Error, "task review record");
        CollectionAssert.AreEqual(before, File.ReadAllBytes(TasksPath));
        var gated = CreateTask(reviewRequired: true);
        var wrongState = Run("task", "review", "approve", "--task", gated, "--actor", "reviewer");
        StringAssert.Contains(wrongState.Error, "TASK_REVIEW_STATE_INVALID");
        StringAssert.Contains(wrongState.Error, "task review status");
        Succeed("task", "start", "--task", gated);
        Succeed("task", "verify", "--task", gated);
        Succeed("task", "review", "record", "--task", gated, "--actor", "implementer", "--result", "approved", "--summary", "Caller-reported review outcome.");
        var unfinished = Run("task", "finish", "--task", gated);
        StringAssert.Contains(unfinished.Error, "TASK_REVIEW_REQUIRED");
        StringAssert.Contains(unfinished.Error, "task review status");
        var sameActor = Run("task", "review", "approve", "--task", gated, "--actor", "implementer");
        StringAssert.Contains(sameActor.Error, "TASK_REVIEW_ACTOR_CONFLICT");
        StringAssert.Contains(sameActor.Error, "task review record");
        Succeed("task", "review", "approve", "--task", gated, "--actor", "reviewer", "--summary", "Independent formal approval.");
        var reviews = XDocument.Parse(Succeed("task", "review", "records", "--task", gated, "--last", "1")).Root!;
        Assert.AreEqual("2", (string?)reviews.Attribute("total_records"));
        Assert.AreEqual("submission", (string?)reviews.Element("review")!.Attribute("source"));
        Succeed("task", "finish", "--task", gated);
    }

    [TestMethod]
    public void ReviewReadAndInputValidation_DoNotWriteOrTriggerPendingRecovery()
    {
        var id = CreateTask();
        var before = File.ReadAllBytes(TasksPath);
        var pending = Path.Combine(_root, ".dogdouspec", "_tmp", "tx_pending_review_read", "staged");
        Directory.CreateDirectory(pending);
        Succeed("task", "review", "status", "--task", id);
        Succeed("task", "review", "records", "--task", id);
        Assert.IsTrue(Directory.Exists(pending));
        foreach (var arguments in new[] {
            new[] { "task", "review", "record", "--task", id, "--summary", " " },
            new[] { "task", "review", "record", "--task", id, "--summary", "Note", "--result", "unknown" },
            new[] { "task", "review", "records", "--task", id, "--last", "0" },
            new[] { "task", "review", "status", "--task", "20261028-missing-task" } })
            Assert.AreNotEqual(0, Run(arguments).Exit);
        CollectionAssert.AreEqual(before, File.ReadAllBytes(TasksPath));
    }

    private string CreateTask(bool reviewRequired = false)
    {
        var arguments = new List<string> { "task", "quick", "--title", reviewRequired ? "Required review convenience" : "Optional review convenience", "--scope", "src", "--done-when", "Verified", "--why", "CLI regression", "--agent", "implementer" };
        if (reviewRequired) arguments.Add("--review-required");
        Succeed([.. arguments]);
        return TaskId();
    }

    private string TasksPath => Path.Combine(_root, ".dogdouspec", Iteration, "tasks.xml");
    private XDocument Tasks() => XDocument.Load(TasksPath);
    private string TaskId() => (string)Tasks().Root!.Elements("task").Last().Attribute("id")!;
    private XElement Task(string id) => Tasks().Root!.Elements("task").Single(t => (string?)t.Attribute("id") == id);
    private string Succeed(params string[] arguments)
    {
        var result = Run(arguments);
        Assert.AreEqual(0, result.Exit, result.Error);
        return result.Output;
    }

    private (int Exit, string Output, string Error) Run(params string[] arguments)
    {
        var oldOut = Console.Out;
        var oldError = Console.Error;
        using var output = new StringWriter(CultureInfo.InvariantCulture);
        using var error = new StringWriter(CultureInfo.InvariantCulture);
        Console.SetOut(output);
        Console.SetError(error);
        try
        {
            var format = arguments.Contains("--format", StringComparer.Ordinal) ? Array.Empty<string>() : new[] { "--format", "xml" };
            var iteration = arguments.Length >= 2 && arguments[0] == "iteration" && arguments[1] == "create" || arguments[0] == "validate"
                ? Array.Empty<string>() : new[] { "--iteration", Iteration };
            var exit = Program.Main([.. arguments, .. iteration, "--workspace-root", _root, .. format]);
            return (exit, output.ToString(), error.ToString());
        }
        finally { Console.SetOut(oldOut); Console.SetError(oldError); }
    }
}
