using System.CommandLine;
using DogdouSpec.Core.Diagnostics;
using DogdouSpec.Core.Formatting;
using DogdouSpec.Core.Security;
using DogdouSpec.Core.Workspace;
using DogdouSpec.Core.XPath;

namespace DogdouSpec.Cli.Commands;

public static class SearchCommand
{
    public static Command BuildCommand()
    {
        var searchCmd = new Command("search", "Evaluate an XPath 1.0 expression independently across all managed documents in a scope");

        var scopeOption = new Option<string?>("--scope")
        {
            Description = "Search boundary scope: 'project' or 'iteration'",
            Required = true
        };
        scopeOption.AcceptOnlyFromAmong("project", "iteration");

        var iterationOption = new Option<string?>("--iteration")
        {
            Description = "Iteration identifier (required when scope is 'iteration', rejected when scope is 'project')"
        };

        var xpathOption = new Option<string?>("--xpath")
        {
            Description = "XPath 1.0 expression to evaluate against each managed document",
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

        searchCmd.Add(scopeOption);
        searchCmd.Add(iterationOption);
        searchCmd.Add(xpathOption);
        searchCmd.Add(varOption);
        searchCmd.Add(workspaceRootOption);
        searchCmd.Add(formatOption);

        searchCmd.SetAction(parseResult =>
        {
            var scope = parseResult.GetValue(scopeOption);
            var iterationId = parseResult.GetValue(iterationOption);
            var xpath = parseResult.GetValue(xpathOption);
            var rawVars = parseResult.GetValue(varOption);
            var workspaceRoot = parseResult.GetValue(workspaceRootOption);
            var formatArg = parseResult.GetValue(formatOption);
            var format = WorkspaceCommand.ResolveFormat(formatArg);

            if (string.IsNullOrWhiteSpace(scope))
            {
                var envelope = new DiagnosticsEnvelope("search", Diagnostic.Error(
                    DiagnosticCodes.InvalidArgument,
                    "--scope option is required (must be 'project' or 'iteration')."));
                Console.Error.Write(envelope.Format(format));
                return 2;
            }

            string? normalizedIterationId = null;
            if (string.Equals(scope, "iteration", StringComparison.OrdinalIgnoreCase))
            {
                if (string.IsNullOrWhiteSpace(iterationId))
                {
                    var envelope = new DiagnosticsEnvelope("search", Diagnostic.Error(
                        DiagnosticCodes.InvalidArgument,
                        "--iteration is required when search scope is 'iteration'."));
                    Console.Error.Write(envelope.Format(format));
                    return 2;
                }

                var (isValidIter, normIter, iterErr) = PathSecurity.ValidateIterationId(iterationId);
                if (!isValidIter || iterErr != null)
                {
                    var envelope = new DiagnosticsEnvelope("search", iterErr!);
                    Console.Error.Write(envelope.Format(format));
                    return 2;
                }
                normalizedIterationId = normIter;
            }
            else if (string.Equals(scope, "project", StringComparison.OrdinalIgnoreCase))
            {
                if (!string.IsNullOrWhiteSpace(iterationId))
                {
                    var envelope = new DiagnosticsEnvelope("search", Diagnostic.Error(
                        DiagnosticCodes.InvalidArgument,
                        "--iteration option cannot be specified when search scope is 'project'."));
                    Console.Error.Write(envelope.Format(format));
                    return 2;
                }
            }
            else
            {
                var envelope = new DiagnosticsEnvelope("search", Diagnostic.Error(
                    DiagnosticCodes.InvalidArgument,
                    $"Invalid search scope '{scope}'. Must be 'project' or 'iteration'."));
                Console.Error.Write(envelope.Format(format));
                return 2;
            }

            if (string.IsNullOrWhiteSpace(xpath))
            {
                var envelope = new DiagnosticsEnvelope("search", Diagnostic.Error(
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
                var envelope = new DiagnosticsEnvelope("search", ex.ToDiagnostic());
                Console.Error.Write(envelope.Format(format));
                return ex.ExitCode;
            }

            var (success, discoveredRoot, discoverError) = WorkspaceDiscovery.FindWorkspaceRoot(
                workspaceRoot,
                Environment.CurrentDirectory);

            if (!success || discoverError != null)
            {
                var envelope = new DiagnosticsEnvelope("search", discoverError!);
                Console.Error.Write(envelope.Format(format));
                return 2;
            }

            try
            {
                var searchResult = XPathQueryEngine.EvaluateSearch(
                    discoveredRoot,
                    scope.ToLowerInvariant(),
                    normalizedIterationId,
                    xpath,
                    variables);

                if (format == OutputFormat.Xml)
                {
                    var xml = XPathResultFormatter.FormatSearchXml(searchResult);
                    Console.Out.Write(xml);
                }
                else
                {
                    var human = XPathResultFormatter.FormatSearchHuman(searchResult);
                    Console.Out.Write(human);
                }

                return 0;
            }
            catch (DogdouXPathException ex)
            {
                var envelope = new DiagnosticsEnvelope("search", ex.ToDiagnostic());
                Console.Error.Write(envelope.Format(format));
                return ex.ExitCode;
            }
            catch (Exception ex)
            {
                var envelope = new DiagnosticsEnvelope("search", Diagnostic.Error(
                    DiagnosticCodes.InvalidArgument,
                    $"Search execution failed: {ex.Message}"));
                Console.Error.Write(envelope.Format(format));
                return 2;
            }
        });

        return searchCmd;
    }
}
