using System.Diagnostics;
using System.Runtime.CompilerServices;
using DogdouSpec.Cli;
using DogdouSpec.Core.Diagnostics;

namespace DogdouSpec.Cli.Tests;

[TestClass]
public sealed class TaskDependencyAndScopeCliTests
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
        _tempDir = Path.Combine(Path.GetTempPath(), "DogdouSpec_DepScopeCliTests_" + Guid.NewGuid().ToString("N"));
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

    private static (int ExitCode, string Stdout, string Stderr) RunCli(params string[] args)
    {
        var originalOut = Console.Out;
        var originalErr = Console.Error;
        var originalIn = Console.In;

        using var outSw = new StringWriter();
        using var errSw = new StringWriter();
        using var inSr = new StringReader(string.Empty);

        try
        {
            Console.SetOut(outSw);
            Console.SetError(errSw);
            Console.SetIn(inSr);

            var exitCode = Program.Main(args);
            return (exitCode, outSw.ToString(), errSw.ToString());
        }
        finally
        {
            Console.SetOut(originalOut);
            Console.SetError(originalErr);
            Console.SetIn(originalIn);
        }
    }

    [TestMethod]
    public void TaskNextCli_XmlAndHumanOutput()
    {
        var workspace = CreateWorkspaceCopy();

        // 1. XML output
        var (exitCodeXml, stdoutXml, stderrXml) = RunCli(
            "task", "next",
            "--iteration", "20260823-xpath-core",
            "--workspace-root", workspace,
            "--format", "xml");

        Assert.AreEqual(0, exitCodeXml, $"Stderr: {stderrXml}");
        Assert.IsTrue(stdoutXml.Contains("<task-next"), "Expected <task-next> element in XML output");
        Assert.IsTrue(stdoutXml.Contains("20260823-task-xpath-projection"), "Expected active task in XML output");

        // 2. Human output
        var (exitCodeHuman, stdoutHuman, stderrHuman) = RunCli(
            "task", "next",
            "--iteration", "20260823-xpath-core",
            "--workspace-root", workspace,
            "--format", "human");

        Assert.AreEqual(0, exitCodeHuman, $"Stderr: {stderrHuman}");
        Assert.IsTrue(stdoutHuman.Contains("Selected Task: 20260823-task-xpath-projection"), "Expected task ID in human output");
        Assert.IsTrue(stdoutHuman.Contains("Status: in-progress"), "Expected status in human output");
    }

    [TestMethod]
    public void TaskScopeCli_InScopePaths_ExitsZero()
    {
        var workspace = CreateWorkspaceCopy();

        var (exitCode, stdout, stderr) = RunCli(
            "task", "scope",
            "--task", "20260823-task-xpath-projection",
            "--iteration", "20260823-xpath-core",
            "--path", "src/DogdouSpec.Core/XPath/XPathQueryEngine.cs",
            "--path", "tests/DogdouSpec.Core.Tests/XPathCoreTests.cs",
            "--workspace-root", workspace,
            "--format", "xml");

        Assert.AreEqual(0, exitCode, $"Stderr: {stderr}");
        Assert.IsTrue(stdout.Contains("valid=\"true\""), "Expected valid='true' in XML");
        Assert.IsTrue(stdout.Contains("in_scope_count=\"2\""), "Expected 2 in-scope paths");
        Assert.IsTrue(stdout.Contains("out_of_scope_count=\"0\""), "Expected 0 out-of-scope paths");
    }

    [TestMethod]
    public void TaskScopeCli_OutOfScopePaths_ExitsOne()
    {
        var workspace = CreateWorkspaceCopy();

        var (exitCode, stdout, stderr) = RunCli(
            "task", "scope",
            "--task", "20260823-task-xpath-projection",
            "--iteration", "20260823-xpath-core",
            "--path", "src/DogdouSpec.Core/XPath/XPathQueryEngine.cs",
            "--path", "src/DogdouSpec.Cli/Program.cs",
            "--workspace-root", workspace,
            "--format", "human");

        Assert.AreEqual(1, exitCode, "Expected exit code 1 when scope violations are detected.");
        Assert.IsTrue(stdout.Contains("Result: VIOLATION"), "Expected VIOLATION in human output");
        Assert.IsTrue(stdout.Contains("src/DogdouSpec.Cli/Program.cs"), "Expected out-of-scope path to be reported");
    }

    [TestMethod]
    public void TaskScopeCli_InvalidArguments_ExitsTwo()
    {
        var workspace = CreateWorkspaceCopy();

        // Mutually exclusive inputs: --path and --git-ref
        var (exitCode, _, stderr) = RunCli(
            "task", "scope",
            "--task", "20260823-task-xpath-projection",
            "--iteration", "20260823-xpath-core",
            "--path", "src/Foo.cs",
            "--git-ref", "HEAD",
            "--workspace-root", workspace);

        Assert.AreEqual(2, exitCode);
        Assert.IsTrue(stderr.Contains("INVALID_ARGUMENT") || stderr.Contains("not multiple"));
    }

    [TestMethod]
    public void TaskScopeCli_GitRef_DoesNotConflictWithImplicitEmptyPathOption()
    {
        var workspace = CreateWorkspaceCopy();
        var repositoryRoot = Directory.GetParent(workspace)!.FullName;
        var inScopePath = Path.Combine(repositoryRoot, "src", "DogdouSpec.Core", "XPath", "Tracked.cs");
        Directory.CreateDirectory(Path.GetDirectoryName(inScopePath)!);
        File.WriteAllText(inScopePath, "// initial\n");

        RunGit(repositoryRoot, "init");
        RunGit(repositoryRoot, "config", "user.email", "scope-cli-tests@example.invalid");
        RunGit(repositoryRoot, "config", "user.name", "Scope CLI Tests");
        RunGit(repositoryRoot, "add", "--", ".");
        RunGit(repositoryRoot, "commit", "-m", "Initial fixture");
        File.AppendAllText(inScopePath, "// changed\n");

        var (exitCode, stdout, stderr) = RunCli(
            "task", "scope",
            "--task", "20260823-task-xpath-projection",
            "--iteration", "20260823-xpath-core",
            "--git-ref", "HEAD",
            "--workspace-root", workspace,
            "--format", "xml");

        Assert.AreEqual(0, exitCode, $"Stderr: {stderr}");
        Assert.IsTrue(stdout.Contains("src/DogdouSpec.Core/XPath/Tracked.cs", StringComparison.Ordinal));
        Assert.IsFalse(stderr.Contains("multiple sources", StringComparison.OrdinalIgnoreCase));
    }
}
