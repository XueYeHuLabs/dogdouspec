using System.CommandLine;
using DogdouSpec.Core.Append;
using DogdouSpec.Core.Diagnostics;
using DogdouSpec.Core.Formatting;
using DogdouSpec.Core.Security;
using DogdouSpec.Core.Workspace;
using DogdouSpec.Core.XPath;

namespace DogdouSpec.Cli.Commands;

public static class AppendCommand
{
    public static Command BuildCommand()
    {
        var appendCmd = new Command("append", "Append a complete element to a managed document (mutating)");

        var documentOption = new Option<string?>("--document")
        {
            Description = "Relative path to managed document (relative to .dogdouspec)",
            Required = true
        };

        var parentXpathOption = new Option<string?>("--parent-xpath")
        {
            Description = "XPath 1.0 expression selecting exactly one parent element",
            Required = true
        };

        var varOption = new Option<string[]>("--var")
        {
            Description = "XPath string variable in name=value format (repeatable)",
            AllowMultipleArgumentsPerToken = false
        };

        var expectedRevisionOption = new Option<int?>("--expected-revision")
        {
            Description = "Expected positive integer revision of the target document",
            Required = true
        };

        var stdinOption = new Option<bool>("--stdin")
        {
            Description = "Read appended XML fragment from standard input"
        };

        var fileOption = new Option<string?>("--file")
        {
            Description = "Path to file containing appended XML fragment"
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

        appendCmd.Add(documentOption);
        appendCmd.Add(parentXpathOption);
        appendCmd.Add(varOption);
        appendCmd.Add(expectedRevisionOption);
        appendCmd.Add(stdinOption);
        appendCmd.Add(fileOption);
        appendCmd.Add(workspaceRootOption);
        appendCmd.Add(formatOption);

        appendCmd.SetAction(parseResult =>
        {
            var documentPath = parseResult.GetValue(documentOption);
            var parentXpath = parseResult.GetValue(parentXpathOption);
            var rawVars = parseResult.GetValue(varOption);
            var expectedRevision = parseResult.GetValue(expectedRevisionOption);
            var hasStdin = parseResult.GetValue(stdinOption);
            var filePath = parseResult.GetValue(fileOption);
            var workspaceRoot = parseResult.GetValue(workspaceRootOption);
            var formatArg = parseResult.GetValue(formatOption);
            var format = WorkspaceCommand.ResolveFormat(formatArg);

            // 1. Validate stdin vs file (mutually exclusive, exactly one required)
            if (hasStdin && !string.IsNullOrWhiteSpace(filePath))
            {
                var envelope = new DiagnosticsEnvelope("append", Diagnostic.Error(
                    DiagnosticCodes.InvalidArgument,
                    "Specify either --stdin or --file, not both."));
                Console.Error.Write(envelope.Format(format));
                return 2;
            }

            if (!hasStdin && string.IsNullOrWhiteSpace(filePath))
            {
                var envelope = new DiagnosticsEnvelope("append", Diagnostic.Error(
                    DiagnosticCodes.InvalidArgument,
                    "Either --stdin or --file must be specified."));
                Console.Error.Write(envelope.Format(format));
                return 2;
            }

            string fragmentXml;
            if (hasStdin)
            {
                fragmentXml = Console.In.ReadToEnd();
            }
            else
            {
                if (!File.Exists(filePath))
                {
                    var envelope = new DiagnosticsEnvelope("append", Diagnostic.Error(
                        DiagnosticCodes.InvalidArgument,
                        $"Appended XML file '{filePath}' does not exist."));
                    Console.Error.Write(envelope.Format(format));
                    return 2;
                }

                fragmentXml = File.ReadAllText(filePath!);
            }

            if (string.IsNullOrWhiteSpace(documentPath))
            {
                var envelope = new DiagnosticsEnvelope("append", Diagnostic.Error(
                    DiagnosticCodes.InvalidArgument,
                    "--document option is required."));
                Console.Error.Write(envelope.Format(format));
                return 2;
            }

            if (string.IsNullOrWhiteSpace(parentXpath))
            {
                var envelope = new DiagnosticsEnvelope("append", Diagnostic.Error(
                    DiagnosticCodes.InvalidArgument,
                    "--parent-xpath option is required."));
                Console.Error.Write(envelope.Format(format));
                return 2;
            }

            if (!expectedRevision.HasValue || expectedRevision.Value <= 0)
            {
                var envelope = new DiagnosticsEnvelope("append", Diagnostic.Error(
                    DiagnosticCodes.InvalidArgument,
                    "--expected-revision must be a positive integer."));
                Console.Error.Write(envelope.Format(format));
                return 2;
            }

            Dictionary<string, string> variables;
            try
            {
                variables = XPathVariables.Parse(rawVars);
            }
            catch (DogdouXPathException ex)
            {
                var envelope = new DiagnosticsEnvelope("append", ex.ToDiagnostic());
                Console.Error.Write(envelope.Format(format));
                return ex.ExitCode;
            }

            var (discoverSuccess, discoveredRoot, discoverError) = WorkspaceDiscovery.FindWorkspaceRoot(
                workspaceRoot,
                Environment.CurrentDirectory);

            if (!discoverSuccess || discoverError != null)
            {
                var envelope = new DiagnosticsEnvelope("append", discoverError!);
                Console.Error.Write(envelope.Format(format));
                return 2;
            }

            var (success, mutationEnvelope, diagnostics) = GenericAppender.Append(
                discoveredRoot,
                documentPath,
                parentXpath,
                expectedRevision.Value,
                fragmentXml,
                variables);

            if (!success || diagnostics.Count > 0)
            {
                var diagEnvelope = new DiagnosticsEnvelope("append", diagnostics);
                Console.Error.Write(diagEnvelope.Format(format));
                return diagEnvelope.GetExitCode();
            }

            if (mutationEnvelope != null)
            {
                Console.Out.Write(mutationEnvelope.Format(format));
            }

            return 0;
        });

        return appendCmd;
    }
}
