using System.CommandLine;
using System.Globalization;
using System.Text;
using System.Xml;
using DogdouSpec.Core.Diagnostics;
using DogdouSpec.Core.Formatting;
using DogdouSpec.Core.Resources;
using DogdouSpec.Core.Workspace;

namespace DogdouSpec.Cli.Commands;

public static class SchemaCommand
{
    public static Command BuildCommand()
    {
        var schemaCmd = new Command("schema", "Inspect embedded schemas and synchronize optional readable workspace copies");
        var showCmd = new Command("show", "Display the exact XSD schema resource to stdout");

        var nameOption = new Option<string>("--name")
        {
            Description = "Schema name (e.g. spec, tasks, knowledge, backlog, requests, common)",
            Required = true
        };

        var versionOption = new Option<string>("--version")
        {
            Description = "Schema version (default: 1.0)",
            DefaultValueFactory = _ => "1.0"
        };

        showCmd.Add(nameOption);
        showCmd.Add(versionOption);

        showCmd.SetAction(parseResult =>
        {
            var name = parseResult.GetValue(nameOption);
            var version = parseResult.GetValue(versionOption) ?? "1.0";

            var format = WorkspaceCommand.ResolveFormat(null);

            if (string.IsNullOrWhiteSpace(name))
            {
                var envelope = new DiagnosticsEnvelope("schema show", Diagnostic.Error(
                    DiagnosticCodes.InvalidArgument,
                    "Schema name must be specified."));
                Console.Error.Write(envelope.Format(format));
                return 2;
            }

            if (!EmbeddedResources.IsVersionSupported(version))
            {
                var envelope = new DiagnosticsEnvelope("schema show", Diagnostic.Error(
                    DiagnosticCodes.UnsupportedVersion,
                    $"Schema version '{version}' is not supported. Supported versions: {string.Join(", ", EmbeddedResources.SupportedVersions)}."));
                Console.Error.Write(envelope.Format(format));
                return 2;
            }

            var text = EmbeddedResources.GetSchemaText(name, version);
            if (text == null)
            {
                var envelope = new DiagnosticsEnvelope("schema show", Diagnostic.Error(
                    DiagnosticCodes.ResourceNotFound,
                    $"Schema '{name}' (version {version}) was not found. Available schemas: {string.Join(", ", EmbeddedResources.SchemaNames)}."));
                Console.Error.Write(envelope.Format(format));
                return 2;
            }

            Console.Out.Write(text);
            return 0;
        });

        schemaCmd.Add(showCmd);
        schemaCmd.Add(BuildStatusCommand());
        schemaCmd.Add(BuildSyncCommand());
        return schemaCmd;
    }

    private static Command BuildStatusCommand()
    {
        var command = new Command("status", "Compare readable workspace schema copies with embedded authoritative schemas without modifying files");
        var workspaceRootOption = CreateWorkspaceRootOption();
        var versionOption = new Option<string>("--version")
        {
            Description = "Embedded schema version to compare (default: 1.0)",
            DefaultValueFactory = _ => "1.0"
        };
        var formatOption = CreateFormatOption();
        command.Add(workspaceRootOption);
        command.Add(versionOption);
        command.Add(formatOption);

        command.SetAction(parseResult =>
        {
            var format = WorkspaceCommand.ResolveFormat(parseResult.GetValue(formatOption));
            var (found, workspaceRoot, discoveryError) = WorkspaceDiscovery.FindWorkspaceRoot(
                parseResult.GetValue(workspaceRootOption),
                Environment.CurrentDirectory);
            if (!found || discoveryError != null)
            {
                Console.Error.Write(new DiagnosticsEnvelope("schema status", discoveryError!).Format(format));
                return 2;
            }

            var version = parseResult.GetValue(versionOption) ?? "1.0";
            var (success, result, diagnostics) = WorkspaceSchemaCopies.Inspect(workspaceRoot, version);
            if (!success || result == null)
            {
                var envelope = new DiagnosticsEnvelope("schema status", diagnostics);
                Console.Error.Write(envelope.Format(format));
                return envelope.GetExitCode();
            }

            Console.Out.Write(FormatStatus(result, format));
            return result.InSync ? 0 : 1;
        });

        return command;
    }

