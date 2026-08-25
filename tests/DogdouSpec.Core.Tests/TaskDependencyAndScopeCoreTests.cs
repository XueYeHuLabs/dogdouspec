using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Xml.Linq;
using DogdouSpec.Core.Diagnostics;
using DogdouSpec.Core.Tasks;
using DogdouSpec.Core.Validation;

namespace DogdouSpec.Core.Tests;

[TestClass]
public sealed class TaskDependencyAndScopeCoreTests
{
    private static string RepoRoot = null!;
    private string _tempDir = null!;

    [ClassInitialize]
    public static void ClassInit(TestContext context)
    {
        RepoRoot = FindRepositoryRootFromSource()
            ?? FindRepositoryRoot(Environment.CurrentDirectory)
            ?? FindRepositoryRoot(AppDomain.CurrentDomain.BaseDirectory)
            ?? string.Empty;
        Assert.IsFalse(string.IsNullOrEmpty(RepoRoot), "Repository root could not be located.");
    }

    private static string? FindRepositoryRoot(string startDirectory)
    {
        if (string.IsNullOrWhiteSpace(startDirectory))
        {
            return null;
        }

        for (var current = new DirectoryInfo(Path.GetFullPath(startDirectory)); current != null; current = current.Parent)
        {
            if (File.Exists(Path.Combine(current.FullName, "DogdouSpec.slnx")) ||
                File.Exists(Path.Combine(current.FullName, "DogdouSpec.sln")))
            {
                return current.FullName;
            }
        }

        return null;
    }

    private static string? FindRepositoryRootFromSource([CallerFilePath] string sourceFile = "") =>
        FindRepositoryRoot(Path.GetDirectoryName(sourceFile) ?? string.Empty);

