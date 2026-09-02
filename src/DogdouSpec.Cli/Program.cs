using System.CommandLine;
using DogdouSpec.Cli.Commands;
using DogdouSpec.Core.Diagnostics;
using DogdouSpec.Core.Formatting;

namespace DogdouSpec.Cli;

public static class Program
{
    public static int Main(string[] args)
    {
        try
        {
            var rootDescription = """
DogdouSpec CLI - iteration-first XML/XPath project workspace engine

AI AGENT SETUP & WORKFLOW:
  1. Print agent setup & workflow guide   : dogdouspec skill guide
  2. Initialize workspace & skills        : dogdouspec workspace init
  3. Upgrade / overwrite skill files      : dogdouspec skill sync --force
  4. Select next ready task to implement  : dogdouspec task next --format xml
  5. View instant progress summary card   : dogdouspec summary --format markdown

PERSISTENCE & GOVERNANCE:
  Semantic agent results belong in tasks.xml records, not external report folders.
  In Git-backed governed work, validate and checkpoint managed .dogdouspec state;
  DogdouSpec never stages or commits repository files on your behalf.
""";

            var rootCommand = new RootCommand(rootDescription);

            rootCommand.Add(WorkspaceCommand.BuildCommand());
            rootCommand.Add(IterationCommand.BuildCommand());
            rootCommand.Add(SummaryCommand.BuildCommand());
            rootCommand.Add(SchemaCommand.BuildCommand());
            rootCommand.Add(TemplateCommand.BuildCommand());
            rootCommand.Add(ValidateCommand.BuildCommand());
            rootCommand.Add(QueryCommand.BuildCommand());
            rootCommand.Add(SearchCommand.BuildCommand());
            rootCommand.Add(AppendCommand.BuildCommand());
            rootCommand.Add(TaskCommand.BuildCommand());
            rootCommand.Add(RequirementCommand.BuildCommand());
            rootCommand.Add(ChangeCommand.BuildCommand());
            rootCommand.Add(BacklogCommand.BuildCommand());
            rootCommand.Add(TransactionCommand.BuildCommand());
            rootCommand.Add(SkillCommand.BuildCommand());

            var parseResult = rootCommand.Parse(args);
            if (parseResult.Errors.Count > 0)
            {
                var format = WorkspaceCommand.ResolveFormat(GetFormatArg(args));
                var diagnostics = parseResult.Errors
                    .Select(e => Diagnostic.Error(DiagnosticCodes.InvalidArgument, e.Message))
                    .ToList();
                var envelope = new DiagnosticsEnvelope("dogdouspec", diagnostics);
                Console.Error.Write(envelope.Format(format));
                return 2;
            }

            return parseResult.Invoke();
        }
        catch (Exception ex)
        {
            var envelope = new DiagnosticsEnvelope("dogdouspec", Diagnostic.Error(
                DiagnosticCodes.InvalidArgument,
                $"Unhandled CLI error: {ex.Message}"));
            Console.Error.Write(envelope.Format(OutputFormat.Human));
            return 2;
        }
    }

    private static string? GetFormatArg(string[] args)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], "--format", StringComparison.OrdinalIgnoreCase))
            {
                return args[i + 1];
            }
        }
        return null;
    }
}
