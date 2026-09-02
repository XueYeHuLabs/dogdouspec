using DogdouSpec.Core.Diagnostics;
using DogdouSpec.Core.Resources;
using DogdouSpec.Core.Validation;
using DogdouSpec.Core.Workspace;

namespace DogdouSpec.Core.Tests;

[TestClass]
public sealed class InitTests
{
    private string _tempDir = null!;

    [TestInitialize]
    public void SetUp()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "DogdouSpec_InitTests_" + Guid.NewGuid().ToString("N"));
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

    [TestMethod]
    public void Initialize_HappyPath_CreatesRequiredStructureAndSchemas()
    {
        var (success, root, error) = WorkspaceInitializer.Initialize(null, _tempDir);

        Assert.IsTrue(success);
        Assert.IsNull(error);

        var dogdouDir = Path.Combine(_tempDir, ".dogdouspec");
        Assert.IsTrue(Directory.Exists(dogdouDir));

        // Check _schema
        var schemaDir = Path.Combine(dogdouDir, "_schema");
        Assert.IsTrue(Directory.Exists(schemaDir));
        Assert.IsTrue(File.Exists(Path.Combine(schemaDir, "README.md")));
        foreach (var schemaName in EmbeddedResources.SchemaNames)
        {
            Assert.IsTrue(File.Exists(Path.Combine(schemaDir, $"{schemaName}.xsd")), $"Schema {schemaName}.xsd missing in _schema");
        }

        // Check _skill
        var skillDir = Path.Combine(dogdouDir, "_skill");
        Assert.IsTrue(Directory.Exists(skillDir));
        var skillReadmePath = Path.Combine(skillDir, "README.md");
        Assert.IsTrue(File.Exists(skillReadmePath));
        var skillReadme = File.ReadAllText(skillReadmePath);
        Assert.IsTrue(skillReadme.Contains("Semantic agent results", StringComparison.Ordinal));
        Assert.IsTrue(skillReadme.Contains("tasks.xml", StringComparison.Ordinal));
        Assert.IsTrue(skillReadme.Contains(".dogdouspec/_tmp/", StringComparison.Ordinal));
        Assert.IsTrue(skillReadme.Contains("does not stage, commit, or push", StringComparison.Ordinal));

        // Check knowledge.xml and backlog.xml
        var knowledgePath = Path.Combine(dogdouDir, "knowledge.xml");
        var backlogPath = Path.Combine(dogdouDir, "backlog.xml");
        Assert.IsTrue(File.Exists(knowledgePath));
        Assert.IsTrue(File.Exists(backlogPath));

        // Check EmbeddedResources methods
        var agentsTemplate = EmbeddedResources.GetAgentsTemplateText();
        Assert.IsNotNull(agentsTemplate);
        Assert.IsTrue(agentsTemplate.Contains("DogdouSpec Workflow", StringComparison.OrdinalIgnoreCase));
        Assert.IsTrue(agentsTemplate.Contains("Persist semantic agent results", StringComparison.Ordinal));
        Assert.IsTrue(agentsTemplate.Contains("locally durable but not transport-ready", StringComparison.Ordinal));
        foreach (var relPath in EmbeddedResources.SkillFilePaths)
        {
            var content = EmbeddedResources.GetSkillText(relPath);
            Assert.IsNotNull(content, $"Skill resource {relPath} must exist in EmbeddedResources");
        }

        // Validate created files against embedded schemas
        var validationResult = SchemaValidator.Validate(root);
        Assert.IsTrue(validationResult.IsValid, "Newly initialized workspace must pass schema validation");
        Assert.AreEqual(2, validationResult.CheckedDocumentsCount);
    }

    [TestMethod]
    public void Initialize_CollisionNonOverwrite_FailsWithoutModifyingState()
    {
        var (firstSuccess, root, _) = WorkspaceInitializer.Initialize(null, _tempDir);
        Assert.IsTrue(firstSuccess);

        // Put a custom canary in knowledge.xml
        var knowledgePath = Path.Combine(_tempDir, ".dogdouspec", "knowledge.xml");
        var originalContent = File.ReadAllText(knowledgePath);

        // Try initializing again
        var (secondSuccess, _, secondError) = WorkspaceInitializer.Initialize(null, _tempDir);

        Assert.IsFalse(secondSuccess);
        Assert.IsNotNull(secondError);
        Assert.AreEqual(DiagnosticCodes.ManagedStateExists, secondError.Code);

        // Assert original content untouched
        var currentContent = File.ReadAllText(knowledgePath);
        Assert.AreEqual(originalContent, currentContent);
    }

    [TestMethod]
    public void Initialize_ExplicitWorkspaceRoot_InitializesAtExplicitPath()
    {
        var targetProj = Path.Combine(_tempDir, "custom_proj");
        Directory.CreateDirectory(targetProj);

        var (success, root, error) = WorkspaceInitializer.Initialize(targetProj, _tempDir);

        Assert.IsTrue(success);
        Assert.IsNull(error);

        var expectedDogdou = Path.Combine(targetProj, ".dogdouspec");
        Assert.IsTrue(Directory.Exists(expectedDogdou));
        Assert.IsTrue(File.Exists(Path.Combine(expectedDogdou, "knowledge.xml")));
        Assert.IsTrue(File.Exists(Path.Combine(expectedDogdou, "backlog.xml")));
    }
}
