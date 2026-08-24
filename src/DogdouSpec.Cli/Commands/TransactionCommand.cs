using System.CommandLine;
using DogdouSpec.Core.Diagnostics;
using DogdouSpec.Core.Formatting;
using DogdouSpec.Core.Transactions;
using DogdouSpec.Core.Workspace;

namespace DogdouSpec.Cli.Commands;

/// <summary>
/// CLI registration for the 'transaction' command group.
/// Currently exposes the low-level 'apply' sub-command which atomically patches managed XML documents.
/// </summary>
public static class TransactionCommand
{
    public static Command BuildCommand()
    {
        var transactionCmd = new Command("transaction", "Low-level XML transaction operations (mutating)");

        transactionCmd.Add(BuildApplyCommand());

        return transactionCmd;
    }

    private static Command BuildApplyCommand()
    {
        var applyCmd = new Command(
            "apply",
            "Apply a low-level XML transaction to managed documents (mutating). " +
            "Accepts a <transaction> XML request with assert, append-child, replace-node, set-attribute, and remove-node operations. " +
            "All changed documents use one recovery-backed commit. " +
            "Protected product decisions (iteration lifecycle, confirmations, requirement/question/decision status, etc.) " +
            "remain enforced and cannot be bypassed via this command.");

        var stdinOption = new Option<bool>("--stdin")
        {
            Description = "Read transaction XML request from standard input"
        };

        var fileOption = new Option<string?>("--file")
        {
            Description = "Path to file containing the transaction XML request"
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

        applyCmd.Add(stdinOption);
        applyCmd.Add(fileOption);
        applyCmd.Add(workspaceRootOption);
        applyCmd.Add(formatOption);

        applyCmd.SetAction(parseResult =>
        {
            var hasStdin = parseResult.GetValue(stdinOption);
            var filePath = parseResult.GetValue(fileOption);
            var workspaceRoot = parseResult.GetValue(workspaceRootOption);
            var formatArg = parseResult.GetValue(formatOption);
            var format = WorkspaceCommand.ResolveFormat(formatArg);
            const string commandName = "transaction apply";

            // Validate stdin vs file (mutually exclusive, exactly one required)
            if (hasStdin && !string.IsNullOrWhiteSpace(filePath))
            {
                var envelope = new DiagnosticsEnvelope(commandName, Diagnostic.Error(
                    DiagnosticCodes.InvalidArgument,
                    "Specify either --stdin or --file, not both."));
                Console.Error.Write(envelope.Format(format));
                return 2;
            }

            if (!hasStdin && string.IsNullOrWhiteSpace(filePath))
            {
                var envelope = new DiagnosticsEnvelope(commandName, Diagnostic.Error(
                    DiagnosticCodes.InvalidArgument,
                    "Either --stdin or --file must be specified."));
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
                if (!File.Exists(filePath))
                {
                    var envelope = new DiagnosticsEnvelope(commandName, Diagnostic.Error(
                        DiagnosticCodes.InvalidArgument,
                        $"Transaction XML file '{filePath}' does not exist."));
                    Console.Error.Write(envelope.Format(format));
                    return 2;
                }

                requestXml = File.ReadAllText(filePath!);
            }

            var (discoverSuccess, discoveredRoot, discoverError) = WorkspaceDiscovery.FindWorkspaceRoot(
                workspaceRoot,
                Environment.CurrentDirectory);

            if (!discoverSuccess || discoverError != null)
            {
                var envelope = new DiagnosticsEnvelope(commandName, discoverError!);
                Console.Error.Write(envelope.Format(format));
                return 2;
            }

            var (success, mutationEnvelope, diagnostics) = TransactionApplier.Apply(
                discoveredRoot,
                requestXml);

            if (!success || diagnostics.Count > 0)
            {
                var diagEnvelope = new DiagnosticsEnvelope(commandName, diagnostics);
                Console.Error.Write(diagEnvelope.Format(format));
                return diagEnvelope.GetExitCode();
            }

            if (mutationEnvelope != null)
            {
                Console.Out.Write(mutationEnvelope.Format(format));
            }

            return 0;
        });

        return applyCmd;
    }
}
