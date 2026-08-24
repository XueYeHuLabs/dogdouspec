using System.CommandLine;
using DogdouSpec.Core.Diagnostics;
using DogdouSpec.Core.Formatting;
using DogdouSpec.Core.Security;
using DogdouSpec.Core.Validation;
using DogdouSpec.Core.Workspace;

namespace DogdouSpec.Cli.Commands;

public static class ValidateCommand
{
    public static Command BuildCommand()
    {
        var validateCmd = new Command("validate", "Validate managed XML documents against authoritative schemas");

        var iterationOption = new Option<string?>("--iteration")
        {
            Description = "Iteration identifier (YYYYMMDD-name) to restrict validation to"
        };

        var documentOption = new Option<string?>("--document")
        {
            Description = "Relative path to a single managed document to validate (relative to .dogdouspec)"
        };

        var workspaceRootOption = new Option<string?>("--workspace-root")
        {
            Description = "Explicit path to workspace root or project directory containing .dogdouspec"
        };

        var formatOption = new Option<string?>("--format")
        {
            Description = "Output format (xml or human)"
        };
        formatOption.AcceptOnlyFromAmong("xml", "human");

        validateCmd.Add(iterationOption);
        validateCmd.Add(documentOption);
        validateCmd.Add(workspaceRootOption);
        validateCmd.Add(formatOption);

        validateCmd.SetAction(parseResult =>
        {
            var iterationId = parseResult.GetValue(iterationOption);
            var documentPath = parseResult.GetValue(documentOption);
            var workspaceRoot = parseResult.GetValue(workspaceRootOption);
            var formatArg = parseResult.GetValue(formatOption);
            var format = WorkspaceCommand.ResolveFormat(formatArg);

            // Mutual exclusivity check
            if (!string.IsNullOrWhiteSpace(iterationId) && !string.IsNullOrWhiteSpace(documentPath))
            {
                var envelope = new DiagnosticsEnvelope("validate", Diagnostic.Error(
                    DiagnosticCodes.InvalidArgument,
                    "--iteration and --document options are mutually exclusive and cannot be used together."));
                Console.Error.Write(envelope.Format(format));
                return 2;
            }

            // Option grammar validations
            if (!string.IsNullOrWhiteSpace(iterationId))
            {
                var (isValidIter, _, iterError) = PathSecurity.ValidateIterationId(iterationId);
                if (!isValidIter || iterError != null)
                {
                    var envelope = new DiagnosticsEnvelope("validate", iterError!);
                    Console.Error.Write(envelope.Format(format));
                    return 2;
                }
            }

            if (!string.IsNullOrWhiteSpace(documentPath))
            {
                var (isValidDoc, _, docError) = PathSecurity.ValidateRelativeDocumentPath(documentPath);
                if (!isValidDoc || docError != null)
                {
                    var envelope = new DiagnosticsEnvelope("validate", docError!);
                    Console.Error.Write(envelope.Format(format));
                    return 2;
                }
            }

            var (success, discoveredRoot, discoverError) = WorkspaceDiscovery.FindWorkspaceRoot(
                workspaceRoot,
                Environment.CurrentDirectory);

            if (!success || discoverError != null)
            {
                var envelope = new DiagnosticsEnvelope("validate", discoverError!);
                Console.Error.Write(envelope.Format(format));
                return 2;
            }

            var result = SchemaValidator.Validate(
                discoveredRoot,
                iterationId,
                documentPath,
                version: "1.0");

            if (result.IsValid)
            {
                if (format == OutputFormat.Xml)
                {
                    Console.Out.Write(result.ToSuccessXmlString());
                }
                else
                {
                    Console.Out.Write(result.ToSuccessHumanString());
                }
                return 0;
            }

            var diagEnvelope = result.CreateDiagnosticsEnvelope("validate");
            Console.Error.Write(diagEnvelope.Format(format));
            return 3;
        });

        return validateCmd;
    }
}
