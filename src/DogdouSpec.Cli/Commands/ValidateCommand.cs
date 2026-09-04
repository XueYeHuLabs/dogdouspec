using System.CommandLine;
using DogdouSpec.Core.Diagnostics;
using DogdouSpec.Core.Formatting;
using DogdouSpec.Core.Security;
using DogdouSpec.Core.Transactions;
using DogdouSpec.Core.Validation;
using DogdouSpec.Core.Workspace;

namespace DogdouSpec.Cli.Commands;

public static class ValidateCommand
{
    public static Command BuildCommand()
    {
        var validateCmd = new Command("validate", "Validate managed XML documents or mutation requests against authoritative schemas and workspace state");

        var iterationOption = new Option<string?>("--iteration")
        {
            Description = "Iteration identifier (YYYYMMDD-name) to restrict validation to or pair with shorthand --document"
        };

        var documentOption = new Option<string?>("--document")
        {
            Description = "Relative managed document path, or spec.xml/tasks.xml with --iteration; unavailable in mutation request mode"
        };

        var requestOption = new Option<string?>("--request")
        {
            Description = "Mutation request XML file to preflight without writing (mutually exclusive with --stdin and --document)"
        };

        var stdinOption = new Option<bool>("--stdin")
        {
            Description = "Read mutation request XML from standard input (mutually exclusive with --request and --document)"
        };

        var taskOption = new Option<string?>("--task")
        {
            Description = "Target task identifier required for task update, revise, split, and review request preflight"
        };

        var expectedRevisionOption = new Option<int?>("--expected-revision")
        {
            Description = "Expected positive spec.xml or target document revision; fills a missing request value and must match when present"
        };

        var expectedTasksRevisionOption = new Option<int?>("--expected-tasks-revision")
        {
            Description = "Expected positive tasks.xml revision for confirmation or two-document change preflight; fills a missing value and must match when present"
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
        validateCmd.Add(requestOption);
        validateCmd.Add(stdinOption);
        validateCmd.Add(taskOption);
        validateCmd.Add(expectedRevisionOption);
        validateCmd.Add(expectedTasksRevisionOption);
        validateCmd.Add(workspaceRootOption);
        validateCmd.Add(formatOption);

        validateCmd.SetAction(parseResult =>
        {
            var iterationId = parseResult.GetValue(iterationOption);
            var documentPath = parseResult.GetValue(documentOption);
            var requestPath = parseResult.GetValue(requestOption);
            var hasStdin = parseResult.GetValue(stdinOption);
            var taskId = parseResult.GetValue(taskOption);
            var expectedRevision = parseResult.GetValue(expectedRevisionOption);
            var expectedTasksRevision = parseResult.GetValue(expectedTasksRevisionOption);
            var workspaceRoot = parseResult.GetValue(workspaceRootOption);
            var formatArg = parseResult.GetValue(formatOption);
            var format = WorkspaceCommand.ResolveFormat(formatArg);

            var (successWs, discoveredRoot, discoverError) = WorkspaceDiscovery.FindWorkspaceRoot(
                workspaceRoot,
                Environment.CurrentDirectory);

            if (!successWs || discoverError != null)
            {
                var envelope = new DiagnosticsEnvelope("validate", discoverError!);
                Console.Error.Write(envelope.Format(format));
                return 2;
            }

            // Mutation request preflight mode
            if (hasStdin || !string.IsNullOrWhiteSpace(requestPath))
            {
                if (hasStdin && !string.IsNullOrWhiteSpace(requestPath))
                {
                    var envelope = new DiagnosticsEnvelope("validate", Diagnostic.Error(
                        DiagnosticCodes.InvalidArgument,
                        "Specify either --stdin or --request, not both."));
                    Console.Error.Write(envelope.Format(format));
                    return 2;
                }

                if (!string.IsNullOrWhiteSpace(documentPath))
                {
                    var envelope = new DiagnosticsEnvelope("validate", Diagnostic.Error(
                        DiagnosticCodes.InvalidArgument,
                        "Option --document cannot be used with mutation request preflight (--request or --stdin)."));
                    Console.Error.Write(envelope.Format(format));
                    return 2;
                }

                string requestXml;
                if (hasStdin)
                {
                    requestXml = Console.In.ReadToEnd();
                }
                else
                {
                    if (!File.Exists(requestPath))
                    {
                        var envelope = new DiagnosticsEnvelope("validate", Diagnostic.Error(
                            DiagnosticCodes.InvalidArgument,
                            $"Mutation request file '{requestPath}' does not exist."));
                        Console.Error.Write(envelope.Format(format));
                        return 2;
                    }

                    try
                    {
                        requestXml = File.ReadAllText(requestPath!);
                    }
                    catch (Exception ex)
                    {
                        var envelope = new DiagnosticsEnvelope("validate", Diagnostic.Error(
                            DiagnosticCodes.InvalidArgument,
                            $"Failed to read mutation request file '{requestPath}': {ex.Message}"));
                        Console.Error.Write(envelope.Format(format));
                        return 2;
                    }
                }

                var (preflightOk, preflightResult, preflightDiags) = MutationPreflight.Preflight(
                    discoveredRoot,
                    requestXml,
                    iterationId,
                    taskId,
                    expectedRevision,
                    expectedTasksRevision);

                if (!preflightOk || preflightResult == null || preflightDiags.Count > 0)
                {
                    var diagEnvelope = new DiagnosticsEnvelope("validate", preflightDiags);
                    Console.Error.Write(diagEnvelope.Format(format));
                    return diagEnvelope.GetExitCode();
                }

                if (format == OutputFormat.Xml)
                {
                    Console.Out.Write(preflightResult.ProspectiveEnvelope!.ToXmlString());
                }
                else
                {
                    var docSummary = string.Join(", ", preflightResult.MutatedDocuments.Select(d => $"{d.Path}@r{d.Revision}"));
                    Console.Out.WriteLine($"Mutation preflight succeeded: command='{preflightResult.ProspectiveEnvelope!.Command}', request='{preflightResult.RequestType}', mutates=[{docSummary}]. No files were written.");
                }
                return 0;
            }

            // Document validation mode with shared DocumentAddressResolver
            var (isAddrValid, resolvedPath, resolvedIter, addrError) = DocumentAddressResolver.Resolve(
                iterationId,
                documentPath,
                requireDocument: false);

            if (!isAddrValid || addrError != null)
            {
                var envelope = new DiagnosticsEnvelope("validate", addrError ?? Diagnostic.Error(
                    DiagnosticCodes.InvalidArgument,
                    "Invalid document address."));
                Console.Error.Write(envelope.Format(format));
                return 2;
            }

            var result = SchemaValidator.Validate(
                discoveredRoot,
                resolvedPath != null ? null : resolvedIter,
                resolvedPath,
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

            var validateDiagEnvelope = result.CreateDiagnosticsEnvelope("validate");
            Console.Error.Write(validateDiagEnvelope.Format(format));
            return 3;
        });

        return validateCmd;
    }
}
