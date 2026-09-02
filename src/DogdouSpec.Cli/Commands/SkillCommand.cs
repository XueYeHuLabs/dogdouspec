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
        var syncCmd = BuildSyncCommand();
        var exportCmd = BuildExportCommand();

        skillCmd.Add(guideCmd);
        skillCmd.Add(syncCmd);
        skillCmd.Add(exportCmd);

        return skillCmd;
    }

    private static Command BuildGuideCommand()
    {
        var guideCmd = new Command("guide", "Display installation and workflow guidance, including Task-record result ownership and VCS checkpoints");

        var formatOption = new Option<string?>("--format")
        {
            Description = "Output format (markdown, human, xml)"
        };
        formatOption.AcceptOnlyFromAmong("markdown", "md", "human", "xml");

        var allOption = new Option<bool>("--all")
        {
            Description = "Include all supporting reference documents (authority, mutations, xpath)"
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
}
