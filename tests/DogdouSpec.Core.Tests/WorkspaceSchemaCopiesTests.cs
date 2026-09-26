using System.Xml.Linq;
using DogdouSpec.Core.Resources;
using DogdouSpec.Core.Transactions;
using DogdouSpec.Core.Workspace;

namespace DogdouSpec.Core.Tests;

[TestClass]
public sealed class WorkspaceSchemaCopiesTests
{
    private string _projectRoot = null!;
    private string _workspaceRoot = null!;

    [TestInitialize]
    public void SetUp()
    {
        _projectRoot = Path.Combine(Path.GetTempPath(), "DogdouSpec_SchemaCopies_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_projectRoot);
        var (success, workspaceRoot, error) = WorkspaceInitializer.Initialize(_projectRoot, _projectRoot);
        Assert.IsTrue(success, error?.Message);
        _workspaceRoot = workspaceRoot;
    }

    [TestCleanup]
    public void TearDown()
    {
        if (Directory.Exists(_projectRoot))
        {
            try { Directory.Delete(_projectRoot, recursive: true); } catch { }
        }
    }

    [TestMethod]
    public void StartupRecovery_ApplyingSchemaSync_RestoresExistingAndRemovesNewCopies()
    {
        var schemaDir = Path.Combine(_workspaceRoot, "_schema");
        var originalSpec = File.ReadAllBytes(Path.Combine(schemaDir, "spec.xsd"));
        var staging = Path.Combine(_workspaceRoot, "_tmp", "schema_sync_interrupted");
        Directory.CreateDirectory(staging);

        File.Copy(Path.Combine(schemaDir, "spec.xsd"), Path.Combine(staging, "spec.xsd.bak"));
        File.WriteAllText(Path.Combine(schemaDir, "spec.xsd"), "partially applied");
        File.Delete(Path.Combine(schemaDir, "tasks.xsd"));
        File.WriteAllBytes(Path.Combine(schemaDir, "tasks.xsd"), EmbeddedResources.GetSchemaBytes("tasks", "1.0")!);
        var marker = new XDocument(
            new XElement("schema-sync",
                new XAttribute("state", "applying"),
                new XElement("file", new XAttribute("name", "spec"), new XAttribute("existed", "true")),
                new XElement("file", new XAttribute("name", "tasks"), new XAttribute("existed", "false"))));
        marker.Save(Path.Combine(staging, "marker.xml"), SaveOptions.DisableFormatting);

        var (success, error) = StartupRecovery.Run(_workspaceRoot);

        Assert.IsTrue(success, error?.Message);
        CollectionAssert.AreEqual(originalSpec, File.ReadAllBytes(Path.Combine(schemaDir, "spec.xsd")));
        Assert.IsFalse(File.Exists(Path.Combine(schemaDir, "tasks.xsd")));
        Assert.IsFalse(Directory.Exists(staging));
    }

    [TestMethod]
    public void StartupRecovery_CommittedSchemaSync_PreservesTargetsAndCleansStaging()
    {
        var schemaDir = Path.Combine(_workspaceRoot, "_schema");
        var expectedSpec = EmbeddedResources.GetSchemaBytes("spec", "1.0")!;
        var staging = Path.Combine(_workspaceRoot, "_tmp", "schema_sync_committed");
        Directory.CreateDirectory(staging);
        File.WriteAllText(Path.Combine(staging, "spec.xsd.bak"), "old copy");
        var marker = new XDocument(
            new XElement("schema-sync",
                new XAttribute("state", "committed"),
                new XElement("file", new XAttribute("name", "spec"), new XAttribute("existed", "true"))));
        marker.Save(Path.Combine(staging, "marker.xml"), SaveOptions.DisableFormatting);

        var (success, error) = StartupRecovery.Run(_workspaceRoot);

        Assert.IsTrue(success, error?.Message);
        CollectionAssert.AreEqual(expectedSpec, File.ReadAllBytes(Path.Combine(schemaDir, "spec.xsd")));
        Assert.IsFalse(Directory.Exists(staging));
    }

    [TestMethod]
    public void StartupRecovery_ApplyingSchemaSync_RejectsReparseTargetWithoutChangingExternalFiles()
    {
        var schemaDir = Path.Combine(_workspaceRoot, "_schema");
        var originalSchemaDir = Path.Combine(_workspaceRoot, "_schema-original");
        var externalDir = Path.Combine(Path.GetTempPath(), "DogdouSpec_SchemaCopiesExternal_" + Guid.NewGuid().ToString("N"));
        Directory.Move(schemaDir, originalSchemaDir);
        Directory.CreateDirectory(externalDir);
        var externalTarget = Path.Combine(externalDir, "spec.xsd");
        File.WriteAllText(externalTarget, "external sentinel");

        try
        {
            if (!OperatingSystem.IsWindows())
            {
                Assert.Inconclusive("This reparse-point regression uses a Windows directory junction.");
                return;
            }
            var startInfo = new System.Diagnostics.ProcessStartInfo(
                "cmd.exe",
                $"/c mklink /J \"{schemaDir}\" \"{externalDir}\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            using (var process = System.Diagnostics.Process.Start(startInfo))
            {
                process!.WaitForExit();
            }
            if (!Directory.Exists(schemaDir))
            {
                Assert.Inconclusive("Directory junction creation is unavailable on this test host.");
                return;
            }

            var staging = Path.Combine(_workspaceRoot, "_tmp", "schema_sync_reparse_target");
            Directory.CreateDirectory(staging);
            var marker = new XDocument(
                new XElement("schema-sync",
                    new XAttribute("state", "applying"),
                    new XElement("file", new XAttribute("name", "spec"), new XAttribute("existed", "false"))));
            marker.Save(Path.Combine(staging, "marker.xml"), SaveOptions.DisableFormatting);

            var (success, error) = StartupRecovery.Run(_workspaceRoot);

            Assert.IsFalse(success);
            Assert.IsNotNull(error);
            Assert.AreEqual("external sentinel", File.ReadAllText(externalTarget));
            Assert.IsTrue(Directory.Exists(staging), "Recovery evidence must remain after a security refusal.");
        }
        finally
        {
            if (Directory.Exists(schemaDir))
            {
                Directory.Delete(schemaDir);
            }
            if (Directory.Exists(originalSchemaDir))
            {
                Directory.Move(originalSchemaDir, schemaDir);
            }
            if (Directory.Exists(externalDir))
            {
                Directory.Delete(externalDir, recursive: true);
            }
        }
    }
}
