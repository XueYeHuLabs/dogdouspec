using System.Xml.Linq;
using DogdouSpec.Cli;
using DogdouSpec.Core.Resources;

namespace DogdouSpec.Cli.Tests;

[TestClass]
public sealed class IterationConstraintCliTests
{
    private static string RepoRoot = null!;
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
        _tempDir = Path.Combine(Path.GetTempPath(), "DogdouSpec_ConstraintCliTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    [TestCleanup]
    public void TearDown()
    {
        if (Directory.Exists(_tempDir))
        {
            try
            {
                Directory.Delete(_tempDir, true);
            }
            catch
            {
                // Ignore cleanup errors
            }
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

    private static (int ExitCode, string Stdout, string Stderr) RunCli(params string[] args)
    {
        var originalOut = Console.Out;
        var originalErr = Console.Error;
        var originalDir = Environment.CurrentDirectory;

        using var outWriter = new StringWriter();
        using var errWriter = new StringWriter();

        try
        {
            Console.SetOut(outWriter);
            Console.SetError(errWriter);

            var exitCode = Program.Main(args);
            return (exitCode, outWriter.ToString(), errWriter.ToString());
        }
        finally
        {
            Console.SetOut(originalOut);
            Console.SetError(originalErr);
            Environment.CurrentDirectory = originalDir;
        }
    }

    [TestMethod]
    public void IterationConstraint_Add_SucceedsAndEmitsEnvelope()
    {
        var workspace = CreateWorkspaceCopy();
        var iterId = "20260823-xpath-core";

        var (exitCode, stdout, stderr) = RunCli(
            "iteration", "constraint", "add",
            "--iteration", iterId,
            "--text", ".NET 10.0 runtime is required",
            "--kind", "environment",
            "--workspace-root", workspace,
            "--format", "xml");

        Assert.AreEqual(0, exitCode, $"Stderr: {stderr}");
        StringAssert.Contains(stdout, "<mutation command=\"iteration constraint add\"");

        // Check spec.xml directly
        var specDoc = XDocument.Load(Path.Combine(workspace, iterId, "spec.xml"));
        var constraint = specDoc.Root?.Element("constraints")?.Element("constraint");
        Assert.IsNotNull(constraint);
        Assert.AreEqual("environment", constraint.Attribute("kind")?.Value);
        Assert.AreEqual(".NET 10.0 runtime is required", constraint.Value);
    }

    [TestMethod]
    public void IterationConstraint_List_XmlAndHuman_OutputsCorrectly()
    {
        var workspace = CreateWorkspaceCopy();
        var iterId = "20260823-xpath-core";

        // Add 2 constraints
        RunCli("iteration", "constraint", "add", "--iteration", iterId, "--text", "environment: Linux x64 or Windows", "--workspace-root", workspace);
        RunCli("iteration", "constraint", "add", "--iteration", iterId, "--text", "configuration: Strict mode enabled", "--workspace-root", workspace);

        // List XML
        var (xmlExit, xmlOut, xmlErr) = RunCli(
            "iteration", "constraint", "list",
            "--iteration", iterId,
            "--workspace-root", workspace,
            "--format", "xml");

        Assert.AreEqual(0, xmlExit, $"Stderr: {xmlErr}");
        StringAssert.Contains(xmlOut, "<iteration-constraints iteration=\"20260823-xpath-core\" count=\"2\">");
        StringAssert.Contains(xmlOut, "kind=\"environment\"");
        StringAssert.Contains(xmlOut, "kind=\"configuration\"");

        // List Human
        var (humanExit, humanOut, humanErr) = RunCli(
            "iteration", "constraint", "list",
            "--iteration", iterId,
            "--workspace-root", workspace,
            "--format", "human");

        Assert.AreEqual(0, humanExit, $"Stderr: {humanErr}");
        StringAssert.Contains(humanOut, "Iteration Constraints for 20260823-xpath-core (2):");
        StringAssert.Contains(humanOut, "(environment) Linux x64 or Windows");
        StringAssert.Contains(humanOut, "(configuration) Strict mode enabled");

        // List with omitted --iteration should auto-resolve the candidate iteration
        var (autoExit, autoOut, autoErr) = RunCli(
            "iteration", "constraint", "list",
            "--workspace-root", workspace,
            "--format", "human");

        Assert.AreEqual(0, autoExit, $"Stderr: {autoErr}");
        StringAssert.Contains(autoOut, "Iteration Constraints for 20260823-xpath-core");
    }

    [TestMethod]
    public void IterationConstraint_Remove_Succeeds()
    {
        var workspace = CreateWorkspaceCopy();
        var iterId = "20260823-xpath-core";
        var constraintId = "20260823-constraint-to-remove";

        RunCli(
            "iteration", "constraint", "add",
            "--iteration", iterId,
            "--text", "To be removed",
            "--constraint-id", constraintId,
            "--workspace-root", workspace);

        var (remExit, remOut, remErr) = RunCli(
            "iteration", "constraint", "remove",
            "--iteration", iterId,
            "--constraint-id", constraintId,
            "--workspace-root", workspace,
            "--format", "xml");

        Assert.AreEqual(0, remExit, $"Stderr: {remErr}");
        StringAssert.Contains(remOut, "<mutation command=\"iteration constraint remove\"");

        // List to confirm removed
        var (listExit, listOut, _) = RunCli(
            "iteration", "constraint", "list",
            "--iteration", iterId,
            "--workspace-root", workspace,
            "--format", "human");

        Assert.AreEqual(0, listExit);
        StringAssert.Contains(listOut, "has no iteration-level constraints");
    }

    [TestMethod]
    public void IterationCreate_WithConstraintOption_CreatesIterationWithConstraints()
    {
        var workspace = CreateWorkspaceCopy();
        var iterId = "20260829-create-constraints-cli";

        var (exitCode, stdout, stderr) = RunCli(
            "iteration", "create",
            "--id", iterId,
            "--kind", "feature",
            "--criterion", "First criterion",
            "--constraint", "environment: .NET 10.0 is required",
            "--constraint", "configuration: Deterministic builds",
            "--workspace-root", workspace,
            "--format", "xml");

        Assert.AreEqual(0, exitCode, $"Stderr: {stderr}");
        StringAssert.Contains(stdout, "<mutation command=\"iteration create\"");

        var specDoc = XDocument.Load(Path.Combine(workspace, iterId, "spec.xml"));
        var constraints = specDoc.Root?.Element("constraints")?.Elements("constraint").ToList();
        Assert.IsNotNull(constraints);
        Assert.AreEqual(2, constraints.Count);
        Assert.AreEqual("environment", constraints[0].Attribute("kind")?.Value);
        Assert.AreEqual(".NET 10.0 is required", constraints[0].Value);
        Assert.AreEqual("configuration", constraints[1].Attribute("kind")?.Value);
        Assert.AreEqual("Deterministic builds", constraints[1].Value);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void ConstraintResources_FreshOrUpgradedWorkspace_ExposeEffectiveExecutionContext(bool upgrade)
    {
        const string iterationId = "20261027-constraint-deployment";
        var workspace = Path.Combine(_tempDir, ".dogdouspec");
        var skillDir = Path.Combine(_tempDir, ".agents", "skills", "dogdouspec");
        var agentsPath = Path.Combine(_tempDir, "AGENTS.md");
        File.WriteAllText(agentsPath, "# Repository-owned agent rules\n");

        string Execute(params string[] args)
        {
            var result = RunCli(args.Concat(new[] { "--workspace-root", _tempDir, "--format", "xml" }).ToArray());
            Assert.AreEqual(0, result.ExitCode, $"{string.Join(' ', args)}: {result.Stderr}");
            return result.Stdout;
        }

        Execute("workspace", "init");
        Execute("iteration", "create", "--id", iterationId, "--kind", "feature", "--activate",
            "--criterion", "Installed constraints remain visible to Task executors.");
        Execute("task", "quick", "--iteration", iterationId, "--title", "Verify deployed constraint capability",
            "--scope", "src/**", "--done-when", "Effective constraints are visible.", "--why", "Verify adoption guidance.");
        var tasksPath = Path.Combine(workspace, iterationId, "tasks.xml");
        var taskId = (string)XDocument.Load(tasksPath).Root!.Element("task")!.Attribute("id")!;
        Execute("task", "revise", "--iteration", iterationId, "--task", taskId,
            "--add-constraint", "Preserve existing project rules.");

        if (upgrade)
        {
            // Recreate the v1 resources from before iteration constraints existed.
            WritePreviousConstraintSchemas(workspace);
            File.WriteAllText(Path.Combine(skillDir, "SKILL.md"), "# Previous workflow\nRead the selected Task from tasks.xml.\n");
            var beforeSync = Directory.GetFiles(workspace, "*.xml", SearchOption.AllDirectories)
                .ToDictionary(path => path, File.ReadAllBytes);
            Assert.AreEqual(1, RunCli("schema", "status", "--workspace-root", _tempDir, "--format", "xml").ExitCode);
            Assert.AreEqual(1, RunCli("skill", "status", "--output-dir", skillDir, "--format", "xml").ExitCode);
            var guide = RunCli("skill", "guide", "--all");
            Assert.AreEqual(0, guide.ExitCode, guide.Stderr);
            StringAssert.Contains(guide.Stdout, "scope=\"iteration\"");

            Execute("schema", "sync", "--expected-version", "1.0");
            var sync = RunCli("skill", "sync", "--force", "--output-dir", skillDir, "--format", "xml");
            Assert.AreEqual(0, sync.ExitCode, sync.Stderr);
            foreach (var pair in beforeSync)
                CollectionAssert.AreEqual(pair.Value, File.ReadAllBytes(pair.Key), $"Synchronization changed managed XML: {pair.Key}");
        }

        StringAssert.Contains(Execute("schema", "status"), "in_sync=\"true\"");
        var skillStatus = RunCli("skill", "status", "--output-dir", skillDir, "--format", "xml");
        Assert.AreEqual(0, skillStatus.ExitCode, skillStatus.Stderr);
        StringAssert.Contains(skillStatus.Stdout, "in_sync=\"true\"");
        var installedGuide = File.ReadAllText(Path.Combine(skillDir, "SKILL.md"));
        StringAssert.Contains(installedGuide, "task context --task");
        StringAssert.Contains(installedGuide, "scope=\"iteration\"");
        StringAssert.Contains(installedGuide, "scope=\"task\"");
        StringAssert.Contains(installedGuide, "task records --task");
        StringAssert.Contains(installedGuide, "--check-command");
        StringAssert.Contains(installedGuide, "iteration create --id");
        StringAssert.Contains(installedGuide, "iteration-content.xml");
        var xpathGuide = File.ReadAllText(Path.Combine(skillDir, "references", "xpath.md"));
        AssertXPathExecutionGuidance(xpathGuide);
        var exportedGuide = RunCli("skill", "guide", "--all");
        Assert.AreEqual(0, exportedGuide.ExitCode, exportedGuide.Stderr);
        AssertXPathExecutionGuidance(exportedGuide.Stdout);
        StringAssert.Contains(xpathGuide, "--xpath-file");
        StringAssert.Contains(xpathGuide, "/backlog/items/item");
        var mutationGuide = File.ReadAllText(Path.Combine(skillDir, "references", "mutations.md"));
        StringAssert.Contains(mutationGuide, "transaction apply --dry-run");
        StringAssert.Contains(mutationGuide, "partial=\"true\"");
        StringAssert.Contains(mutationGuide, "backlog-identity");
        StringAssert.Contains(EmbeddedResources.GetAgentsTemplateText()!, "task context --task");
        Assert.AreEqual("# Repository-owned agent rules\n", File.ReadAllText(agentsPath));

        Execute("iteration", "constraint", "add", "--iteration", iterationId,
            "--text", "[release-baseline] Deterministic builds are required.", "--expected-spec-revision", "1");
        Execute("validate");
        var context = XDocument.Parse(Execute("task", "context", "--iteration", iterationId,
            "--task", taskId, "--no-history"));
        var constraints = context.Root!.Element("essential")!.Element("constraints")!.Elements("constraint").ToArray();
        Assert.AreEqual(2, constraints.Length);
        Assert.AreEqual("iteration", (string?)constraints[0].Attribute("scope"));
        Assert.AreEqual("release-baseline", (string?)constraints[0].Attribute("kind"));
        Assert.AreEqual("Deterministic builds are required.", constraints[0].Value);
        Assert.AreEqual("task", (string?)constraints[1].Attribute("scope"));
        Assert.AreEqual("Preserve existing project rules.", constraints[1].Value);

        var rawTask = XDocument.Parse(Execute("task", "show", "--iteration", iterationId, "--task", taskId));
        Assert.IsFalse(rawTask.Descendants("constraint").Any(c => c.Value == constraints[0].Value),
            "Raw Task reads must retain their Task-local semantics.");
    }

    private static void AssertXPathExecutionGuidance(string guide)
    {
        var referenceStart = guide.IndexOf("# XPath Query & Projection Reference", StringComparison.Ordinal);
        Assert.IsGreaterThanOrEqualTo(0, referenceStart, "The XPath reference must be included in the distributed guide.");
        var reference = guide[referenceStart..];
        StringAssert.Contains(reference, "dogdouspec task next --iteration");
        StringAssert.Contains(reference, "dogdouspec task context --iteration");
        StringAssert.Contains(reference, "--no-history --format xml");
        StringAssert.Contains(reference, "scope=\"iteration\"");
        StringAssert.Contains(reference, "scope=\"task\"");
        Assert.IsFalse(reference.Contains("@status='pending' and not(dependencies", StringComparison.Ordinal),
            "A document-local XPath cannot select ready work across iterations.");
        Assert.IsFalse(reference.Contains("Phase 2: Full Document Loading", StringComparison.Ordinal),
            "Raw tasks.xml reads cannot substitute for effective execution context.");
    }

    private static void WritePreviousConstraintSchemas(string workspace)
    {
        XNamespace xs = "http://www.w3.org/2001/XMLSchema";
        var schemaDir = Path.Combine(workspace, "_schema");
        var commonPath = Path.Combine(schemaDir, "common.xsd");
        var specPath = Path.Combine(schemaDir, "spec.xsd");
        var tasksPath = Path.Combine(schemaDir, "tasks.xsd");
        var common = XDocument.Load(commonPath);
        var spec = XDocument.Load(specPath);
        var tasks = XDocument.Load(tasksPath);
        var sharedTypes = common.Root!.Elements(xs + "complexType")
            .Where(e => (string?)e.Attribute("name") is "ConstraintType" or "ConstraintsType").ToArray();
        foreach (var type in sharedTypes)
        {
            type.Descendants(xs + "attribute").Where(a => (string?)a.Attribute("name") == "kind").Remove();
            type.Remove();
            tasks.Root!.Add(type);
        }
        common.Root!.Elements(xs + "element")
            .Where(e => (string?)e.Attribute("name") is "constraint" or "constraints").Remove();
        spec.Descendants(xs + "complexType").Single(e => (string?)e.Attribute("name") == "IterationType")
            .Element(xs + "sequence")!.Elements(xs + "element")
            .Where(e => (string?)e.Attribute("name") == "constraints").Remove();
        common.Save(commonPath);
        spec.Save(specPath);
        tasks.Save(tasksPath);
    }
}
