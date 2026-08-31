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
        var skillCmd = new Command("skill", "Manage, inspect, and synchronize DogdouSpec agent skills and guidelines");

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
        var guideCmd = new Command("guide", "Display recommended installation, workflow, and skill guidance for AI coding agents");

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
        var syncCmd = new Command("sync", "Synchronize embedded DogdouSpec skill to .agents/skills/dogdouspec and workspace guidelines");

        var outputDirOption = new Option<string?>("--output-dir")
        {
            Description = "Output directory for skill files (default: .agents/skills/dogdouspec)"
        };

        var includeAgentsOption = new Option<bool>("--agents")
        {
            Description = "Also synchronize root AGENTS.md instructions if not present"
        };

        var formatOption = new Option<string?>("--format")
        {
            Description = "Output format (xml or human)"
        };
        formatOption.AcceptOnlyFromAmong("xml", "human");

        syncCmd.Add(outputDirOption);
        syncCmd.Add(includeAgentsOption);
        syncCmd.Add(formatOption);

        syncCmd.SetAction(parseResult =>
        {
            var outputDir = parseResult.GetValue(outputDirOption) ?? Path.Combine(Environment.CurrentDirectory, ".agents", "skills", "dogdouspec");
            var includeAgents = parseResult.GetValue(includeAgentsOption);
            var formatArg = parseResult.GetValue(formatOption);
            var format = WorkspaceCommand.ResolveFormat(formatArg);

            return ExportSkillFiles(outputDir, includeAgents, format, "skill sync");
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

            return ExportSkillFiles(outputDir, false, format, "skill export");
        });

        return exportCmd;
    }

    private static int ExportSkillFiles(string targetDir, bool includeAgents, OutputFormat format, string commandName)
    {
        try
        {
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

            if (includeAgents)
            {
                var agentsContent = EmbeddedResources.GetAgentsTemplateText();
                if (agentsContent != null)
                {
                    var agentsPath = Path.Combine(Environment.CurrentDirectory, "AGENTS.md");
                    if (!File.Exists(agentsPath))
                    {
                        File.WriteAllText(agentsPath, agentsContent, new UTF8Encoding(false));
                        exportedFiles.Add(agentsPath);
                    }
                }
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