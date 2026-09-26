using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using DogdouSpec.Core.Append;
using DogdouSpec.Core.Backlog;
using DogdouSpec.Core.Changes;
using DogdouSpec.Core.Diagnostics;
using DogdouSpec.Core.Iterations;
using DogdouSpec.Core.Requirements;
using DogdouSpec.Core.Security;
using DogdouSpec.Core.Serialization;
using DogdouSpec.Core.Tasks;
using DogdouSpec.Core.Time;
using DogdouSpec.Core.Transactions;
using DogdouSpec.Core.Workspace;

namespace DogdouSpec.Core.Tests;

[TestClass]
public sealed class CanonicalXmlSerializerTests
{
    private static string RepoRoot = null!;
    private static readonly string[] SingleXPathIterationArray = new[] { "20260823-xpath-core" };
    private static readonly string[] EmptyStringArray = Array.Empty<string>();
    private static readonly string[] CoreScopeArray = new[] { "src/DogdouSpec.Core/" };
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
        _tempDir = Path.Combine(Path.GetTempPath(), "DogdouSpec_CanonicalXmlTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    [TestCleanup]
    public void TearDown()
    {
        if (Directory.Exists(_tempDir))
        {
            try { Directory.Delete(_tempDir, true); } catch { }
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

    private static void AssertExactSingleTrailingLf(byte[] bytes)
    {
        Assert.IsTrue(bytes.Length >= 2, "File must have at least 2 bytes.");
        Assert.AreEqual((byte)'\n', bytes[^1], "Last byte must be LF (0x0A).");
        Assert.AreNotEqual((byte)'\n', bytes[^2], "Second to last byte must not be LF (no newline accumulation / trailing blank line).");
        Assert.IsFalse(bytes.Contains((byte)'\r'), "File must not contain CR (0x0D) bytes.");
    }

    private static void AssertGitDiffCheckClean(string repoDir)
    {
        var psi = new ProcessStartInfo("git", "diff --check")
        {
            WorkingDirectory = repoDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        using var proc = Process.Start(psi);
        Assert.IsNotNull(proc, "Failed to start git process.");
        var stdout = proc.StandardOutput.ReadToEnd();
        var stderr = proc.StandardError.ReadToEnd();
        proc.WaitForExit();
        Assert.AreEqual(0, proc.ExitCode, $"git diff --check failed with exit code {proc.ExitCode}.\nStdout: {stdout}\nStderr: {stderr}");
        Assert.IsTrue(string.IsNullOrWhiteSpace(stdout), $"git diff --check reported whitespace issues:\n{stdout}");
    }

    private static void InitGitRepo(string dir)
    {
        RunGit(dir, "init");
        RunGit(dir, "config user.name test");
        RunGit(dir, "config user.email test@example.com");
        RunGit(dir, "config core.autocrlf false");
        RunGit(dir, "add .");
        RunGit(dir, "commit -m initial");
    }

    private static void RunGit(string dir, string args)
    {
        var psi = new ProcessStartInfo("git", args)
        {
            WorkingDirectory = dir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        using var proc = Process.Start(psi);
        proc?.WaitForExit();
    }

    #region Serializer Unit Tests

    [TestMethod]
    public void Serializer_ProducesUtf8DeclarationWithoutBomAndSingleTrailingLf()
    {
        var doc = new XDocument(new XElement("tasks", new XAttribute("revision", "1")));
        var xml = ManagedDocumentSerializer.Serialize(doc);
        var bytes = Encoding.UTF8.GetBytes(xml);

        Assert.IsTrue(xml.StartsWith("<?xml version=\"1.0\" encoding=\"utf-8\"?>\n", StringComparison.Ordinal));
        AssertExactSingleTrailingLf(bytes);

        // Check BOM absence
        var rawBytes = ManagedDocumentSerializer.SerializeToBytes(doc);
        Assert.IsFalse(rawBytes.Length >= 3 && rawBytes[0] == 0xEF && rawBytes[1] == 0xBB && rawBytes[2] == 0xBF, "Output must not contain UTF-8 BOM.");
        AssertExactSingleTrailingLf(rawBytes);
    }

    [TestMethod]
    public void Serializer_TwoSpaceStructuralIndentationAndSiblingLineBreaks()
    {
        var doc = new XDocument(
            new XElement("tasks",
                new XAttribute("revision", "1"),
                new XElement("index",
                    new XElement("summary", "Summary text"),
                    new XElement("term", new XAttribute("key", "k1"), new XAttribute("value", "v1"))),
                new XElement("task",
                    new XAttribute("id", "t1"),
                    new XAttribute("status", "pending"),
                    new XElement("title", "Task 1"),
                    new XElement("records",
                        new XElement("record", new XAttribute("id", "r1")),
                        new XElement("record", new XAttribute("id", "r2"))))));

        var xml = ManagedDocumentSerializer.Serialize(doc);
        var lines = xml.Split('\n');

        // Check indentation of structural children
        Assert.IsTrue(lines.Any(l => l.StartsWith("  <index>", StringComparison.Ordinal)));
        Assert.IsTrue(lines.Any(l => l.StartsWith("    <summary>Summary text</summary>", StringComparison.Ordinal)));
        Assert.IsTrue(lines.Any(l => l.StartsWith("    <term key=\"k1\" value=\"v1\" />", StringComparison.Ordinal)));
        Assert.IsTrue(lines.Any(l => l.StartsWith("  <task id=\"t1\"", StringComparison.Ordinal)));
        Assert.IsTrue(lines.Any(l => l.StartsWith("    <records>", StringComparison.Ordinal)));
        Assert.IsTrue(lines.Any(l => l.StartsWith("      <record id=\"r1\" />", StringComparison.Ordinal)));
        Assert.IsTrue(lines.Any(l => l.StartsWith("      <record id=\"r2\" />", StringComparison.Ordinal)));

        // Sibling records are on distinct separate lines
        var r1Index = Array.FindIndex(lines, l => l.Contains("id=\"r1\"", StringComparison.Ordinal));
        var r2Index = Array.FindIndex(lines, l => l.Contains("id=\"r2\"", StringComparison.Ordinal));
        Assert.IsTrue(r1Index >= 0 && r2Index >= 0 && r2Index == r1Index + 1, "Sibling records must appear on consecutive separate indented lines.");
    }

    [TestMethod]
    public void Serializer_PreservesNonWhitespaceProseTextValuesWithoutTrimmingOrWrapping()
    {
        var prose = "  This is a multiline\n    prose statement with internal indentation\n  and trailing spaces.   ";
        var doc = new XDocument(
            new XElement("product",
                new XElement("objective", prose)));

        var xml = ManagedDocumentSerializer.Serialize(doc);
        Assert.IsTrue(xml.Contains(prose, StringComparison.Ordinal), "Exact non-whitespace prose text including multiline indents and spaces must be preserved.");
    }

    [TestMethod]
    public void Serializer_PreservesMixedContent()
    {
        var doc = new XDocument(
            new XElement("root",
                new XElement("p", "Prefix text ", new XElement("b", "bold text"), " suffix text.")));

        var xml = ManagedDocumentSerializer.Serialize(doc);
        Assert.IsTrue(xml.Contains("<p>Prefix text <b>bold text</b> suffix text.</p>", StringComparison.Ordinal),
            "Mixed content must preserve internal text and tags without disruption.");
    }

    [TestMethod]
    public void Serializer_PreservesWhitespaceOnlyInlineSeparatorsInMixedContent()
    {
        // 1. Mixed content with space separator between inline tags: <p><b>x</b> <i>y</i></p>
        var doc1 = new XDocument(
            new XElement("root",
                new XElement("p",
                    new XElement("b", "x"),
                    " ",
                    new XElement("i", "y"))));

        var xml1 = ManagedDocumentSerializer.Serialize(doc1);
        Assert.IsTrue(xml1.Contains("<b>x</b> <i>y</i>", StringComparison.Ordinal), "Inline elements with space separator must produce inline output.");
        using (var sr1 = new StringReader(xml1))
        {
            var parsed1 = XDocument.Load(sr1, LoadOptions.PreserveWhitespace);
            Assert.AreEqual("x y", parsed1.Descendants("p").First().Value, "Parsed .Value must preserve exact inline space separator.");
        }

        // 2. Mixed content with tab separator: <p><b>x</b>\t<i>y</i></p>
        var doc2 = new XDocument(
            new XElement("root",
                new XElement("p",
                    new XElement("b", "x"),
                    "\t",
                    new XElement("i", "y"))));

        var xml2 = ManagedDocumentSerializer.Serialize(doc2);
        Assert.IsTrue(xml2.Contains("<b>x</b>\t<i>y</i>", StringComparison.Ordinal), "Inline elements with tab separator must produce inline output.");
        using (var sr2 = new StringReader(xml2))
        {
            var parsed2 = XDocument.Load(sr2, LoadOptions.PreserveWhitespace);
            Assert.AreEqual("x\ty", parsed2.Descendants("p").First().Value, "Parsed .Value must preserve exact inline tab separator.");
        }

        // 3. Mixed content parsed from XML string with multiple spaces
        var rawXml = "<root><p><b>hello</b>   <i>world</i></p></root>";
        var normalized = ManagedDocumentSerializer.Normalize(rawXml);
        Assert.IsTrue(normalized.Contains("<b>hello</b>   <i>world</i>", StringComparison.Ordinal));
        using (var sr3 = new StringReader(normalized))
        {
            var parsed3 = XDocument.Load(sr3, LoadOptions.PreserveWhitespace);
            Assert.AreEqual("hello   world", parsed3.Descendants("p").First().Value);
        }
    }

    [TestMethod]
    public void Serializer_PreservesCrLfAndCrSemanticsThroughRoundtrip_WithoutRawCrFormattingBytes()
    {
        var crText = "Line1\rLine2";
        var lfText = "Line1\nLine2";
        var crlfText = "Line1\r\nLine2";

        var doc = new XDocument(
            new XElement("tasks",
                new XAttribute("revision", "1"),
                new XElement("task",
                    new XAttribute("id", "t1"),
                    new XElement("cr", crText),
                    new XElement("lf", lfText),
                    new XElement("crlf", crlfText))));

        var serializedXml = ManagedDocumentSerializer.Serialize(doc);
        var bytes = ManagedDocumentSerializer.SerializeToBytes(doc);

        // 1. Verify byte format: LF structural bytes, single trailing LF, NO raw CR byte (0x0D)
        AssertExactSingleTrailingLf(bytes);
        Assert.IsFalse(serializedXml.Contains('\r'), "Serialized XML string must not contain raw CR character (must be entitized).");
        Assert.IsFalse(bytes.Contains((byte)'\r'), "Serialized bytes must not contain raw CR byte (0x0D).");

        // 2. Verify CR was entitized as &#xD;
        Assert.IsTrue(serializedXml.Contains("&#xD;"), "Carriage return must be entitized as &#xD; to survive XML normalization.");

        // 3. Roundtrip parse using standard XML reader and assert exact semantic .Value preservation
        using (var sr = new StringReader(serializedXml))
        {
            var parsedDoc = XDocument.Load(sr);
            var task = parsedDoc.Root!.Element("task")!;

            Assert.AreEqual(crText, task.Element("cr")!.Value, "Programmatic CR value must be preserved exactly after serialize+parse roundtrip.");
            Assert.AreEqual(lfText, task.Element("lf")!.Value, "Programmatic LF value must be preserved exactly after serialize+parse roundtrip.");
            Assert.AreEqual(crlfText, task.Element("crlf")!.Value, "Programmatic CRLF value must be preserved exactly after serialize+parse roundtrip.");
        }

        // 4. Also verify roundtrip via SecureXmlReaderFactory
        using (var ms = new MemoryStream(bytes))
        using (var reader = SecureXmlReaderFactory.CreateReader(ms))
        {
            var parsedSecure = XDocument.Load(reader, LoadOptions.PreserveWhitespace | LoadOptions.SetLineInfo);
            var task = parsedSecure.Root!.Element("task")!;

            Assert.AreEqual(crText, task.Element("cr")!.Value);
            Assert.AreEqual(lfText, task.Element("lf")!.Value);
            Assert.AreEqual(crlfText, task.Element("crlf")!.Value);
        }
    }

    [TestMethod]
    public void Serializer_PreservesComments()
    {
        var doc = new XDocument(
            new XComment("Top-level document comment"),
            new XElement("tasks",
                new XComment("Structural comment inside element"),
                new XElement("task", new XAttribute("id", "t1"))));

        var xml = ManagedDocumentSerializer.Serialize(doc);
        Assert.IsTrue(xml.Contains("<!--Top-level document comment-->", StringComparison.Ordinal));
        Assert.IsTrue(xml.Contains("  <!--Structural comment inside element-->", StringComparison.Ordinal));
    }

    [TestMethod]
    public void Serializer_ByteIdenticalNoOpRoundTrip_AcrossAllLoadOptions()
    {
        var canonicalSample = @"<?xml version=""1.0"" encoding=""utf-8""?>
<tasks id=""test-tasks"" revision=""1"">
  <index>
    <summary>Tasks for testing canonical serializer.</summary>
    <term key=""iteration"" value=""test"" />
  </index>
  <task id=""20260904-task-test"" status=""pending"">
    <title>Canonical roundtrip test</title>
    <objective>Ensure byte identity across all historically leaking load options.</objective>
    <constraints />
    <acceptance>
      <criterion id=""20260904-task-test-done"" status=""pending"">Passes all checks.</criterion>
    </acceptance>
    <records>
      <record id=""r1"" kind=""discussion"">
        <summary>First record</summary>
      </record>
      <record id=""r2"" kind=""discussion"">
        <summary>Second record</summary>
      </record>
    </records>
  </task>
</tasks>
".Replace("\r\n", "\n");

        var canonicalBytes = Encoding.UTF8.GetBytes(canonicalSample);

        // 1. LoadOptions.None
        using (var srNone = new StringReader(canonicalSample))
        {
            var docNone = XDocument.Load(srNone, LoadOptions.None);
            var resNone = ManagedDocumentSerializer.Serialize(docNone);
            Assert.AreEqual(canonicalSample, resNone, "No-op round trip with LoadOptions.None must be byte-identical.");
        }

        // 2. LoadOptions.SetLineInfo
        using (var srLine = new StringReader(canonicalSample))
        {
            var docLine = XDocument.Load(srLine, LoadOptions.SetLineInfo);
            var resLine = ManagedDocumentSerializer.Serialize(docLine);
            Assert.AreEqual(canonicalSample, resLine, "No-op round trip with LoadOptions.SetLineInfo must be byte-identical.");
        }

        // 3. LoadOptions.PreserveWhitespace
        using (var srWs = new StringReader(canonicalSample))
        {
            var docWs = XDocument.Load(srWs, LoadOptions.PreserveWhitespace);
            var resWs = ManagedDocumentSerializer.Serialize(docWs);
            Assert.AreEqual(canonicalSample, resWs, "No-op round trip with LoadOptions.PreserveWhitespace must be byte-identical.");
        }

        // 4. LoadOptions.PreserveWhitespace | LoadOptions.SetLineInfo
        using (var srWsLine = new StringReader(canonicalSample))
        {
            var docWsLine = XDocument.Load(srWsLine, LoadOptions.PreserveWhitespace | LoadOptions.SetLineInfo);
            var resWsLine = ManagedDocumentSerializer.Serialize(docWsLine);
            Assert.AreEqual(canonicalSample, resWsLine, "No-op round trip with PreserveWhitespace | SetLineInfo must be byte-identical.");
        }

        // 5. SecureXmlReaderFactory with IgnoreWhitespace = false
        using (var ms = new MemoryStream(canonicalBytes))
        using (var reader = SecureXmlReaderFactory.CreateReader(ms))
        {
            var docSecure = XDocument.Load(reader, LoadOptions.SetLineInfo);
            var resSecure = ManagedDocumentSerializer.Serialize(docSecure);
            Assert.AreEqual(canonicalSample, resSecure, "No-op round trip with SecureXmlReaderFactory (IgnoreWhitespace=false) must be byte-identical.");
        }
    }

    [TestMethod]
    public void Serializer_NoNewlineAccumulation_AfterRepeatedMutations()
    {
        var currentXml = "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<tasks revision=\"1\">\n</tasks>\n";

        for (var i = 1; i <= 15; i++)
        {
            // Simulate the exact historically leaking load pattern:
            // SecureXmlReaderFactory with IgnoreWhitespace=false + SetLineInfo + PreserveWhitespace
            using var ms = new MemoryStream(Encoding.UTF8.GetBytes(currentXml));
            using var reader = SecureXmlReaderFactory.CreateReader(ms);
            var doc = XDocument.Load(reader, LoadOptions.PreserveWhitespace | LoadOptions.SetLineInfo);

            doc.Root!.SetAttributeValue("revision", (i + 1).ToString(CultureInfo.InvariantCulture));
            doc.Root!.Add(new XElement("task",
                new XAttribute("id", $"task-{i}"),
                new XElement("summary", $"Task summary {i}")));

            currentXml = ManagedDocumentSerializer.Serialize(doc);
            var bytes = Encoding.UTF8.GetBytes(currentXml);

            AssertExactSingleTrailingLf(bytes);
        }
    }

    [TestMethod]
    public void Serializer_CanonicalXmlSerializerAliasParity()
    {
        var doc = new XDocument(new XElement("tasks", new XAttribute("revision", "1")));
        Assert.AreEqual(ManagedDocumentSerializer.Serialize(doc), CanonicalXmlSerializer.Serialize(doc));
        CollectionAssert.AreEqual(ManagedDocumentSerializer.SerializeToBytes(doc), CanonicalXmlSerializer.SerializeToBytes(doc));
    }

    #endregion

    #region Mutation Regressions Across All Write Paths

    [TestMethod]
    public void MutationRegression_BacklogLifecycle_NoNewlineAccumulationAndCleanGitDiff()
    {
        var workspace = CreateWorkspaceCopy();
        InitGitRepo(workspace);

        var backlogPath = Path.Combine(workspace, "backlog.xml");
        AssertExactSingleTrailingLf(File.ReadAllBytes(backlogPath));

        // Mutation 1: Add item 1
        var add1 = new BacklogCreateInput(
            "20260904-item-1", "20260904T080100Z-add-1", "tester",
            new DateTimeOffset(2026, 9, 4, 8, 1, 0, TimeSpan.Zero),
            "defect", "p1", "Item 1", "Statement 1", "Rationale 1", "Impact 1",
            SingleXPathIterationArray, EmptyStringArray, null, "when test passes");
        var (ok1, _, diags1) = BacklogLifecycle.Add(workspace, 1, add1);
        Assert.IsTrue(ok1, string.Join("; ", diags1.Select(d => d.Message)));
        AssertExactSingleTrailingLf(File.ReadAllBytes(backlogPath));
        AssertGitDiffCheckClean(workspace);

        // Mutation 2: Add item 2
        var add2 = new BacklogCreateInput(
            "20260904-item-2", "20260904T080200Z-add-2", "tester",
            new DateTimeOffset(2026, 9, 4, 8, 2, 0, TimeSpan.Zero),
            "defect", "p2", "Item 2", "Statement 2", "Rationale 2", "Impact 2",
            SingleXPathIterationArray, EmptyStringArray, null, "when test passes");
        var (ok2, _, diags2) = BacklogLifecycle.Add(workspace, 2, add2);
        Assert.IsTrue(ok2, string.Join("; ", diags2.Select(d => d.Message)));
        AssertExactSingleTrailingLf(File.ReadAllBytes(backlogPath));
        AssertGitDiffCheckClean(workspace);

        // Mutation 3: Schedule item 1
        var sched = new BacklogTransitionInput(
            "20260904-item-1", "20260904T080300Z-sched-1", "tester",
            new DateTimeOffset(2026, 9, 4, 8, 3, 0, TimeSpan.Zero), "20260823-task-xpath-projection");
        var (ok3, _, diags3) = BacklogLifecycle.Schedule(workspace, 3, sched);
        Assert.IsTrue(ok3, string.Join("; ", diags3.Select(d => d.Message)));
        AssertExactSingleTrailingLf(File.ReadAllBytes(backlogPath));
        AssertGitDiffCheckClean(workspace);

        // Mutation 4: Complete item 1
        var comp = new BacklogTransitionInput(
            "20260904-item-1", "20260904T080400Z-comp-1", "tester",
            new DateTimeOffset(2026, 9, 4, 8, 4, 0, TimeSpan.Zero), "20260823-task-xpath-projection");
        var (ok4, _, diags4) = BacklogLifecycle.Complete(workspace, 4, comp);
        Assert.IsTrue(ok4, string.Join("; ", diags4.Select(d => d.Message)));
        AssertExactSingleTrailingLf(File.ReadAllBytes(backlogPath));
        AssertGitDiffCheckClean(workspace);
    }

    [TestMethod]
    public void MutationRegression_TaskReview_NoNewlineAccumulationAndCleanGitDiff()
    {
        var workspace = CreateWorkspaceCopy();

        var tasksPath = Path.Combine(workspace, "20260823-xpath-core", "tasks.xml");
        var tasksDoc = XDocument.Load(tasksPath);
        var task = tasksDoc.Root!.Elements("task").First(t => (string?)t.Attribute("id") == "20260823-task-xpath-projection");
        task.SetAttributeValue("status", "verification");
        task.SetAttributeValue("agent", "implementer");
        var statusTerm = task.Element("index")!.Elements("term").FirstOrDefault(t => (string?)t.Attribute("key") == "status");
        if (statusTerm == null) task.Element("index")!.Add(new XElement("term", new XAttribute("key", "status"), new XAttribute("value", "verification")));
        else statusTerm.SetAttributeValue("value", "verification");
        if (task.Element("review") == null)
            task.Element("records")!.AddBeforeSelf(new XElement("review", new XAttribute("required", "true")));
        File.WriteAllText(tasksPath, ManagedDocumentSerializer.Serialize(tasksDoc));

        InitGitRepo(workspace);
        AssertExactSingleTrailingLf(File.ReadAllBytes(tasksPath));

        var reviewRequest = @"<task-review id=""20260904T081000Z-review-1"" actor=""independent-reviewer"" occurred_at=""2026-09-04T08:10:00Z"">
  <submission id=""20260904T081000Z-sub-1"" disposition=""approved"">
    <summary>Task review approved cleanly.</summary>
  </submission>
</task-review>";

        var (ok, env, diags) = TaskReviewer.Submit(workspace, "20260823-xpath-core", "20260823-task-xpath-projection", 9, reviewRequest);
        Assert.IsTrue(ok, string.Join("; ", diags.Select(d => d.Message)));
        AssertExactSingleTrailingLf(File.ReadAllBytes(tasksPath));
        AssertGitDiffCheckClean(workspace);
    }

    [TestMethod]
    public void MutationRegression_TaskQuick_NoNewlineAccumulationAndCleanGitDiff()
    {
        var workspace = CreateWorkspaceCopy();
        InitGitRepo(workspace);

        var tasksPath = Path.Combine(workspace, "20260823-xpath-core", "tasks.xml");
        AssertExactSingleTrailingLf(File.ReadAllBytes(tasksPath));

        var input = new QuickTaskInput(
            Title: "Quick task title",
            Scopes: CoreScopeArray,
            DoneWhen: "Criterion done",
            Why: "Testing canonical quick task",
            Origins: EmptyStringArray,
            Dependencies: EmptyStringArray,
            Terms: EmptyStringArray,
            IterationId: "20260823-xpath-core",
            ExpectedRevision: 9,
            Start: false,
            DryRun: false,
            TaskId: "20260904-task-quick-test",
            OperationId: "20260904T080000Z-quick-op-1",
            Agent: "agy-quick-implementer",
            ReviewRequired: true);

        var (ok, _, _, diags) = TaskQuick.Create(workspace, input);
        Assert.IsTrue(ok, string.Join("; ", diags.Select(d => d.Message)));
        AssertExactSingleTrailingLf(File.ReadAllBytes(tasksPath));
        AssertGitDiffCheckClean(workspace);

        // Verify that newly added task has structural line breaks and indentation
        var content = File.ReadAllText(tasksPath);
        Assert.IsFalse(content.Contains("</task><task", StringComparison.Ordinal), "Tasks must not be smashed on one line.");
        Assert.IsFalse(content.Contains("</record></records>", StringComparison.Ordinal), "Records must not be smashed together.");
    }

    [TestMethod]
    public void MutationRegression_TaskUpdate_NoNewlineAccumulationAndCleanGitDiff()
    {
        var workspace = CreateWorkspaceCopy();
        InitGitRepo(workspace);

        var tasksPath = Path.Combine(workspace, "20260823-xpath-core", "tasks.xml");
        var updateRequest = @"<task-update id=""20260904T082000Z-update-1"" transition=""start"" actor=""codex"" occurred_at=""2026-09-04T08:20:00Z"">
  <records>
    <record id=""20260904T082000Z-record-1"" kind=""start"" status=""informational"" created_at=""2026-09-04T08:20:00Z"" actor=""codex"">
      <summary>Starting task.</summary>
    </record>
  </records>
</task-update>";

        var (ok, _, diags) = TaskUpdater.Update(workspace, "20260823-xpath-core", "20260823-task-task-history", 9, updateRequest);
        Assert.IsTrue(ok, string.Join("; ", diags.Select(d => d.Message)));
        AssertExactSingleTrailingLf(File.ReadAllBytes(tasksPath));
        AssertGitDiffCheckClean(workspace);
    }

    [TestMethod]
    public void MutationRegression_TaskRevise_NoNewlineAccumulationAndCleanGitDiff()
    {
        var workspace = CreateWorkspaceCopy();
        InitGitRepo(workspace);

        var tasksPath = Path.Combine(workspace, "20260823-xpath-core", "tasks.xml");
        var reviseRequest = @"<task-revise id=""20260904T083000Z-revise-1"" actor=""codex"" occurred_at=""2026-09-04T08:30:00Z"">
  <rationale>Updated rationale for task.</rationale>
  <records>
    <record id=""20260904T083000Z-rec-1"" kind=""discussion"" status=""informational"" created_at=""2026-09-04T08:30:00Z"" actor=""codex"">
      <summary>Elaborated rationale.</summary>
    </record>
  </records>
</task-revise>";

        var (ok, _, diags) = TaskReviser.Revise(workspace, "20260823-xpath-core", "20260823-task-task-history", 9, reviseRequest);
        Assert.IsTrue(ok, string.Join("; ", diags.Select(d => d.Message)));
        AssertExactSingleTrailingLf(File.ReadAllBytes(tasksPath));
        AssertGitDiffCheckClean(workspace);
    }

    [TestMethod]
    public void MutationRegression_GenericAppend_NoNewlineAccumulationAndCleanGitDiff()
    {
        var workspace = CreateWorkspaceCopy();
        InitGitRepo(workspace);

        var tasksPath = Path.Combine(workspace, "20260823-xpath-core", "tasks.xml");
        var recordXml = @"<record id=""20260904T084000Z-rec-append"" kind=""discussion"" status=""informational"" created_at=""2026-09-04T08:40:00Z"" actor=""agent"">
  <summary>Appended discussion record.</summary>
</record>";

        var (ok, _, diags) = GenericAppender.Append(workspace, "20260823-xpath-core/tasks.xml", "//task[@id='20260823-task-xpath-projection']/records", 9, recordXml);
        Assert.IsTrue(ok, string.Join("; ", diags.Select(d => d.Message)));
        AssertExactSingleTrailingLf(File.ReadAllBytes(tasksPath));
        AssertGitDiffCheckClean(workspace);
    }

    [TestMethod]
    public void MutationRegression_RequirementProposer_NoNewlineAccumulationAndCleanGitDiff()
    {
        var workspace = CreateWorkspaceCopy();
        InitGitRepo(workspace);

        var specPath = Path.Combine(workspace, "20260823-xpath-core", "spec.xml");
        var reqRequest = @"<requirement-propose id=""20260904T085000Z-req-prop-1"" actor=""author"" occurred_at=""2026-09-04T08:50:00Z"">
  <requirement id=""20260904-req-test-proposal"" status=""proposed"">
    <index><summary>Test requirement proposal.</summary><term key=""topic"" value=""canonical"" /></index>
    <statement>Requirement statement here.</statement>
    <rationale>Requirement rationale here.</rationale>
  </requirement>
</requirement-propose>";

        var (ok, _, diags) = RequirementProposer.Propose(workspace, "20260823-xpath-core", 4, reqRequest);
        Assert.IsTrue(ok, string.Join("; ", diags.Select(d => d.Message)));
        AssertExactSingleTrailingLf(File.ReadAllBytes(specPath));
        AssertGitDiffCheckClean(workspace);
    }

    [TestMethod]
    public void MutationRegression_IterationCreatorAndInitializer_ProduceCanonicalDocuments()
    {
        // Test WorkspaceInitializer
        var initTarget = Path.Combine(_tempDir, "fresh-workspace");
        var (initOk, wsRoot, initErr) = WorkspaceInitializer.Initialize(null, initTarget);
        Assert.IsTrue(initOk, initErr?.Message);

        var backlogPath = Path.Combine(wsRoot, "backlog.xml");
        var knowledgePath = Path.Combine(wsRoot, "knowledge.xml");

        AssertExactSingleTrailingLf(File.ReadAllBytes(backlogPath));
        AssertExactSingleTrailingLf(File.ReadAllBytes(knowledgePath));

        InitGitRepo(wsRoot);
        AssertGitDiffCheckClean(wsRoot);

        // Test IterationCreator
        var (createOk, iterRes, createDiags) = IterationCreator.Create(wsRoot, "20260905-test-iteration", "feature", activate: false);
        Assert.IsTrue(createOk, string.Join("; ", createDiags.Select(d => d.Message)));

        var createdSpecPath = Path.Combine(wsRoot, "20260905-test-iteration", "spec.xml");
        var createdTasksPath = Path.Combine(wsRoot, "20260905-test-iteration", "tasks.xml");

        AssertExactSingleTrailingLf(File.ReadAllBytes(createdSpecPath));
        AssertExactSingleTrailingLf(File.ReadAllBytes(createdTasksPath));

        RunGit(wsRoot, "add .");
        AssertGitDiffCheckClean(wsRoot);
    }

    [TestMethod]
    public void Committer_EnforcesCanonicalNormalization_WhenGivenNoncanonicalReplacementContent()
    {
        var workspace = CreateWorkspaceCopy();
        InitGitRepo(workspace);

        var tasksPath = Path.Combine(workspace, "20260823-xpath-core", "tasks.xml");
        AssertExactSingleTrailingLf(File.ReadAllBytes(tasksPath));

        var tasksDoc = XDocument.Load(tasksPath);
        tasksDoc.Root!.SetAttributeValue("revision", "10");

        var targetTask = tasksDoc.Root.Elements("task").First(t => (string?)t.Attribute("id") == "20260823-task-xpath-projection");
        var records = targetTask.Element("records")!;
        records.Add(
            new XElement("record",
                new XAttribute("id", "20260904T090000Z-rec-nc1"),
                new XAttribute("kind", "discussion"),
                new XAttribute("status", "informational"),
                new XAttribute("created_at", "2026-09-04T09:00:00Z"),
                new XAttribute("actor", "tester"),
                new XElement("summary", "Noncanonical test 1")),
            new XElement("record",
                new XAttribute("id", "20260904T090000Z-rec-nc2"),
                new XAttribute("kind", "discussion"),
                new XAttribute("status", "informational"),
                new XAttribute("created_at", "2026-09-04T09:00:00Z"),
                new XAttribute("actor", "tester"),
                new XElement("summary", "Noncanonical test 2")));

        // Produce intentionally noncanonical XML:
        // 1. Disable formatting so sibling elements run together without newlines or indentation
        // 2. Inject CRLF (\r\n) line endings
        // 3. Add multiple trailing newlines (\r\n\r\n\r\n)
        var unformattedXml = tasksDoc.ToString(SaveOptions.DisableFormatting);
        var noncanonicalReplacement = "<?xml version=\"1.0\" encoding=\"utf-8\"?>\r\n" +
            unformattedXml.Replace("\n", "\r\n") + "\r\n\r\n\r\n\r\n";

        var op = new TransactionDocumentOperation(
            "20260823-xpath-core/tasks.xml",
            noncanonicalReplacement,
            ExpectedRevision: 9,
            NewRevision: 10);

        var (ok, envelope, diags) = WorkspaceTransactionCommitter.Commit(
            workspace,
            "test direct noncanonical commit",
            new[] { op });

        Assert.IsTrue(ok, string.Join("; ", diags.Select(d => d.Message)));
        Assert.IsNotNull(envelope);

        // Verify the persisted document on disk is strictly canonical:
        var committedBytes = File.ReadAllBytes(tasksPath);
        AssertExactSingleTrailingLf(committedBytes);

        var committedText = File.ReadAllText(tasksPath);
        // Must not contain CRLF
        Assert.IsFalse(committedText.Contains('\r'), "Persisted document must not contain CR bytes.");
        // Must not contain run-on siblings
        Assert.IsFalse(committedText.Contains("</record><record"), "Run-on siblings must be normalized into separate indented lines.");
        // Sibling records must be on separate lines
        var lines = committedText.Split('\n');
        var r1Idx = Array.FindIndex(lines, l => l.Contains("id=\"20260904T090000Z-rec-nc1\""));
        var r2Idx = Array.FindIndex(lines, l => l.Contains("id=\"20260904T090000Z-rec-nc2\""));
        Assert.IsTrue(r1Idx >= 0 && r2Idx >= 0 && r2Idx > r1Idx, "Sibling records must be formatted onto separate lines.");

        // git diff --check must be completely clean
        AssertGitDiffCheckClean(workspace);
    }

    [TestMethod]
    public void MutationRegression_IterationConfirmer_PreserveWhitespace_SingleTrailingLfAndStructuralFormatting()
    {
        var workspace = CreateWorkspaceCopy();
        InitGitRepo(workspace);

        var specPath = Path.Combine(workspace, "20260823-xpath-core", "spec.xml");
        AssertExactSingleTrailingLf(File.ReadAllBytes(specPath));

        // Read initial spec document to record prose text values
        var specDocBefore = XDocument.Load(specPath, LoadOptions.PreserveWhitespace | LoadOptions.SetLineInfo);
        var initialObjective = specDocBefore.Descendants("objective").First().Value;
        var initialStatements = specDocBefore.Descendants("statement").Select(s => s.Value).ToList();
        var initialRationales = specDocBefore.Descendants("rationale").Select(r => r.Value).ToList();

        // Perform iteration confirmation transition (replan active -> replanning)
        var replanXml = @"<?xml version=""1.0"" encoding=""utf-8""?>
<iteration-confirmation
  id=""20260904T120000Z-confirm-replan""
  iteration=""20260823-xpath-core""
  action=""replan""
  expected_spec_revision=""4""
  actor=""architect""
  decided_at=""2026-09-04T12:00:00Z"">
  <summary>Replanning iteration due to scope change.</summary>
</iteration-confirmation>";

        var (ok, envelope, diags) = IterationConfirmer.Confirm(workspace, replanXml);
        Assert.IsTrue(ok, string.Join("; ", diags.Select(d => d.Message)));
        Assert.IsNotNull(envelope);

        // Verify trailing LF and no CR
        var committedBytes = File.ReadAllBytes(specPath);
        AssertExactSingleTrailingLf(committedBytes);

        // Verify structural formatting
        var committedText = File.ReadAllText(specPath);
        var lines = committedText.Split('\n');
        Assert.IsTrue(lines.Any(l => l.StartsWith("  <confirmations>", StringComparison.Ordinal)), "Confirmations element must be indented 2 spaces.");
        Assert.IsTrue(lines.Any(l => l.StartsWith("    <confirmation id=\"20260904T120000Z-confirm-replan\"", StringComparison.Ordinal)), "Confirmation item must be indented 4 spaces.");

        // Verify unchanged prose values
        var specDocAfter = XDocument.Load(specPath, LoadOptions.PreserveWhitespace | LoadOptions.SetLineInfo);
        Assert.AreEqual(initialObjective, specDocAfter.Descendants("objective").First().Value, "Objective prose value must remain unchanged.");
        var afterStatements = specDocAfter.Descendants("statement").Select(s => s.Value).ToList();
        CollectionAssert.AreEqual(initialStatements, afterStatements, "All requirement statements must remain unchanged.");
        var afterRationales = specDocAfter.Descendants("rationale").Select(r => r.Value).ToList();
        CollectionAssert.AreEqual(initialRationales, afterRationales, "All requirement rationales must remain unchanged.");

        // Verify git diff --check cleanliness
        AssertGitDiffCheckClean(workspace);
    }

    [TestMethod]
    public void MutationRegression_TransactionApplier_MultiDocument_NoNewlineAccumulationAndCleanGitDiff()
    {
        var workspace = CreateWorkspaceCopy();
        InitGitRepo(workspace);

        var tasksPath = Path.Combine(workspace, "20260823-xpath-core", "tasks.xml");
        AssertExactSingleTrailingLf(File.ReadAllBytes(tasksPath));

        var request = """
            <transaction operation_id="20260904T130000Z-applier-test">
              <variables><variable name="task_id">20260823-task-xpath-projection</variable></variables>
              <document path="20260823-xpath-core/tasks.xml" expected_revision="9">
                <assert test="count(/tasks/task[@id=$task_id]) = 1"/>
                <append-child select="/tasks/task[@id=$task_id]/records" expect="1">
                  <record id="20260904T130000Z-record-applier" kind="discussion" status="informational" created_at="2026-09-04T13:00:00Z" actor="core-test">
                    <summary>Transaction applier regression record.</summary>
                  </record>
                </append-child>
              </document>
            </transaction>
            """;

        var (success, envelope, diagnostics) = TransactionApplier.Apply(workspace, request,
            new TestClock(new DateTime(2026, 9, 4, 13, 0, 0, DateTimeKind.Utc)));

        Assert.IsTrue(success, string.Join("; ", diagnostics.Select(d => d.Message)));
        Assert.IsNotNull(envelope);

        AssertExactSingleTrailingLf(File.ReadAllBytes(tasksPath));
        AssertGitDiffCheckClean(workspace);
    }

    [TestMethod]
    public void MutationRegression_TaskQuick_DryRunRequestSerialization_ProducesCanonicalXml()
    {
        var workspace = CreateWorkspaceCopy();

        var input = new QuickTaskInput(
            Title: "Dry run quick task",
            Scopes: CoreScopeArray,
            DoneWhen: "Criterion done",
            Why: "Testing canonical dry-run serialization",
            Origins: EmptyStringArray,
            Dependencies: EmptyStringArray,
            Terms: EmptyStringArray,
            IterationId: "20260823-xpath-core",
            ExpectedRevision: 9,
            Start: false,
            DryRun: true,
            TaskId: "20260904-task-dryrun",
            OperationId: "20260904T080000Z-dryrun-op-1",
            Agent: "agy-quick-implementer",
            ReviewRequired: true);

        var (ok, result, _, diags) = TaskQuick.Create(workspace, input);
        Assert.IsTrue(ok, string.Join("; ", diags.Select(d => d.Message)));
        Assert.IsNotNull(result);
        Assert.IsNotNull(result.RequestXml);

        var bytes = Encoding.UTF8.GetBytes(result.RequestXml);
        AssertExactSingleTrailingLf(bytes);
        Assert.IsTrue(result.RequestXml.StartsWith("<?xml version=\"1.0\" encoding=\"utf-8\"?>\n", StringComparison.Ordinal));
        Assert.IsTrue(result.RequestXml.Contains("<task-add id=\"20260904T080000Z-dryrun-op-1\"", StringComparison.Ordinal));
    }

    #endregion
}
