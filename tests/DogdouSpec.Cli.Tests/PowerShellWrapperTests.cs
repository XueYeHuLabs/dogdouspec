using System.Diagnostics;
using DogdouSpec.Core.Diagnostics;

namespace DogdouSpec.Cli.Tests;

[TestClass]
public sealed class PowerShellWrapperTests
{
    private static string RepoRoot = null!;
    private static string CmdWrapperPath = null!;
    private static string PowerShellExe = null!;

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
        var debugExe = Path.Combine(RepoRoot, "src", "DogdouSpec.Cli", "bin", "Debug", "net10.0", "dogdouspec.exe");
        var releaseExe = Path.Combine(RepoRoot, "src", "DogdouSpec.Cli", "bin", "Release", "net10.0", "dogdouspec.exe");
        CmdWrapperPath = File.Exists(debugExe) ? debugExe : releaseExe;

        var pwshPath = FindExecutableInPath("pwsh.exe") ?? FindExecutableInPath("powershell.exe");
        PowerShellExe = pwshPath ?? "powershell.exe";
    }

    [TestInitialize]
    public void TestInit()
    {
        if (string.IsNullOrWhiteSpace(CmdWrapperPath) || !File.Exists(CmdWrapperPath))
        {
            Assert.Inconclusive($"dogdouspec.exe not found at '{CmdWrapperPath}'. Build the CLI project before running PowerShell wrapper tests.");
        }
    }

    private static string? FindExecutableInPath(string exeName)
    {
        var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        var paths = pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);
        foreach (var p in paths)
        {
            var full = Path.Combine(p, exeName);
            if (File.Exists(full))
            {
                return full;
            }
        }
        return null;
    }

    private static (int ExitCode, string Stdout, string Stderr) RunPowerShellCommand(string script)
    {
        var psi = new ProcessStartInfo
        {
            FileName = PowerShellExe,
            Arguments = $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command \"{script}; exit $LASTEXITCODE\"",
            WorkingDirectory = RepoRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = Process.Start(psi);
        Assert.IsNotNull(process, "Failed to launch PowerShell process.");

        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();

        return (process.ExitCode, stdout, stderr);
    }

    [TestMethod]
    public void PowerShell_SimpleVariableInSingleQuotes_PreservesVariableAndSucceeds()
    {
        var script = $"& '{CmdWrapperPath}' query --workspace-root 'docs\\demos\\v1-core\\.dogdouspec' --document '20260823-xpath-core/tasks.xml' --var 'task_id=20260823-task-xpath-projection' --xpath '//task[@id=$task_id]' --format xml";

        var (exitCode, stdout, stderr) = RunPowerShellCommand(script);

        Assert.AreEqual(0, exitCode, $"Stderr: {stderr}");
        Assert.IsTrue(stdout.Contains("<task id=\"20260823-task-xpath-projection\"", StringComparison.Ordinal));
    }

    [TestMethod]
    public void PowerShell_ComplexProjectionDoubledSingleQuotes_PreservesLiteralsAndVariables()
    {
        var script = $"& '{CmdWrapperPath}' query --workspace-root 'docs\\demos\\v1-core\\.dogdouspec' --document '20260823-xpath-core/tasks.xml' --var 'task_id=20260823-task-xpath-projection' --xpath 'ds:filter(//task[@id=$task_id], ''@id'', ''@status'', ''index'')' --format xml";

        var (exitCode, stdout, stderr) = RunPowerShellCommand(script);

        Assert.AreEqual(0, exitCode, $"Stderr: {stderr}");
        Assert.IsTrue(stdout.Contains("derived=\"true\"", StringComparison.Ordinal));
        Assert.IsTrue(stdout.Contains("<task id=\"20260823-task-xpath-projection\" status=\"in-progress\">", StringComparison.Ordinal));
        Assert.IsTrue(stdout.Contains("<index>", StringComparison.Ordinal));
        Assert.IsFalse(stdout.Contains("<context>", StringComparison.Ordinal));
        Assert.IsFalse(stdout.Contains("<records>", StringComparison.Ordinal));
    }

    [TestMethod]
    public void PowerShell_DoubleQuotesWithBacktickEscape_PreservesVariableAndSucceeds()
    {
        var script = $"& '{CmdWrapperPath}' query --workspace-root 'docs\\demos\\v1-core\\.dogdouspec' --document '20260823-xpath-core/tasks.xml' --var 'task_id=20260823-task-xpath-projection' --xpath \"\"\"ds:filter(//task[@id=`$task_id], '@id', '@status', 'index')\"\"\" --format xml";

        var (exitCode, stdout, stderr) = RunPowerShellCommand(script);

        Assert.AreEqual(0, exitCode, $"Stderr: {stderr}");
        Assert.IsTrue(stdout.Contains("derived=\"true\"", StringComparison.Ordinal));
        Assert.IsTrue(stdout.Contains("<task id=\"20260823-task-xpath-projection\" status=\"in-progress\">", StringComparison.Ordinal));
    }

    [TestMethod]
    public void PowerShell_SearchCommandWithProjection_PreservesLiteralsAndVariables()
    {
        var script = $"& '{CmdWrapperPath}' search --workspace-root 'docs\\demos\\v1-core\\.dogdouspec' --scope project --var 'topic=xpath-extension' --xpath 'ds:filter(//*[@id and index/term[@key=''topic'' and @value=$topic]], ''@id'', ''index'')' --format xml";

        var (exitCode, stdout, stderr) = RunPowerShellCommand(script);

        Assert.AreEqual(0, exitCode, $"Stderr: {stderr}");
        Assert.IsTrue(stdout.Contains("<search scope=\"project\" derived=\"true\">", StringComparison.Ordinal));
        Assert.IsTrue(stdout.Contains("<task id=\"20260823-task-xpath-projection\">", StringComparison.Ordinal));
        Assert.IsFalse(stdout.Contains("<context>", StringComparison.Ordinal));
    }

    [TestMethod]
    public void PowerShell_UnquotedNodeSetMember_FailsWithExitCode2AndEmptyStdout()
    {
        var script = $"& '{CmdWrapperPath}' query --workspace-root 'docs\\demos\\v1-core\\.dogdouspec' --document '20260823-xpath-core/tasks.xml' --xpath 'ds:filter(//task, @id)' --format xml";

        var (exitCode, stdout, stderr) = RunPowerShellCommand(script);

        Assert.AreEqual(2, exitCode, $"Expected exit code 2, got {exitCode}");
        Assert.IsTrue(string.IsNullOrWhiteSpace(stdout), $"Expected empty stdout, got: {stdout}");
        Assert.IsTrue(stderr.Contains(DiagnosticCodes.InvalidArgument, StringComparison.Ordinal));
    }
}
