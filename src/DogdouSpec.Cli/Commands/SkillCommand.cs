using System.CommandLine;
using System.Globalization;
using System.Text;
using System.Xml;
using DogdouSpec.Core.Diagnostics;
using DogdouSpec.Core.Formatting;
using DogdouSpec.Core.Resources;

namespace DogdouSpec.Cli.Commands;

public static class SkillCommand
{
    public static Command BuildCommand()
    {
        var skillCmd = new Command("skill", "Manage DogdouSpec guidance for iteration-owned results, authority, and workspace persistence");

        var guideCmd = BuildGuideCommand();
        var statusCmd = BuildStatusCommand();
        var syncCmd = BuildSyncCommand();
        var exportCmd = BuildExportCommand();

        skillCmd.Add(guideCmd);
        skillCmd.Add(statusCmd);
        skillCmd.Add(syncCmd);
        skillCmd.Add(exportCmd);

        return skillCmd;
    }

    private static Command BuildGuideCommand()
    {
        var guideCmd = new Command("guide", "Display the current binary's authoritative setup, upgrade, workflow, and governance guidance");

        var formatOption = new Option<string?>("--format")
        {
            Description = "Output format (markdown, human, xml)"
        };
        formatOption.AcceptOnlyFromAmong("markdown", "md", "human", "xml");

        var allOption = new Option<bool>("--all")
        {
            Description = "Include all supporting reference documents, including the authoritative upgrade contract"
        };

        guideCmd.Add(formatOption);
        guideCmd.Add(allOption);

        guideCmd.SetAction(parseResult =>
        {
            var formatArg = parseResult.GetValue(formatOption);
            var includeAll = parseResult.GetValue(allOption);
            var format = WorkspaceCommand.ResolveFormat(formatArg);

            var skillContent = EmbeddedResources.GetSkillText("SKILL.md") ?? "# DogdouSpec Agent Guide\nNo embedded guide found.";

            if (format == OutputFormat.Xml)
            {
                var settings = new XmlWriterSettings
                {
                    Indent = true,
                    IndentChars = "  ",
                    OmitXmlDeclaration = false,
                    Encoding = new UTF8Encoding(false),
                    NewLineHandling = NewLineHandling.Replace,
                    NewLineChars = "\n"
                };

                using var ms = new MemoryStream();
                using (var writer = XmlWriter.Create(ms, settings))
                {
                    writer.WriteStartDocument();
                    writer.WriteStartElement("skill-guide");
                    writer.WriteAttributeString("name", "dogdouspec");

                    writer.WriteStartElement("file");
                    writer.WriteAttributeString("path", "SKILL.md");
                    writer.WriteCData(skillContent);
                    writer.WriteEndElement();

                    if (includeAll)
                    {
                        foreach (var relPath in EmbeddedResources.SkillFilePaths.Where(p => p != "SKILL.md"))
                        {
                            var content = EmbeddedResources.GetSkillText(relPath);
                            if (content != null)
                            {
                                writer.WriteStartElement("file");
                                writer.WriteAttributeString("path", relPath);
                                writer.WriteCData(content);
                                writer.WriteEndElement();
                            }
                        }
                    }

                    writer.WriteEndElement(); // </skill-guide>
                    writer.WriteEndDocument();
                }

                Console.Out.Write(Encoding.UTF8.GetString(ms.ToArray()) + "\n");
                return 0;
            }

            var sb = new StringBuilder();
            sb.AppendLine(skillContent);

            if (includeAll)
            {
                foreach (var relPath in EmbeddedResources.SkillFilePaths.Where(p => p != "SKILL.md"))
                {
                    var content = EmbeddedResources.GetSkillText(relPath);
                    if (content != null)
                    {
                        sb.AppendLine();
                        sb.AppendLine("---");
                        sb.Append("# Reference: ").Append(relPath).AppendLine();
                        sb.AppendLine();
                        sb.AppendLine(content);
                    }
                }
            }

            Console.Out.Write(sb.ToString());
            return 0;
        });

        return guideCmd;
    }

