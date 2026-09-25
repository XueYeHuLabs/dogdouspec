using System;
using System.IO;
using System.Xml.Linq;
using DogdouSpec.Cli;
using DogdouSpec.Core.Diagnostics;
using DogdouSpec.Core.Resources;

namespace DogdouSpec.Cli.Tests;

[TestClass]
public sealed class TemplateCliTests
{
    private static readonly string[] TemplateListArgs = new[] { "template", "list", "--format", "human" };
    private static readonly string[] TemplateListXmlArgs = new[] { "template", "list", "--format", "xml" };
    private static readonly string[] TemplateListInvalidVersionArgs = new[] { "template", "list", "--version", "99.0" };
    private static readonly string[] TemplateShowConfirmationArgs = new[] { "template", "show", "--name", "iteration.confirmation" };
    private static readonly string[] ConfirmHelpArgs = new[] { "iteration", "confirm", "--help" };
    private static readonly string[] TaskUpdateHelpArgs = new[] { "task", "update", "--help" };
    private static readonly string[] TaskReviewHelpArgs = new[] { "task", "review", "--help" };
    private static readonly string[] TaskAddHelpArgs = new[] { "task", "add", "--help" };
    private static readonly string[] TaskSplitHelpArgs = new[] { "task", "split", "--help" };
    private static readonly string[] TransactionApplyHelpArgs = new[] { "transaction", "apply", "--help" };

    [TestMethod]
    public void TemplateList_Human_OutputsRegisteredTemplatesWithSummaries()
    {
        var (code, stdout, stderr) = RunCli(TemplateListArgs);
        Assert.AreEqual(0, code, stderr);
        Assert.IsTrue(string.IsNullOrWhiteSpace(stderr), stderr);
        StringAssert.Contains(stdout, "Available templates (version 1.0):");
        StringAssert.Contains(stdout, "backlog.item");
        StringAssert.Contains(stdout, "iteration.confirmation");
        StringAssert.Contains(stdout, "task.update");
        StringAssert.Contains(stdout, "transaction.apply");
        StringAssert.Contains(stdout, "Iteration human owner confirmation request template");
    }

    [TestMethod]
    public void TemplateList_Xml_OutputsValidXmlWithSummaries()
    {
        var (code, stdout, stderr) = RunCli(TemplateListXmlArgs);
        Assert.AreEqual(0, code, stderr);
        Assert.IsTrue(string.IsNullOrWhiteSpace(stderr), stderr);

        var doc = XDocument.Parse(stdout);
        Assert.IsNotNull(doc.Root);
        Assert.AreEqual("templates", doc.Root.Name.LocalName);
        Assert.AreEqual("1.0", doc.Root.Attribute("version")?.Value);

        var templates = doc.Root.Elements("template").ToList();
        Assert.IsTrue(templates.Count >= 15);

        var confirmTmpl = templates.FirstOrDefault(t => t.Attribute("name")?.Value == "iteration.confirmation");
        Assert.IsNotNull(confirmTmpl);
        Assert.AreEqual("iteration-confirmation", confirmTmpl.Attribute("root_element")?.Value);
        Assert.AreEqual("Iteration human owner confirmation request template", confirmTmpl.Element("summary")?.Value);
    }

    [TestMethod]
    public void TemplateList_UnsupportedVersion_ReturnsError()
    {
        var (code, stdout, stderr) = RunCli(TemplateListInvalidVersionArgs);
        Assert.AreEqual(2, code);
        StringAssert.Contains(stderr, DiagnosticCodes.UnsupportedVersion);
    }

    [TestMethod]
    public void TemplateShow_ExistingTemplate_OutputsTemplateXml()
    {
        var (code, stdout, stderr) = RunCli(TemplateShowConfirmationArgs);
        Assert.AreEqual(0, code, stderr);
        StringAssert.Contains(stdout, "<iteration-confirmation");
        StringAssert.Contains(stdout, "</iteration-confirmation>");
    }

    [TestMethod]
    public void TemplateCatalog_EnrichDiagnosticWithExpectedShape_EmbedsShape()
    {
        var baseMessage = "The element 'requirements' has invalid child element 'criterion'.";
        var enriched = TemplateCatalog.EnrichDiagnosticWithExpectedShape(baseMessage, "iteration-confirmation");

        StringAssert.Contains(enriched, baseMessage);
        StringAssert.Contains(enriched, "Expected XML shape (iteration.confirmation):");
        StringAssert.Contains(enriched, "<iteration-confirmation");
        StringAssert.Contains(enriched, "<requirement target=\"REQ-ID\" decision=\"approved\"/>");
        StringAssert.Contains(enriched, "dogdouspec template show --name iteration.confirmation");
    }

    [TestMethod]
    public void TemplateCatalog_EnrichDiagnosticWithExpectedShape_AutoDetectsFromMessage()
    {
        var baseMessage = "The element 'iteration-confirmation' is missing attribute 'actor'.";
        var enriched = TemplateCatalog.EnrichDiagnosticWithExpectedShape(baseMessage);

        StringAssert.Contains(enriched, baseMessage);
        StringAssert.Contains(enriched, "Expected XML shape (iteration.confirmation):");
    }

    [TestMethod]
    public void Commands_HelpText_ReferencesTemplates()
    {
        var (_, confirmOut, _) = RunCli(ConfirmHelpArgs);
        StringAssert.Contains(confirmOut, "iteration.confirmation");

        var (_, updateOut, _) = RunCli(TaskUpdateHelpArgs);
        StringAssert.Contains(updateOut, "task.update");

        var (_, reviewOut, _) = RunCli(TaskReviewHelpArgs);
        StringAssert.Contains(reviewOut, "task.review");

        var (_, addOut, _) = RunCli(TaskAddHelpArgs);
        StringAssert.Contains(addOut, "task.add");

        var (_, splitOut, _) = RunCli(TaskSplitHelpArgs);
        StringAssert.Contains(splitOut, "task.split");

        var (_, txOut, _) = RunCli(TransactionApplyHelpArgs);
        StringAssert.Contains(txOut, "transaction.apply");
    }

    private static (int ExitCode, string StdOut, string StdErr) RunCli(string[] args)
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
}
