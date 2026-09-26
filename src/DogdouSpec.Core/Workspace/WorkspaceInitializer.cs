using System.Text;
using DogdouSpec.Core.Diagnostics;
using DogdouSpec.Core.Resources;
using DogdouSpec.Core.Security;
using DogdouSpec.Core.Serialization;

namespace DogdouSpec.Core.Workspace;

/// <summary>
/// Initializes a DogdouSpec workspace (.dogdouspec) atomically without overwriting existing state.
/// </summary>
public static class WorkspaceInitializer
{
    private static readonly UTF8Encoding Utf8NoBom = new(false);

    public static (bool Success, string WorkspaceRoot, Diagnostic? Error) Initialize(
        string? explicitWorkspaceRoot,
        string startDirectory)
    {
        string targetDogdouDir;
        string projectRoot;

        if (!string.IsNullOrWhiteSpace(explicitWorkspaceRoot))
        {
            var (isValid, normalizedPath, pathError) = PathSecurity.ValidateWorkspaceRootPath(explicitWorkspaceRoot);
            if (!isValid || pathError != null)
            {
                return (false, string.Empty, pathError);
            }

            var fullPath = Path.GetFullPath(normalizedPath);
            var dirName = Path.GetFileName(fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            if (string.Equals(dirName, ".dogdouspec", StringComparison.OrdinalIgnoreCase))
            {
                targetDogdouDir = fullPath;
                projectRoot = Path.GetDirectoryName(fullPath) ?? fullPath;
            }
            else
            {
                targetDogdouDir = Path.Combine(fullPath, ".dogdouspec");
                projectRoot = fullPath;
            }
        }
        else
        {
            projectRoot = Path.GetFullPath(startDirectory);
            targetDogdouDir = Path.Combine(projectRoot, ".dogdouspec");
        }

        targetDogdouDir = PathSecurity.NormalizeSeparators(targetDogdouDir);

        // Check if managed state already exists
        if (Directory.Exists(targetDogdouDir))
        {
            var existingFiles = Directory.GetFileSystemEntries(targetDogdouDir);
            if (existingFiles.Length > 0)
            {
                return (false, targetDogdouDir, Diagnostic.Error(
                    DiagnosticCodes.ManagedStateExists,
                    $"Managed DogdouSpec workspace already exists at '{targetDogdouDir}'. Refusing to overwrite existing state."));
            }
        }

        // Perform atomic initialization with rollback tracking
        var createdFiles = new List<string>();
        var createdDirs = new List<string>();

        try
        {
            if (!Directory.Exists(targetDogdouDir))
            {
                Directory.CreateDirectory(targetDogdouDir);
                createdDirs.Add(targetDogdouDir);
            }

            var schemaDir = Path.Combine(targetDogdouDir, "_schema");
            if (!Directory.Exists(schemaDir))
            {
                Directory.CreateDirectory(schemaDir);
                createdDirs.Add(schemaDir);
            }

            var skillDir = Path.Combine(targetDogdouDir, "_skill");
            if (!Directory.Exists(skillDir))
            {
                Directory.CreateDirectory(skillDir);
                createdDirs.Add(skillDir);
            }

            // Copy readable schemas into _schema
            foreach (var schemaName in EmbeddedResources.SchemaNames)
            {
                var schemaText = EmbeddedResources.GetSchemaText(schemaName, "1.0");
                if (schemaText != null)
                {
                    var filePath = Path.Combine(schemaDir, $"{schemaName}.xsd");
                    File.WriteAllText(filePath, schemaText, Utf8NoBom);
                    createdFiles.Add(filePath);
                }
            }

            // Write _schema/README.md
            var schemaReadme = Path.Combine(schemaDir, "README.md");
            var schemaReadmeContent = "# Schema Directory\n\nReadable copies of DogdouSpec v1 schemas.\nThe CLI embedded schemas remain the authoritative validation source.\n";
            File.WriteAllText(schemaReadme, schemaReadmeContent, Utf8NoBom);
            createdFiles.Add(schemaReadme);

            // Write _skill/README.md
            var skillReadme = Path.Combine(skillDir, "README.md");
            var skillReadmeContent = """
# Skill Directory

This directory contains managed DogdouSpec workflow guidance and environment adapters.

Authoritative specification and execution state lives in the managed XML documents under `.dogdouspec/`. Semantic agent results—including summaries, source commits, checks, findings, risks, review outcomes, blockers, and handoff instructions—belong in the relevant `tasks.xml` Task records. Temporary agent reports or response files are transport only; no external report directory is required for recovery.

In a Git-backed governed workspace, version the managed `.dogdouspec/` documents and checkpoint them at material lifecycle, review, handoff, external-blocker, and release boundaries. Ignore only `.dogdouspec/_tmp/`, which contains runtime transaction and recovery state. DogdouSpec does not stage, commit, or push files; repository writes require explicit caller authority.
""";
            var normalizedSkillReadme = skillReadmeContent.Replace("\r\n", "\n").Replace("\r", "\n").TrimEnd('\n') + "\n";
            File.WriteAllText(skillReadme, normalizedSkillReadme, Utf8NoBom);
            createdFiles.Add(skillReadme);

            // Date for initial objects
            var today = DateTime.UtcNow.ToString("yyyyMMdd", System.Globalization.CultureInfo.InvariantCulture);

            // Write knowledge.xml
            var knowledgePath = Path.Combine(targetDogdouDir, "knowledge.xml");
            var knowledgeContent = $"""
<?xml version="1.0" encoding="utf-8"?>
<knowledge
  id="{today}-knowledge"
  schema_version="1.0"
  revision="1">
  <index>
    <summary>Verified reusable project knowledge.</summary>
    <term key="scope" value="project"/>
  </index>
</knowledge>

""";
            File.WriteAllText(knowledgePath, ManagedDocumentSerializer.Normalize(knowledgeContent), Utf8NoBom);
            createdFiles.Add(knowledgePath);

            // Write backlog.xml
            var backlogPath = Path.Combine(targetDogdouDir, "backlog.xml");
            var backlogContent = $"""
<?xml version="1.0" encoding="utf-8"?>
<backlog
  id="{today}-backlog"
  schema_version="1.0"
  revision="1">
  <index>
    <summary>Project obligations not owned by an active Iteration.</summary>
    <term key="scope" value="project"/>
  </index>
  <items/>
</backlog>

""";
            File.WriteAllText(backlogPath, ManagedDocumentSerializer.Normalize(backlogContent), Utf8NoBom);
            createdFiles.Add(backlogPath);

            // Copy embedded skill files to <projectRoot>/.agents/skills/dogdouspec/
            // Skip files that already exist — never overwrite on init (use 'skill sync' to upgrade).
            var agentSkillDir = Path.Combine(projectRoot, ".agents", "skills", "dogdouspec");
            var agentSkillRefDir = Path.Combine(agentSkillDir, "references");

            if (!Directory.Exists(agentSkillDir))
            {
                Directory.CreateDirectory(agentSkillDir);
                createdDirs.Add(agentSkillDir);
            }
            if (!Directory.Exists(agentSkillRefDir))
            {
                Directory.CreateDirectory(agentSkillRefDir);
                createdDirs.Add(agentSkillRefDir);
            }

            foreach (var relPath in EmbeddedResources.SkillFilePaths)
            {
                var destPath = Path.Combine(agentSkillDir, relPath.Replace('/', Path.DirectorySeparatorChar));
                if (!File.Exists(destPath))
                {
                    var content = EmbeddedResources.GetSkillText(relPath);
                    if (content != null)
                    {
                        File.WriteAllText(destPath, content, Utf8NoBom);
                        createdFiles.Add(destPath);
                    }
                }
            }

            // Update .gitignore: append /.dogdouspec/_tmp/ if not already present (idempotent).
            var gitignorePath = Path.Combine(projectRoot, ".gitignore");
            const string GitignoreEntry = "/.dogdouspec/_tmp/";
            if (!File.Exists(gitignorePath))
            {
                File.WriteAllText(gitignorePath, $"# DogdouSpec runtime temporary staging files\n{GitignoreEntry}\n", Utf8NoBom);
                createdFiles.Add(gitignorePath);
            }
            else
            {
                var existing = File.ReadAllText(gitignorePath);
                if (!existing.Contains(GitignoreEntry, StringComparison.Ordinal))
                {
                    var suffix = existing.EndsWith('\n') ? "" : "\n";
                    File.AppendAllText(gitignorePath, $"{suffix}\n# DogdouSpec runtime temporary staging files\n{GitignoreEntry}\n", Utf8NoBom);
                }
            }

            return (true, targetDogdouDir, null);
        }
        catch (Exception ex)
        {
            // Rollback on failure
            foreach (var file in createdFiles)
            {
                try
                {
                    if (File.Exists(file))
                    {
                        File.Delete(file);
                    }
                }
                catch { /* Ignore rollback individual failures */ }
            }

            // Delete directories in reverse order
            for (var i = createdDirs.Count - 1; i >= 0; i--)
            {
                try
                {
                    var dir = createdDirs[i];
                    if (Directory.Exists(dir) && Directory.GetFileSystemEntries(dir).Length == 0)
                    {
                        Directory.Delete(dir);
                    }
                }
                catch { /* Ignore rollback individual failures */ }
            }

            return (false, string.Empty, Diagnostic.Error(
                DiagnosticCodes.InitializationFailed,
                $"Workspace initialization failed: {ex.Message}"));
        }
    }
}
