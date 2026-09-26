using System;
using System.CommandLine;
using System.Globalization;
using System.Linq;
using System.Security;
using System.Text;
using DogdouSpec.Core.Diagnostics;
using DogdouSpec.Core.Formatting;
using DogdouSpec.Core.Resources;

namespace DogdouSpec.Cli.Commands;

public static class TemplateCommand
{
    public static Command BuildCommand()
    {
        var templateCmd = new Command("template", "Inspect DogdouSpec templates");
        templateCmd.Add(BuildListCommand());
        templateCmd.Add(BuildShowCommand());
        return templateCmd;
    }

    private static Command BuildListCommand()
    {
        var listCmd = new Command("list", "List registered templates with their one-line summaries");

        var versionOption = new Option<string>("--version")
        {
            Description = "Template version (default: 1.0)",
            DefaultValueFactory = _ => "1.0"
        };

        var formatOption = new Option<string?>("--format")
        {
            Description = "Output format (human or xml)"
        };
        formatOption.AcceptOnlyFromAmong("human", "xml");

        listCmd.Add(versionOption);
        listCmd.Add(formatOption);

        listCmd.SetAction(parseResult =>
        {
            var version = parseResult.GetValue(versionOption) ?? "1.0";
            var formatArg = parseResult.GetValue(formatOption);
            var format = WorkspaceCommand.ResolveFormat(formatArg);

            if (!EmbeddedResources.IsVersionSupported(version))
            {
                var envelope = new DiagnosticsEnvelope("template list", Diagnostic.Error(
                    DiagnosticCodes.UnsupportedVersion,
                    $"Template version '{version}' is not supported. Supported versions: {string.Join(", ", EmbeddedResources.SupportedVersions)}."));
                Console.Error.Write(envelope.Format(format));
                return 2;
            }

            var templates = TemplateCatalog.Templates;

            if (format == OutputFormat.Xml)
            {
                var sb = new StringBuilder();
                sb.AppendLine("<?xml version=\"1.0\" encoding=\"utf-8\"?>");
                sb.AppendLine(CultureInfo.InvariantCulture, $"<templates version=\"{SecurityElement.Escape(version)}\">");
                foreach (var t in templates)
                {
                    sb.AppendLine(CultureInfo.InvariantCulture, $"  <template name=\"{SecurityElement.Escape(t.Name)}\" root_element=\"{SecurityElement.Escape(t.RootElement)}\">");
                    sb.AppendLine(CultureInfo.InvariantCulture, $"    <summary>{SecurityElement.Escape(t.Summary)}</summary>");
                    sb.AppendLine("  </template>");
                }
                sb.AppendLine("</templates>");
                Console.Out.Write(sb.ToString());
            }
            else
            {
                var maxNameLen = templates.Max(t => t.Name.Length);
                var sb = new StringBuilder();
                sb.AppendLine(CultureInfo.InvariantCulture, $"Available templates (version {version}):");
                foreach (var t in templates)
                {
                    sb.AppendLine(CultureInfo.InvariantCulture, $"  {t.Name.PadRight(maxNameLen + 4)}{t.Summary}");
                }
                Console.Out.Write(sb.ToString());
            }

            return 0;
        });

        return listCmd;
    }

    private static Command BuildShowCommand()
    {
        var showCmd = new Command("show", "Display the exact template XML resource to stdout");

        var nameOption = new Option<string>("--name")
        {
            Description = "Template name (e.g. record.discussion, record.finding, record.verification, task.update, transaction.apply, iteration.confirmation, knowledge.entry, backlog.item)",
            Required = true
        };

        var versionOption = new Option<string>("--version")
        {
            Description = "Template version (default: 1.0)",
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
                var envelope = new DiagnosticsEnvelope("template show", Diagnostic.Error(
                    DiagnosticCodes.InvalidArgument,
                    "Template name must be specified."));
                Console.Error.Write(envelope.Format(format));
                return 2;
            }

            if (!EmbeddedResources.IsVersionSupported(version))
            {
                var envelope = new DiagnosticsEnvelope("template show", Diagnostic.Error(
                    DiagnosticCodes.UnsupportedVersion,
                    $"Template version '{version}' is not supported. Supported versions: {string.Join(", ", EmbeddedResources.SupportedVersions)}."));
                Console.Error.Write(envelope.Format(format));
                return 2;
            }

            var text = EmbeddedResources.GetTemplateText(name, version);
            if (text == null)
            {
                var envelope = new DiagnosticsEnvelope("template show", Diagnostic.Error(
                    DiagnosticCodes.ResourceNotFound,
                    $"Template '{name}' (version {version}) was not found. Available templates: {string.Join(", ", EmbeddedResources.TemplateNames)}."));
                Console.Error.Write(envelope.Format(format));
                return 2;
            }

            Console.Out.Write(text);
            return 0;
        });

        return showCmd;
    }
}