    private static Command BuildStatusCommand()
    {
        var statusCmd = new Command("status", "Compare repository Skill files with this binary's embedded version without modifying files");

        var outputDirOption = new Option<string?>("--output-dir")
        {
            Description = "Skill directory to inspect (default: .agents/skills/dogdouspec relative to current directory)"
        };

        var formatOption = new Option<string?>("--format")
        {
            Description = "Output format (xml or human)"
        };
        formatOption.AcceptOnlyFromAmong("xml", "human");

        statusCmd.Add(outputDirOption);
        statusCmd.Add(formatOption);

        statusCmd.SetAction(parseResult =>
        {
            const string commandName = "skill status";
            var targetDir = parseResult.GetValue(outputDirOption) ?? Path.Combine(Environment.CurrentDirectory, ".agents", "skills", "dogdouspec");
            var format = WorkspaceCommand.ResolveFormat(parseResult.GetValue(formatOption));

            try
            {
                var fullTargetDir = Path.GetFullPath(targetDir);
                var files = InspectSkillFiles(fullTargetDir);
                Console.Out.Write(FormatSkillStatus(fullTargetDir, files, format));
                return files.All(file => file.State == "matching") ? 0 : 1;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                var envelope = new DiagnosticsEnvelope(commandName, Diagnostic.Error(
                    DiagnosticCodes.FilesystemError,
                    $"Cannot inspect Skill directory '{targetDir}': {ex.Message}"));
                Console.Error.Write(envelope.Format(format));
                return envelope.GetExitCode();
            }
        });

        return statusCmd;
    }

    private static Command BuildSyncCommand()
    {
        var syncCmd = new Command("sync", "Synchronize skill files with the version embedded in this CLI binary. Pass --force to overwrite existing files upon upgrade.");

        var forceOption = new Option<bool>("--force")
        {
            Description = "Force overwrite existing skill files in target directory"
        };

        var outputDirOption = new Option<string?>("--output-dir")
        {
            Description = "Output directory for skill files (default: .agents/skills/dogdouspec relative to current directory)"
        };

        var formatOption = new Option<string?>("--format")
        {
            Description = "Output format (xml or human)"
        };
        formatOption.AcceptOnlyFromAmong("xml", "human");

        syncCmd.Add(forceOption);
        syncCmd.Add(outputDirOption);
        syncCmd.Add(formatOption);

        syncCmd.SetAction(parseResult =>
        {
            var force = parseResult.GetValue(forceOption);
            var outputDir = parseResult.GetValue(outputDirOption) ?? Path.Combine(Environment.CurrentDirectory, ".agents", "skills", "dogdouspec");
            var formatArg = parseResult.GetValue(formatOption);
            var format = WorkspaceCommand.ResolveFormat(formatArg);

            return ExportSkillFiles(outputDir, force, format, "skill sync");
        });

        return syncCmd;
    }

    private static Command BuildExportCommand()
    {
        var exportCmd = new Command("export", "Export embedded DogdouSpec skill files to a specified directory");

        var outputDirOption = new Option<string>("--output-dir")
        {
            Description = "Output directory for exported skill files",
            Required = true
        };

        var formatOption = new Option<string?>("--format")
        {
            Description = "Output format (xml or human)"
        };
        formatOption.AcceptOnlyFromAmong("xml", "human");

        exportCmd.Add(outputDirOption);
        exportCmd.Add(formatOption);

        exportCmd.SetAction(parseResult =>
        {
            var outputDir = parseResult.GetValue(outputDirOption)!;
            var formatArg = parseResult.GetValue(formatOption);
            var format = WorkspaceCommand.ResolveFormat(formatArg);

            return ExportSkillFiles(outputDir, true, format, "skill export");
        });

        return exportCmd;
    }

