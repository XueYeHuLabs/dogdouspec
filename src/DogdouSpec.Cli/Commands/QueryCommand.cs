using System.CommandLine;
using System.Xml;
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
            Description = "XPath 1.0 expression; use --xpath-file to avoid shell path rewriting"
        };
        var xpathFileOption = new Option<string?>("--xpath-file") { Description = "Read the expression from a file instead of --xpath" };
        var warnEmptyOption = new Option<bool>("--warn-empty") { Description = "Emit an advisory structure hint on stderr when a node-set is empty; exit status stays successful" };

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
        queryCmd.Add(xpathFileOption);
        queryCmd.Add(warnEmptyOption);
        queryCmd.Add(varOption);
        queryCmd.Add(workspaceRootOption);
        queryCmd.Add(formatOption);

        queryCmd.SetAction(parseResult =>
        {
            var iterationId = parseResult.GetValue(iterationOption);
            var documentPath = parseResult.GetValue(documentOption);
            var (xpath, xpathError) = XPathInput.Read(parseResult.GetValue(xpathOption), parseResult.GetValue(xpathFileOption));
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

            if (xpathError != null)
            {
                var envelope = new DiagnosticsEnvelope("query", xpathError);
                Console.Error.Write(envelope.Format(format));
                return envelope.GetExitCode();
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
                var result = XPathQueryEngine.EvaluateDocument(discoveredRoot, managedDoc, xpath!, variables);
                if (parseResult.GetValue(warnEmptyOption) && result.ResultType == XPathResultKind.NodeSet && result.Nodes.Count == 0)
                {
                    var hint = DescribeStructure(fullPath);
                    Console.Error.Write(new DiagnosticsEnvelope("query", Diagnostic.Warning(DiagnosticCodes.EmptyQueryResult,
                        $"No nodes matched in '{normalizedDocPath}'. {hint}Check the XPath and variable values.", normalizedDocPath)).Format(format));
                }

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

    private static string DescribeStructure(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            using var reader = SecureXmlReaderFactory.CreateReader(stream);
            reader.MoveToContent();
            var root = reader.Name;
            var children = new HashSet<string>(StringComparer.Ordinal);
            while (reader.Read() && children.Count < 8)
                if (reader.Depth == 1 && reader.NodeType == XmlNodeType.Element) children.Add(reader.Name);
            return $"Root: /{root}; direct children: {string.Join(", ", children)}. ";
        }
        catch (Exception error) when (error is IOException or XmlException or UnauthorizedAccessException)
        {
            return "Document structure hint is unavailable. ";
        }
    }
}
