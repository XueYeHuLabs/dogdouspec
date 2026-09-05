using System.Diagnostics;
using System.Text.RegularExpressions;

namespace DogdouSpec.Cli.Tests;

public static class DocSnippetExtractor
{
    public static Dictionary<string, string> ExtractSnippets(string docPath)
    {
        var lines = File.ReadAllLines(docPath);
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        string? currentSnippetId = null;
        var currentSnippetLines = new List<string>();
        bool inCodeBlock = false;
        bool inHereString = false;

        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var trimmed = line.Trim();

            if (!inCodeBlock)
            {
                if (trimmed.StartsWith("```powershell", StringComparison.OrdinalIgnoreCase))
                {
                    if (i + 1 < lines.Length && lines[i + 1].Trim().StartsWith("# snippet:", StringComparison.OrdinalIgnoreCase))
                    {
                        var snippetLine = lines[i + 1].Trim();
                        currentSnippetId = snippetLine.Substring("# snippet:".Length).Trim();
                        inCodeBlock = true;
                        inHereString = false;
                        currentSnippetLines.Clear();
                        i++; // skip snippet comment line
                        continue;
                    }
                }
            }
            else
            {
                if (!inHereString)
                {
                    if (trimmed.StartsWith("```", StringComparison.OrdinalIgnoreCase) && !trimmed.StartsWith("```powershell", StringComparison.OrdinalIgnoreCase))
                    {
                        if (currentSnippetId != null)
                        {
                            dict[currentSnippetId] = string.Join("\r\n", currentSnippetLines);
                        }
                        inCodeBlock = false;
                        currentSnippetId = null;
                        currentSnippetLines.Clear();
                        continue;
                    }

                    if (trimmed.EndsWith("@'", StringComparison.Ordinal) || trimmed.EndsWith("@\"", StringComparison.Ordinal))
                    {
                        inHereString = true;
                    }
                }
                else
                {
                    if (trimmed.StartsWith("'@", StringComparison.Ordinal) || trimmed.StartsWith("\"@", StringComparison.Ordinal))
                    {
                        inHereString = false;
                    }
                }

                currentSnippetLines.Add(line);
            }
        }

        return dict;
    }
}

[TestClass]
public sealed class SkillDeploymentTests
{
    private static string RepoRoot = null!;
    private static string DocPath = null!;
    private static string BuiltCliExe = null!;
    private static string BuiltCliDir = null!;
    private static Dictionary<string, string> DocSnippets = null!;

    private string _tempDir = null!;
    private string _stagingDir = null!;
    private string _cleanSourceRepo = null!;
    private string _sourceCommitSha = null!;

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

        DocPath = Path.Combine(RepoRoot, "docs", "INSTALL_IN_OTHER_REPOSITORY.md");
        Assert.IsTrue(File.Exists(DocPath), $"Documentation file missing at {DocPath}");

        DocSnippets = DocSnippetExtractor.ExtractSnippets(DocPath);

        string[] requiredSnippets =
        [
            "preflight-setup",
            "preflight-checks",
            "tool-publish",
            "tool-verify",
            "tool-install",
            "wrapper-create",
            "workspace-init",
            "skill-install",
            "agents-guide",
            "workspace-validate",
            "rollback-initial",
            "uninstall"
        ];

        foreach (var key in requiredSnippets)
        {
            Assert.IsTrue(DocSnippets.ContainsKey(key), $"Required snippet '{key}' missing from documentation {DocPath}");
        }

