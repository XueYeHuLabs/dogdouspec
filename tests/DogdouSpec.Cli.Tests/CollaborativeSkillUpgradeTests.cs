using System.Diagnostics;
using System.Text;
using System.Xml.Linq;
using DogdouSpec.Core.Resources;
using DogdouSpec.Core.Skills;
using DogdouSpec.Core.Workspace;

namespace DogdouSpec.Cli.Tests;

[TestClass]
public sealed class CollaborativeSkillUpgradeTests
{
    private string _root = null!;
    private string _skill = null!;
    private const string CustomizedPath = "references/authority.md";

    [TestInitialize]
    public void Setup()
    {
        _root = Path.Combine(Path.GetTempPath(), "DogdouSpec_CollaborativeSkill_" + Guid.NewGuid().ToString("N"));
        _skill = Path.Combine(_root, ".agents", "skills", "dogdouspec");
        Directory.CreateDirectory(_root);
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    [TestMethod]
    public void NewInstall_RecordsPortableXmlBaseline_WithoutRequiringHistory()
    {
        Sync();
        var state = XDocument.Load(StatePath);
        var baseline = state.Root!.Element("baseline")!;
        Assert.AreEqual(SkillStateStore.CliVersion, (string?)baseline.Attribute("cli_version"));
        Assert.AreEqual("utf8-lf", (string?)baseline.Attribute("content_normalization"));
        Assert.AreEqual(EmbeddedResources.SkillFilePaths.Count, baseline.Elements("file").Count());
        foreach (var file in baseline.Elements("file"))
        {
            var path = (string)file.Attribute("path")!;
            Assert.IsFalse(Path.IsPathRooted(path));
            Assert.AreEqual(SkillStateStore.Digest(File.ReadAllBytes(Path.Combine(_skill, path))), (string?)file.Attribute("sha256"));
        }
        Assert.AreEqual(0, state.Root.Element("history")!.Elements().Count());
        var status = Status();
        Assert.AreEqual("0", (string?)status.Attribute("extra"));
        Assert.AreEqual("true", (string?)status.Attribute("in_sync"));
        Assert.AreEqual("not-needed", (string?)status.Attribute("automatic_upgrade"));
    }

    [TestMethod]
    public void UncustomizedUpgrade_RecognizesVersionDrift_AndSurvivesCopyAndCrLfCheckout()
    {
        Sync();
        InstallPreviousFile();
        var clone = Path.Combine(_root, "copied-skill");
        foreach (var path in Directory.GetFiles(_skill, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(clone, Path.GetRelativePath(_skill, path));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.WriteAllText(target, File.ReadAllText(path).Replace("\r\n", "\n", StringComparison.Ordinal).Replace("\n", "\r\n", StringComparison.Ordinal), new UTF8Encoding(false));
        }
        var result = Run("skill", "status", "--output-dir", clone, "--format", "xml");
        Assert.AreEqual(1, result.ExitCode, result.Stderr);
        var status = XElement.Parse(result.Stdout);
        Assert.AreEqual("ready", (string?)status.Attribute("automatic_upgrade"));
        Assert.AreEqual("0", (string?)status.Attribute("local_changes"));
        Assert.AreEqual("outdated", (string?)status.Elements("file").Single(file => (string?)file.Attribute("path") == CustomizedPath).Attribute("provenance"));
        Assert.AreEqual(0, Run("skill", "sync", "--force", "--output-dir", clone, "--format", "xml").ExitCode);
        Assert.AreEqual(0, Run("skill", "status", "--output-dir", clone, "--format", "xml").ExitCode);
    }

    [TestMethod]
    public void CustomizedUpgrade_ReportsFacts_PreservesOptionalHistory_AndAllowsForce()
    {
        Sync();
        InstallPreviousFile();
        File.AppendAllText(Path.Combine(_skill, CustomizedPath), "\nLocal independent review requirement.\n");
        var baseline = new XElement(XDocument.Load(StatePath).Root!.Element("baseline")!);
        const string summary = "增加独立审核要求 <local> & preserve intent.\n记录原因，不限制升级。";
        var record = Run("skill", "record", "--summary", summary, "--output-dir", _skill, "--format", "xml");
        Assert.AreEqual(0, record.ExitCode, record.Stderr);
        Assert.AreEqual(summary, XElement.Parse(record.Stdout).Element("record")!.Element("summary")!.Value);
        Assert.IsTrue(XNode.DeepEquals(baseline, XDocument.Load(StatePath).Root!.Element("baseline")), "record must never reset the sync baseline");
        var status = Status();
        Assert.AreEqual("review-needed", (string?)status.Attribute("automatic_upgrade"));
        Assert.AreEqual("1", (string?)status.Attribute("local_changes"));
        Assert.AreEqual(1, status.Elements("file").Count(file => (string?)file.Attribute("provenance") == "locally-modified"));

        var before = Snapshot(_skill);
        var diff = Run("skill", "diff", "--file", CustomizedPath, "--output-dir", _skill, "--format", "xml");
        Assert.AreEqual(1, diff.ExitCode, diff.Stderr);
        Assert.IsTrue(XElement.Parse(diff.Stdout).Element("file")!.Element("diff")!.Value.Contains("-Local independent review requirement.", StringComparison.Ordinal));
        AssertSnapshot(before, _skill);

        var sync = Run("skill", "sync", "--force", "--output-dir", _skill, "--format", "xml");
        Assert.AreEqual(0, sync.ExitCode, sync.Stderr);
        Assert.AreEqual(CustomizedPath, (string?)XElement.Parse(sync.Stdout).Element("replaced-local-changes")!.Element("file")!.Attribute("path"));
        var after = XDocument.Load(StatePath);
        Assert.AreEqual(summary, after.Root!.Element("history")!.Element("record")!.Element("summary")!.Value);
        Assert.AreEqual(SkillStateStore.Digest(EmbeddedResources.GetSkillBytes(CustomizedPath)!),
            (string?)after.Root.Element("baseline")!.Elements("file").Single(file => (string?)file.Attribute("path") == CustomizedPath).Attribute("sha256"));
        Assert.AreEqual("0", (string?)Status().Attribute("local_changes"), "old history is not current customization");

        File.AppendAllText(Path.Combine(_skill, CustomizedPath), "\nReapplied local independent review requirement.\n");
        Assert.AreEqual(0, Run("skill", "record", "--summary", "Adopted new guidance and reapplied the local review convention.", "--output-dir", _skill).ExitCode);
        status = Status();
        Assert.AreEqual("1", (string?)status.Attribute("local_changes"));
        Assert.AreEqual("0", (string?)status.Attribute("embedded_changes"));
        Assert.AreEqual("not-needed", (string?)status.Attribute("automatic_upgrade"));
        var records = Run("skill", "records", "--last", "1", "--output-dir", _skill, "--format", "xml");
        var history = XElement.Parse(records.Stdout);
        Assert.AreEqual("2", (string?)history.Attribute("total"));
        Assert.AreEqual(1, history.Elements("record").Count());
    }

    [TestMethod]
    public void UnknownBaseline_AndUnrecordedEdits_NeverAddAnOverwriteApprovalGate()
    {
        Sync();
        File.Delete(StatePath);
        File.AppendAllText(Path.Combine(_skill, "SKILL.md"), "\nUnrecorded local guidance.\n");
        Assert.AreEqual("unknown", (string?)Status().Attribute("automatic_upgrade"));
        Assert.AreEqual(0, Run("skill", "record", "--summary", "Legacy customization without a known baseline.", "--output-dir", _skill).ExitCode);
        Assert.IsNull(XDocument.Load(StatePath).Root!.Element("baseline"));
        Assert.AreEqual("unknown", (string?)Status().Attribute("automatic_upgrade"));
        Sync(force: true);
        File.AppendAllText(Path.Combine(_skill, "SKILL.md"), "\nAnother change with no history record.\n");
        Sync(force: true);
        Assert.AreEqual(0, Run("skill", "status", "--output-dir", _skill).ExitCode);
        Assert.AreEqual(1, XDocument.Load(StatePath).Root!.Element("history")!.Elements("record").Count());
    }

    [TestMethod]
    public void Export_PreservesHistory_AndRecordsItsWrittenBaseline()
    {
        Sync();
        Assert.AreEqual(0, Run("skill", "record", "--summary", "Keep my collaboration notes.", "--output-dir", _skill).ExitCode);
        File.WriteAllText(Path.Combine(_skill, "SKILL.md"), "custom text");
        var result = Run("skill", "export", "--output-dir", _skill, "--format", "xml");
        Assert.AreEqual(0, result.ExitCode, result.Stderr);
        Assert.AreEqual("1", (string?)XElement.Parse(result.Stdout).Attribute("history_preserved"));
        Assert.AreEqual(0, Run("skill", "status", "--output-dir", _skill).ExitCode);
    }

    [TestMethod]
    public void WorkspaceInit_StampsNewSkills_WithoutCertifyingExistingCustomization()
    {
        var initialized = WorkspaceInitializer.Initialize(_root, _root);
        Assert.IsTrue(initialized.Success, initialized.Error?.Message);
        Assert.IsTrue(File.Exists(StatePath));
        var existingRoot = Path.Combine(_root, "another-project");
        var existingSkill = Path.Combine(existingRoot, ".agents", "skills", "dogdouspec");
        Directory.CreateDirectory(existingSkill);
        File.WriteAllText(Path.Combine(existingSkill, "SKILL.md"), "Existing local customization");
        initialized = WorkspaceInitializer.Initialize(existingRoot, existingRoot);
        Assert.IsTrue(initialized.Success, initialized.Error?.Message);
        Assert.IsFalse(File.Exists(Path.Combine(existingSkill, SkillStateStore.FileName)));
        Assert.AreEqual("Existing local customization", File.ReadAllText(Path.Combine(existingSkill, "SKILL.md")));
    }

    [TestMethod]
    public void MissingAndRetiredFiles_AreReported_WithoutDeletingExtras()
    {
        Sync();
        File.Delete(Path.Combine(_skill, CustomizedPath));
        Assert.AreEqual("true", (string?)Status().Elements("file").Single(file => (string?)file.Attribute("path") == CustomizedPath).Attribute("local_changed"));
        var state = XDocument.Load(StatePath);
        var retired = "references/retired.md";
        File.WriteAllText(Path.Combine(_skill, retired), "old managed content\n");
        state.Root!.Element("baseline")!.Add(new XElement("file", new XAttribute("path", retired),
            new XAttribute("sha256", SkillStateStore.Digest(File.ReadAllBytes(Path.Combine(_skill, retired))))));
        state.Save(StatePath);
        File.WriteAllText(Path.Combine(_skill, "repository-notes.md"), "unmanaged notes");
        var diff = Run("skill", "diff", "--file", retired, "--output-dir", _skill, "--format", "xml");
        Assert.AreEqual(1, diff.ExitCode, diff.Stderr);
        Assert.AreEqual("preserve", (string?)XElement.Parse(diff.Stdout).Element("file")!.Attribute("sync_action"));
        Sync(force: true);
        Assert.AreEqual("old managed content\n", File.ReadAllText(Path.Combine(_skill, retired)));
        Assert.AreEqual("unmanaged notes", File.ReadAllText(Path.Combine(_skill, "repository-notes.md")));
    }

    [TestMethod]
    public void ReadCommands_DoNotCreateMetadata_AndFocusedGuideEqualsTheResource()
    {
        var records = Run("skill", "records", "--output-dir", _skill, "--format", "xml");
        Assert.AreEqual(0, records.ExitCode, records.Stderr);
        Assert.AreEqual("0", (string?)XElement.Parse(records.Stdout).Attribute("total"));
        Assert.AreEqual(1, Run("skill", "status", "--output-dir", _skill).ExitCode);
        Assert.AreEqual(1, Run("skill", "diff", "--output-dir", _skill).ExitCode);
        Assert.IsFalse(Directory.Exists(_skill));
        var guide = Run("skill", "guide", "--file", "references/upgrade.md", "--format", "markdown");
        Assert.AreEqual(0, guide.ExitCode, guide.Stderr);
        Assert.AreEqual(EmbeddedResources.GetSkillText("references/upgrade.md"), guide.Stdout);
        Assert.AreEqual(2, Run("skill", "guide", "--all", "--file", "references/upgrade.md").ExitCode);
        Assert.AreEqual(2, Run("skill", "guide", "--file", "../AGENTS.md").ExitCode);
        Assert.AreEqual(2, Run("skill", "records", "--last", "0", "--output-dir", _skill).ExitCode);
    }

    [TestMethod]
    public void SchemaDiff_IsReadOnlyAndFilterable_AndDriftDiagnosticNamesSync()
    {
        var initialized = WorkspaceInitializer.Initialize(_root, _root);
        Assert.IsTrue(initialized.Success, initialized.Error?.Message);
        var schemaPath = Path.Combine(initialized.WorkspaceRoot, "_schema", "tasks.xsd");
        File.WriteAllText(schemaPath, "local schema text\n");
        var before = Snapshot(_root);
        var diff = Run("schema", "diff", "--name", "tasks", "--workspace-root", _root, "--format", "xml");
        Assert.AreEqual(1, diff.ExitCode, diff.Stderr);
        var xml = XElement.Parse(diff.Stdout);
        Assert.AreEqual(1, xml.Elements("file").Count());
        Assert.IsTrue(xml.Element("file")!.Element("diff")!.Value.Contains("-local schema text", StringComparison.Ordinal));
        AssertSnapshot(before, _root);
        Assert.AreEqual(0, Run("schema", "diff", "--name", "spec", "--workspace-root", _root).ExitCode);
        var validation = Run("validate", "--workspace-root", _root, "--format", "xml");
        Assert.IsTrue(validation.Stderr.Contains("schema sync --expected-version 1.0", StringComparison.Ordinal));
    }

    [TestMethod]
    public void DeletedRetiredFile_IsNotAnExtra_AndItsExplicitDiffIsEmpty()
    {
        Sync();
        const string retired = "references/retired.md";
        var state = XDocument.Load(StatePath);
        state.Root!.Element("baseline")!.Add(new XElement("file", new XAttribute("path", retired),
            new XAttribute("sha256", SkillStateStore.Digest(Encoding.UTF8.GetBytes("old managed text\n")))));
        state.Save(StatePath);
        var before = Snapshot(_skill);
        var result = Run("skill", "status", "--output-dir", _skill, "--format", "xml");
        Assert.AreEqual(0, result.ExitCode, result.Stderr);
        var status = XElement.Parse(result.Stdout);
        Assert.AreEqual("0", (string?)status.Attribute("extra"));
        Assert.IsFalse(status.Elements("file").Any(file => (string?)file.Attribute("path") == retired));
        var diff = Run("skill", "diff", "--file", retired, "--output-dir", _skill, "--format", "xml");
        Assert.AreEqual(0, diff.ExitCode, diff.Stderr);
        Assert.AreEqual("0", (string?)XElement.Parse(diff.Stdout).Attribute("changed"));
        AssertSnapshot(before, _skill);
    }

    [TestMethod]
    [DataRow("skill", "unified", false)]
    [DataRow("skill", "xml", false)]
    [DataRow("schema", "unified", false)]
    [DataRow("schema", "xml", false)]
    [DataRow("skill", "unified", true)]
    [DataRow("schema", "xml", true)]
    public void UnreadableDiffText_ReportsTheFile_WithoutPartialOutput(string kind, string format, bool utf16WithoutBom)
    {
        string invalidPath;
        string[] arguments;
        if (kind == "skill")
        {
            Sync();
            File.WriteAllText(Path.Combine(_skill, "SKILL.md"), "Earlier valid change.\n");
            invalidPath = Path.Combine(_skill, "references", "upgrade.md");
            arguments = ["skill", "diff", "--output-dir", _skill, "--format", format];
        }
        else
        {
            var initialized = WorkspaceInitializer.Initialize(_root, _root);
            Assert.IsTrue(initialized.Success, initialized.Error?.Message);
            File.WriteAllText(Path.Combine(initialized.WorkspaceRoot, "_schema", "backlog.xsd"), "Earlier valid change.\n");
            invalidPath = Path.Combine(initialized.WorkspaceRoot, "_schema", "tasks.xsd");
            arguments = ["schema", "diff", "--workspace-root", _root, "--format", format];
        }
        File.WriteAllBytes(invalidPath, utf16WithoutBom ? Encoding.Unicode.GetBytes("local text\n") : [0xff, 0xfe, 0x80]);
        var before = Snapshot(_root);
        var result = Run(arguments);
        Assert.AreEqual(2, result.ExitCode);
        Assert.AreEqual(string.Empty, result.Stdout, "Failed comparisons must not emit incomplete diffs.");
        Assert.IsTrue(result.Stderr.Contains("FILESYSTEM_ERROR", StringComparison.Ordinal), result.Stderr);
        Assert.IsTrue(result.Stderr.Contains(Path.GetFullPath(invalidPath), StringComparison.Ordinal), result.Stderr);
        AssertSnapshot(before, _root);
    }

    [TestMethod]
    public void SchemaDiff_XmlPreservesTheExactUnifiedPayloadAndCarriageReturns()
    {
        var initialized = WorkspaceInitializer.Initialize(_root, _root);
        Assert.IsTrue(initialized.Success, initialized.Error?.Message);
        var path = Path.Combine(initialized.WorkspaceRoot, "_schema", "tasks.xsd");
        var original = File.ReadAllText(path);
        var lf = original.Replace("\r\n", "\n", StringComparison.Ordinal);
        File.WriteAllText(path, original.Contains("\r\n", StringComparison.Ordinal) ? lf : lf.Replace("\n", "\r\n", StringComparison.Ordinal));
        var unified = Run("schema", "diff", "--name", "tasks", "--workspace-root", _root, "--format", "unified");
        var xml = Run("schema", "diff", "--name", "tasks", "--workspace-root", _root, "--format", "xml");
        Assert.AreEqual(1, unified.ExitCode, unified.Stderr);
        Assert.AreEqual(1, xml.ExitCode, xml.Stderr);
        Assert.IsTrue(unified.Stdout.Contains('\r', StringComparison.Ordinal));
        Assert.AreEqual(unified.Stdout, XElement.Parse(xml.Stdout).Element("file")!.Element("diff")!.Value);
        Assert.IsFalse(xml.Stdout.Contains('\r', StringComparison.Ordinal), "XML formatting uses LF; payload CR must be entitized.");
    }

    [TestMethod]
    public void XmlHistoryAndGuide_PreserveTheirOriginalText()
    {
        Sync();
        const string summary = "Reason\r\nChange\rOutcome";
        var result = Run("skill", "record", "--summary", summary, "--output-dir", _skill, "--format", "xml");
        Assert.AreEqual(0, result.ExitCode, result.Stderr);
        Assert.AreEqual(summary, XElement.Parse(result.Stdout).Element("record")!.Element("summary")!.Value);
        var guide = Run("skill", "guide", "--all", "--format", "xml");
        Assert.AreEqual(0, guide.ExitCode, guide.Stderr);
        foreach (var file in XElement.Parse(guide.Stdout).Elements("file"))
            Assert.AreEqual(EmbeddedResources.GetSkillText((string)file.Attribute("path")!), file.Value);
    }

    [TestMethod]
    public void SyncReceipt_ReportsOnlyReplacedLocalContent_AndUsesNativePaths()
    {
        Sync();
        var state = XDocument.Load(StatePath);
        state.Root!.Element("baseline")!.Elements("file").Single(file => (string?)file.Attribute("path") == CustomizedPath).Remove();
        state.Root.Element("baseline")!.Elements("file").Single(file => (string?)file.Attribute("path") == "SKILL.md")
            .SetAttributeValue("sha256", new string('0', 64));
        state.Save(StatePath);
        File.WriteAllText(Path.Combine(_skill, CustomizedPath), "Existing customization at a newly managed path.\n");
        var skillBytes = File.ReadAllBytes(Path.Combine(_skill, "SKILL.md"));
        var sync = Run("skill", "sync", "--force", "--output-dir", _skill, "--format", "xml");
        Assert.AreEqual(0, sync.ExitCode, sync.Stderr);
        var receipt = XElement.Parse(sync.Stdout);
        CollectionAssert.AreEqual(new[] { CustomizedPath }, receipt.Element("replaced-local-changes")!.Elements("file")
            .Select(file => (string)file.Attribute("path")!).ToArray());
        CollectionAssert.AreEqual(skillBytes, File.ReadAllBytes(Path.Combine(_skill, "SKILL.md")));
        CollectionAssert.AreEqual(EmbeddedResources.SkillFilePaths.Select(path => Path.Combine(_skill, path.Replace('/', Path.DirectorySeparatorChar))).ToArray(),
            receipt.Elements("file").Select(file => (string)file.Attribute("path")!).ToArray());
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public void SelectedDiffPath_UsesFilesystemCaseSemantics_WithOrWithoutBaseline(bool withBaseline)
    {
        Sync();
        if (!withBaseline) File.Delete(StatePath);
        File.AppendAllText(Path.Combine(_skill, CustomizedPath), "\nLocal case-sensitive path fixture.\n");
        var result = Run("skill", "diff", "--file", "REFERENCES/AUTHORITY.MD", "--output-dir", _skill, "--format", "xml");
        if (OperatingSystem.IsWindows())
        {
            Assert.AreEqual(1, result.ExitCode, result.Stderr);
            var diff = XElement.Parse(result.Stdout);
            Assert.AreEqual("1", (string?)diff.Attribute("changed"));
            Assert.AreEqual(CustomizedPath, (string?)diff.Element("file")!.Attribute("path"));
            Assert.IsTrue(diff.Element("file")!.Element("diff")!.Value.Contains("-Local case-sensitive path fixture.", StringComparison.Ordinal));
        }
        else
        {
            Assert.AreEqual(2, result.ExitCode, "A differently cased path is not a managed path on a case-sensitive filesystem.");
            Assert.AreEqual(string.Empty, result.Stdout);
        }
    }

    [TestMethod]
    public void ForceSync_OverwritesNonUtf8ContentWithoutAnotherApproval()
    {
        Sync();
        File.WriteAllBytes(Path.Combine(_skill, CustomizedPath), [0xff, 0xfe, 0x80]);
        Sync(force: true);
        Assert.AreEqual(0, Run("skill", "status", "--output-dir", _skill).ExitCode);
    }

    [TestMethod]
    public void RealProcess_RoundTripsUnicodeHistoryThroughUtf8RedirectedOutput()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "DogdouSpec.slnx"))) directory = directory.Parent;
        Assert.IsNotNull(directory);
        var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
        var cli = Path.Combine(directory.FullName, "src", "DogdouSpec.Cli", "bin", configuration, "net10.0", "dogdouspec.dll");
        const string summary = "定制原因：中文、é、✓；保留协作意图。";
        var start = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = new UTF8Encoding(false, true), StandardErrorEncoding = Encoding.UTF8
        };
        foreach (var argument in new[] { cli, "skill", "record", "--summary", summary, "--output-dir", _skill, "--format", "xml" }) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        Assert.IsTrue(process.WaitForExit(30000));
        Assert.AreEqual(0, process.ExitCode, stderr);
        Assert.AreEqual(summary, XElement.Parse(stdout).Element("record")!.Element("summary")!.Value);
        Assert.AreEqual(summary, XDocument.Load(StatePath).Root!.Element("history")!.Element("record")!.Element("summary")!.Value);
    }

    private string StatePath => Path.Combine(_skill, SkillStateStore.FileName);
    private void Sync(bool force = false)
    {
        var arguments = new List<string> { "skill", "sync", "--output-dir", _skill, "--format", "xml" };
        if (force) arguments.Add("--force");
        var result = Run([.. arguments]);
        Assert.AreEqual(0, result.ExitCode, result.Stderr);
    }
    private XElement Status()
    {
        var result = Run("skill", "status", "--output-dir", _skill, "--format", "xml");
        Assert.IsTrue(result.ExitCode is 0 or 1, result.Stderr);
        return XElement.Parse(result.Stdout);
    }
    private void InstallPreviousFile()
    {
        const string text = "# Previous authority\nReview verification evidence before completion.\n";
        File.WriteAllText(Path.Combine(_skill, CustomizedPath), text);
        var state = XDocument.Load(StatePath);
        var baseline = state.Root!.Element("baseline")!;
        baseline.SetAttributeValue("cli_version", "0.9.0-test-fixture");
        baseline.Elements("file").Single(file => (string?)file.Attribute("path") == CustomizedPath)
            .SetAttributeValue("sha256", SkillStateStore.Digest(Encoding.UTF8.GetBytes(text)));
        state.Save(StatePath);
    }
    private static Dictionary<string, byte[]> Snapshot(string root) => Directory.GetFiles(root, "*", SearchOption.AllDirectories)
        .ToDictionary(path => Path.GetRelativePath(root, path), File.ReadAllBytes);
    private static void AssertSnapshot(Dictionary<string, byte[]> before, string root)
    {
        var after = Snapshot(root);
        CollectionAssert.AreEquivalent(before.Keys.ToArray(), after.Keys.ToArray());
        foreach (var (path, bytes) in before) CollectionAssert.AreEqual(bytes, after[path], path);
    }
    private static (int ExitCode, string Stdout, string Stderr) Run(params string[] args)
    {
        var originalOut = Console.Out;
        var originalErr = Console.Error;
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        try
        {
            Console.SetOut(stdout);
            Console.SetError(stderr);
            return (Program.Main(args), stdout.ToString(), stderr.ToString());
        }
        finally
        {
            Console.SetOut(originalOut);
            Console.SetError(originalErr);
        }
    }
}
