using System.Globalization;
using System.Xml.Linq;
using DogdouSpec.Core.Iterations;

namespace DogdouSpec.Cli.Tests;

[TestClass]
public sealed class UsageReportRegressionCliTests
{
    private string _root = null!;
    private const string Iteration = "20261027-usage-regressions";
    private static readonly string[] ExpandedIncludes = { "src/**", "docs/**" };
    private static readonly string[] XmlFormat = { "--format", "xml" };
    private static readonly string[] ReviewOptions = { "--review-required", "--agent", "implementer" };
    private static readonly string[] ConflictingCriteria = { "--criterion", "A second acceptance source." };

    [TestInitialize]
    public void Initialize()
    {
        _root = Path.Combine(Path.GetTempPath(), "DogdouSpec_UsageRegression_" + Guid.NewGuid().ToString("N"));
        Succeed("workspace", "init");
        CreateIteration(Iteration);
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private (int Exit, string Output, string Error) Run(params string[] args)
    {
        using var output = new StringWriter(CultureInfo.InvariantCulture);
        using var error = new StringWriter(CultureInfo.InvariantCulture);
        var originalOutput = Console.Out;
        var originalError = Console.Error;
        try
        {
            Console.SetOut(output);
            Console.SetError(error);
            var rawTemplate = args.Length >= 2 && args[0] == "template" && args[1] == "show";
            IEnumerable<string> arguments = rawTemplate ? args : args.Concat(new[] { "--workspace-root", _root });
            if (!rawTemplate && !args.Contains("--format", StringComparer.Ordinal)) arguments = arguments.Concat(XmlFormat);
            var exit = Program.Main(arguments.ToArray());
            return (exit, output.ToString(), error.ToString());
        }
        finally { Console.SetOut(originalOutput); Console.SetError(originalError); }
    }

    private string Succeed(params string[] args)
    {
        var result = Run(args);
        Assert.AreEqual(0, result.Exit, result.Error);
        return result.Output;
    }

    private void CreateIteration(string id) => Succeed("iteration", "create", "--id", id, "--kind", "feature", "--activate", "--criterion", "Public operations preserve governed state.");

    private string CreateTask(string suffix, string iteration = Iteration, bool reviewRequired = false)
    {
        var id = "20261027-task-" + suffix;
        var args = new List<string> { "task", "quick", "--iteration", iteration, "--title", suffix, "--scope", "src/**", "--done-when", "The observable behavior is verified.",
            "--why", "Preserve the original task rationale.", "--id", id, "--operation-id", "20261027T000000Z-op-" + suffix };
        if (reviewRequired) args.AddRange(ReviewOptions);
        Succeed(args.ToArray());
        return id;
    }

    private string TasksPath(string iteration = Iteration) => Path.Combine(_root, ".dogdouspec", iteration, "tasks.xml");
    private XDocument Tasks(string iteration = Iteration) => XDocument.Load(TasksPath(iteration));
    private XElement Task(string id, string iteration = Iteration) => Tasks(iteration).Root!.Elements("task").Single(t => (string?)t.Attribute("id") == id);

    private XDocument CreateIterationContent(string kind) => kind == "feature"
        ? XDocument.Parse(Succeed("template", "show", "--name", "iteration.create"))
        : XDocument.Parse("""
<iteration-content><research>
  <objective>Compare compatible approaches.</objective>
  <questions><question id="20000101-q-review" status="open"><index><summary>Compatibility question.</summary></index><statement>Which approach preserves compatibility?</statement><rationale>Preserve existing contracts.</rationale></question></questions>
  <method>Compare documented behavior.</method><boundaries/><outputs/>
  <acceptance><criterion id="20000101-crit-review" decision="pending">Research compares compatibility with evidence.</criterion></acceptance>
</research></iteration-content>
""");

    [TestMethod]
    [DataRow("task", "records", "--last")]
    [DataRow("task", "record", "--check-command")]
    [DataRow("task", "show", "task records")]
    [DataRow("iteration", "create", "--file")]
    [DataRow("transaction", "apply", "--dry-run")]
    [DataRow("query", "", "--xpath-file")]
    [DataRow("search", "", "--xpath-file")]
    public void Help_ExposesUsageRepairEntries(string command, string subcommand, string entry)
    {
        var args = string.IsNullOrEmpty(subcommand) ? new[] { command, "--help" } : new[] { command, subcommand, "--help" };
        var result = Run(args);
        Assert.AreEqual(0, result.Exit, result.Error);
        StringAssert.Contains(result.Output, entry);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void AddScope_PreservesRepositoriesExclusionsAndRationale(bool started)
    {
        var id = CreateTask("scope");
        var timestamp = DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
        var request = new XElement("task-revise", new XAttribute("id", "20261027-scope-baseline"), new XAttribute("actor", "tester"),
            new XAttribute("occurred_at", timestamp),
            new XElement("scope",
                new XElement("repository", new XAttribute("path", "."), new XElement("include", new XAttribute("path", "src/**")), new XElement("exclude", new XAttribute("path", "src/generated/**"))),
                new XElement("repository", new XAttribute("path", "shared"), new XElement("include", new XAttribute("path", "api/**")))),
            new XElement("records", new XElement("record", new XAttribute("id", "20261027-record-scope-baseline"), new XAttribute("kind", "discussion"),
                new XAttribute("status", "informational"), new XAttribute("created_at", timestamp), new XAttribute("actor", "tester"), new XElement("summary", "Define the initial two-repository scope."))));
        var requestPath = Path.Combine(_root, "scope-request.xml");
        File.WriteAllText(requestPath, request.ToString());
        Succeed("task", "revise", "--iteration", Iteration, "--task", id, "--file", requestPath);
        if (started) Succeed("task", "start", "--iteration", Iteration, "--task", id);
        var rationale = Task(id).Element("rationale")!.Value;

        Succeed("task", "revise", "--iteration", Iteration, "--task", id, "--add-scope", "docs/**", "--add-scope", "docs/**", "--summary", "Expand the scope.");

        var task = Task(id);
        Assert.AreEqual(rationale, task.Element("rationale")!.Value);
        var repositories = task.Element("scope")!.Elements("repository").ToArray();
        Assert.AreEqual(2, repositories.Length);
        CollectionAssert.AreEqual(ExpandedIncludes, repositories[0].Elements("include").Select(i => (string)i.Attribute("path")!).ToArray());
        Assert.AreEqual("src/generated/**", (string?)repositories[0].Element("exclude")!.Attribute("path"));
        Assert.AreEqual("shared", (string?)repositories[1].Attribute("path"));
        Succeed("validate");
    }

    [TestMethod]
    public void AddDependency_ResolvesSameDocumentAndCrossIterationScopes()
    {
        var source = CreateTask("dependency-source");
        var local = CreateTask("dependency-local");
        const string otherIteration = "20261027-other-usage-regressions";
        CreateIteration(otherIteration);
        var foreign = CreateTask("dependency-foreign", otherIteration);

        Succeed("task", "revise", "--iteration", Iteration, "--task", source, "--add-dependency", local, "--add-dependency", foreign);
        var dependencies = Task(source).Element("dependencies")!.Elements("ref").ToArray();
        Assert.AreEqual("document", (string?)dependencies.Single(r => (string?)r.Attribute("target") == local).Attribute("scope"));
        Assert.AreEqual("project", (string?)dependencies.Single(r => (string?)r.Attribute("target") == foreign).Attribute("scope"));
        Succeed("task", "revise", "--iteration", Iteration, "--task", source, "--add-dependency", local, "--add-dependency", foreign);
        Assert.AreEqual(2, Task(source).Element("dependencies")!.Elements("ref").Count());
        Succeed("validate");
    }

    [TestMethod]
    public void AddScope_StaleRevisionCannotReplaceNewerScope()
    {
        var id = CreateTask("scope-conflict");
        var revision = (string)Tasks().Root!.Attribute("revision")!;
        Succeed("task", "revise", "--iteration", Iteration, "--task", id, "--add-scope", "tests/**");
        var before = File.ReadAllBytes(TasksPath());
        var result = Run("task", "revise", "--iteration", Iteration, "--task", id, "--add-scope", "docs/**", "--expected-revision", revision);
        Assert.AreEqual(4, result.Exit, result.Error);
        CollectionAssert.AreEqual(before, File.ReadAllBytes(TasksPath()));
    }

    [TestMethod]
    public void FutureRecord_IsRejectedWithoutPoisoningTheNextAutomaticStart()
    {
        var id = CreateTask("future-record");
        var before = File.ReadAllBytes(TasksPath());
        var future = DateTimeOffset.UtcNow.AddDays(1).ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
        var result = Run("task", "record", "--iteration", Iteration, "--task", id, "--kind", "discussion", "--summary", "Invalid future event.", "--occurred-at", future);
        Assert.AreEqual(2, result.Exit, result.Error);
        StringAssert.Contains(result.Error, "clock skew");
        CollectionAssert.AreEqual(before, File.ReadAllBytes(TasksPath()));
        Succeed("task", "start", "--iteration", Iteration, "--task", id);
    }

    [TestMethod]
    public void LegacyFutureMetadata_AutomaticStartAndFinishDoNotAdvanceTheFutureClock()
    {
        var id = CreateTask("legacy-future");
        var future = DateTimeOffset.UtcNow.AddDays(1).ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
        var transaction = new XElement("transaction", new XAttribute("operation_id", "20261027-legacy-clock-fixture"),
            new XElement("document", new XAttribute("path", Iteration + "/tasks.xml"), new XAttribute("expected_revision", (string)Tasks().Root!.Attribute("revision")!),
                new XElement("set-attribute", new XAttribute("select", "/tasks/task[@id='" + id + "']"), new XAttribute("expect", "1"), new XAttribute("name", "updated_at"), new XAttribute("value", future))));
        var path = Path.Combine(_root, "legacy-clock.xml");
        File.WriteAllText(path, transaction.ToString());
        Succeed("transaction", "apply", "--file", path);

        Succeed("task", "start", "--iteration", Iteration, "--task", id);
        Succeed("task", "finish", "--iteration", Iteration, "--task", id);
        Assert.AreEqual("done", (string?)Task(id).Attribute("status"));
        Assert.AreEqual(future, (string?)Task(id).Attribute("updated_at"));
        Succeed("validate");
    }

    [TestMethod]
    public void TransactionPreview_ValidatesWithoutWritingAndIdentifiesBothOutputFormats()
    {
        var id = CreateTask("transaction-preview");
        var transaction = new XElement("transaction", new XAttribute("operation_id", "20261027-preview-transaction"),
            new XElement("document", new XAttribute("path", Iteration + "/tasks.xml"), new XAttribute("expected_revision", (string)Tasks().Root!.Attribute("revision")!),
                new XElement("replace-node", new XAttribute("select", "/tasks/task[@id='" + id + "']/title"), new XAttribute("expect", "1"), new XElement("title", "Preview title"))));
        var path = Path.Combine(_root, "preview-transaction.xml");
        File.WriteAllText(path, transaction.ToString());
        var before = File.ReadAllBytes(TasksPath());
        var preview = XDocument.Parse(Succeed("transaction", "apply", "--file", path, "--dry-run"));
        Assert.AreEqual("true", (string?)preview.Root!.Attribute("dry_run"));
        var human = Succeed("transaction", "apply", "--file", path, "--dry-run", "--format", "human");
        StringAssert.Contains(human, "DRY RUN, nothing written");
        Assert.IsFalse(human.Contains("Mutation applied", StringComparison.Ordinal));
        CollectionAssert.AreEqual(before, File.ReadAllBytes(TasksPath()));
        Succeed("transaction", "apply", "--file", path);
        Assert.AreEqual("Preview title", Task(id).Element("title")!.Value);
        var replay = XDocument.Load(path);
        replay.Descendants("document").Single().SetAttributeValue("expected_revision", Tasks().Root!.Attribute("revision")!.Value);
        File.WriteAllText(path, replay.ToString());
        var noChangePreview = XDocument.Parse(Succeed("transaction", "apply", "--file", path, "--dry-run"));
        Assert.AreEqual("true", (string?)noChangePreview.Root!.Attribute("dry_run"));
        Assert.AreEqual("true", (string?)noChangePreview.Root.Attribute("already_applied"));
    }

    [TestMethod]
    [DataRow("pending", 3)]
    [DataRow("in-progress", 2)]
    [DataRow("verification", 1)]
    public void Finish_ReportsEveryAppliedTransitionAndFullRevisionRange(string initialStatus, int expectedTransitions)
    {
        var id = CreateTask("finish-receipt");
        if (initialStatus != "pending") Succeed("task", "start", "--iteration", Iteration, "--task", id);
        if (initialStatus == "verification") Succeed("task", "verify", "--iteration", Iteration, "--task", id);
        var revision = (int)Tasks().Root!.Attribute("revision")!;
        var receipt = XDocument.Parse(Succeed("task", "finish", "--iteration", Iteration, "--task", id));
        Assert.AreEqual("task finish", (string?)receipt.Root!.Attribute("command"));
        Assert.AreEqual(revision, (int)receipt.Root.Element("document")!.Attribute("previous_revision")!);
        Assert.AreEqual(revision + expectedTransitions, (int)receipt.Root.Element("document")!.Attribute("revision")!);
        var transitions = receipt.Root.Elements("transition").ToArray();
        Assert.AreEqual(expectedTransitions, transitions.Length);
        Assert.AreEqual(initialStatus, (string?)transitions[0].Attribute("from"));
        Assert.AreEqual("done", (string?)transitions[^1].Attribute("to"));
        Assert.AreEqual("done", (string?)Task(id).Attribute("status"));
    }

    [TestMethod]
    public void Finish_ReportsPartialProgressWhenAnIndependentReviewIsStillRequired()
    {
        var id = CreateTask("finish-review", reviewRequired: true);
        Succeed("task", "start", "--iteration", Iteration, "--task", id);
        var revision = (int)Tasks().Root!.Attribute("revision")!;
        var result = Run("task", "finish", "--iteration", Iteration, "--task", id);
        Assert.AreNotEqual(0, result.Exit);
        StringAssert.Contains(result.Error, "TASK_REVIEW_REQUIRED");
        var receipt = XDocument.Parse(result.Output);
        Assert.AreEqual("true", (string?)receipt.Root!.Attribute("partial"));
        Assert.AreEqual(revision, (int)receipt.Root.Element("document")!.Attribute("previous_revision")!);
        Assert.AreEqual(revision + 1, (int)receipt.Root.Element("document")!.Attribute("revision")!);
        Assert.AreEqual("verification", (string?)Task(id).Attribute("status"));
        Assert.AreEqual(1, receipt.Root.Elements("transition").Count());
    }

    [TestMethod]
    public void Records_ProjectsLastMatchingHistoryWithExplicitUnicodeTruncationAndNoMutation()
    {
        var id = CreateTask("history");
        Succeed("task", "record", "--iteration", Iteration, "--task", id, "--kind", "verification", "--summary", "Earlier proof.");
        Succeed("task", "record", "--iteration", Iteration, "--task", id, "--kind", "discussion", "--summary", "Unrelated discussion.");
        Succeed("task", "record", "--iteration", Iteration, "--task", id, "--kind", "verification", "--summary", "😀😀 latest proof.");
        var before = File.ReadAllBytes(TasksPath());
        var view = XDocument.Parse(Succeed("task", "records", "--iteration", Iteration, "--task", id, "--kind", "verification", "--last", "1", "--fields", "id,created_at,summary", "--max-summary", "3"));
        Assert.AreEqual(2, (int)view.Root!.Attribute("matching_records")!);
        Assert.AreEqual(1, (int)view.Root.Attribute("omitted_records")!);
        var record = view.Root.Element("record")!;
        Assert.AreEqual("true", (string?)record.Attribute("summary_truncated"));
        Assert.AreEqual("😀", record.Element("summary")!.Value);
        Assert.IsNull(record.Attribute("kind"));
        CollectionAssert.AreEqual(before, File.ReadAllBytes(TasksPath()));
        var oversized = Run("task", "records", "--iteration", Iteration, "--task", id, "--max-bytes", "50");
        Assert.AreEqual(7, oversized.Exit, oversized.Error);
    }

    [TestMethod]
    public void XPathFile_QueriesAndSearchesWithVariablesWithoutChangingTheExpression()
    {
        var id = CreateTask("xpath-file");
        var path = Path.Combine(_root, "task expression.xpath");
        File.WriteAllText(path, "/tasks/task[@id=$task_id]/title");
        var query = Succeed("query", "--document", Iteration + "/tasks.xml", "--xpath-file", path, "--var", "task_id=" + id);
        StringAssert.Contains(query, "<title>xpath-file</title>");
        var search = Succeed("search", "--scope", "iteration", "--iteration", Iteration, "--xpath-file", path, "--var", "task_id=" + id);
        StringAssert.Contains(search, "xpath-file");
        var ambiguous = Run("query", "--document", Iteration + "/tasks.xml", "--xpath", "/*", "--xpath-file", path);
        Assert.AreEqual(2, ambiguous.Exit, ambiguous.Error);
        using (var oversized = File.Create(path)) oversized.SetLength(16 * 1024 * 1024 + 1);
        Assert.AreEqual(7, Run("query", "--document", Iteration + "/tasks.xml", "--xpath-file", path).Exit);
        Assert.AreEqual(7, Run("search", "--scope", "iteration", "--iteration", Iteration, "--xpath-file", path).Exit);
    }

    [TestMethod]
    public void EmptyQueryHints_AreOptInAndDoNotTreatScalarZeroAsAnEmptyNodeSet()
    {
        var ordinary = Run("query", "--document", "backlog.xml", "--xpath", "/backlog/item");
        Assert.AreEqual(0, ordinary.Exit, ordinary.Error);
        Assert.AreEqual(string.Empty, ordinary.Error);
        var hinted = Run("query", "--document", "backlog.xml", "--xpath", "/backlog/item", "--warn-empty");
        Assert.AreEqual(0, hinted.Exit, hinted.Error);
        StringAssert.Contains(hinted.Error, "EMPTY_QUERY_RESULT");
        StringAssert.Contains(hinted.Error, "Root: /backlog");
        StringAssert.Contains(hinted.Error, "items");
        var count = Run("query", "--document", "backlog.xml", "--xpath", "count(/backlog/items/item)", "--warn-empty");
        Assert.AreEqual(0, count.Exit, count.Error);
        Assert.AreEqual(string.Empty, count.Error);
    }

    [TestMethod]
    public void RecordCheck_PersistsQueryableCommandResultExitCodeAndCriterionLink()
    {
        var id = CreateTask("structured-check");
        var criterion = (string)Task(id).Element("acceptance")!.Element("criterion")!.Attribute("id")!;
        const string command = "pwsh -Command '<proof & result>'";
        Succeed("task", "record", "--iteration", Iteration, "--task", id, "--kind", "verification", "--summary", "Command evidence.",
            "--check-command", command, "--check-result", "failed", "--check-exit-code", "17", "--check-summary", "Expected diagnostic was observed.", "--covers", criterion);
        var history = XDocument.Parse(Succeed("task", "records", "--iteration", Iteration, "--task", id, "--kind", "verification", "--last", "1", "--fields", "id,checks,covers"));
        var record = history.Root!.Element("record")!;
        var check = record.Element("checks")!.Element("check")!;
        Assert.AreEqual(command, (string?)check.Attribute("command"));
        Assert.AreEqual("failed", (string?)check.Attribute("result"));
        Assert.AreEqual(17, (int)check.Attribute("exit_code")!);
        Assert.AreEqual(criterion, (string?)record.Element("covers")!.Element("ref")!.Attribute("target"));
        Assert.AreEqual("pending", (string?)Task(id).Element("acceptance")!.Element("criterion")!.Attribute("status"));
        var before = File.ReadAllBytes(TasksPath());
        var incomplete = Run("task", "record", "--iteration", Iteration, "--task", id, "--kind", "verification", "--summary", "Incomplete check.", "--check-command", "dotnet test");
        Assert.AreEqual(2, incomplete.Exit, incomplete.Error);
        CollectionAssert.AreEqual(before, File.ReadAllBytes(TasksPath()));
    }

    [TestMethod]
    public void BacklogDefaults_ReturnReplayableIdentityAndPreserveOpenKindSemantics()
    {
        var oldActor = Environment.GetEnvironmentVariable("DOGDOUSPEC_ACTOR");
        Environment.SetEnvironmentVariable("DOGDOUSPEC_ACTOR", "regression-runner");
        try
        {
            var args = new[] { "backlog", "add", "--kind", "xyz", "--summary", "Deferred investigation.", "--statement", "Investigate the candidate.",
                "--rationale", "Research before implementation.", "--impact", "Future product choice.", "--source-iteration", Iteration, "--review-condition", "Owner reviews the research." };
            var receipt = XDocument.Parse(Succeed(args));
            var identity = receipt.Root!.Element("backlog-identity")!;
            Assert.AreEqual("regression-runner", (string?)identity.Attribute("actor"));
            var itemId = (string)identity.Attribute("id")!;
            var path = Path.Combine(_root, ".dogdouspec", "backlog.xml");
            var item = XDocument.Load(path).Root!.Element("items")!.Elements("item").Single(i => (string?)i.Attribute("id") == itemId);
            Assert.AreEqual("xyz", (string?)item.Element("index")!.Elements("term").Single(t => (string?)t.Attribute("key") == "kind").Attribute("value"));
            var before = File.ReadAllBytes(path);
            var replayArgs = args.Concat(new[] { "--id", itemId, "--operation-id", (string)identity.Attribute("operation_id")!,
                "--actor", (string)identity.Attribute("actor")!, "--occurred-at", (string)identity.Attribute("occurred_at")! }).ToArray();
            var replay = XDocument.Parse(Succeed(replayArgs));
            Assert.AreEqual("true", (string?)replay.Root!.Attribute("already_applied"));
            CollectionAssert.AreEqual(before, File.ReadAllBytes(path));
        }
        finally { Environment.SetEnvironmentVariable("DOGDOUSPEC_ACTOR", oldActor); }
    }

    [TestMethod]
    public void BacklogExplicitOperationPrefix_MakesOmittedIdentityAndTimeDeterministic()
    {
        var args = new[] { "backlog", "add", "--operation-id", "20261027T000000Z-op-defaults-replay", "--kind", "research", "--summary", "Research item.",
            "--statement", "Research alternatives.", "--rationale", "Defer design.", "--impact", "Requires owner review.", "--source-iteration", Iteration, "--review-condition", "Review the findings." };
        Succeed(args);
        var path = Path.Combine(_root, ".dogdouspec", "backlog.xml");
        var before = File.ReadAllBytes(path);
        var replay = XDocument.Parse(Succeed(args));
        Assert.AreEqual("true", (string?)replay.Root!.Attribute("already_applied"));
        CollectionAssert.AreEqual(before, File.ReadAllBytes(path));
    }

    [TestMethod]
    [DataRow(1)]
    [DataRow(2)]
    public void KnowledgeReplay_DryRunLabelsXmlAndHumanWithoutWriting(int expectedRevision)
    {
        var args = new List<string> { "knowledge", "add", "--id", "20261027-knowledge-review-preview", "--operation-id", "20261027T000000Z-knowledge-review-preview",
            "--actor", "review-tester", "--occurred-at", DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture),
            "--topic", "preview", "--summary", "Preview replay evidence.", "--statement", "Replay retains its preview identity.",
            "--rationale", "Keep output truthful.", "--source-iteration", Iteration, "--expected-revision", "1" };
        Succeed(args.ToArray());
        var path = Path.Combine(_root, ".dogdouspec", "knowledge.xml");
        var before = File.ReadAllBytes(path);
        args[^1] = expectedRevision.ToString(CultureInfo.InvariantCulture);
        args.Add("--dry-run");
        var receipt = XDocument.Parse(Succeed(args.ToArray()));
        Assert.AreEqual("true", (string?)receipt.Root!.Attribute("already_applied"));
        Assert.AreEqual("true", (string?)receipt.Root.Attribute("dry_run"));
        Assert.AreEqual(2, (int)receipt.Root.Element("document")!.Attribute("revision")!);
        args.Add("--format");
        args.Add("human");
        var human = Succeed(args.ToArray());
        StringAssert.Contains(human, "DRY RUN, nothing written");
        Assert.IsFalse(human.Contains("Mutation applied", StringComparison.Ordinal));
        CollectionAssert.AreEqual(before, File.ReadAllBytes(path));
        Succeed("validate");
    }

    [TestMethod]
    [DataRow("feature", "", false)]
    [DataRow("feature", "   ", false)]
    [DataRow("feature", IterationCriterionPolicy.SeededFeatureCriterion, false)]
    [DataRow("feature", IterationCriterionPolicy.SeededResearchCriterion, false)]
    [DataRow("research", "", false)]
    [DataRow("research", "   ", false)]
    [DataRow("research", IterationCriterionPolicy.SeededResearchCriterion, false)]
    [DataRow("research", IterationCriterionPolicy.SeededFeatureCriterion, false)]
    [DataRow("feature", "   ", true)]
    [DataRow("research", "   ", true)]
    public void IterationFile_ExplicitUndefinedCriteriaFailBeforePublishing(string kind, string text, bool activate)
    {
        var content = CreateIterationContent(kind);
        content.Descendants("criterion").Single().Value = text;
        var path = Path.Combine(_root, "undefined-criterion.xml");
        File.WriteAllText(path, content.ToString());
        var workspace = Path.Combine(_root, ".dogdouspec");
        var before = Directory.GetFiles(workspace, "*.xml", SearchOption.AllDirectories).ToDictionary(p => p, File.ReadAllBytes);
        const string id = "20261027-undefined-file-criterion";
        var args = new List<string> { "iteration", "create", "--id", id, "--kind", kind, "--file", path };
        if (activate) args.Add("--activate");
        var result = Run(args.ToArray());
        Assert.AreEqual(5, result.Exit, result.Error);
        StringAssert.Contains(result.Error, "CRITERION_UNDEFINED");
        Assert.IsFalse(Directory.Exists(Path.Combine(workspace, id)));
        Assert.AreEqual(before.Count, Directory.GetFiles(workspace, "*.xml", SearchOption.AllDirectories).Length);
        foreach (var pair in before) CollectionAssert.AreEqual(pair.Value, File.ReadAllBytes(pair.Key), pair.Key);
        Succeed("validate");
    }

    [TestMethod]
    [DataRow("feature")]
    [DataRow("research")]
    public void IterationFile_OmittedAcceptanceKeepsDraftScaffoldAndStillBlocksActivation(string kind)
    {
        var content = CreateIterationContent(kind);
        content.Descendants("acceptance").Single().Remove();
        var path = Path.Combine(_root, "omitted-acceptance.xml");
        File.WriteAllText(path, content.ToString());
        const string draftId = "20261027-omitted-file-draft";
        Succeed("iteration", "create", "--id", draftId, "--kind", kind, "--file", path);
        var spec = XDocument.Load(Path.Combine(_root, ".dogdouspec", draftId, "spec.xml"));
        Assert.AreEqual("draft", (string?)spec.Root!.Attribute("status"));
        Assert.AreEqual(kind == "feature" ? IterationCriterionPolicy.SeededFeatureCriterion : IterationCriterionPolicy.SeededResearchCriterion,
            spec.Descendants("criterion").Single().Value);
        const string activeId = "20261027-omitted-file-active";
        var result = Run("iteration", "create", "--id", activeId, "--kind", kind, "--file", path, "--activate");
        Assert.AreEqual(5, result.Exit, result.Error);
        StringAssert.Contains(result.Error, "CRITERION_UNDEFINED");
        Assert.IsFalse(Directory.Exists(Path.Combine(_root, ".dogdouspec", activeId)));
        Succeed("validate");
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void IterationFile_CreatesCompleteIntentAndKeepsOwnerCriteriaPending(bool activate)
    {
        var content = XDocument.Parse(Succeed("template", "show", "--name", "iteration.create"));
        content.Root!.Add(XElement.Parse("""
<constraints><constraint id="20000101-constraint-input" kind="policy">Preserve existing request schemas.</constraint></constraints>
"""));
        content.Root.Add(XElement.Parse("""
<design><overview>Use validated public authoring entries.</overview><boundaries/><decisions><decision id="20000101-design-input" status="proposed"><index><summary>Retain existing schemas.</summary></index><rationale>Preserve compatibility.</rationale></decision></decisions></design>
"""));
        var path = Path.Combine(_root, "iteration-content.xml");
        File.WriteAllText(path, content.ToString());
        const string id = "20261027-file-created";
        var args = new List<string> { "iteration", "create", "--id", id, "--kind", "feature", "--file", path, "--constraint", "environment: Use the test workspace." };
        if (activate) args.Add("--activate");
        Succeed(args.ToArray());
        var spec = XDocument.Load(Path.Combine(_root, ".dogdouspec", id, "spec.xml"));
        Assert.AreEqual(activate ? "active" : "draft", (string?)spec.Root!.Attribute("status"));
        Assert.AreEqual("Describe the intended product outcome.", spec.Root.Element("product")!.Element("objective")!.Value);
        Assert.AreEqual(activate ? "approved" : "proposed", (string?)spec.Descendants("requirement").First().Attribute("status"));
        Assert.AreEqual("pending", (string?)spec.Descendants("criterion").Single().Attribute("decision"));
        Assert.AreEqual(2, spec.Root.Element("constraints")!.Elements("constraint").Count());
        Assert.AreEqual(activate ? "accepted" : "proposed", (string?)spec.Root.Element("design")!.Element("decisions")!.Element("decision")!.Attribute("status"));
        if (activate) Assert.AreEqual("20000101-design-input", (string?)spec.Descendants("confirmation").Single().Element("design")!.Element("decision")!.Attribute("target"));
        if (activate) Assert.AreEqual("20000101-req-replace", (string?)spec.Descendants("confirmation").Single().Element("requirements")!.Element("requirement")!.Attribute("target"));
        Succeed("validate");
    }

    [TestMethod]
    public void IterationFile_RejectsImportedDecisionsConflictingCriteriaAndInvalidShapesWithoutPublishing()
    {
        var content = XDocument.Parse(Succeed("template", "show", "--name", "iteration.create"));
        var path = Path.Combine(_root, "invalid-content.xml");
        const string id = "20261027-invalid-file";
        var args = new[] { "iteration", "create", "--id", id, "--kind", "feature", "--file", path };
        content.Descendants("criterion").Single().SetAttributeValue("decision", "passed");
        File.WriteAllText(path, content.ToString());
        Assert.AreNotEqual(0, Run(args).Exit);
        Assert.IsFalse(Directory.Exists(Path.Combine(_root, ".dogdouspec", id)));
        content.Descendants("criterion").Single().SetAttributeValue("decision", "pending");
        File.WriteAllText(path, content.ToString());
        Assert.AreNotEqual(0, Run(args.Concat(ConflictingCriteria).ToArray()).Exit);
        content.Descendants("objective").Single().Remove();
        File.WriteAllText(path, content.ToString());
        Assert.AreNotEqual(0, Run(args).Exit);
        Assert.IsFalse(Directory.Exists(Path.Combine(_root, ".dogdouspec", id)));
        Succeed("validate");
    }

    [TestMethod]
    public void IterationFile_ResearchBodyUsesCliCriteriaWhenAcceptanceIsOmitted()
    {
        var path = Path.Combine(_root, "research-content.xml");
        File.WriteAllText(path, """
<iteration-content><research>
  <objective>Compare candidate approaches.</objective>
  <questions><question id="20261027-q-input" status="open"><index><summary>Compatibility question.</summary></index><statement>Which approach preserves compatibility?</statement><rationale>Preserve existing contracts.</rationale></question></questions>
  <method>Compare prototypes and documented behavior.</method><boundaries/><outputs/>
</research></iteration-content>
""");
        const string id = "20261027-file-research";
        Succeed("iteration", "create", "--id", id, "--kind", "research", "--file", path, "--criterion", "Research compares compatibility with evidence.", "--activate");
        var spec = XDocument.Load(Path.Combine(_root, ".dogdouspec", id, "spec.xml"));
        Assert.AreEqual("active", (string?)spec.Root!.Attribute("status"));
        Assert.AreEqual("open", (string?)spec.Descendants("question").Single().Attribute("status"));
        Assert.AreEqual("Research compares compatibility with evidence.", spec.Descendants("criterion").Single().Value);
        Succeed("validate");
    }
}
