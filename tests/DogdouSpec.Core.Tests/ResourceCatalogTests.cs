using DogdouSpec.Core.Resources;
using System.Xml.Linq;
using System.Xml.Schema;

namespace DogdouSpec.Core.Tests;

[TestClass]
public sealed class ResourceCatalogTests
{
    [TestMethod]
    [DataRow("common")]
    [DataRow("common.xsd")]
    [DataRow("spec")]
    [DataRow("spec.xsd")]
    [DataRow("tasks")]
    [DataRow("tasks.xsd")]
    [DataRow("knowledge")]
    [DataRow("knowledge.xsd")]
    [DataRow("backlog")]
    [DataRow("backlog.xsd")]
    [DataRow("requests")]
    [DataRow("requests.xsd")]
    public void GetSchemaBytes_AllShippedSchemas_ReturnsExactContent(string schemaName)
    {
        var bytes = EmbeddedResources.GetSchemaBytes(schemaName, "1.0");
        Assert.IsNotNull(bytes);
        Assert.IsTrue(bytes.Length > 0);

        var text = EmbeddedResources.GetSchemaText(schemaName, "1.0");
        Assert.IsNotNull(text);
        Assert.IsTrue(text.Contains("<?xml", StringComparison.Ordinal));
    }

    [TestMethod]
    [DataRow("backlog.item")]
    [DataRow("backlog.item.xml")]
    [DataRow("iteration.confirmation")]
    [DataRow("iteration.confirmation.xml")]
    [DataRow("knowledge.entry")]
    [DataRow("knowledge.entry.xml")]
    [DataRow("record.discussion")]
    [DataRow("record.discussion.xml")]
    [DataRow("record.finding")]
    [DataRow("record.finding.xml")]
    [DataRow("record.verification")]
    [DataRow("record.verification.xml")]
    [DataRow("task.update")]
    [DataRow("task.update.xml")]
    [DataRow("transaction.apply")]
    [DataRow("transaction.apply.xml")]
    public void GetTemplateBytes_AllShippedTemplates_ReturnsExactContent(string templateName)
    {
        var bytes = EmbeddedResources.GetTemplateBytes(templateName, "1.0");
        Assert.IsNotNull(bytes);
        Assert.IsTrue(bytes.Length > 0);

        var text = EmbeddedResources.GetTemplateText(templateName, "1.0");
        Assert.IsNotNull(text);
        Assert.IsTrue(text.Contains("<?xml", StringComparison.Ordinal));
    }

    [TestMethod]
    public void GetSchema_UnknownName_ReturnsNull()
    {
        var bytes = EmbeddedResources.GetSchemaBytes("non_existent_schema", "1.0");
        Assert.IsNull(bytes);
    }

    [TestMethod]
    public void GetSchema_UnsupportedVersion_ReturnsNull()
    {
        var bytes = EmbeddedResources.GetSchemaBytes("spec", "2.0");
        Assert.IsNull(bytes);
    }

    [TestMethod]
    public void GetTemplate_UnknownName_ReturnsNull()
    {
        var bytes = EmbeddedResources.GetTemplateBytes("non_existent_template", "1.0");
        Assert.IsNull(bytes);
    }

    [TestMethod]
    public void GetTemplate_UnsupportedVersion_ReturnsNull()
    {
        var bytes = EmbeddedResources.GetTemplateBytes("record.discussion", "9.9");
        Assert.IsNull(bytes);
    }

    [TestMethod]
    [DataRow("common")]
    [DataRow("spec")]
    [DataRow("tasks")]
    [DataRow("knowledge")]
    [DataRow("backlog")]
    [DataRow("requests")]
    public void GetCompiledSchemaSet_EachSchemaCompilesCleanly(string schemaName)
    {
        var schemaSet = EmbeddedResources.GetCompiledSchemaSet(schemaName, "1.0");
        Assert.IsNotNull(schemaSet);
        Assert.IsTrue(schemaSet.IsCompiled);
        Assert.IsTrue(schemaSet.GlobalElements.Count > 0);
    }

    [TestMethod]
    public void GetCompiledSchemaSet_SpecAndTasks_HaveDistinctGlobalElements()
    {
        var specSet = EmbeddedResources.GetCompiledSchemaSet("spec", "1.0");
        var tasksSet = EmbeddedResources.GetCompiledSchemaSet("tasks", "1.0");

        Assert.IsTrue(specSet.GlobalElements.Contains(new System.Xml.XmlQualifiedName("iteration", "")));
        Assert.IsFalse(specSet.GlobalElements.Contains(new System.Xml.XmlQualifiedName("tasks", "")));

        Assert.IsTrue(tasksSet.GlobalElements.Contains(new System.Xml.XmlQualifiedName("tasks", "")));
        Assert.IsFalse(tasksSet.GlobalElements.Contains(new System.Xml.XmlQualifiedName("iteration", "")));
    }

    [TestMethod]
    public void AllShippedRequestTemplates_ValidateAgainstRequestsSchema()
    {
        var schemas = EmbeddedResources.GetCompiledSchemaSet("requests", "1.0");
        var requestTemplateNames = new[]
        {
            "change.apply", "change.propose", "iteration.confirmation", "requirement.propose",
            "task.add", "task.revise", "task.split", "task.update", "transaction.apply"
        };
        foreach (var templateName in requestTemplateNames)
        {
            var document = XDocument.Parse(EmbeddedResources.GetTemplateText(templateName, "1.0")!);
            var errors = new List<string>();
            document.Validate(schemas, (_, eventArgs) => errors.Add(eventArgs.Message));
            Assert.AreEqual(0, errors.Count, $"Template '{templateName}' is not valid against requests.xsd: {string.Join(" | ", errors)}");
        }
    }
}
