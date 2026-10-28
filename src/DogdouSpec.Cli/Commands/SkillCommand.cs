using System.CommandLine;
using System.Globalization;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using DogdouSpec.Core.Diagnostics;
using DogdouSpec.Core.Formatting;
using DogdouSpec.Core.Resources;
using DogdouSpec.Core.Skills;

namespace DogdouSpec.Cli.Commands;

public static class SkillCommand
{
    public static Command BuildCommand()
    {
        var command = new Command("skill", "Inspect, customize and synchronize Skill guidance; upgrade decisions belong to the caller");
        command.Add(BuildGuideCommand());
        command.Add(BuildStatusCommand());
        command.Add(BuildSyncCommand(export: false));
        command.Add(BuildSyncCommand(export: true));
        command.Add(BuildRecordCommand());
        command.Add(BuildRecordsCommand());
        command.Add(BuildDiffCommand());
        return command;
    }

    private static Command BuildGuideCommand()
    {
        var command = new Command("guide", "Read current binary guidance; select references/upgrade.md for the compact upgrade workflow");
        var formatOption = new Option<string?>("--format") { Description = "Output format (markdown, human, xml)" };
        formatOption.AcceptOnlyFromAmong("markdown", "md", "human", "xml");
        var allOption = new Option<bool>("--all") { Description = "Include all supporting reference documents" };
        var fileOption = new Option<string?>("--file") { Description = "Read one embedded Skill file, such as references/upgrade.md" };
        command.Add(formatOption);
        command.Add(allOption);
        command.Add(fileOption);
        command.SetAction(parse =>
        {
            var format = WorkspaceCommand.ResolveFormat(parse.GetValue(formatOption));
            return Execute("skill guide", format, () =>
            {
                var file = parse.GetValue(fileOption)?.Replace('\\', '/');
                var all = parse.GetValue(allOption);
                if (file != null && (all || !EmbeddedResources.SkillFilePaths.Contains(file)))
                    throw new ArgumentException("Use either --all or --file with an embedded Skill path: " + string.Join(", ", EmbeddedResources.SkillFilePaths));
                var paths = all ? EmbeddedResources.SkillFilePaths : new[] { file ?? "SKILL.md" };
                if (format == OutputFormat.Xml)
                {
                    WriteXml(new XElement("skill-guide", new XAttribute("name", "dogdouspec"), paths.Select(path =>
                        new XElement("file", new XAttribute("path", path), GuideContent(EmbeddedResources.GetSkillText(path)!)))));
                }
                else
                {
                    foreach (var path in paths)
                    {
                        if (all && path != "SKILL.md") Console.Out.Write($"\n---\n# Reference: {path}\n\n");
                        Console.Out.Write(EmbeddedResources.GetSkillText(path));
                    }
                }
                return 0;
            });
        });
        return command;
    }

    private static Command BuildStatusCommand()
    {
        var command = new Command("status", "Report content differences, baseline provenance and advisory automatic-upgrade conditions without writing");
        var directoryOption = DirectoryOption();
        var formatOption = FormatOption();
        command.Add(directoryOption);
        command.Add(formatOption);
        command.SetAction(parse =>
        {
            var format = WorkspaceCommand.ResolveFormat(parse.GetValue(formatOption));
            return Execute("skill status", format, () =>
            {
                var result = SkillInspection.Inspect(DirectoryPath(parse.GetValue(directoryOption)));
                WriteStatus(result, format);
                return result.InSync ? 0 : 1;
            });
        });
        return command;
    }

    private static Command BuildSyncCommand(bool export)
    {
        var name = export ? "export" : "sync";
        var command = new Command(name, export
            ? "Export embedded Skill files and baseline, preserving any collaboration history"
            : "Synchronize embedded Skill files and baseline; --force overwrites existing files regardless of customization or history");
        var directoryOption = DirectoryOption(export);
        var forceOption = new Option<bool>("--force") { Description = "Overwrite existing managed files; no customization approval is required" };
        var formatOption = FormatOption();
        command.Add(directoryOption);
        command.Add(formatOption);
        if (!export) command.Add(forceOption);
        command.SetAction(parse =>
        {
            var format = WorkspaceCommand.ResolveFormat(parse.GetValue(formatOption));
            return Execute("skill " + name, format, () =>
                Synchronize(DirectoryPath(parse.GetValue(directoryOption)), export || parse.GetValue(forceOption), format, "skill " + name));
        });
        return command;
    }

