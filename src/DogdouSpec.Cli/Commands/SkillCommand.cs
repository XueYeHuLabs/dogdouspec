using System.CommandLine;
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
        var skillCmd = new Command("skill", "Manage and synchronize DogdouSpec agent skills");

        var syncCmd = BuildSyncCommand();
        var exportCmd = BuildExportCommand();

        skillCmd.Add(syncCmd);
        skillCmd.Add(exportCmd);

        return skillCmd;
    }

    private static Command BuildSyncCommand()
    {
        var syncCmd = new Command("sync", "Synchronize embedded DogdouSpec skill to .agents/skills/dogdouspec");

        var outputDirOption = new Option<string?>("--output-dir")
        {
            Description = "Output directory for skill files (default: .agents/skills/dogdouspec)"
        };

        var formatOption = new Option<string?>("--format")
        {
            Description = "Output format (xml or human)"
        };
        formatOption.AcceptOnlyFromAmong("xml", "human");

        syncCmd.Add(outputDirOption);
        syncCmd.Add(formatOption);

        syncCmd.SetAction(parseResult =>
        {
            var outputDir = parseResult.GetValue(outputDirOption) ?? Path.Combine(Environment.CurrentDirectory, ".agents", "skills", "dogdouspec");
            var formatArg = parseResult.GetValue(formatOption);
            var format = WorkspaceCommand.ResolveFormat(formatArg);

            return ExportSkillFiles(outputDir, format, "skill sync");
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

            return ExportSkillFiles(outputDir, format, "skill export");
        });

        return exportCmd;
    }

    private static int ExportSkillFiles(string targetDir, OutputFormat format, string commandName)
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