    private static Command BuildSyncCommand()
    {
        var command = new Command("sync", "Atomically refresh known readable workspace schema copies from embedded authoritative schemas");
        var workspaceRootOption = CreateWorkspaceRootOption();
        var expectedVersionOption = new Option<string>("--expected-version")
        {
            Description = "Expected schema_version for every managed document",
            Required = true
        };
        var formatOption = CreateFormatOption();
        command.Add(workspaceRootOption);
        command.Add(expectedVersionOption);
        command.Add(formatOption);

        command.SetAction(parseResult =>
        {
            var format = WorkspaceCommand.ResolveFormat(parseResult.GetValue(formatOption));
            var (found, workspaceRoot, discoveryError) = WorkspaceDiscovery.FindWorkspaceRoot(
                parseResult.GetValue(workspaceRootOption),
                Environment.CurrentDirectory);
            if (!found || discoveryError != null)
            {
                Console.Error.Write(new DiagnosticsEnvelope("schema sync", discoveryError!).Format(format));
                return 2;
            }

            var expectedVersion = parseResult.GetValue(expectedVersionOption);
            if (string.IsNullOrWhiteSpace(expectedVersion))
            {
                var envelope = new DiagnosticsEnvelope("schema sync", Diagnostic.Error(
                    DiagnosticCodes.InvalidArgument,
                    "Expected schema version must be specified."));
                Console.Error.Write(envelope.Format(format));
                return 2;
            }

            var (success, result, diagnostics) = WorkspaceSchemaCopies.Sync(workspaceRoot, expectedVersion);
            if (!success || result == null)
            {
                var envelope = new DiagnosticsEnvelope("schema sync", diagnostics);
                Console.Error.Write(envelope.Format(format));
                return envelope.GetExitCode();
            }

            Console.Out.Write(FormatSync(result, format));
            return 0;
        });

        return command;
    }

    private static Option<string?> CreateWorkspaceRootOption() => new("--workspace-root")
    {
        Description = "Explicit path to workspace root or project directory containing .dogdouspec"
    };

    private static Option<string?> CreateFormatOption()
    {
        var option = new Option<string?>("--format")
        {
            Description = "Output format (xml or human)"
        };
        option.AcceptOnlyFromAmong("xml", "human");
        return option;
    }

    private static string FormatStatus(SchemaCopyStatusResult result, OutputFormat format)
    {
        if (format != OutputFormat.Xml)
        {
            var sb = new StringBuilder();
            sb.Append("Schema-copy status: ").AppendLine(result.InSync ? "in sync" : "differences found");
            sb.Append("Workspace: ").AppendLine(result.WorkspaceRoot);
            sb.Append("Version: ").AppendLine(result.Version);
            sb.Append("Matching: ").Append(result.Matching.ToString(CultureInfo.InvariantCulture))
                .Append(", modified: ").Append(result.Modified.ToString(CultureInfo.InvariantCulture))
                .Append(", missing: ").AppendLine(result.Missing.ToString(CultureInfo.InvariantCulture));
            foreach (var file in result.Files.Where(file => file.State != "matching"))
            {
                sb.Append("  ").Append(file.State).Append(": ").AppendLine(file.Path);
            }
            return sb.ToString();
        }

        return WriteXml(writer =>
        {
            writer.WriteStartElement("schema-status");
            WriteStatusAttributes(writer, result);
            WriteStatusFiles(writer, result);
            writer.WriteEndElement();
        });
    }

    private static string FormatSync(SchemaCopySyncResult result, OutputFormat format)
    {
        if (format != OutputFormat.Xml)
        {
            return $"Schema copies synchronized: {result.Changed.ToString(CultureInfo.InvariantCulture)} changed; version {result.Version}; workspace {result.WorkspaceRoot}{Environment.NewLine}";
        }

        return WriteXml(writer =>
        {
            writer.WriteStartElement("schema-sync");
            writer.WriteAttributeString("workspace", result.WorkspaceRoot);
            writer.WriteAttributeString("version", result.Version);
            writer.WriteAttributeString("changed", result.Changed.ToString(CultureInfo.InvariantCulture));
            writer.WriteAttributeString("in_sync", result.Status.InSync ? "true" : "false");
            WriteStatusFiles(writer, result.Status);
            writer.WriteEndElement();
        });
    }

    private static void WriteStatusAttributes(XmlWriter writer, SchemaCopyStatusResult result)
    {
        writer.WriteAttributeString("workspace", result.WorkspaceRoot);
        writer.WriteAttributeString("version", result.Version);
        writer.WriteAttributeString("in_sync", result.InSync ? "true" : "false");
        writer.WriteAttributeString("matching", result.Matching.ToString(CultureInfo.InvariantCulture));
        writer.WriteAttributeString("modified", result.Modified.ToString(CultureInfo.InvariantCulture));
        writer.WriteAttributeString("missing", result.Missing.ToString(CultureInfo.InvariantCulture));
    }

    private static void WriteStatusFiles(XmlWriter writer, SchemaCopyStatusResult result)
    {
        foreach (var file in result.Files)
        {
            writer.WriteStartElement("file");
            writer.WriteAttributeString("name", file.Name);
            writer.WriteAttributeString("path", file.Path);
            writer.WriteAttributeString("state", file.State);
            writer.WriteEndElement();
        }
    }

    private static string WriteXml(Action<XmlWriter> writeBody)
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
            writeBody(writer);
            writer.WriteEndDocument();
        }
        return Encoding.UTF8.GetString(ms.ToArray()) + "\n";
    }
}