    private static int Synchronize(string directory, bool force, OutputFormat format, string commandName)
    {
        using var lease = SkillStateStore.AcquireWriter(directory);
        var existing = EmbeddedResources.SkillFilePaths.Where(path => File.Exists(SkillPath(directory, path))).ToList();
        if (!force && existing.Count > 0)
        {
            Console.Error.Write(new DiagnosticsEnvelope(commandName, Diagnostic.Error(DiagnosticCodes.ManagedStateExists,
                $"Skill files already exist at '{directory}'. Pass --force to overwrite existing skill files.")).Format(format));
            return 2;
        }
        var metadata = SkillStateStore.Read(directory);
        var baseline = SkillStateStore.ReadBaseline(metadata);
        var previousVersion = (string?)metadata.Root?.Element("baseline")?.Attribute("cli_version");
        var replacedLocal = new List<string>();
        var resources = EmbeddedResources.SkillFilePaths.ToDictionary(path => path,
            path => EmbeddedResources.GetSkillBytes(path) ?? throw new IOException($"Embedded Skill resource '{path}' is unavailable."));
        foreach (var path in resources.Keys) SkillStateStore.CheckPath(directory, SkillPath(directory, path));
        foreach (var path in existing)
        {
            if (baseline == null) continue;
            try
            {
                var fullPath = SkillPath(directory, path);
                if (new FileInfo(fullPath).Length > SkillInspection.MaximumFileBytes)
                {
                    replacedLocal.Add(path);
                    continue;
                }
                var localHash = SkillStateStore.Digest(SkillInspection.ReadFile(fullPath));
                if (localHash != SkillStateStore.Digest(resources[path]) &&
                    (!baseline.TryGetValue(path, out var hash) || localHash != hash)) replacedLocal.Add(path);
            }
            catch (DecoderFallbackException) { replacedLocal.Add(path); }
        }
        foreach (var (path, content) in resources)
        {
            var target = SkillPath(directory, path);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.WriteAllBytes(target, content);
        }
        // Publish the new baseline only after all embedded files were written; never reset history.
        SkillStateStore.SetBaseline(metadata, resources.Keys);
        SkillStateStore.Write(directory, metadata);
        var records = metadata.Root!.Element("history")?.Elements("record").Count() ?? 0;
        if (format == OutputFormat.Xml)
        {
            WriteXml(new XElement("skill", new XAttribute("action", commandName),
                new XAttribute("output_directory", directory), new XAttribute("count", resources.Count),
                new XAttribute("cli_version", SkillStateStore.CliVersion),
                new XAttribute("previous_baseline_version", previousVersion ?? "unknown"),
                new XAttribute("state_file", Path.Combine(directory, SkillStateStore.FileName)),
                new XAttribute("history_preserved", records),
                resources.Keys.Select(path => new XElement("file", new XAttribute("path", SkillPath(directory, path)))),
                new XElement("replaced-local-changes", replacedLocal.Select(path => new XElement("file", new XAttribute("path", path))))));
        }
        else
        {
            Console.Out.WriteLine($"Successfully synchronized {resources.Count} skill files to: {directory}");
            Console.Out.WriteLine($"Baseline: {previousVersion ?? "unknown"} -> {SkillStateStore.CliVersion}");
            Console.Out.WriteLine($"State file: {Path.Combine(directory, SkillStateStore.FileName)}");
            Console.Out.WriteLine($"History preserved: {records.ToString(CultureInfo.InvariantCulture)} records");
            foreach (var path in replacedLocal) Console.Out.WriteLine($"Replaced local changes: {path}");
        }
        return 0;
    }

    private static Command BuildRecordCommand()
    {
        var command = new Command("record", "Append an optional free-text customization or upgrade summary; never changes the sync baseline");
        var summaryOption = new Option<string>("--summary") { Required = true, Description = "Why the Skill changed and what changed; no prescribed template" };
        var directoryOption = DirectoryOption();
        var formatOption = FormatOption();
        command.Add(summaryOption);
        command.Add(directoryOption);
        command.Add(formatOption);
        command.SetAction(parse =>
        {
            var format = WorkspaceCommand.ResolveFormat(parse.GetValue(formatOption));
            return Execute("skill record", format, () =>
            {
                var directory = DirectoryPath(parse.GetValue(directoryOption));
                using var lease = SkillStateStore.AcquireWriter(directory);
                var metadata = SkillStateStore.Read(directory);
                var record = SkillStateStore.AddRecord(metadata, parse.GetValue(summaryOption)!);
                SkillStateStore.Write(directory, metadata);
                if (format == OutputFormat.Xml)
                    WriteXml(new XElement("skill-record", new XAttribute("state_file", Path.Combine(directory, SkillStateStore.FileName)), new XElement(record)));
                else
                {
                    Console.Out.WriteLine($"Skill history recorded: {(string?)record.Attribute("id")}");
                    Console.Out.WriteLine(record.Element("summary")!.Value);
                }
                return 0;
            });
        });
        return command;
    }

