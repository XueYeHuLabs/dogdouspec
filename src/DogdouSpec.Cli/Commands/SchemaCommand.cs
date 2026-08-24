using System.CommandLine;
using DogdouSpec.Core.Diagnostics;
using DogdouSpec.Core.Formatting;
using DogdouSpec.Core.Resources;

namespace DogdouSpec.Cli.Commands;

public static class SchemaCommand
{
    public static Command BuildCommand()
    {
        var schemaCmd = new Command("schema", "Inspect DogdouSpec schemas");
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
        return schemaCmd;
    }
}
