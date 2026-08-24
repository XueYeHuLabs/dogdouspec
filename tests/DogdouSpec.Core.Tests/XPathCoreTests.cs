using System.Globalization;
using System.Xml.XPath;
using DogdouSpec.Core.Diagnostics;
using DogdouSpec.Core.Workspace;
using DogdouSpec.Core.XPath;

namespace DogdouSpec.Core.Tests;

[TestClass]
public sealed class XPathCoreTests
{
    private string _tempDir = null!;

    [TestInitialize]
    public void SetUp()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "DogdouSpec_XPathCoreTests_" + Guid.NewGuid().ToString("N"));
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
    public void Variables_ValidGrammar_ParsedSuccessfully()
    {
        var raw = new[]
        {
            "task_id=20260823-task-1",
            "empty_val=",
            "contains_equals=a=b=c",
            "topic=xpath_projection",
            "n123=456"
        };

        var vars = XPathVariables.Parse(raw);

        Assert.AreEqual("20260823-task-1", vars["task_id"]);
        Assert.AreEqual("", vars["empty_val"]);
        Assert.AreEqual("a=b=c", vars["contains_equals"]);
        Assert.AreEqual("xpath_projection", vars["topic"]);
        Assert.AreEqual("456", vars["n123"]);
    }

    [TestMethod]
    [DataRow("Task=1")]
    [DataRow("task-id=1")]
    [DataRow("1task=1")]
    [DataRow("_task=1")]
    [DataRow("task.id=1")]
    [DataRow("task@id=1")]
    [DataRow("TASK=1")]
    public void Variables_InvalidName_ThrowsInvalidArgument(string invalidVar)
    {
        var ex = Assert.Throws<DogdouXPathException>(() =>
            XPathVariables.Parse(new[] { invalidVar }));

        Assert.AreEqual(DiagnosticCodes.InvalidArgument, ex.Code);
        Assert.AreEqual(2, ex.ExitCode);
    }

    [TestMethod]
    public void Variables_DuplicateName_ThrowsInvalidArgument()
    {
        var raw = new[] { "topic=xml", "topic=xpath" };

        var ex = Assert.Throws<DogdouXPathException>(() =>
            XPathVariables.Parse(raw));

        Assert.AreEqual(DiagnosticCodes.InvalidArgument, ex.Code);
        Assert.AreEqual(2, ex.ExitCode);
    }

    [TestMethod]
    public void Variables_MissingEquals_ThrowsInvalidArgument()
    {
        var raw = new[] { "invalid_no_equals" };

        var ex = Assert.Throws<DogdouXPathException>(() =>
            XPathVariables.Parse(raw));

        Assert.AreEqual(DiagnosticCodes.InvalidArgument, ex.Code);
        Assert.AreEqual(2, ex.ExitCode);
    }

    [TestMethod]
    public void Variables_UnboundVariableInXPath_ThrowsInvalidArgument()
    {
        var doc = CreateTestDoc("tasks.xml", """
            <?xml version="1.0" encoding="utf-8"?>
            <tasks id="t1" revision="3">
              <task id="T1" status="pending"/>
            </tasks>
            """);

        var ex = Assert.Throws<DogdouXPathException>(() =>
            XPathQueryEngine.EvaluateDocument(_tempDir, doc, "//task[@id=$unbound_var]", variables: null));

        Assert.AreEqual(DiagnosticCodes.InvalidArgument, ex.Code);
        Assert.AreEqual(2, ex.ExitCode);
        Assert.IsTrue(ex.Message.Contains("unbound_var", StringComparison.Ordinal));
    }

    [TestMethod]
    public void Evaluate_StandardAxesAndPredicates_ReturnsCorrectNodeSet()
    {
        var doc = CreateTestDoc("tasks.xml", """
            <?xml version="1.0" encoding="utf-8"?>
            <tasks id="tasks-root" revision="5">
              <task id="T1" status="done">
                <title>Task 1</title>
              </task>
              <task id="T2" status="in-progress">
                <title>Task 2</title>
              </task>
              <task id="T3" status="pending">
                <title>Task 3</title>
              </task>
            </tasks>
            """);

        var vars = new Dictionary<string, string> { { "status", "in-progress" } };

        // Predicate with variable
        var res1 = XPathQueryEngine.EvaluateDocument(_tempDir, doc, "//task[@status=$status]", vars);
        Assert.AreEqual(XPathResultKind.NodeSet, res1.ResultType);
        Assert.AreEqual(1, res1.Nodes.Count);
        Assert.AreEqual("T2", res1.Nodes[0].GetAttribute("id", ""));

        // Descendant and child axes
        var res2 = XPathQueryEngine.EvaluateDocument(_tempDir, doc, "/tasks/task/title", vars);
        Assert.AreEqual(3, res2.Nodes.Count);

        // Following-sibling
        var res3 = XPathQueryEngine.EvaluateDocument(_tempDir, doc, "//task[@id='T1']/following-sibling::task", vars);
        Assert.AreEqual(2, res3.Nodes.Count);

        // Parent axis
        var res4 = XPathQueryEngine.EvaluateDocument(_tempDir, doc, "//task[@id='T2']/title/parent::task", vars);
        Assert.AreEqual(1, res4.Nodes.Count);
        Assert.AreEqual("T2", res4.Nodes[0].GetAttribute("id", ""));
    }

    [TestMethod]
    public void Evaluate_Union_CombinesNodesInDocumentOrder()
    {
        var doc = CreateTestDoc("tasks.xml", """
            <?xml version="1.0" encoding="utf-8"?>
            <tasks id="t" revision="1">
              <task id="T1" status="done"/>
              <task id="T2" status="pending"/>
              <task id="T3" status="in-progress"/>
            </tasks>
            """);

        var res = XPathQueryEngine.EvaluateDocument(_tempDir, doc, "//task[@id='T3'] | //task[@id='T1']", null);
        Assert.AreEqual(2, res.Nodes.Count);
        Assert.AreEqual("T1", res.Nodes[0].GetAttribute("id", ""));
        Assert.AreEqual("T3", res.Nodes[1].GetAttribute("id", ""));
    }

    [TestMethod]
    public void Evaluate_Scalars_ReturnsCorrectTypedResult()
    {
        var doc = CreateTestDoc("tasks.xml", """
            <?xml version="1.0" encoding="utf-8"?>
            <tasks id="t" revision="7">
              <task id="T1" status="done"><title>First</title></task>
              <task id="T2" status="pending"><title>Second</title></task>
            </tasks>
            """);

        // Count scalar
        var countRes = XPathQueryEngine.EvaluateDocument(_tempDir, doc, "count(//task)", null);
        Assert.AreEqual(XPathResultKind.Number, countRes.ResultType);
        Assert.AreEqual(2.0, countRes.NumberValue);
        Assert.AreEqual("7", countRes.Revision);

        // Boolean scalar
        var boolRes = XPathQueryEngine.EvaluateDocument(_tempDir, doc, "count(//task[@status='done']) > 0", null);
        Assert.AreEqual(XPathResultKind.Boolean, boolRes.ResultType);
        Assert.AreEqual(true, boolRes.BooleanValue);

        // String scalar
        var strRes = XPathQueryEngine.EvaluateDocument(_tempDir, doc, "string(//task[@id='T1']/title)", null);
        Assert.AreEqual(XPathResultKind.String, strRes.ResultType);
        Assert.AreEqual("First", strRes.StringValue);
    }

    [TestMethod]
    public void Evaluate_ResultLimitExceeded_ThrowsLimitExceededWithExitCode7()
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("<?xml version=\"1.0\" encoding=\"utf-8\"?>");
        sb.AppendLine("<tasks id=\"t\" revision=\"1\">");
        for (var i = 0; i < 10_005; i++)
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"  <task id=\"T{i}\" status=\"pending\"/>");
        }
        sb.AppendLine("</tasks>");

        var doc = CreateTestDoc("large_tasks.xml", sb.ToString());

        var ex = Assert.Throws<DogdouXPathException>(() =>
            XPathQueryEngine.EvaluateDocument(_tempDir, doc, "//task", null));

        Assert.AreEqual(DiagnosticCodes.LimitExceeded, ex.Code);
        Assert.AreEqual(7, ex.ExitCode);
        Assert.IsTrue(ex.Message.Contains("10000", StringComparison.Ordinal));
    }

    [TestMethod]
    public void Evaluate_ExactResultNodeBoundary_10000Passes()
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("<?xml version=\"1.0\" encoding=\"utf-8\"?>");
        sb.AppendLine("<tasks id=\"t\" revision=\"1\">");
        for (var i = 0; i < 10_000; i++)
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"  <task id=\"T{i}\" status=\"pending\"/>");
        }
        sb.AppendLine("</tasks>");

        var doc = CreateTestDoc("exact_10000.xml", sb.ToString());

        var res = XPathQueryEngine.EvaluateDocument(_tempDir, doc, "//task", null);
        Assert.AreEqual(10_000, res.Nodes.Count);
    }

    [TestMethod]
    public void Evaluate_DtdProhibited_ThrowsDtdProhibited()
    {
        var doc = CreateTestDoc("dtd.xml", """
            <?xml version="1.0" encoding="utf-8"?>
            <!DOCTYPE tasks [ <!ENTITY test "test"> ]>
            <tasks id="t" revision="1">
              <task id="T1"/>
            </tasks>
            """);

        var ex = Assert.Throws<DogdouXPathException>(() =>
            XPathQueryEngine.EvaluateDocument(_tempDir, doc, "//task", null));

        Assert.AreEqual(DiagnosticCodes.DtdProhibited, ex.Code);
        Assert.AreEqual(2, ex.ExitCode);
    }

    [TestMethod]
    public void Evaluate_DerivedFlag_FalseForStandardXPath_TrueWhenExtensionUsed()
    {
        var doc = CreateTestDoc("tasks.xml", """
            <?xml version="1.0" encoding="utf-8"?>
            <tasks id="t" revision="1">
              <task id="T1" status="pending">
                <index><summary>Summary</summary></index>
              </task>
            </tasks>
            """);

        var stdRes = XPathQueryEngine.EvaluateDocument(_tempDir, doc, "//task[@id='T1']", null);
        Assert.IsFalse(stdRes.Derived);

        var extRes = XPathQueryEngine.EvaluateDocument(_tempDir, doc, "ds:filter(//task, '@id')", null);
        Assert.IsTrue(extRes.Derived);
    }

    [TestMethod]
    public void Evaluate_CommentsAndProcessingInstructions_PreservedAndFormatted()
    {
        var doc = CreateTestDoc("tasks.xml", """
            <?xml version="1.0" encoding="utf-8"?>
            <tasks id="t" revision="1">
              <?custom-pi sample data?>
              <!-- This is a comment -->
              <task id="T1"/>
            </tasks>
            """);

        var commentRes = XPathQueryEngine.EvaluateDocument(_tempDir, doc, "//comment()", null);
        Assert.AreEqual(1, commentRes.Nodes.Count);
        Assert.AreEqual(XPathNodeType.Comment, commentRes.Nodes[0].NodeType);

        var commentXml = XPathResultFormatter.FormatQueryXml(commentRes);
        Assert.IsTrue(commentXml.Contains("<item type=\"comment\"> This is a comment </item>", StringComparison.Ordinal));

        var piRes = XPathQueryEngine.EvaluateDocument(_tempDir, doc, "//processing-instruction()", null);
        Assert.AreEqual(1, piRes.Nodes.Count);
        Assert.AreEqual(XPathNodeType.ProcessingInstruction, piRes.Nodes[0].NodeType);

        var piXml = XPathResultFormatter.FormatQueryXml(piRes);
        Assert.IsTrue(piXml.Contains("<item type=\"processing-instruction\" name=\"custom-pi\">sample data</item>", StringComparison.Ordinal));
    }

    [TestMethod]
    public void Evaluate_OutputByteLimitExceeded_ThrowsLimitExceededWithExitCode7()
    {
        var doc = CreateTestDoc("large_narrative.xml", """
            <?xml version="1.0" encoding="utf-8"?>
            <tasks id="t" revision="1">
              <task id="T1">
                <title>A very long title...</title>
              </task>
            </tasks>
            """);

        var largeString = new string('x', 4 * 1024 * 1024 + 100);
        var result = XPathQueryResult.ForString("tasks.xml", "1", largeString, false);

        var ex = Assert.Throws<DogdouXPathException>(() =>
            XPathResultFormatter.FormatQueryXml(result));

        Assert.AreEqual(DiagnosticCodes.LimitExceeded, ex.Code);
        Assert.AreEqual(7, ex.ExitCode);
        Assert.IsTrue(ex.Message.Contains("4194304", StringComparison.Ordinal));
    }

    [TestMethod]
    public void Evaluate_DocumentSizeLimitExceeded_ThrowsLimitExceededWithExitCode7()
    {
        var path = Path.Combine(_tempDir, "oversized.xml");
        using (var fs = new FileStream(path, FileMode.Create, FileAccess.Write))
        {
            fs.SetLength(16 * 1024 * 1024 + 1024); // 16 MiB + 1 KiB
        }

        var doc = new ManagedDocument("oversized.xml", path);
        var ex = Assert.Throws<DogdouXPathException>(() =>
            XPathQueryEngine.EvaluateDocument(_tempDir, doc, "//task", null));

        Assert.AreEqual(DiagnosticCodes.LimitExceeded, ex.Code);
        Assert.AreEqual(7, ex.ExitCode);
    }
}
