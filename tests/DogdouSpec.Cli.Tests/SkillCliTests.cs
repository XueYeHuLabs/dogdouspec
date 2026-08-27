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
    public void SkillSync_ExportsSkillFilesToDefaultLocation()
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
}