    private static int ExportSkillFiles(string targetDir, bool force, OutputFormat format, string commandName)
    {
        try
        {
            if (!force)
            {
                var existingFiles = EmbeddedResources.SkillFilePaths
                    .Select(rel => Path.Combine(targetDir, rel.Replace('/', Path.DirectorySeparatorChar)))
                    .Where(File.Exists)
                    .ToList();

                if (existingFiles.Count > 0)
                {
                    var envelope = new DiagnosticsEnvelope(commandName, Diagnostic.Error(
                        DiagnosticCodes.ManagedStateExists,
                        $"Skill files already exist at '{targetDir}'. Pass --force to overwrite existing skill files."));
                    Console.Error.Write(envelope.Format(format));
                    return 2;
                }
            }

            var exportedFiles = new List<string>();
            foreach (var relPath in EmbeddedResources.SkillFilePaths)
            {
                var content = EmbeddedResources.GetSkillText(relPath);
                if (content == null)
                {
                    var envelope = new DiagnosticsEnvelope(commandName, Diagnostic.Error(
                        DiagnosticCodes.ResourceNotFound,
                        $"Embedded skill resource '{relPath}' not found."));
                    Console.Error.Write(envelope.Format(format));
                    return 2;
                }

                var fullPath = Path.Combine(targetDir, relPath.Replace('/', Path.DirectorySeparatorChar));
                var dir = Path.GetDirectoryName(fullPath);
                if (dir != null && !Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                File.WriteAllText(fullPath, content, new UTF8Encoding(false));
                exportedFiles.Add(fullPath);
            }

            if (format == OutputFormat.Xml)
            {
                var settings = new XmlWriterSettings
                {
                    Indent = true,
                    IndentChars = "  ",
                    OmitXmlDeclaration = false,
                    Encoding = new UTF8Encoding(false),
                    NewLineHandling = NewLineHandling.Replace,
                    NewLineChars = "\n"
                };

                using var ms = new MemoryStream();
                using (var writer = XmlWriter.Create(ms, settings))
                {
                    writer.WriteStartDocument();
                    writer.WriteStartElement("skill");
                    writer.WriteAttributeString("action", commandName);
                    writer.WriteAttributeString("output_directory", targetDir);
                    writer.WriteAttributeString("count", exportedFiles.Count.ToString(System.Globalization.CultureInfo.InvariantCulture));
                    foreach (var file in exportedFiles)
                    {
                        writer.WriteStartElement("file");
                        writer.WriteAttributeString("path", file);
                        writer.WriteEndElement();
                    }
                    writer.WriteEndElement();
                    writer.WriteEndDocument();
                }

                Console.Out.Write(Encoding.UTF8.GetString(ms.ToArray()) + "\n");
            }
            else
            {
                Console.Out.WriteLine($"Successfully synchronized {exportedFiles.Count} skill files to: {targetDir}");
            }

            return 0;
        }
        catch (Exception ex)
        {
            var envelope = new DiagnosticsEnvelope(commandName, Diagnostic.Error(
                DiagnosticCodes.FilesystemError,
                $"Failed to export skill files: {ex.Message}"));
            Console.Error.Write(envelope.Format(format));
            return 2;
        }
    }

    private static List<SkillFileStatus> InspectSkillFiles(string targetDir)
    {
        const long MaxManagedFileSize = 1024 * 1024;
        const int MaxEnumeratedFiles = 1024;
        var statuses = new List<SkillFileStatus>();
        var managedPaths = new HashSet<string>(EmbeddedResources.SkillFilePaths, StringComparer.OrdinalIgnoreCase);

        if (Directory.Exists(targetDir) && new DirectoryInfo(targetDir).Attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            throw new IOException("Skill directory is a reparse point and cannot be inspected safely.");
        }

        foreach (var relPath in EmbeddedResources.SkillFilePaths)
        {
            var fullPath = Path.Combine(targetDir, relPath.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(fullPath))
            {
                statuses.Add(new SkillFileStatus(relPath, "missing", true));
                continue;
            }

            var expectedBytes = EmbeddedResources.GetSkillBytes(relPath)
                ?? throw new IOException($"Embedded Skill resource '{relPath}' is unavailable.");
            var matching = !ContainsReparsePoint(targetDir, fullPath)
                && FileMatches(fullPath, expectedBytes, MaxManagedFileSize);
            statuses.Add(new SkillFileStatus(relPath, matching ? "matching" : "modified", true));
        }

        if (!Directory.Exists(targetDir))
        {
            return statuses;
        }

        var enumerated = 0;
        var pendingDirectories = new Stack<string>();
        pendingDirectories.Push(targetDir);
        while (pendingDirectories.Count > 0)
        {
            var currentDirectory = pendingDirectories.Pop();
            foreach (var entry in Directory.EnumerateFileSystemEntries(currentDirectory))
            {
                enumerated++;
                if (enumerated > MaxEnumeratedFiles)
                {
                    throw new IOException($"Skill directory exceeds the {MaxEnumeratedFiles}-entry inspection limit.");
                }

                var attributes = File.GetAttributes(entry);
                var relPath = Path.GetRelativePath(targetDir, entry).Replace('\\', '/');
                if (attributes.HasFlag(FileAttributes.Directory))
                {
                    if (attributes.HasFlag(FileAttributes.ReparsePoint))
                    {
                        statuses.Add(new SkillFileStatus(relPath, "extra", false));
                    }
                    else
                    {
                        pendingDirectories.Push(entry);
                    }
                }
                else if (!managedPaths.Contains(relPath))
                {
                    statuses.Add(new SkillFileStatus(relPath, "extra", false));
                }
            }
        }

        return statuses
            .OrderBy(status => status.Managed ? 0 : 1)
            .ThenBy(status => status.Path, StringComparer.Ordinal)
            .ToList();
    }

    private static bool FileMatches(string fullPath, byte[] expectedBytes, long maximumLength)
    {
        using var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > maximumLength || stream.Length != expectedBytes.Length)
        {
            return false;
        }

        var actualBytes = new byte[expectedBytes.Length];
        stream.ReadExactly(actualBytes);
        return stream.ReadByte() == -1 && actualBytes.AsSpan().SequenceEqual(expectedBytes);
    }

