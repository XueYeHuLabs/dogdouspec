using System.Diagnostics;
using System.Globalization;

namespace DogdouSpec.Cli.Tests;

[TestClass]
public sealed class ReleaseScriptTests
{
    [TestMethod]
    [DataRow(false, 0)]
    [DataRow(true, 0)]
    [DataRow(false, 17)]
    [DataRow(true, 17)]
    public async Task AutoUpload_ReportsTheActualGitHubCommandOutcome(bool existingRelease, int uploadExitCode)
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("The release wrapper requires Windows PowerShell.");
        }

        var repository = new DirectoryInfo(AppContext.BaseDirectory);
        while (repository != null && !File.Exists(Path.Combine(repository.FullName, "DogdouSpec.slnx")))
        {
            repository = repository.Parent;
        }
        Assert.IsNotNull(repository);

        var fixture = Path.Combine(Path.GetTempPath(), "DogdouSpec_Release_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(fixture);
        try
        {
            var scripts = Directory.CreateDirectory(Path.Combine(fixture, "scripts")).FullName;
            File.Copy(Path.Combine(repository.FullName, "scripts", "publish-release.ps1"), Path.Combine(scripts, "publish-release.ps1"));
            var manifests = Directory.CreateDirectory(Path.Combine(fixture, "manifests", "winget")).FullName;
            foreach (var name in new[] { "Vixasol.DogdouSpec.yaml", "Vixasol.DogdouSpec.locale.en-US.yaml", "Vixasol.DogdouSpec.installer.yaml" })
            {
                File.WriteAllText(Path.Combine(manifests, name), "PackageVersion: 1.0.4\nInstallers:\n  - Architecture: x64\n    InstallerUrl: old\n    InstallerSha256: old\n");
            }

            // Replace external tools inside the child session; no compilation, signing or network calls occur.
            File.WriteAllText(Path.Combine(fixture, "gh.cmd"), $$"""
                @echo off
                if "%2"=="view" (
                    {{(existingRelease ? "rem release exists" : "echo release not found 1>&2")}}
                    exit /b {{(existingRelease ? "0" : "1")}}
                )
                echo GITHUB_ACTION=%2
                exit /b {{uploadExitCode.ToString(CultureInfo.InvariantCulture)}}
                """);
            var harness = Path.Combine(fixture, "run.ps1");
            File.WriteAllText(harness, """
                $ErrorActionPreference = 'Stop'
                function global:dotnet {
                    $outputIndex = [Array]::IndexOf([object[]]$args, '-o')
                    $output = [string]$args[$outputIndex + 1]
                    New-Item -ItemType Directory -Path $output -Force | Out-Null
                    [IO.File]::WriteAllText((Join-Path (Get-Location).Path "$output/dogdouspec.exe"), 'fixture executable')
                    $global:LASTEXITCODE = 0
                }
                & (Join-Path $PSScriptRoot 'scripts/publish-release.ps1') -Version 1.0.5 -SkipSigning -AutoUpload
                exit 0
                """);

            var start = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                WorkingDirectory = fixture,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            start.Environment.Remove("PSModulePath");
            start.Environment["PATH"] = fixture + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH");
            foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", harness })
            {
                start.ArgumentList.Add(argument);
            }
            using var process = Process.Start(start);
            Assert.IsNotNull(process);
            var outputTask = process.StandardOutput.ReadToEndAsync();
            var errorTask = process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            try
            {
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                process.Kill(entireProcessTree: true);
                throw;
            }
            var output = await outputTask;
            var error = await errorTask;
            StringAssert.Contains(output, "GITHUB_ACTION=" + (existingRelease ? "upload" : "create"), error);
            if (uploadExitCode == 0)
            {
                Assert.AreEqual(0, process.ExitCode, error);
                StringAssert.Contains(output, "[SUCCESS]");
                Assert.IsTrue(File.Exists(Path.Combine(fixture, "dogdouspec-win-x64.zip")));
                StringAssert.Contains(File.ReadAllText(Path.Combine(manifests, "Vixasol.DogdouSpec.installer.yaml")), "/v1.0.5/");
            }
            else
            {
                Assert.AreNotEqual(0, process.ExitCode, output + error);
                Assert.IsFalse(output.Contains("[SUCCESS]", StringComparison.Ordinal), output);
                StringAssert.Contains(error, uploadExitCode.ToString(CultureInfo.InvariantCulture));
            }
        }
        finally
        {
            Directory.Delete(fixture, recursive: true);
        }
    }
}
