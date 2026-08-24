using System.CommandLine;
using DogdouSpec.Core.Diagnostics;
using DogdouSpec.Core.Formatting;
using DogdouSpec.Core.Resources;

namespace DogdouSpec.Cli.Commands;

public static class TemplateCommand
{
    public static Command BuildCommand()
    {
        var templateCmd = new Command("template", "Inspect DogdouSpec templates");
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

        templateCmd.Add(showCmd);
        return templateCmd;
    }
}