        // Locate existing built CLI executable & directory
        var debugDir = Path.Combine(RepoRoot, "src", "DogdouSpec.Cli", "bin", "Debug", "net10.0");
        var candidateExe = Path.Combine(debugDir, "dogdouspec.exe");
        if (File.Exists(candidateExe))
        {
            BuiltCliExe = candidateExe;
            BuiltCliDir = debugDir;
        }
        else
        {
            var releaseDir = Path.Combine(RepoRoot, "src", "DogdouSpec.Cli", "bin", "Release", "net10.0");
            var releaseExe = Path.Combine(releaseDir, "dogdouspec.exe");
            BuiltCliExe = File.Exists(releaseExe) ? releaseExe : candidateExe;
            BuiltCliDir = File.Exists(releaseExe) ? releaseDir : debugDir;
        }
    }

    [TestInitialize]
    public void SetUp()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "DogdouSpec_SkillDeployTests_" + Guid.NewGuid().ToString("N"));
        _stagingDir = Path.Combine(Path.GetTempPath(), "DogdouSpec_SkillStage_" + Guid.NewGuid().ToString("N"));
        _cleanSourceRepo = Path.Combine(_tempDir, "CleanSourceRepo");
        Directory.CreateDirectory(_tempDir);

        // Clone a clean copy of the repo to serve as the clean source repository fixture
        RunProcess("git", $"clone --shared \"{RepoRoot}\" \"{_cleanSourceRepo}\"", _tempDir);
        var globalJsonPath = Path.Combine(RepoRoot, "global.json");
        if (File.Exists(globalJsonPath))
        {
            File.Copy(globalJsonPath, Path.Combine(_cleanSourceRepo, "global.json"), true);
            RunProcess("git", "update-index --assume-unchanged global.json", _cleanSourceRepo);
        }
        _sourceCommitSha = RunProcess("git", "rev-parse HEAD", _cleanSourceRepo).Stdout.Trim();
    }

    [TestCleanup]
    public void TearDown()
    {
        if (Directory.Exists(_tempDir))
        {
            try { Directory.Delete(_tempDir, true); } catch { }
        }
        if (Directory.Exists(_stagingDir))
        {
            try { Directory.Delete(_stagingDir, true); } catch { }
        }
    }

    #region Helper Methods

    private static (int ExitCode, string Stdout, string Stderr) RunProcess(
        string fileName,
        string arguments,
        string workingDirectory,
        IDictionary<string, string>? environment = null,
        int timeoutMs = 30000)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        if (environment != null)
        {
            foreach (var kvp in environment)
            {
                psi.Environment[kvp.Key] = kvp.Value;
            }
        }

        using var process = new Process { StartInfo = psi };
        var stdoutBuilder = new System.Text.StringBuilder();
        var stderrBuilder = new System.Text.StringBuilder();

        using var outputWaitHandle = new AutoResetEvent(false);
        using var errorWaitHandle = new AutoResetEvent(false);

        process.OutputDataReceived += (sender, e) =>
        {
            if (e.Data == null)
            {
                outputWaitHandle.Set();
            }
            else
            {
                stdoutBuilder.AppendLine(e.Data);
            }
        };
        process.ErrorDataReceived += (sender, e) =>
        {
            if (e.Data == null)
            {
                errorWaitHandle.Set();
            }
            else
            {
                stderrBuilder.AppendLine(e.Data);
            }
        };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        if (!process.WaitForExit(timeoutMs))
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch { }

            var outStr = stdoutBuilder.ToString();
            var errStr = stderrBuilder.ToString();
            throw new TimeoutException($"Process '{fileName} {arguments}' timed out after {timeoutMs}ms.\nStdout: {outStr}\nStderr: {errStr}");
        }

        outputWaitHandle.WaitOne(2000);
        errorWaitHandle.WaitOne(2000);

        return (process.ExitCode, stdoutBuilder.ToString(), stderrBuilder.ToString());
    }

    private static string FindPowerShellExe()
    {
        var pwshPath = @"C:\Program Files\PowerShell\7\pwsh.exe";
        if (File.Exists(pwshPath)) return pwshPath;
        return "powershell.exe";
    }

    private (int ExitCode, string Stdout, string Stderr) ExecutePowerShellSnippet(
        string snippetCode,
        string targetRepo,
        string? customSourceRepo = null,
        string? customPinnedCommit = null,
        IDictionary<string, string>? extraEnv = null)
    {
        var sourceRepo = customSourceRepo ?? _cleanSourceRepo;
        var commitSha = customPinnedCommit ?? _sourceCommitSha;

        var functionDefs = @"
function Test-IsSubpath([string]$child, [string]$parent) {
    $normChild = [System.IO.Path]::GetFullPath($child).TrimEnd('\', '/') + '\'
    $normParent = [System.IO.Path]::GetFullPath($parent).TrimEnd('\', '/') + '\'
    return $normChild.StartsWith($normParent, [System.StringComparison]::OrdinalIgnoreCase)
}

function Assert-InsideTarget([string]$path, [string]$targetRoot) {
    $normChild = [System.IO.Path]::GetFullPath($path).TrimEnd('\', '/') + '\'
    $normParent = [System.IO.Path]::GetFullPath($targetRoot).TrimEnd('\', '/') + '\'
    if (-not $normChild.StartsWith($normParent, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw ""[SECURITY ERROR] Path '$path' is outside target repository '$targetRoot'.""
    }
}

function Remove-StagingDirectory([string]$staging, [string]$source, [string]$target, [string]$expectedGuidPath) {
    $normStaging = [System.IO.Path]::GetFullPath($staging)
    $normExpected = [System.IO.Path]::GetFullPath($expectedGuidPath)
    if ($normStaging -ne $normExpected) {
        throw ""[SECURITY ERROR] Staging path does not match expected GUID path created by this run.""
    }
    if ((Test-IsSubpath $normStaging $source) -or (Test-IsSubpath $normStaging $target)) {
        throw ""[SECURITY ERROR] Staging path is unexpectedly inside a repository.""
    }
    if (Test-Path ""$normStaging"") {
        Remove-Item -Recurse -Force ""$normStaging""
        Write-Host ""[OK] Cleaned staging directory: $normStaging""
    }
}

function Test-SkillDirectoryDivergence([string]$dirPath, [string]$sourceDir) {
    if (-not (Test-Path ""$dirPath"")) {
        return @{ Exists = $false; IsDivergent = $false; IsExactMatch = $false; Differences = @() }
    }
    $srcFiles = Get-ChildItem -Recurse -File ""$sourceDir""
    $tgtFiles = Get-ChildItem -Recurse -File ""$dirPath""

    $srcMap = @{}
    foreach ($f in $srcFiles) {
        $rel = $f.FullName.Substring($sourceDir.Length).TrimStart('\', '/') -replace '\\', '/'
        $srcMap[$rel] = (Get-FileHash $f.FullName -Algorithm SHA256).Hash
    }

    $tgtMap = @{}
    foreach ($f in $tgtFiles) {
        $rel = $f.FullName.Substring($dirPath.Length).TrimStart('\', '/') -replace '\\', '/'
        $tgtMap[$rel] = (Get-FileHash $f.FullName -Algorithm SHA256).Hash
    }

    $diffs = @()
    foreach ($rel in $srcMap.Keys) {
        if (-not $tgtMap.ContainsKey($rel)) {
            $diffs += ""Missing standard file: $rel""
        } elseif ($srcMap[$rel] -ne $tgtMap[$rel]) {
            $diffs += ""Modified standard file: $rel""
        }
    }
    foreach ($rel in $tgtMap.Keys) {
        if (-not $srcMap.ContainsKey($rel)) {
            $diffs += ""Extra custom file: $rel""
        }
    }

    $isExact = ($diffs.Count -eq 0)
    return @{
        Exists = $true
        IsDivergent = (-not $isExact)
        IsExactMatch = $isExact
        Differences = $diffs
    }
}
";

        var fullScript = $@"
$ErrorActionPreference = 'Stop'
$SOURCE_REPO = '{sourceRepo.Replace("'", "''")}'
$TARGET_REPO = '{targetRepo.Replace("'", "''")}'
$PINNED_COMMIT = '{commitSha}'
$STAGING_DIR = '{_stagingDir.Replace("'", "''")}'
$EXPECTED_STAGING_PATH = $STAGING_DIR

{functionDefs}

{snippetCode}
";

        var scriptFile = Path.Combine(_tempDir, "run_snippet_" + Guid.NewGuid().ToString("N") + ".ps1");
        File.WriteAllText(scriptFile, fullScript);

        var env = new Dictionary<string, string>
        {
            ["DOGDOUSPEC_PREBUILT_EXE"] = BuiltCliExe,
            ["DOGDOUSPEC_PREBUILT_DIR"] = BuiltCliDir
        };
        if (extraEnv != null)
        {
            foreach (var kvp in extraEnv)
            {
                env[kvp.Key] = kvp.Value;
            }
        }

        var pwshExe = FindPowerShellExe();
        return RunProcess(pwshExe, $"-NoProfile -ExecutionPolicy Bypass -File \"{scriptFile}\"", targetRepo, env);
    }

    private string CreateTargetGitRepo(string repoName)
    {
        var targetPath = Path.Combine(_tempDir, repoName);
        Directory.CreateDirectory(targetPath);
        RunProcess("git", $"init -b main \"{targetPath}\"", targetPath);
        RunProcess("git", "config user.name \"TestUser\"", targetPath);
        RunProcess("git", "config user.email \"test@example.com\"", targetPath);
        return targetPath;
    }

    private static void CommitAllInRepo(string targetPath, string message = "Commit changes")
    {
        RunProcess("git", "add -A", targetPath);
        RunProcess("git", $"commit -m \"{message}\"", targetPath);
    }

    private void SetupExistingInstalledWorkspace(string targetPath)
    {
        // Place wrapper
        var wrapperContent = "@echo off\r\nsetlocal\r\n\"%~dp0tools\\dogdouspec\\dogdouspec.exe\" %*\r\nexit /b %ERRORLEVEL%\r\n";
        File.WriteAllText(Path.Combine(targetPath, "dogdouspec.cmd"), wrapperContent);

        // Place tool
        var toolDir = Path.Combine(targetPath, "tools", "dogdouspec");
        Directory.CreateDirectory(toolDir);
        foreach (var file in Directory.GetFiles(BuiltCliDir))
        {
            File.Copy(file, Path.Combine(toolDir, Path.GetFileName(file)), true);
        }

        // Init workspace via CLI wrapper
        var cmdPath = Path.Combine(targetPath, "dogdouspec.cmd");
        var res = RunProcess("cmd.exe", $"/c \"{cmdPath}\" workspace init --format xml", targetPath);
        Assert.AreEqual(0, res.ExitCode, $"Setup workspace init failed: {res.Stderr}");

        // Ensure installed skill files match source fixture
        InstallDefaultSkillFiles(targetPath);
    }

    private void InstallDefaultSkillFiles(string targetPath)
    {
        var sourceSkillDir = Path.Combine(_cleanSourceRepo, ".agents", "skills", "dogdouspec");
        var targetSkillDir = Path.Combine(targetPath, ".agents", "skills", "dogdouspec");
        Directory.CreateDirectory(Path.Combine(targetSkillDir, "references"));
        File.Copy(Path.Combine(sourceSkillDir, "SKILL.md"), Path.Combine(targetSkillDir, "SKILL.md"), true);
        foreach (var file in Directory.GetFiles(Path.Combine(sourceSkillDir, "references")))
        {
            File.Copy(file, Path.Combine(targetSkillDir, "references", Path.GetFileName(file)), true);
        }
    }

    private void InstallLegacySkillFiles(string targetPath, bool removeDotAgents = false)
    {
        if (removeDotAgents)
        {
            var dotAgents = Path.Combine(targetPath, ".agents");
            if (Directory.Exists(dotAgents))
            {
                Directory.Delete(dotAgents, true);
            }
        }

        var sourceSkillDir = Path.Combine(_cleanSourceRepo, ".agents", "skills", "dogdouspec");
        var targetSkillDir = Path.Combine(targetPath, "skills", "dogdouspec");
        Directory.CreateDirectory(Path.Combine(targetSkillDir, "references"));
        File.Copy(Path.Combine(sourceSkillDir, "SKILL.md"), Path.Combine(targetSkillDir, "SKILL.md"), true);
        foreach (var file in Directory.GetFiles(Path.Combine(sourceSkillDir, "references")))
        {
            File.Copy(file, Path.Combine(targetSkillDir, "references", Path.GetFileName(file)), true);
        }
    }

    #endregion

    [TestMethod]
    public void CheckedInSkill_DefaultLayout_ExistsAndContainsAllRequiredFiles()
    {
        var skillDir = Path.Combine(RepoRoot, ".agents", "skills", "dogdouspec");
        Assert.IsTrue(Directory.Exists(skillDir), $"Skill directory missing at {skillDir}");

        var skillMd = Path.Combine(skillDir, "SKILL.md");
        Assert.IsTrue(File.Exists(skillMd), $"SKILL.md missing at {skillMd}");
        Assert.IsTrue(File.ReadAllText(skillMd).Contains("name: dogdouspec"), "SKILL.md must contain frontmatter");

        var refDir = Path.Combine(skillDir, "references");
        Assert.IsTrue(Directory.Exists(refDir), $"references/ missing at {refDir}");

        var authorityMd = Path.Combine(refDir, "authority.md");
        var mutationsMd = Path.Combine(refDir, "mutations.md");
        var xpathMd = Path.Combine(refDir, "xpath.md");

        Assert.IsTrue(File.Exists(authorityMd), $"authority.md missing at {authorityMd}");
        Assert.IsTrue(File.Exists(mutationsMd), $"mutations.md missing at {mutationsMd}");
        Assert.IsTrue(File.Exists(xpathMd), $"xpath.md missing at {xpathMd}");

        // Legacy skills directory in source root should no longer exist
        var legacySkillDir = Path.Combine(RepoRoot, "skills", "dogdouspec");
        Assert.IsFalse(Directory.Exists(legacySkillDir), $"Legacy skill directory {legacySkillDir} should not exist in source repository");
    }

    [TestMethod]
    public void RepositoryReferences_DefaultToDotAgentsSkills()
    {
        var agentsMdPath = Path.Combine(RepoRoot, "AGENTS.md");
        Assert.IsTrue(File.Exists(agentsMdPath));
        var agentsText = File.ReadAllText(agentsMdPath);
        Assert.IsTrue(agentsText.Contains(".agents/skills/dogdouspec/SKILL.md"), "AGENTS.md must reference .agents/skills/dogdouspec/SKILL.md");
        Assert.IsFalse(Regex.IsMatch(agentsText, @"(?<!\.agents/)skills/dogdouspec/SKILL\.md"), "AGENTS.md must not reference legacy skills/dogdouspec/SKILL.md");

        var readmeMdPath = Path.Combine(RepoRoot, "README.md");
        Assert.IsTrue(File.Exists(readmeMdPath));
        var readmeText = File.ReadAllText(readmeMdPath);
        Assert.IsTrue(readmeText.Contains(".agents/skills/dogdouspec/SKILL.md"), "README.md must reference .agents/skills/dogdouspec/SKILL.md");
        Assert.IsFalse(Regex.IsMatch(readmeText, @"(?<!\.agents/)skills/dogdouspec/SKILL\.md"), "README.md must not reference legacy skills/dogdouspec/SKILL.md");

        var v1SkillWorkflowPath = Path.Combine(RepoRoot, "docs", "V1_SKILL_WORKFLOW.md");
        Assert.IsTrue(File.Exists(v1SkillWorkflowPath));
        var v1Text = File.ReadAllText(v1SkillWorkflowPath);
        Assert.IsTrue(v1Text.Contains(".agents/skills/dogdouspec/SKILL.md"), "V1_SKILL_WORKFLOW.md must reference .agents/skills/dogdouspec/SKILL.md");
    }

    [TestMethod]
    public void InitialInstallation_CleanRepo_ExecutesDocumentedSnippets_InstallsToDotAgentsAndMergesAgentsMd()
    {
        var targetRepo = CreateTargetGitRepo("CleanInstallTarget");
        File.WriteAllText(Path.Combine(targetRepo, "AGENTS.md"), "# Project Rules\n\nRule 1.\n");
        CommitAllInRepo(targetRepo);

        // Run documented snippets in sequence
        var installScript = @"
" + DocSnippets["tool-publish"] + @"

" + DocSnippets["tool-verify"] + @"

" + DocSnippets["tool-install"] + @"

" + DocSnippets["wrapper-create"] + @"

" + DocSnippets["workspace-init"] + @"

" + DocSnippets["skill-install"] + @"

" + DocSnippets["agents-guide"] + @"

" + DocSnippets["workspace-validate"];

        var result = ExecutePowerShellSnippet(installScript, targetRepo);
        Assert.AreEqual(0, result.ExitCode, $"Install failed. Stderr: {result.Stderr}\nStdout: {result.Stdout}");

        // Assert target filesystem state
        Assert.IsTrue(File.Exists(Path.Combine(targetRepo, "dogdouspec.cmd")), "dogdouspec.cmd must exist");
        Assert.IsTrue(File.Exists(Path.Combine(targetRepo, "tools", "dogdouspec", "dogdouspec.exe")), "tools/dogdouspec/dogdouspec.exe must exist");
        Assert.IsTrue(Directory.Exists(Path.Combine(targetRepo, ".dogdouspec")), ".dogdouspec workspace must exist");

        var targetDefaultSkill = Path.Combine(targetRepo, ".agents", "skills", "dogdouspec");
        Assert.IsTrue(File.Exists(Path.Combine(targetDefaultSkill, "SKILL.md")), "SKILL.md must exist in .agents/skills/dogdouspec");
        Assert.IsTrue(File.Exists(Path.Combine(targetDefaultSkill, "references", "authority.md")), "authority.md must exist");
        Assert.IsTrue(File.Exists(Path.Combine(targetDefaultSkill, "references", "mutations.md")), "mutations.md must exist");
        Assert.IsTrue(File.Exists(Path.Combine(targetDefaultSkill, "references", "xpath.md")), "xpath.md must exist");

        // Assert AGENTS.md was left untouched for user+agent decision
        var agentsContent = File.ReadAllText(Path.Combine(targetRepo, "AGENTS.md"));
        Assert.AreEqual("# Project Rules\n\nRule 1.\n", agentsContent, "Original AGENTS.md must be untouched by installation");
    }

    [TestMethod]
    public void InitialInstallation_CollisionDetection_ExecutesDocumentedPreflight_FailsWhenComponentsExist()
    {
        var targetRepo = CreateTargetGitRepo("CollisionTarget");
        Directory.CreateDirectory(Path.Combine(targetRepo, "skills", "dogdouspec"));
        File.WriteAllText(Path.Combine(targetRepo, "skills", "dogdouspec", "SKILL.md"), "existing");
        CommitAllInRepo(targetRepo);

        var preflightSnippet = DocSnippets["preflight-checks"];
        var result = ExecutePowerShellSnippet(preflightSnippet, targetRepo);

        Assert.AreNotEqual(0, result.ExitCode, "Preflight must fail when existing skill directory is detected");
        Assert.IsTrue(result.Stderr.Contains("Existing DogdouSpec components detected", StringComparison.OrdinalIgnoreCase), $"Unexpected error: {result.Stderr}");
    }

    [TestMethod]
    public void Rollback_CleanInstall_ExecutesDocumentedSnippet_RemovesDotAgentsAndRestoresAgentsMd()
    {
        var targetRepo = CreateTargetGitRepo("InitialRollbackTarget");
        var originalAgents = "# Original Guidelines\n";
        File.WriteAllText(Path.Combine(targetRepo, "AGENTS.md"), originalAgents);
        CommitAllInRepo(targetRepo);

        // Run install
        var installScript = @"
" + DocSnippets["tool-publish"] + @"

" + DocSnippets["tool-verify"] + @"

" + DocSnippets["tool-install"] + @"

" + DocSnippets["wrapper-create"] + @"

" + DocSnippets["workspace-init"] + @"

" + DocSnippets["skill-install"] + @"

" + DocSnippets["agents-guide"];

        var installResult = ExecutePowerShellSnippet(installScript, targetRepo);
        Assert.AreEqual(0, installResult.ExitCode, $"Install failed: {installResult.Stderr}");

        // Now run rollback-initial snippet
        var rollbackSnippet = DocSnippets["rollback-initial"];
        var rollbackResult = ExecutePowerShellSnippet(rollbackSnippet, targetRepo);
        Assert.AreEqual(0, rollbackResult.ExitCode, $"Rollback failed. Stderr: {rollbackResult.Stderr}\nStdout: {rollbackResult.Stdout}");

        // Verify rollback outcome
        Assert.IsFalse(File.Exists(Path.Combine(targetRepo, "dogdouspec.cmd")), "dogdouspec.cmd must be removed");
        Assert.IsFalse(File.Exists(Path.Combine(targetRepo, "tools", "dogdouspec", "dogdouspec.exe")), "dogdouspec.exe must be removed");
        Assert.IsFalse(Directory.Exists(Path.Combine(targetRepo, "tools", "dogdouspec")), "Empty tools/dogdouspec must be removed");
        Assert.IsFalse(Directory.Exists(Path.Combine(targetRepo, ".agents")), "Empty .agents parent must be pruned");
        Assert.AreEqual(originalAgents, File.ReadAllText(Path.Combine(targetRepo, "AGENTS.md")), "AGENTS.md must be restored to original content");
    }

    [TestMethod]
    public void Uninstall_ExecutesDocumentedSnippet_RemovesAllComponentsAndPrunesParents()
    {
        var targetRepo = CreateTargetGitRepo("UninstallTarget");
        SetupExistingInstalledWorkspace(targetRepo);
        InstallDefaultSkillFiles(targetRepo);
        InstallLegacySkillFiles(targetRepo);
        CommitAllInRepo(targetRepo);

        var uninstallSnippet = DocSnippets["uninstall"];
        var result = ExecutePowerShellSnippet(uninstallSnippet, targetRepo);
        Assert.AreEqual(0, result.ExitCode, $"Uninstall failed. Stderr: {result.Stderr}\nStdout: {result.Stdout}");

        Assert.IsFalse(File.Exists(Path.Combine(targetRepo, "dogdouspec.cmd")), "Wrapper must be removed");
        Assert.IsFalse(File.Exists(Path.Combine(targetRepo, "tools", "dogdouspec", "dogdouspec.exe")), "Binary must be removed");
        Assert.IsFalse(Directory.Exists(Path.Combine(targetRepo, ".agents")), ".agents parent must be pruned");
        Assert.IsFalse(Directory.Exists(Path.Combine(targetRepo, "skills")), "skills parent must be pruned");
    }
}
