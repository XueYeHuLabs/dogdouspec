using System.CommandLine;
using DogdouSpec.Core.Diagnostics;
using DogdouSpec.Core.Formatting;
using DogdouSpec.Core.Security;
using DogdouSpec.Core.Workspace;
using DogdouSpec.Core.XPath;

namespace DogdouSpec.Cli.Commands;

public static class QueryCommand
{
    public static Command BuildCommand()
    {
        var queryCmd = new Command("query", "Evaluate an XPath 1.0 expression against a single managed document");

        var iterationOption = new Option<string?>("--iteration")
        {
            Description = "Iteration identifier (YYYYMMDD-name) when querying iteration documents"
        };

        var documentOption = new Option<string?>("--document")
        {
            Description = "Relative path to managed document (relative to .dogdouspec, or shorthand spec.xml/tasks.xml with --iteration)",
            Required = true
        };

        var xpathOption = new Option<string?>("--xpath")
        {
            Description = "XPath 1.0 expression to evaluate",
            Required = true
        };

        var varOption = new Option<string[]>("--var")
        {
            Description = "XPath string variable in name=value format (repeatable)",
            AllowMultipleArgumentsPerToken = false
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

        queryCmd.Add(iterationOption);
        queryCmd.Add(documentOption);
        queryCmd.Add(xpathOption);
        queryCmd.Add(varOption);
        queryCmd.Add(workspaceRootOption);
        queryCmd.Add(formatOption);

        queryCmd.SetAction(parseResult =>
        {
            var iterationId = parseResult.GetValue(iterationOption);
            var documentPath = parseResult.GetValue(documentOption);
            var xpath = parseResult.GetValue(xpathOption);
            var rawVars = parseResult.GetValue(varOption);
            var workspaceRoot = parseResult.GetValue(workspaceRootOption);
            var formatArg = parseResult.GetValue(formatOption);
            var format = WorkspaceCommand.ResolveFormat(formatArg);

            if (string.IsNullOrWhiteSpace(documentPath))
            {
                var envelope = new DiagnosticsEnvelope("query", Diagnostic.Error(
                    DiagnosticCodes.InvalidArgument,
                    "--document option is required."));
                Console.Error.Write(envelope.Format(format));
                return 2;
            }

            var (isAddrValid, normalizedDocPath, _, addrError) = DocumentAddressResolver.Resolve(
                iterationId,
                documentPath,
                requireDocument: true);

            if (!isAddrValid || addrError != null || string.IsNullOrWhiteSpace(normalizedDocPath))
            {
                var envelope = new DiagnosticsEnvelope("query", addrError ?? Diagnostic.Error(
                    DiagnosticCodes.InvalidArgument,
                    "Invalid document address."));
                Console.Error.Write(envelope.Format(format));
                return 2;
            }

            if (string.IsNullOrWhiteSpace(xpath))
            {
                var envelope = new DiagnosticsEnvelope("query", Diagnostic.Error(
                    DiagnosticCodes.InvalidArgument,
                    "--xpath expression is required and cannot be empty."));
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
                var envelope = new DiagnosticsEnvelope("query", ex.ToDiagnostic());
                Console.Error.Write(envelope.Format(format));
                return ex.ExitCode;
            }

            var (success, discoveredRoot, discoverError) = WorkspaceDiscovery.FindWorkspaceRoot(
                workspaceRoot,
                Environment.CurrentDirectory);

            if (!success || discoverError != null)
            {
                var envelope = new DiagnosticsEnvelope("query", discoverError!);
                Console.Error.Write(envelope.Format(format));
                return 2;
            }

            var fullPath = Path.Combine(discoveredRoot, normalizedDocPath.Replace('/', Path.DirectorySeparatorChar));
            var (isSafe, safeError) = PathSecurity.CheckContainmentAndReparsePoints(discoveredRoot, fullPath);
            if (!isSafe || safeError != null)
            {
                var envelope = new DiagnosticsEnvelope("query", safeError!);
                Console.Error.Write(envelope.Format(format));
                return 2;
            }

            if (!File.Exists(fullPath))
            {
                var envelope = new DiagnosticsEnvelope("query", Diagnostic.Error(
                    DiagnosticCodes.DocumentNotFound,
                    $"Document '{normalizedDocPath}' does not exist.",
                    normalizedDocPath));
                Console.Error.Write(envelope.Format(format));
                return 2;
            }

            var managedDoc = new ManagedDocument(normalizedDocPath, fullPath);

            try
            {
                var result = XPathQueryEngine.EvaluateDocument(discoveredRoot, managedDoc, xpath, variables);

                if (format == OutputFormat.Xml)
                {
                    var xml = XPathResultFormatter.FormatQueryXml(result);
                    Console.Out.Write(xml);
                }
                else
                {
                    var human = XPathResultFormatter.FormatQueryHuman(result);
                    Console.Out.Write(human);
                }

                return 0;
            }
            catch (DogdouXPathException ex)
            {
                var envelope = new DiagnosticsEnvelope("query", ex.ToDiagnostic());
                Console.Error.Write(envelope.Format(format));
                return ex.ExitCode;
            }
            catch (Exception ex)
            {
                var envelope = new DiagnosticsEnvelope("query", Diagnostic.Error(
                    DiagnosticCodes.InvalidArgument,
                    $"Query execution failed: {ex.Message}"));
                Console.Error.Write(envelope.Format(format));
                return 2;
            }
        });

        return queryCmd;
    }
}