    private static bool ContainsReparsePoint(string root, string target)
    {
        var relativePath = Path.GetRelativePath(root, target);
        var current = root;
        foreach (var segment in relativePath.Split(
            new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
            StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            if ((File.Exists(current) || Directory.Exists(current)) &&
                File.GetAttributes(current).HasFlag(FileAttributes.ReparsePoint))
            {
                return true;
            }
        }
        return false;
    }

    private static string FormatSkillStatus(string targetDir, IReadOnlyList<SkillFileStatus> files, OutputFormat format)
    {
        var matching = files.Count(file => file.State == "matching");
        var modified = files.Count(file => file.State == "modified");
        var missing = files.Count(file => file.State == "missing");
        var extra = files.Count(file => file.State == "extra");
        var inSync = modified == 0 && missing == 0 && extra == 0;

        if (format != OutputFormat.Xml)
        {
            var sb = new StringBuilder();
            sb.Append("Skill status: ").AppendLine(inSync ? "in sync" : "differences found");
            sb.Append("Directory: ").AppendLine(targetDir);
            sb.Append("Matching: ").Append(matching.ToString(CultureInfo.InvariantCulture))
                .Append(", modified: ").Append(modified.ToString(CultureInfo.InvariantCulture))
                .Append(", missing: ").Append(missing.ToString(CultureInfo.InvariantCulture))
                .Append(", extra: ").AppendLine(extra.ToString(CultureInfo.InvariantCulture));
            foreach (var file in files.Where(file => file.State != "matching"))
            {
                sb.Append("  ").Append(file.State).Append(": ").AppendLine(file.Path);
            }
            return sb.ToString();
        }

        var settings = new XmlWriterSettings
        {
            Indent = true,
            IndentChars = "  ",
            OmitXmlDeclaration = false,
            Encoding = new UTF8Encoding(false),
            NewLineHandling = NewLineHandling.Replace,
            NewLineChars = "\n"
        };

        using var ms = new MemoryStream();
        using (var writer = XmlWriter.Create(ms, settings))
        {
            writer.WriteStartDocument();
            writer.WriteStartElement("skill-status");
            writer.WriteAttributeString("directory", targetDir);
            writer.WriteAttributeString("in_sync", inSync ? "true" : "false");
            writer.WriteAttributeString("matching", matching.ToString(CultureInfo.InvariantCulture));
            writer.WriteAttributeString("modified", modified.ToString(CultureInfo.InvariantCulture));
            writer.WriteAttributeString("missing", missing.ToString(CultureInfo.InvariantCulture));
            writer.WriteAttributeString("extra", extra.ToString(CultureInfo.InvariantCulture));
            foreach (var file in files)
            {
                writer.WriteStartElement("file");
                writer.WriteAttributeString("path", file.Path);
                writer.WriteAttributeString("state", file.State);
                writer.WriteAttributeString("managed", file.Managed ? "true" : "false");
                writer.WriteEndElement();
            }
            writer.WriteEndElement();
            writer.WriteEndDocument();
        }

        return Encoding.UTF8.GetString(ms.ToArray()) + "\n";
    }

    private sealed record SkillFileStatus(string Path, string State, bool Managed);
}