    private static Command BuildRecordsCommand()
    {
        var command = new Command("records", "Read recent optional Skill summaries without modifying files");
        var lastOption = new Option<int>("--last") { DefaultValueFactory = _ => 5, Description = "Number of recent records (default: 5)" };
        var directoryOption = DirectoryOption();
        var formatOption = FormatOption();
        command.Add(lastOption);
        command.Add(directoryOption);
        command.Add(formatOption);
        command.SetAction(parse =>
        {
            var format = WorkspaceCommand.ResolveFormat(parse.GetValue(formatOption));
            return Execute("skill records", format, () =>
            {
                var last = parse.GetValue(lastOption);
                if (last < 1) throw new ArgumentException("--last must be positive.");
                var directory = DirectoryPath(parse.GetValue(directoryOption));
                var records = SkillStateStore.Read(directory).Root!.Element("history")?.Elements("record").ToList() ?? [];
                var selected = records.TakeLast(last).ToList();
                if (format == OutputFormat.Xml)
                    WriteXml(new XElement("skill-records", new XAttribute("directory", directory), new XAttribute("total", records.Count),
                        new XAttribute("count", selected.Count), selected.Select(record => new XElement(record))));
                else if (selected.Count == 0) Console.Out.WriteLine("No Skill history records. Recording customization history is optional.");
                else foreach (var record in selected)
                {
                    Console.Out.WriteLine($"{(string?)record.Attribute("id")} | {(string?)record.Attribute("created_at")} | CLI {(string?)record.Attribute("cli_version")}");
                    Console.Out.WriteLine(record.Element("summary")?.Value);
                }
                return 0;
            });
        });
        return command;
    }

    private static Command BuildDiffCommand()
    {
        var command = new Command("diff", "Review local-to-embedded Skill differences without writing files or requiring Git");
        var directoryOption = DirectoryOption();
        var fileOption = new Option<string?>("--file") { Description = "Limit to one managed relative path" };
        var formatOption = new Option<string>("--format") { DefaultValueFactory = _ => "unified" };
        formatOption.AcceptOnlyFromAmong("unified", "xml", "human");
        command.Add(directoryOption);
        command.Add(fileOption);
        command.Add(formatOption);
        command.SetAction(parse =>
        {
            var xml = parse.GetValue(formatOption) == "xml";
            return Execute("skill diff", xml ? OutputFormat.Xml : OutputFormat.Human, () =>
            {
                var inspection = SkillInspection.Inspect(DirectoryPath(parse.GetValue(directoryOption)));
                var file = parse.GetValue(fileOption)?.Replace('\\', '/');
                var pathComparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
                if (file != null && !EmbeddedResources.SkillFilePaths.Contains(file, pathComparer) &&
                    SkillStateStore.ReadBaseline(inspection.Metadata)?.ContainsKey(file) != true)
                    throw new ArgumentException("--file must name a current or baseline-managed Skill file.");
                var root = new XElement("skill-diff", new XAttribute("directory", inspection.Directory), new XAttribute("direction", "local-to-embedded"));
                foreach (var item in inspection.Files.Where(item => (item.Managed || item.Provenance == "retired") && (file == null || pathComparer.Equals(file, item.Path))))
                {
                    var path = SkillPath(inspection.Directory, item.Path);
                    var before = File.Exists(path) ? SkillInspection.ReadText(path) : null;
                    var after = item.Managed ? EmbeddedResources.GetSkillText(item.Path) : null;
                    var diff = UnifiedDiff.Create(item.Path, before, after);
                    if (diff.Length == 0) continue;
                    root.Add(new XElement("file", new XAttribute("path", item.Path), new XAttribute("provenance", item.Provenance),
                        new XAttribute("sync_action", item.Managed ? "write" : "preserve"), new XElement("diff", diff)));
                }
                root.SetAttributeValue("changed", root.Elements("file").Count());
                if (xml) WriteXml(root);
                else foreach (var item in root.Elements("file"))
                {
                    if ((string?)item.Attribute("sync_action") == "preserve")
                        Console.Out.WriteLine($"# Retired file '{(string?)item.Attribute("path")}' is preserved by sync; removal in this comparison is informational.");
                    Console.Out.Write(item.Element("diff")!.Value);
                }
                return root.HasElements ? 1 : 0;
            });
        });
        return command;
    }

