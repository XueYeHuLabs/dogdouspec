using DogdouSpec.Cli.Commands;
using DogdouSpec.Core.Diagnostics;

namespace DogdouSpec.Cli.Tests;

[TestClass]
public sealed class SkillCliTests
{
    private string _tempDir = null!;

    [TestInitialize]
    public void SetUp()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "DogdouSpec_SkillCliTests_" + Guid.NewGuid().ToString("N"));
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

    [TestMethod]
    public void SkillSync_WritesSkillFilesToTargetDirectory()
    {
        var targetSkillDir = Path.Combine(_tempDir, ".agents", "skills", "dogdouspec");
        var exitCode = Program.Main(new[] { "skill", "sync", "--output-dir", targetSkillDir, "--format", "human" });

        Assert.AreEqual(0, exitCode);
        foreach (var relPath in DogdouSpec.Core.Resources.EmbeddedResources.SkillFilePaths)
        {
            var expectedPath = Path.Combine(targetSkillDir, relPath.Replace('/', Path.DirectorySeparatorChar));
            Assert.IsTrue(File.Exists(expectedPath), $"Expected skill file {relPath} missing at {expectedPath}");
        }
    }

    [TestMethod]
    public void SkillSync_WithoutForce_WhenFilesExist_RefusesToOverwrite()
    {
        var targetSkillDir = Path.Combine(_tempDir, ".agents", "skills", "dogdouspec");
        Directory.CreateDirectory(targetSkillDir);
        var skillPath = Path.Combine(targetSkillDir, "SKILL.md");
        File.WriteAllText(skillPath, "# Stale content");

        var exitCode = Program.Main(new[] { "skill", "sync", "--output-dir", targetSkillDir, "--format", "human" });

        Assert.AreEqual(2, exitCode, "skill sync without --force must fail when files already exist");
        var actual = File.ReadAllText(skillPath);
        Assert.AreEqual("# Stale content", actual, "existing file must not be modified without --force");
    }

    [TestMethod]
    public void SkillSync_WithForce_OverwritesExistingSkillFiles()
    {
        var targetSkillDir = Path.Combine(_tempDir, ".agents", "skills", "dogdouspec");
        Directory.CreateDirectory(targetSkillDir);
        var skillPath = Path.Combine(targetSkillDir, "SKILL.md");
        File.WriteAllText(skillPath, "# Stale content");

        var exitCode = Program.Main(new[] { "skill", "sync", "--force", "--output-dir", targetSkillDir, "--format", "human" });

        Assert.AreEqual(0, exitCode);
        var actual = File.ReadAllText(skillPath);
        Assert.IsFalse(actual.Contains("Stale content"), "skill sync --force must overwrite stale skill files");
        Assert.IsTrue(actual.Contains("DogdouSpec"), "overwritten SKILL.md must contain embedded content");
    }

    [TestMethod]
    public void SkillSync_DoesNotCreateOrModifyAgentsMd()
    {
        var targetSkillDir = Path.Combine(_tempDir, ".agents", "skills", "dogdouspec");
        var agentsPath = Path.Combine(_tempDir, "AGENTS.md");

        var exitCode = Program.Main(new[] { "skill", "sync", "--force", "--output-dir", targetSkillDir, "--format", "human" });

        Assert.AreEqual(0, exitCode);
        Assert.IsFalse(File.Exists(agentsPath), "skill sync must never create AGENTS.md");
    }

    [TestMethod]
    public void SkillExport_XmlFormat_OutputsXmlEnvelope()
    {
        var targetSkillDir = Path.Combine(_tempDir, "custom_skill");
        using var sw = new StringWriter();
        var originalOut = Console.Out;
        try
        {
            Console.SetOut(sw);
            var exitCode = Program.Main(new[] { "skill", "export", "--output-dir", targetSkillDir, "--format", "xml" });
            Assert.AreEqual(0, exitCode);

            var xmlOutput = sw.ToString();
            Assert.IsTrue(xmlOutput.Contains("<skill action=\"skill export\""));
            Assert.IsTrue(xmlOutput.Contains("output_directory=\""));
            Assert.IsTrue(xmlOutput.Contains($"count=\"{DogdouSpec.Core.Resources.EmbeddedResources.SkillFilePaths.Count}\""));
        }
        finally
        {
            Console.SetOut(originalOut);
        }

        Assert.IsTrue(File.Exists(Path.Combine(targetSkillDir, "SKILL.md")));
    }

    private static readonly string[] SkillGuideMarkdownArgs = new[] { "skill", "guide", "--format", "markdown" };
    private static readonly string[] SkillGuideXmlArgs = new[] { "skill", "guide", "--format", "xml" };

    [TestMethod]
    public void SkillGuide_MarkdownFormat_OutputsGuideContent()
    {
        using var sw = new StringWriter();
        var originalOut = Console.Out;
        try
        {
            Console.SetOut(sw);
            var exitCode = Program.Main(SkillGuideMarkdownArgs);
            Assert.AreEqual(0, exitCode);

            var output = sw.ToString();
            Assert.IsTrue(output.Contains("DogdouSpec"));
            Assert.IsTrue(output.Contains("Mode A: Direct Execution"));
            Assert.IsTrue(output.Contains("Mode B: Governed Iterations"));
            Assert.IsTrue(output.Contains("Semantic Agent Results", StringComparison.Ordinal));
            Assert.IsTrue(output.Contains("tasks.xml", StringComparison.Ordinal));
            Assert.IsTrue(output.Contains("transport-ready", StringComparison.Ordinal));
        }
        finally
        {
            Console.SetOut(originalOut);
        }
    }

    [TestMethod]
    public void SkillGuide_XmlFormat_OutputsXmlStructure()
    {
        using var sw = new StringWriter();
        var originalOut = Console.Out;
        try
        {
            Console.SetOut(sw);
            var exitCode = Program.Main(SkillGuideXmlArgs);
            Assert.AreEqual(0, exitCode);

            var output = sw.ToString();
            Assert.IsTrue(output.Contains("<skill-guide name=\"dogdouspec\">"));
            Assert.IsTrue(output.Contains("<file path=\"SKILL.md\">"));
        }
        finally
        {
            Console.SetOut(originalOut);
        }
    }
}
