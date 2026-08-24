using System.Globalization;
using System.Xml.XPath;
using DogdouSpec.Core.Diagnostics;
using DogdouSpec.Core.Workspace;
using DogdouSpec.Core.XPath;

namespace DogdouSpec.Core.Tests;

[TestClass]
public sealed class XPathProjectionTests
{
    private string _tempDir = null!;

    [TestInitialize]
    public void SetUp()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "DogdouSpec_XPathProjectionTests_" + Guid.NewGuid().ToString("N"));
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

    private ManagedDocument CreateTestDoc(string fileName, string xmlContent)
    {
        var fullPath = Path.Combine(_tempDir, fileName);
        var dir = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }
        File.WriteAllText(fullPath, xmlContent);
        return new ManagedDocument(fileName, fullPath);
    }

    [TestMethod]
    public void Filter_RetainsOnlySpecifiedAttributesAndChildren()
    {
        var doc = CreateTestDoc("tasks.xml", """
            <?xml version="1.0" encoding="utf-8"?>
            <tasks id="tasks-root" revision="9">
              <task id="T-002" status="pending" agent="codex" created_at="2026-08-23T01:00:00Z">
                <index>
                  <summary>Summary info</summary>
                  <term key="topic" value="projection"/>
                </index>
                <title>Define XPath projection</title>
                <context>Large context content...</context>
                <records>Large history records...</records>
              </task>
            </tasks>
            """);

        var res = XPathQueryEngine.EvaluateDocument(_tempDir, doc, "ds:filter(//task, '@id', '@status', 'index')", null);

        Assert.AreEqual(1, res.Nodes.Count);
        Assert.IsTrue(res.Derived);

        var node = res.Nodes[0];
        Assert.AreEqual("T-002", node.GetAttribute("id", ""));
        Assert.AreEqual("pending", node.GetAttribute("status", ""));
        Assert.AreEqual("", node.GetAttribute("agent", "")); // excluded
        Assert.AreEqual("", node.GetAttribute("created_at", "")); // excluded

        // Child elements
        var nav = node.Clone();
        Assert.IsTrue(nav.MoveToChild("index", ""));
        Assert.IsTrue(nav.MoveToChild("summary", ""));
        Assert.AreEqual("Summary info", nav.Value);

        // title, context, records excluded
        Assert.IsFalse(node.Clone().MoveToChild("title", ""));
        Assert.IsFalse(node.Clone().MoveToChild("context", ""));
        Assert.IsFalse(node.Clone().MoveToChild("records", ""));
    }

    [TestMethod]
    public void Filter_PreservesDirectRootText()
    {
        var doc = CreateTestDoc("tasks.xml", """
            <?xml version="1.0" encoding="utf-8"?>
            <tasks id="t" revision="1">
              <title id="title-1">Direct narrative text</title>
            </tasks>
            """);

        var res = XPathQueryEngine.EvaluateDocument(_tempDir, doc, "ds:filter(//title, '@id')", null);

        Assert.AreEqual(1, res.Nodes.Count);
        var node = res.Nodes[0];
        Assert.AreEqual("title-1", node.GetAttribute("id", ""));
        Assert.AreEqual("Direct narrative text", node.Value);
    }

    [TestMethod]
    public void Filter_MissingAndDuplicateMembers_IgnoredAndCoalesced()
    {
        var doc = CreateTestDoc("tasks.xml", """
            <?xml version="1.0" encoding="utf-8"?>
            <tasks id="t" revision="1">
              <task id="T1" status="pending">
                <index><summary>Test</summary></index>
              </task>
            </tasks>
            """);

        var res = XPathQueryEngine.EvaluateDocument(
            _tempDir,
            doc,
            "ds:filter(//task, '@id', '@id', '@missing_attr', 'index', 'index', 'missing_child')",
            null);

        Assert.AreEqual(1, res.Nodes.Count);
        var node = res.Nodes[0];
        Assert.AreEqual("T1", node.GetAttribute("id", ""));
        Assert.IsTrue(node.Clone().MoveToChild("index", ""));
    }

    [TestMethod]
    public void FilterOut_RemovesSpecifiedAttributesAndChildren()
    {
        var doc = CreateTestDoc("tasks.xml", """
            <?xml version="1.0" encoding="utf-8"?>
            <tasks id="t" revision="1">
              <task id="T1" status="pending" updated_at="2026-08-23T04:00:00Z">
                <index><summary>Test</summary></index>
                <title>Task Title</title>
                <context>Big context</context>
                <records>Big records</records>
              </task>
            </tasks>
            """);

        var res = XPathQueryEngine.EvaluateDocument(
            _tempDir,
            doc,
            "ds:filter-out(//task, '@updated_at', 'context', 'records')",
            null);

        Assert.AreEqual(1, res.Nodes.Count);
        Assert.IsTrue(res.Derived);

        var node = res.Nodes[0];
        Assert.AreEqual("T1", node.GetAttribute("id", ""));
        Assert.AreEqual("pending", node.GetAttribute("status", ""));
        Assert.AreEqual("", node.GetAttribute("updated_at", "")); // removed

        Assert.IsTrue(node.Clone().MoveToChild("index", ""));
        Assert.IsTrue(node.Clone().MoveToChild("title", ""));
        Assert.IsFalse(node.Clone().MoveToChild("context", "")); // removed
        Assert.IsFalse(node.Clone().MoveToChild("records", "")); // removed
    }

    [TestMethod]
    public void Filter_EmptyNodeSetInput_ReturnsEmptyNodeSetWithoutError()
    {
        var doc = CreateTestDoc("tasks.xml", """
            <?xml version="1.0" encoding="utf-8"?>
            <tasks id="t" revision="1"/>
            """);

        var res = XPathQueryEngine.EvaluateDocument(_tempDir, doc, "ds:filter(//task, '@id')", null);

        Assert.AreEqual(0, res.Nodes.Count);
        Assert.IsTrue(res.Derived);
    }

    [TestMethod]
    [DataRow("ds:filter(//task/@id, '@id')")]
    [DataRow("ds:filter(//task/title/text(), '@id')")]
    [DataRow("ds:filter(42, '@id')")]
    [DataRow("ds:filter('invalid', '@id')")]
    public void Filter_NonElementFirstArgument_ThrowsInvalidArgument(string badExpr)
    {
        var doc = CreateTestDoc("tasks.xml", """
            <?xml version="1.0" encoding="utf-8"?>
            <tasks id="t" revision="1">
              <task id="T1"><title>Text</title></task>
            </tasks>
            """);

        var ex = Assert.Throws<DogdouXPathException>(() =>
            XPathQueryEngine.EvaluateDocument(_tempDir, doc, badExpr, null));

        Assert.AreEqual(DiagnosticCodes.InvalidArgument, ex.Code);
        Assert.AreEqual(2, ex.ExitCode);
    }

    [TestMethod]
    [DataRow("ds:filter(//task)")]
    [DataRow("ds:filter-out(//task)")]
    public void Filter_ZeroMembers_ThrowsInvalidArgument(string badExpr)
    {
        var doc = CreateTestDoc("tasks.xml", """
            <?xml version="1.0" encoding="utf-8"?>
            <tasks id="t" revision="1">
              <task id="T1"/>
            </tasks>
            """);

        var ex = Assert.Throws<DogdouXPathException>(() =>
            XPathQueryEngine.EvaluateDocument(_tempDir, doc, badExpr, null));

        Assert.AreEqual(DiagnosticCodes.InvalidArgument, ex.Code);
        Assert.AreEqual(2, ex.ExitCode);
    }

    [TestMethod]
    [DataRow("ds:filter(//task, 'index/term')")]
    [DataRow("ds:filter(//task, 'child::index')")]
    [DataRow("ds:filter(//task, '*')")]
    [DataRow("ds:filter(//task, 'index[1]')")]
    [DataRow("ds:filter(//task, 'ns:index')")]
    [DataRow("ds:filter(//task, '.')")]
    [DataRow("ds:filter(//task, '..')")]
    [DataRow("ds:filter(//task, 'count()')")]
    [DataRow("ds:filter(//task, '@')")]
    [DataRow("ds:filter(//task, '')")]
    public void Filter_InvalidMemberSyntax_ThrowsInvalidArgument(string badExpr)
    {
        var doc = CreateTestDoc("tasks.xml", """
            <?xml version="1.0" encoding="utf-8"?>
            <tasks id="t" revision="1">
              <task id="T1"/>
            </tasks>
            """);

        var ex = Assert.Throws<DogdouXPathException>(() =>
            XPathQueryEngine.EvaluateDocument(_tempDir, doc, badExpr, null));

        Assert.AreEqual(DiagnosticCodes.InvalidArgument, ex.Code);
        Assert.AreEqual(2, ex.ExitCode);
    }

    [TestMethod]
    public void Filter_PostProjectionPredicatesAndAxes_WorkComposably()
    {
        var doc = CreateTestDoc("tasks.xml", """
            <?xml version="1.0" encoding="utf-8"?>
            <tasks id="t" revision="1">
              <task id="T1" status="pending">
                <index>
                  <term key="topic" value="xml"/>
                </index>
              </task>
              <task id="T2" status="pending">
                <index>
                  <term key="topic" value="xpath"/>
                </index>
              </task>
            </tasks>
            """);

        var vars = new Dictionary<string, string> { { "topic", "xpath" } };

        // Predicate filtering on projected node
        var res1 = XPathQueryEngine.EvaluateDocument(
            _tempDir,
            doc,
            "ds:filter(//task, '@id', '@status', 'index')[index/term[@key='topic' and @value=$topic]]",
            vars);

        Assert.AreEqual(1, res1.Nodes.Count);
        Assert.AreEqual("T2", res1.Nodes[0].GetAttribute("id", ""));

        // Path step from projected node
        var res2 = XPathQueryEngine.EvaluateDocument(
            _tempDir,
            doc,
            "ds:filter(//task, '@id', 'index')/index/term[@key='topic']",
            vars);

        Assert.AreEqual(2, res2.Nodes.Count);
    }

    [TestMethod]
    public void Filter_NestedProjections_WorkComposably()
    {
        var doc = CreateTestDoc("tasks.xml", """
            <?xml version="1.0" encoding="utf-8"?>
            <tasks id="t" revision="1">
              <task id="T1" status="pending">
                <index><summary>Summary</summary></index>
                <title>Task Title</title>
                <context>Context text</context>
              </task>
            </tasks>
            """);

        // Inner filter keeps id, status, index, title. Outer filter-out removes title.
        var res = XPathQueryEngine.EvaluateDocument(
            _tempDir,
            doc,
            "ds:filter-out(ds:filter(//task, '@id', '@status', 'index', 'title'), 'title')",
            null);

        Assert.AreEqual(1, res.Nodes.Count);
        var node = res.Nodes[0];
        Assert.AreEqual("T1", node.GetAttribute("id", ""));
        Assert.AreEqual("pending", node.GetAttribute("status", ""));
        Assert.IsTrue(node.Clone().MoveToChild("index", ""));
        Assert.IsFalse(node.Clone().MoveToChild("title", "")); // Removed by outer
        Assert.IsFalse(node.Clone().MoveToChild("context", "")); // Removed by inner
    }

    [TestMethod]
    [DataRow("ds:filter(//task, @id)")]
    [DataRow("ds:filter(//task, title)")]
    [DataRow("ds:filter(//task, //task/@id)")]
    [DataRow("ds:filter-out(//task, @id)")]
    [DataRow("ds:filter-out(//task, title)")]
    [DataRow("ds:filter(//task, true())")]
    [DataRow("ds:filter(//task, false())")]
    [DataRow("ds:filter(//task, 1 = 1)")]
    [DataRow("ds:filter-out(//task, true())")]
    [DataRow("ds:filter(//task, 42)")]
    [DataRow("ds:filter(//task, 3.14)")]
    [DataRow("ds:filter(//task, count(//task))")]
    [DataRow("ds:filter-out(//task, 42)")]
    public void Filter_NonStringMemberArguments_ThrowsInvalidArgument(string badExpr)
    {
        var doc = CreateTestDoc("tasks.xml", """
            <?xml version="1.0" encoding="utf-8"?>
            <tasks id="t" revision="1">
              <task id="T1"><title>Text</title></task>
            </tasks>
            """);

        var ex = Assert.Throws<DogdouXPathException>(() =>
            XPathQueryEngine.EvaluateDocument(_tempDir, doc, badExpr, null));

        Assert.AreEqual(DiagnosticCodes.InvalidArgument, ex.Code);
        Assert.AreEqual(2, ex.ExitCode);
        Assert.IsTrue(
            ex.Message.Contains("must be an XPath string", StringComparison.Ordinal) ||
            ex.Message.Contains("Invalid member argument", StringComparison.Ordinal));
    }

    [TestMethod]
    public void Filter_BoundStringVariableMembers_Succeeds()
    {
        var doc = CreateTestDoc("tasks.xml", """
            <?xml version="1.0" encoding="utf-8"?>
            <tasks id="t" revision="1">
              <task id="T1" status="pending">
                <index><summary>Summary</summary></index>
                <context>Context</context>
              </task>
            </tasks>
            """);

        var vars = new Dictionary<string, string>
        {
            { "id_attr", "@id" },
            { "status_attr", "@status" },
            { "index_elem", "index" }
        };

        var res = XPathQueryEngine.EvaluateDocument(
            _tempDir,
            doc,
            "ds:filter(//task, $id_attr, $status_attr, $index_elem)",
            vars);

        Assert.AreEqual(1, res.Nodes.Count);
        Assert.IsTrue(res.Derived);
        var node = res.Nodes[0];
        Assert.AreEqual("T1", node.GetAttribute("id", ""));
        Assert.AreEqual("pending", node.GetAttribute("status", ""));
        Assert.IsTrue(node.Clone().MoveToChild("index", ""));
        Assert.IsFalse(node.Clone().MoveToChild("context", ""));
    }

    [TestMethod]
    public void Filter_BoundVariableWithInvalidSyntax_ThrowsInvalidArgument()
    {
        var doc = CreateTestDoc("tasks.xml", """
            <?xml version="1.0" encoding="utf-8"?>
            <tasks id="t" revision="1">
              <task id="T1"/>
            </tasks>
            """);

        var vars = new Dictionary<string, string>
        {
            { "bad_member", "invalid/path" }
        };

        var ex = Assert.Throws<DogdouXPathException>(() =>
            XPathQueryEngine.EvaluateDocument(_tempDir, doc, "ds:filter(//task, $bad_member)", vars));

        Assert.AreEqual(DiagnosticCodes.InvalidArgument, ex.Code);
        Assert.AreEqual(2, ex.ExitCode);
    }

    [TestMethod]
    public void DogdouXsltContext_CompareDocument_EqualUris_ReturnsZero()
    {
        var evalContext = new XPathEvaluationContext();
        var xsltContext = new DogdouXsltContext(null, evalContext);

        Assert.AreEqual(0, xsltContext.CompareDocument("dogdou://managed/tasks.xml", "dogdou://managed/tasks.xml"));
        Assert.AreEqual(0, xsltContext.CompareDocument("dogdou://projected/0", "dogdou://projected/0"));
        Assert.AreEqual(0, xsltContext.CompareDocument(string.Empty, string.Empty));
    }

    [TestMethod]
    public void DogdouXsltContext_CompareDocument_DistinctUris_IsDeterministicAndAntisymmetric()
    {
        var evalContext = new XPathEvaluationContext();
        var xsltContext = new DogdouXsltContext(null, evalContext);

        var uriA = "dogdou://managed/a.xml";
        var uriB = "dogdou://managed/b.xml";

        var cmpAB = xsltContext.CompareDocument(uriA, uriB);
        var cmpBA = xsltContext.CompareDocument(uriB, uriA);

        Assert.AreEqual(-1, cmpAB, "uriA should precede uriB");
        Assert.AreEqual(1, cmpBA, "uriB should follow uriA");
        Assert.AreEqual(cmpAB, -cmpBA, "CompareDocument must be antisymmetric");
    }

    [TestMethod]
    public void Filter_UnionWithUnprojectedElements_PreservesDocumentOrder()
    {
        var doc = CreateTestDoc("tasks.xml", """
            <?xml version="1.0" encoding="utf-8"?>
            <tasks id="t" revision="1">
              <task id="T1" status="done"><title>One</title><context>Ctx1</context></task>
              <task id="T2" status="pending"><title>Two</title><context>Ctx2</context></task>
            </tasks>
            """);

        var res = XPathQueryEngine.EvaluateDocument(
            _tempDir,
            doc,
            "//task[@status='done'] | ds:filter(//task[@status='pending'], '@id')",
            null);

        Assert.AreEqual(2, res.Nodes.Count);
        Assert.IsTrue(res.Derived);

        // First node is unprojected T1 (retains title and context)
        var node1 = res.Nodes[0];
        Assert.AreEqual("T1", node1.GetAttribute("id", ""));
        Assert.AreEqual("done", node1.GetAttribute("status", ""));
        Assert.IsTrue(node1.Clone().MoveToChild("title", ""));
        Assert.IsTrue(node1.Clone().MoveToChild("context", ""));

        // Second node is projected T2 (has @id only, no @status, no title, no context)
        var node2 = res.Nodes[1];
        Assert.AreEqual("T2", node2.GetAttribute("id", ""));
        Assert.AreEqual("", node2.GetAttribute("status", ""));
        Assert.IsFalse(node2.Clone().MoveToChild("title", ""));
        Assert.IsFalse(node2.Clone().MoveToChild("context", ""));
    }

    [TestMethod]
    public void Filter_UnionTwoProjectedElementSets_PreservesDeterministicOrderAndContent()
    {
        var doc = CreateTestDoc("tasks.xml", """
            <?xml version="1.0" encoding="utf-8"?>
            <tasks id="t" revision="1">
              <task id="T1" status="done"><title>One</title></task>
              <task id="T2" status="pending"><title>Two</title></task>
            </tasks>
            """);

        var res = XPathQueryEngine.EvaluateDocument(
            _tempDir,
            doc,
            "ds:filter(//task[@id='T1'], '@id') | ds:filter(//task[@id='T2'], '@status')",
            null);

        Assert.AreEqual(2, res.Nodes.Count);
        Assert.IsTrue(res.Derived);

        var first = res.Nodes[0];
        var second = res.Nodes[1];

        // First projected document was created for T1
        Assert.AreEqual("T1", first.GetAttribute("id", ""));
        Assert.AreEqual("", first.GetAttribute("status", ""));

        // Second projected document was created for T2
        Assert.AreEqual("", second.GetAttribute("id", ""));
        Assert.AreEqual("pending", second.GetAttribute("status", ""));
    }

    [TestMethod]
    public void Filter_UnionCommutativity_ReturnsIdenticalDeterministicNodeOrder()
    {
        var doc = CreateTestDoc("tasks.xml", """
            <?xml version="1.0" encoding="utf-8"?>
            <tasks id="t" revision="1">
              <task id="T1" status="done"><title>One</title></task>
              <task id="T2" status="pending"><title>Two</title></task>
            </tasks>
            """);

        // Evaluates A | B vs B | A on same document
        var res1 = XPathQueryEngine.EvaluateDocument(
            _tempDir,
            doc,
            "//task[@id='T2'] | //task[@id='T1']",
            null);

        var res2 = XPathQueryEngine.EvaluateDocument(
            _tempDir,
            doc,
            "//task[@id='T1'] | //task[@id='T2']",
            null);

        Assert.AreEqual(2, res1.Nodes.Count);
        Assert.AreEqual(2, res2.Nodes.Count);

        Assert.AreEqual(res1.Nodes[0].GetAttribute("id", ""), res2.Nodes[0].GetAttribute("id", ""));
        Assert.AreEqual("T1", res1.Nodes[0].GetAttribute("id", ""));
        Assert.AreEqual(res1.Nodes[1].GetAttribute("id", ""), res2.Nodes[1].GetAttribute("id", ""));
        Assert.AreEqual("T2", res1.Nodes[1].GetAttribute("id", ""));
    }

    [TestMethod]
    public void Filter_ProjectedNodeBudget_ExceededLimitThrowsExitCode7()
    {
        // Construct XML with enough elements to exceed 50,000 projected nodes
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("<?xml version=\"1.0\" encoding=\"utf-8\"?>");
        sb.AppendLine("<tasks id=\"t\" revision=\"1\">");
        for (var i = 0; i < 26_000; i++)
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"  <task id=\"T{i}\" status=\"pending\"><title>Title {i}</title></task>");
        }
        sb.AppendLine("</tasks>");

        var doc = CreateTestDoc("large_proj.xml", sb.ToString());

        // Each task projection has 1 (elem) + 1 (attr @id) + 1 (attr @status) + 1 (title elem) + 1 (title text) = 5 nodes.
        // 26,000 * 5 = 130,000 nodes > 50,000 limit.
        var ex = Assert.Throws<DogdouXPathException>(() =>
            XPathQueryEngine.EvaluateDocument(_tempDir, doc, "ds:filter(//task, '@id', '@status', 'title')", null));

        Assert.AreEqual(DiagnosticCodes.LimitExceeded, ex.Code);
        Assert.AreEqual(7, ex.ExitCode);
        Assert.IsTrue(ex.Message.Contains("50000", StringComparison.Ordinal));
    }
}