    [TestInitialize]
    public void SetUp()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "DogdouSpec_DepScopeCoreTests_" + Guid.NewGuid().ToString("N"));
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

    private static void RunGit(string repositoryRoot, params string[] arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = repositoryRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo);
        Assert.IsNotNull(process);
        var standardOutput = process.StandardOutput.ReadToEnd();
        var standardError = process.StandardError.ReadToEnd();
        Assert.IsTrue(process.WaitForExit(30_000), $"Git command timed out: {string.Join(' ', arguments)}");
        Assert.AreEqual(0, process.ExitCode, $"Git command failed: {string.Join(' ', arguments)}\n{standardOutput}\n{standardError}");
    }

    private static void CreateExternalTerminalIteration(string workspace, string iterationId, string taskId)
    {
        var iterationDirectory = Path.Combine(workspace, iterationId);
        Directory.CreateDirectory(iterationDirectory);
        var requirementId = iterationId + "-req-test";
        var criterionId = iterationId + "-crit-done";
        File.WriteAllText(Path.Combine(iterationDirectory, "spec.xml"), $"""
<?xml version="1.0" encoding="utf-8"?>
<iteration id="{iterationId}" schema_version="1.0" revision="1" kind="feature" status="draft" created_at="2026-08-24T01:00:00Z" updated_at="2026-08-24T01:00:00Z">
  <index><summary>External dependency iteration.</summary></index>
  <product>
    <objective>External dependency fixture.</objective>
    <deliverables><deliverable id="{iterationId}-deliverable"><index><summary>Fixture deliverable.</summary></index><description>Fixture.</description></deliverable></deliverables>
    <scope><included/><excluded/></scope>
    <requirements><requirement id="{requirementId}" status="proposed"><index><summary>Fixture requirement.</summary></index><statement>Fixture.</statement><rationale>Fixture.</rationale></requirement></requirements>
    <acceptance><criterion id="{iterationId}-acceptance" decision="pending">Fixture.</criterion></acceptance>
  </product>
  <confirmations/>
</iteration>
""");
        File.WriteAllText(Path.Combine(iterationDirectory, "tasks.xml"), $"""
<?xml version="1.0" encoding="utf-8"?>
<tasks id="{iterationId}-tasks" iteration="{iterationId}" schema_version="1.0" revision="1">
  <index><summary>External dependency tasks.</summary></index>
  <task id="{taskId}" status="done" created_at="2026-08-24T01:00:00Z" completed_at="2026-08-24T01:00:00Z" updated_at="2026-08-24T01:00:00Z" agent="codex">
    <index><summary>Terminal dependency.</summary><term key="status" value="done"/></index>
    <title>Terminal dependency</title><objective>Fixture.</objective><rationale>Fixture.</rationale>
    <scope><repository path="."><include path="docs/**"/></repository></scope>
    <origin><ref scope="iteration" target="{requirementId}" relation="implements"/></origin>
    <constraints/>
    <acceptance><criterion id="{criterionId}" status="passed">Fixture.</criterion></acceptance>
    <context><summary>Fixture.</summary></context>
    <records><record id="{iterationId}-completion" kind="completion" status="informational" created_at="2026-08-24T01:00:00Z" actor="codex"><summary>Fixture completion.</summary><covers><ref scope="document" target="{criterionId}" relation="covers"/></covers></record></records>
  </task>
</tasks>
""");
    }

    #region Acceptance 1: Dependency Gating on Task Start and Resume

    [TestMethod]
    public void TaskUpdate_Start_FailsClosed_WhenDependencyIsNonTerminal()
    {
        var workspace = CreateWorkspaceCopy();
        // 20260823-task-atomic-update depends on 20260823-task-xpath-projection which is in-progress (non-terminal)
        var requestXml = """
<task-update
  id="20260823T050000Z-update-start-atomic"
  transition="start"
  actor="codex"
  occurred_at="2026-08-23T05:00:00Z">
  <records>
    <record
      id="20260823T050000Z-record-atomic-start"
      kind="start"
      status="informational"
      created_at="2026-08-23T05:00:00Z"
      actor="codex">
      <summary>Attempting to start task with non-terminal dependency.</summary>
    </record>
  </records>
</task-update>
""";

        var (success, envelope, diagnostics) = TaskUpdater.Update(
            workspace,
            "20260823-xpath-core",
            "20260823-task-atomic-update",
            9,
            requestXml);

        Assert.IsFalse(success);
        Assert.IsNull(envelope);
        Assert.IsTrue(diagnostics.Any(d => d.Code == DiagnosticCodes.TaskTransitionConflict && d.Message.Contains("non-terminal status")),
            "Expected TaskTransitionConflict diagnostic due to non-terminal dependency.");
    }

    [TestMethod]
    public void TaskUpdate_Start_FailsClosed_WhenDependencyIsDangling()
    {
        var workspace = CreateWorkspaceCopy();
        // Modify tasks.xml to add a dangling dependency to 20260823-task-task-history
        var tasksPath = Path.Combine(workspace, "20260823-xpath-core", "tasks.xml");
        var xdoc = XDocument.Load(tasksPath);
        var task = xdoc.Root!.Elements("task").First(t => t.Attribute("id")?.Value == "20260823-task-task-history");
        task.Element("origin")!.AddAfterSelf(new XElement("dependencies",
            new XElement("ref",
                new XAttribute("scope", "document"),
                new XAttribute("target", "20260823-task-non-existent"),
                new XAttribute("relation", "depends-on"))));
        xdoc.Save(tasksPath);

        var requestXml = """
<task-update
  id="20260823T050000Z-update-start-history"
  transition="start"
  actor="codex"
  occurred_at="2026-08-23T05:00:00Z">
  <records>
    <record
      id="20260823T050000Z-record-history-start"
      kind="start"
      status="informational"
      created_at="2026-08-23T05:00:00Z"
      actor="codex">
      <summary>Starting task history with dangling dependency.</summary>
    </record>
  </records>
</task-update>
""";

        var (success, envelope, diagnostics) = TaskUpdater.Update(
            workspace,
            "20260823-xpath-core",
            "20260823-task-task-history",
            9,
            requestXml);

        Assert.IsFalse(success);
        Assert.IsNull(envelope);
        Assert.IsTrue(diagnostics.Any(d => d.Code == DiagnosticCodes.DanglingReference),
            "Expected DanglingReference diagnostic for unresolved dependency.");
    }

    [TestMethod]
    public void TaskUpdate_Start_FailsClosed_WhenDependencyTargetsNonTask()
    {
        var workspace = CreateWorkspaceCopy();
        // Modify tasks.xml to add a dependency targeting a requirement instead of a task
        var tasksPath = Path.Combine(workspace, "20260823-xpath-core", "tasks.xml");
        var xdoc = XDocument.Load(tasksPath);
        var task = xdoc.Root!.Elements("task").First(t => t.Attribute("id")?.Value == "20260823-task-task-history");
        task.Element("origin")!.AddAfterSelf(new XElement("dependencies",
            new XElement("ref",
                new XAttribute("scope", "iteration"),
                new XAttribute("target", "20260823-req-iteration-discovery"),
                new XAttribute("relation", "depends-on"))));
        xdoc.Save(tasksPath);

        var requestXml = """
<task-update
  id="20260823T050000Z-update-start-history"
  transition="start"
  actor="codex"
  occurred_at="2026-08-23T05:00:00Z">
  <records>
    <record
      id="20260823T050000Z-record-history-start"
      kind="start"
      status="informational"
      created_at="2026-08-23T05:00:00Z"
      actor="codex">
      <summary>Starting task history targeting non-task dependency.</summary>
    </record>
  </records>
</task-update>
""";

        var (success, envelope, diagnostics) = TaskUpdater.Update(
            workspace,
            "20260823-xpath-core",
            "20260823-task-task-history",
            9,
            requestXml);

        Assert.IsFalse(success);
        Assert.IsNull(envelope);
        Assert.IsTrue(diagnostics.Any(d => d.Code == DiagnosticCodes.InvalidReferenceTargetType),
            "Expected InvalidReferenceTargetType diagnostic when dependency targets a non-task element.");
    }

    [TestMethod]
    public void TaskUpdate_Start_FailsClosed_WhenDocumentScopeViolated()
    {
        var workspace = CreateWorkspaceCopy();
        CreateExternalTerminalIteration(workspace, "20260824-feature", "20260824-task-done");

        // Reference 20260824-task-done from 20260823-task-task-history with scope="document" (which is false, it is in another doc)
        var tasksPath = Path.Combine(workspace, "20260823-xpath-core", "tasks.xml");
        var xdoc = XDocument.Load(tasksPath);
        var task = xdoc.Root!.Elements("task").First(t => t.Attribute("id")?.Value == "20260823-task-task-history");
        task.Element("origin")!.AddAfterSelf(new XElement("dependencies",
            new XElement("ref",
                new XAttribute("scope", "document"),
                new XAttribute("target", "20260824-task-done"),
                new XAttribute("relation", "depends-on"))));
        xdoc.Save(tasksPath);

        var requestXml = """
<task-update
  id="20260823T050000Z-update-start-history"
  transition="start"
  actor="codex"
  occurred_at="2026-08-23T05:00:00Z">
  <records>
    <record
      id="20260823T050000Z-record-history-start"
      kind="start"
      status="informational"
      created_at="2026-08-23T05:00:00Z"
      actor="codex">
      <summary>Starting task history with scope violation.</summary>
    </record>
  </records>
</task-update>
""";

        var (success, envelope, diagnostics) = TaskUpdater.Update(
            workspace,
            "20260823-xpath-core",
            "20260823-task-task-history",
            9,
            requestXml);

        Assert.IsFalse(success);
        Assert.IsTrue(diagnostics.Any(d => d.Code == DiagnosticCodes.ReferenceScopeViolation),
            "Expected ReferenceScopeViolation when declaring scope='document' for task in another iteration.");
    }

    [TestMethod]
    public void TaskUpdate_Start_FailsClosed_WhenDependencyScopeIsBroaderThanNecessary()
    {
        var workspace = CreateWorkspaceCopy();
        var tasksPath = Path.Combine(workspace, "20260823-xpath-core", "tasks.xml");
        var document = XDocument.Load(tasksPath);
        var task = document.Root!.Elements("task").First(element => element.Attribute("id")?.Value == "20260823-task-task-history");
        task.Element("origin")!.AddAfterSelf(new XElement("dependencies",
            new XElement("ref",
                new XAttribute("scope", "project"),
                new XAttribute("target", "20260823-task-xpath-projection"),
                new XAttribute("relation", "depends-on"))));
        document.Save(tasksPath);

        var requestXml = """
<task-update id="20260823T050000Z-update-start-history-wide-scope" transition="start" actor="codex" occurred_at="2026-08-23T05:00:00Z">
  <records><record id="20260823T050000Z-record-history-wide-scope" kind="start" status="informational" created_at="2026-08-23T05:00:00Z" actor="codex"><summary>Attempting start with a broader-than-necessary dependency scope.</summary></record></records>
</task-update>
""";

        var (success, envelope, diagnostics) = TaskUpdater.Update(
            workspace,
            "20260823-xpath-core",
            "20260823-task-task-history",
            9,
            requestXml);

        Assert.IsFalse(success);
        Assert.IsNull(envelope);
        Assert.IsTrue(diagnostics.Any(diagnostic => diagnostic.Code == DiagnosticCodes.ReferenceScopeNotNarrowest));
    }

    [TestMethod]
    public void TaskUpdate_Start_Succeeds_WhenCrossIterationDependencyIsTerminal()
    {
        var workspace = CreateWorkspaceCopy();
        CreateExternalTerminalIteration(workspace, "20260824-feature", "20260824-task-done");

        // Reference 20260824-task-done from 20260823-task-task-history with scope="project"
        var tasksPath = Path.Combine(workspace, "20260823-xpath-core", "tasks.xml");
        var xdoc = XDocument.Load(tasksPath);
        var task = xdoc.Root!.Elements("task").First(t => t.Attribute("id")?.Value == "20260823-task-task-history");
        task.Element("origin")!.AddAfterSelf(new XElement("dependencies",
            new XElement("ref",
                new XAttribute("scope", "project"),
                new XAttribute("target", "20260824-task-done"),
                new XAttribute("relation", "depends-on"))));
        xdoc.Save(tasksPath);

        var requestXml = """
<task-update
  id="20260823T050000Z-update-start-history"
  transition="start"
  actor="codex"
  occurred_at="2026-08-23T05:00:00Z">
  <records>
    <record
      id="20260823T050000Z-record-history-start"
      kind="start"
      status="informational"
      created_at="2026-08-23T05:00:00Z"
      actor="codex">
      <summary>Starting task history with satisfied cross-iteration project dependency.</summary>
    </record>
  </records>
</task-update>
""";

        var (success, envelope, diagnostics) = TaskUpdater.Update(
            workspace,
            "20260823-xpath-core",
            "20260823-task-task-history",
            9,
            requestXml);

        Assert.IsTrue(success, $"Update failed with: {string.Join("; ", diagnostics.Select(d => d.Message))}");
        Assert.IsNotNull(envelope);
    }

    [TestMethod]
    public void DependencyGate_DeduplicatesExternalDocumentReadPreconditions()
    {
        var workspace = CreateWorkspaceCopy();
        CreateExternalTerminalIteration(workspace, "20260824-feature", "20260824-task-done");
        var externalTasksPath = Path.Combine(workspace, "20260824-feature", "tasks.xml");
        var externalDocument = XDocument.Load(externalTasksPath);
        var secondTask = new XElement(externalDocument.Root!.Elements("task").Single());
        secondTask.SetAttributeValue("id", "20260824-task-done-two");
        secondTask.Element("acceptance")!.Element("criterion")!.SetAttributeValue("id", "20260824-crit-done-two");
        var completionRecord = secondTask.Element("records")!.Element("record")!;
        completionRecord.SetAttributeValue("id", "20260824-completion-two");
        completionRecord.Element("covers")!.Element("ref")!.SetAttributeValue("target", "20260824-crit-done-two");
        externalDocument.Root.Add(secondTask);
        externalDocument.Save(externalTasksPath);

        var localTasksPath = Path.Combine(workspace, "20260823-xpath-core", "tasks.xml");
        var localDocument = XDocument.Load(localTasksPath);
        var localTask = localDocument.Root!.Elements("task").First(element => element.Attribute("id")?.Value == "20260823-task-task-history");
        localTask.Element("origin")!.AddAfterSelf(new XElement("dependencies",
            new XElement("ref", new XAttribute("scope", "project"), new XAttribute("target", "20260824-task-done"), new XAttribute("relation", "depends-on")),
            new XElement("ref", new XAttribute("scope", "project"), new XAttribute("target", "20260824-task-done-two"), new XAttribute("relation", "depends-on"))));

        var (satisfied, diagnostics, readPreconditions) = TaskDependencyGate.EvaluateTaskDependencies(
            workspace,
            "20260823-task-task-history",
            localTask,
            "20260823-xpath-core/tasks.xml");

        Assert.IsTrue(satisfied, string.Join("; ", diagnostics.Select(diagnostic => diagnostic.Message)));
        Assert.AreEqual(1, readPreconditions.Count);
        Assert.AreEqual("20260824-feature/tasks.xml", readPreconditions[0].RelativePath);
        Assert.AreEqual(1, readPreconditions[0].ExpectedRevision);
    }

    [TestMethod]
    public void TaskUpdate_Resume_FailsClosed_WhenDependencyIsNonTerminal()
    {
        var workspace = CreateWorkspaceCopy();
        // Modify 20260823-task-atomic-update to blocked status
        var tasksPath = Path.Combine(workspace, "20260823-xpath-core", "tasks.xml");
        var xdoc = XDocument.Load(tasksPath);
        var task = xdoc.Root!.Elements("task").First(t => t.Attribute("id")?.Value == "20260823-task-atomic-update");
        task.SetAttributeValue("status", "blocked");
        task.SetAttributeValue("started_at", "2026-08-23T03:30:00Z");
        xdoc.Save(tasksPath);

        // Try to resume while dependency 20260823-task-xpath-projection is still in-progress
        var requestXml = """
<task-update
  id="20260823T050000Z-update-resume-atomic"
  transition="resume"
  actor="codex"
  occurred_at="2026-08-23T05:00:00Z">
  <records>
    <record
      id="20260823T050000Z-record-atomic-resume"
      kind="decision"
      status="informational"
      created_at="2026-08-23T05:00:00Z"
      actor="codex">
      <summary>Attempting to resume task with non-terminal dependency.</summary>
    </record>
  </records>
</task-update>
""";

        var (success, envelope, diagnostics) = TaskUpdater.Update(
            workspace,
            "20260823-xpath-core",
            "20260823-task-atomic-update",
            9,
            requestXml);

        Assert.IsFalse(success);
        Assert.IsTrue(diagnostics.Any(d => d.Code == DiagnosticCodes.TaskTransitionConflict && d.Message.Contains("non-terminal status")),
            "Expected TaskTransitionConflict on resume when dependency is non-terminal.");
    }

    #endregion

    #region Acceptance 2: Actionable Task Selection (TaskNext)

    [TestMethod]
    public void TaskNext_ReturnsActiveInProgressTaskFirst()
    {
        var workspace = CreateWorkspaceCopy();
        var (success, result, diagnostics) = TaskNext.SelectNext(workspace, "20260823-xpath-core");

        Assert.IsTrue(success);
        Assert.AreEqual(0, diagnostics.Count);
        Assert.IsNotNull(result);
        Assert.IsTrue(result.HasTask);
        Assert.AreEqual("20260823-task-xpath-projection", result.Task!.Id);
        Assert.AreEqual("in-progress", result.Task.Status);
    }

    [TestMethod]
    public void TaskNext_SelectsReadyPendingTask_WhenNoTaskIsActive()
    {
        var workspace = CreateWorkspaceCopy();
        // Mark 20260823-task-xpath-projection as done
        var tasksPath = Path.Combine(workspace, "20260823-xpath-core", "tasks.xml");
        var xdoc = XDocument.Load(tasksPath);
        var activeTask = xdoc.Root!.Elements("task").First(t => t.Attribute("id")?.Value == "20260823-task-xpath-projection");
        activeTask.SetAttributeValue("status", "done");
        activeTask.SetAttributeValue("completed_at", "2026-08-23T04:30:00Z");
        xdoc.Save(tasksPath);

        var (success, result, diagnostics) = TaskNext.SelectNext(workspace, "20260823-xpath-core");

        Assert.IsTrue(success);
        Assert.IsNotNull(result);
        Assert.IsTrue(result.HasTask);
        // Next ready pending task is 20260823-task-task-history (no dependencies)
        Assert.AreEqual("20260823-task-task-history", result.Task!.Id);
        Assert.AreEqual("pending", result.Task.Status);
    }

    [TestMethod]
    public void TaskNext_SelectsPendingTaskWithSatisfiedDependencies()
    {
        var workspace = CreateWorkspaceCopy();
        // Mark 20260823-task-xpath-projection and 20260823-task-task-history as done
        var tasksPath = Path.Combine(workspace, "20260823-xpath-core", "tasks.xml");
        var xdoc = XDocument.Load(tasksPath);
        xdoc.Root!.Elements("task").First(t => t.Attribute("id")?.Value == "20260823-task-xpath-projection").SetAttributeValue("status", "done");
        xdoc.Root!.Elements("task").First(t => t.Attribute("id")?.Value == "20260823-task-task-history").SetAttributeValue("status", "done");
        xdoc.Save(tasksPath);

        var (success, result, diagnostics) = TaskNext.SelectNext(workspace, "20260823-xpath-core");

        Assert.IsTrue(success);
        Assert.IsNotNull(result);
        Assert.IsTrue(result.HasTask);
        // 20260823-task-atomic-update depends on 20260823-task-xpath-projection which is now done!
        Assert.AreEqual("20260823-task-atomic-update", result.Task!.Id);
        Assert.AreEqual("pending", result.Task.Status);
    }

    [TestMethod]
    public void TaskNext_ReturnsNoActionable_WhenAllTasksCompleted()
    {
        var workspace = CreateWorkspaceCopy();
        // Mark all tasks done
        var tasksPath = Path.Combine(workspace, "20260823-xpath-core", "tasks.xml");
        var xdoc = XDocument.Load(tasksPath);
        foreach (var t in xdoc.Root!.Elements("task"))
        {
            t.SetAttributeValue("status", "done");
        }
        xdoc.Save(tasksPath);

        var (success, result, diagnostics) = TaskNext.SelectNext(workspace, "20260823-xpath-core");

        Assert.IsTrue(success);
        Assert.IsNotNull(result);
        Assert.IsFalse(result.HasTask);
        Assert.IsTrue(result.Reason.Contains("All tasks"));
    }

    [TestMethod]
    public void TaskNext_FailsClosed_WhenPendingTaskHasDanglingDependency()
    {
        var workspace = CreateWorkspaceCopy();
        var tasksPath = Path.Combine(workspace, "20260823-xpath-core", "tasks.xml");
        var document = XDocument.Load(tasksPath);
        var activeTask = document.Root!.Elements("task").First(element => element.Attribute("id")?.Value == "20260823-task-xpath-projection");
        activeTask.SetAttributeValue("status", "done");
        var pendingTask = document.Root.Elements("task").First(element => element.Attribute("id")?.Value == "20260823-task-task-history");
        pendingTask.Element("origin")!.AddAfterSelf(new XElement("dependencies",
            new XElement("ref",
                new XAttribute("scope", "document"),
                new XAttribute("target", "20260823-task-missing"),
                new XAttribute("relation", "depends-on"))));
        document.Save(tasksPath);

        var (success, result, diagnostics) = TaskNext.SelectNext(workspace, "20260823-xpath-core");

        Assert.IsFalse(success);
        Assert.IsNull(result);
        Assert.IsTrue(diagnostics.Any(diagnostic => diagnostic.Code == DiagnosticCodes.DanglingReference));
    }

    #endregion

    #region Acceptance 3 & 4: Task Scope Matcher and Verifier Path Semantics

    private static readonly string[] SampleIncludes1 = new[] { "AGENTS.md", "src/DogdouSpec.Core/Tasks/**", "tests/**" };
    private static readonly string[] SampleIncludes2 = new[] { "src/**", "tests/**" };
    private static readonly string[] SampleExcludes2 = new[] { "src/DogdouSpec.Core/Tasks/Ignored/**", "tests/Ignored/**" };
    private static readonly string[] SampleIncludes3 = new[] { "src/DogdouSpec.?li/*.cs" };
    private static readonly string[] DotPatternArray = new[] { "." };
    private static readonly string[] SecretExcludes = new[] { "secret/**" };
    private static readonly string[] DoubleStarArray = new[] { "**" };
    private static readonly string[] TasksCoreScope = new[] { "src/DogdouSpec.Core/Tasks/**" };
    private static readonly string[] TraversalPaths = new[] { "../escaping/path.cs" };
    private static readonly string[] AbsolutePaths = new[] { "/absolute/path.cs" };
    private static readonly string[] TrailingDotPaths = new[] { "src/unsafe.cs." };
    private static readonly string[] TrailingSpacePaths = new[] { "src/unsafe.cs " };
    private static readonly string[] SingleFooPath = new[] { "src/Foo.cs" };
    private static readonly string[] GlobalSrcIncludes = new[] { "src/**" };
    private static readonly string[] PrivateScopePaths = new[] { "src/private/**" };
    private static readonly string[] MiddleDoubleStarIncludes = new[] { "**/Tasks/*.cs" };

    [TestMethod]
    public void ScopeMatcher_ExactAndWildcardInclude_Matches()
    {
        var scopes = new[]
        {
            new DeclaredRepositoryScope(".", SampleIncludes1, Array.Empty<string>())
        };

        Assert.IsTrue(TaskScopeMatcher.IsPathInScope("AGENTS.md", scopes));
        Assert.IsTrue(TaskScopeMatcher.IsPathInScope("src/DogdouSpec.Core/Tasks/TaskUpdater.cs", scopes));
        Assert.IsTrue(TaskScopeMatcher.IsPathInScope("src/DogdouSpec.Core/Tasks/Sub/Foo.cs", scopes));
        Assert.IsTrue(TaskScopeMatcher.IsPathInScope("tests/DogdouSpec.Core.Tests/DiscoveryTests.cs", scopes));
        Assert.IsFalse(TaskScopeMatcher.IsPathInScope("src/DogdouSpec.Cli/Program.cs", scopes));
        Assert.IsFalse(TaskScopeMatcher.IsPathInScope("README.md", scopes));
    }

    [TestMethod]
    public void ScopeMatcher_ExcludePrecedence_ExcludeWinsOverInclude()
    {
        var scopes = new[]
        {
            new DeclaredRepositoryScope(".",
                SampleIncludes2,
                SampleExcludes2)
        };

        Assert.IsTrue(TaskScopeMatcher.IsPathInScope("src/DogdouSpec.Core/Tasks/TaskUpdater.cs", scopes));
        Assert.IsTrue(TaskScopeMatcher.IsPathInScope("tests/DogdouSpec.Core.Tests/DiscoveryTests.cs", scopes));
        // Excluded files must be out-of-scope even though they match include "src/**"
        Assert.IsFalse(TaskScopeMatcher.IsPathInScope("src/DogdouSpec.Core/Tasks/Ignored/Test.cs", scopes));
        Assert.IsFalse(TaskScopeMatcher.IsPathInScope("tests/Ignored/Foo.cs", scopes));
    }

    [TestMethod]
    public void ScopeMatcher_ExcludeIsGlobalAcrossRepositoryBlocks_AndMiddleDoubleStarMatchesSegments()
    {
        var globalExcludeScopes = new[]
        {
            new DeclaredRepositoryScope(".", GlobalSrcIncludes, PrivateScopePaths),
            new DeclaredRepositoryScope(".", PrivateScopePaths, Array.Empty<string>())
        };
        var middleDoubleStarScopes = new[]
        {
            new DeclaredRepositoryScope("src", MiddleDoubleStarIncludes, Array.Empty<string>())
        };

        Assert.IsFalse(TaskScopeMatcher.IsPathInScope("src/private/Secret.cs", globalExcludeScopes));
        Assert.IsTrue(TaskScopeMatcher.IsPathInScope("src/Tasks/TaskUpdater.cs", middleDoubleStarScopes));
        Assert.IsTrue(TaskScopeMatcher.IsPathInScope("src/a/b/Tasks/TaskUpdater.cs", middleDoubleStarScopes));
        Assert.IsFalse(TaskScopeMatcher.IsPathInScope("src/a/Tasks/nested/TaskUpdater.cs", middleDoubleStarScopes));
        Assert.IsTrue(TaskScopeMatcher.IsPathInScope("src/./Tasks/TaskUpdater.cs", middleDoubleStarScopes));
    }

    [TestMethod]
    public void ScopeMatcher_SingleCharAndSegmentWildcards()
    {
        var scopes = new[]
        {
            new DeclaredRepositoryScope(".",
                SampleIncludes3,
                Array.Empty<string>())
        };

        Assert.IsTrue(TaskScopeMatcher.IsPathInScope("src/DogdouSpec.Cli/Program.cs", scopes));
        Assert.IsFalse(TaskScopeMatcher.IsPathInScope("src/DogdouSpec.Core/Tasks/TaskUpdater.cs", scopes));
        Assert.IsFalse(TaskScopeMatcher.IsPathInScope("src/DogdouSpec.Cli/Sub/Nested.cs", scopes));
    }

    [TestMethod]
    public void ScopeMatcher_BroadPatterns_DotAndDoubleStar()
    {
        var dotScopes = new[]
        {
            new DeclaredRepositoryScope(".", DotPatternArray, SecretExcludes)
        };

        Assert.IsTrue(TaskScopeMatcher.IsPathInScope("src/Foo.cs", dotScopes));
        Assert.IsTrue(TaskScopeMatcher.IsPathInScope("README.md", dotScopes));
        Assert.IsFalse(TaskScopeMatcher.IsPathInScope("secret/key.pem", dotScopes));

        var doubleStarScopes = new[]
        {
            new DeclaredRepositoryScope(".", DoubleStarArray, Array.Empty<string>())
        };

        Assert.IsTrue(TaskScopeMatcher.IsPathInScope("src/Foo.cs", doubleStarScopes));
        Assert.IsTrue(TaskScopeMatcher.IsPathInScope("a/b/c/d/e.txt", doubleStarScopes));
    }

    [TestMethod]
    public void ScopeMatcher_WindowsCaseBehavior()
    {
        var scopes = new[]
        {
            new DeclaredRepositoryScope(".", TasksCoreScope, Array.Empty<string>())
        };

        // Case-insensitive check (Windows behavior)
        Assert.IsTrue(TaskScopeMatcher.IsPathInScope("SRC/dogdouspec.core/tasks/taskupdater.cs", scopes, forceCaseInsensitive: true));
        // Case-sensitive check
        Assert.IsFalse(TaskScopeMatcher.IsPathInScope("SRC/dogdouspec.core/tasks/taskupdater.cs", scopes, forceCaseInsensitive: false));
    }

    [TestMethod]
    public void ScopeVerifier_ValidAndInvalidPaths_PartitionsCorrectly()
    {
        var workspace = CreateWorkspaceCopy();
        // 20260823-task-xpath-projection has scope: src/DogdouSpec.Core/**, tests/DogdouSpec.Core.Tests/**, docs/**
        var explicitPaths = new[]
        {
            "src/DogdouSpec.Core/XPath/XPathQueryEngine.cs",
            "tests/DogdouSpec.Core.Tests/XPathCoreTests.cs",
            "src/DogdouSpec.Cli/Program.cs" // Out of scope
        };

        var (success, result, diagnostics) = TaskScopeVerifier.VerifyScope(
            workspace,
            "20260823-task-xpath-projection",
            "20260823-xpath-core",
            explicitPaths);

        Assert.IsTrue(success);
        Assert.AreEqual(0, diagnostics.Count);
        Assert.IsNotNull(result);
        Assert.IsFalse(result.IsValid); // Violations present
        Assert.AreEqual(2, result.InScopePaths.Count);
        Assert.AreEqual(1, result.OutOfScopePaths.Count);
        Assert.AreEqual("src/DogdouSpec.Cli/Program.cs", result.OutOfScopePaths[0]);
    }

    [TestMethod]
    public void ScopeVerifier_RejectsTraversalAndAbsolutePaths()
    {
        var workspace = CreateWorkspaceCopy();
        var (success1, _, diags1) = TaskScopeVerifier.VerifyScope(
            workspace,
            "20260823-task-xpath-projection",
            "20260823-xpath-core",
            TraversalPaths);

        Assert.IsFalse(success1);
        Assert.IsTrue(diags1.Any(d => d.Code == DiagnosticCodes.PathTraversalDetected));

        var (success2, _, diags2) = TaskScopeVerifier.VerifyScope(
            workspace,
            "20260823-task-xpath-projection",
            "20260823-xpath-core",
            AbsolutePaths);

        Assert.IsFalse(success2);
        Assert.IsTrue(diags2.Any(d => d.Code == DiagnosticCodes.PathEscapeDetected));

        var (success3, _, diags3) = TaskScopeVerifier.VerifyScope(
            workspace,
            "20260823-task-xpath-projection",
            "20260823-xpath-core",
            TrailingDotPaths);

        Assert.IsFalse(success3);
        Assert.IsTrue(diags3.Any(d => d.Code == DiagnosticCodes.InvalidPath));

        var (success4, _, diags4) = TaskScopeVerifier.VerifyScope(
            workspace,
            "20260823-task-xpath-projection",
            "20260823-xpath-core",
            TrailingSpacePaths);

        Assert.IsFalse(success4);
        Assert.IsTrue(diags4.Any(d => d.Code == DiagnosticCodes.InvalidPath));
    }

    [TestMethod]
    public void ScopeVerifier_MutuallyExclusiveInputValidation()
    {
        var workspace = CreateWorkspaceCopy();
        // Providing both --path and --git-ref must fail
        var (success, _, diags) = TaskScopeVerifier.VerifyScope(
            workspace,
            "20260823-task-xpath-projection",
            "20260823-xpath-core",
            explicitPaths: SingleFooPath,
            gitRef: "HEAD");

        Assert.IsFalse(success);
        Assert.IsTrue(diags.Any(d => d.Code == DiagnosticCodes.InvalidArgument));
    }

    [TestMethod]
    public void ScopeVerifier_GitModes_AreTrackedSafeAndRejectOptionLikeRevision()
    {
        var workspace = CreateWorkspaceCopy();
        var repositoryRoot = Directory.GetParent(workspace)!.FullName;
        var inScopePath = Path.Combine(repositoryRoot, "src", "DogdouSpec.Core", "XPath", "InScope.cs");
        var outOfScopePath = Path.Combine(repositoryRoot, "src", "DogdouSpec.Cli", "OutOfScope.cs");
        var untrackedPath = Path.Combine(repositoryRoot, "untracked-out-of-scope.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(inScopePath)!);
        Directory.CreateDirectory(Path.GetDirectoryName(outOfScopePath)!);
        File.WriteAllText(inScopePath, "// initial\n");
        File.WriteAllText(outOfScopePath, "// initial\n");

        RunGit(repositoryRoot, "init");
        RunGit(repositoryRoot, "config", "user.email", "scope-tests@example.invalid");
        RunGit(repositoryRoot, "config", "user.name", "Scope Tests");
        RunGit(repositoryRoot, "add", "--", ".");
        RunGit(repositoryRoot, "commit", "-m", "Initial fixture");

        File.AppendAllText(inScopePath, "// changed\n");
        File.AppendAllText(outOfScopePath, "// changed\n");
        File.WriteAllText(untrackedPath, "untracked\n");

        var (refSuccess, refResult, refDiagnostics) = TaskScopeVerifier.VerifyScope(
            workspace,
            "20260823-task-xpath-projection",
            "20260823-xpath-core",
            explicitPaths: Array.Empty<string>(),
            gitRef: "HEAD");

        Assert.IsTrue(refSuccess, string.Join("; ", refDiagnostics.Select(diagnostic => diagnostic.Message)));
        Assert.IsNotNull(refResult);
        Assert.AreEqual(1, refResult.InScopePaths.Count);
        Assert.AreEqual(1, refResult.OutOfScopePaths.Count);
        Assert.IsFalse(refResult.InScopePaths.Concat(refResult.OutOfScopePaths).Any(path => path.Contains("untracked", StringComparison.Ordinal)));

        RunGit(repositoryRoot, "add", "--", "src/DogdouSpec.Core/XPath/InScope.cs", "src/DogdouSpec.Cli/OutOfScope.cs");
        RunGit(repositoryRoot, "commit", "-m", "Add tracked changes");

        var (mergeBaseSuccess, mergeBaseResult, mergeBaseDiagnostics) = TaskScopeVerifier.VerifyScope(
            workspace,
            "20260823-task-xpath-projection",
            "20260823-xpath-core",
            gitRange: "HEAD^...HEAD");

        Assert.IsTrue(mergeBaseSuccess, string.Join("; ", mergeBaseDiagnostics.Select(diagnostic => diagnostic.Message)));
        Assert.IsNotNull(mergeBaseResult);
        Assert.AreEqual(1, mergeBaseResult.InScopePaths.Count);
        Assert.AreEqual(1, mergeBaseResult.OutOfScopePaths.Count);

        var (rangeSuccess, rangeResult, rangeDiagnostics) = TaskScopeVerifier.VerifyScope(
            workspace,
            "20260823-task-xpath-projection",
            "20260823-xpath-core",
            gitRange: "HEAD..HEAD");

        Assert.IsTrue(rangeSuccess, string.Join("; ", rangeDiagnostics.Select(diagnostic => diagnostic.Message)));
        Assert.IsNotNull(rangeResult);
        Assert.AreEqual(0, rangeResult.InScopePaths.Count);
        Assert.AreEqual(0, rangeResult.OutOfScopePaths.Count);

        var (unsafeSuccess, _, unsafeDiagnostics) = TaskScopeVerifier.VerifyScope(
            workspace,
            "20260823-task-xpath-projection",
            "20260823-xpath-core",
            gitRef: "--no-index");

        Assert.IsFalse(unsafeSuccess);
        Assert.IsTrue(unsafeDiagnostics.Any(diagnostic => diagnostic.Code == DiagnosticCodes.InvalidArgument));
    }

    #endregion
}