    private static void WriteStatus(SkillInspection result, OutputFormat format)
    {
        var matching = result.Files.Count(file => file.State == "matching");
        var modified = result.Files.Count(file => file.State == "modified");
        var missing = result.Files.Count(file => file.State == "missing");
        var extra = result.Files.Count(file => file.State == "extra");
        if (format == OutputFormat.Xml)
        {
            WriteXml(new XElement("skill-status", new XAttribute("directory", result.Directory), new XAttribute("in_sync", result.InSync),
                new XAttribute("matching", matching), new XAttribute("modified", modified), new XAttribute("missing", missing), new XAttribute("extra", extra),
                new XAttribute("cli_version", SkillStateStore.CliVersion), new XAttribute("baseline_version", result.BaselineVersion ?? "unknown"),
                new XAttribute("baseline_available", result.HasBaseline), new XAttribute("local_changes", result.LocalChanges),
                new XAttribute("unknown_local_changes", result.Files.Count(file => file.Managed && file.LocalChanged == null)),
                new XAttribute("embedded_changes", result.EmbeddedChanges), new XAttribute("automatic_upgrade", result.AutomaticUpgrade),
                result.Files.Select(file => new XElement("file", new XAttribute("path", file.Path), new XAttribute("state", file.State),
                    new XAttribute("managed", file.Managed), new XAttribute("provenance", file.Provenance),
                    new XAttribute("local_changed", BooleanText(file.LocalChanged)), new XAttribute("embedded_changed", BooleanText(file.EmbeddedChanged))))));
        }
        else
        {
            Console.Out.WriteLine($"Skill status: {(result.InSync ? "in sync" : "differences found")}");
            Console.Out.WriteLine($"Directory: {result.Directory}\nCLI version: {SkillStateStore.CliVersion}\nBaseline version: {result.BaselineVersion ?? "unknown"}");
            Console.Out.WriteLine($"Matching: {matching}, modified: {modified}, missing: {missing}, extra: {extra}");
            Console.Out.WriteLine($"New embedded changes: {(result.HasBaseline ? result.EmbeddedChanges.ToString(CultureInfo.InvariantCulture) : "unknown")}");
            Console.Out.WriteLine($"Local changes: {(result.HasBaseline ? result.LocalChanges.ToString(CultureInfo.InvariantCulture) : "unknown")}");
            foreach (var file in result.Files.Where(file => file.State != "matching"))
                Console.Out.WriteLine($"  {file.Path}: {file.State}, {file.Provenance}");
            Console.Out.WriteLine($"Automatic upgrade: {result.AutomaticUpgrade} (advisory; synchronization remains the caller's choice)");
        }
    }

    private static string BooleanText(bool? value) => value.HasValue ? XmlConvert.ToString(value.Value) : "unknown";
    private static IEnumerable<XNode> GuideContent(string text)
    {
        // Preserve readable CDATA while entitizing CR separately for exact XML round trips.
        var parts = text.Split('\r');
        for (var index = 0; index < parts.Length; index++)
        {
            if (index > 0) yield return new XText("\r");
            yield return new XCData(parts[index]);
        }
    }
    private static string SkillPath(string directory, string relativePath) => Path.Combine(directory, relativePath.Replace('/', Path.DirectorySeparatorChar));
    private static string DirectoryPath(string? value) => Path.GetFullPath(value ?? Path.Combine(Environment.CurrentDirectory, ".agents", "skills", "dogdouspec"));
    private static Option<string?> DirectoryOption(bool required = false) => new("--output-dir")
    {
        Required = required, Description = "Skill directory (default: .agents/skills/dogdouspec relative to the current directory)"
    };
    private static Option<string?> FormatOption()
    {
        var option = new Option<string?>("--format") { Description = "Output format (xml or human)" };
        option.AcceptOnlyFromAmong("xml", "human");
        return option;
    }
    internal static void WriteXml(XElement root)
    {
        using var buffer = new MemoryStream();
        using (var writer = XmlWriter.Create(buffer, new XmlWriterSettings
        {
            Encoding = new UTF8Encoding(false), Indent = true, IndentChars = "  ",
            NewLineChars = "\n", NewLineHandling = NewLineHandling.Entitize
        }))
        {
            writer.WriteStartDocument();
            root.WriteTo(writer);
            writer.WriteEndDocument();
        }
        Console.Out.Write(Encoding.UTF8.GetString(buffer.ToArray()) + "\n");
    }

    private static int Execute(string command, OutputFormat format, Func<int> action)
    {
        try { return action(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or XmlException or ArgumentException or NotSupportedException)
        {
            var code = ex is XmlException ? DiagnosticCodes.XmlParseError :
                ex is ArgumentException and not DecoderFallbackException ? DiagnosticCodes.InvalidArgument : DiagnosticCodes.FilesystemError;
            Console.Error.Write(new DiagnosticsEnvelope(command, Diagnostic.Error(code, ex.Message)).Format(format));
            return 2;
        }
    }
}
