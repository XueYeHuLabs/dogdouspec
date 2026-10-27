using System.CommandLine;
using System.Globalization;
using DogdouSpec.Core.Diagnostics;
using DogdouSpec.Core.Formatting;
using DogdouSpec.Core.Tasks;
using DogdouSpec.Core.Workspace;

namespace DogdouSpec.Cli.Commands;

public static class TaskRecordsCommand
{
    public static Command BuildCommand()
    {
        var command = new Command("records", "Read bounded task history by last count, kind and fields (read-only); use task context for effective execution constraints");
        var task = new Option<string>("--task") { Required = true, Description = "Task ID" };
        var iteration = new Option<string?>("--iteration") { Description = "Iteration ID; omitted resolves the active iteration" };
        var last = new Option<int?>("--last") { Description = "Last matching records in persisted order (default 20, maximum 10000)" };
        var kinds = new Option<string[]>("--kind") { Description = "Filter record kind (repeatable)", AllowMultipleArgumentsPerToken = false };
        var fields = new Option<string?>("--fields") { Description = "Comma-separated attributes and child fields (default id,kind,status,created_at,summary)" };
        var summary = new Option<int?>("--max-summary") { Description = "Explicitly shorten summaries to this character bound, with truncation metadata" };
        var bytes = new Option<int?>("--max-bytes") { Description = "Maximum UTF-8 output bytes (default 32768)" };
        var workspace = new Option<string?>("--workspace-root");
        var format = new Option<string?>("--format");
        format.AcceptOnlyFromAmong("xml", "human");
        foreach (var option in new Option[] { task, iteration, last, kinds, fields, summary, bytes, workspace, format }) command.Add(option);
        command.SetAction(parse =>
        {
            var outputFormat = WorkspaceCommand.ResolveFormat(parse.GetValue(format));
            var (found, root, error) = WorkspaceDiscovery.FindWorkspaceRoot(parse.GetValue(workspace), Environment.CurrentDirectory);
            if (!found || error != null) { Console.Error.Write(new DiagnosticsEnvelope("task records", error!).Format(outputFormat)); return 2; }
            var (success, result, diagnostics) = TaskRecordsQuery.Query(root, parse.GetValue(task)!, parse.GetValue(iteration), parse.GetValue(last) ?? 20,
                parse.GetValue(kinds), parse.GetValue(fields)?.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries), parse.GetValue(summary), parse.GetValue(bytes) ?? 32768);
            if (!success || result == null)
            {
                var failure = new DiagnosticsEnvelope("task records", diagnostics);
                Console.Error.Write(failure.Format(outputFormat));
                return failure.GetExitCode();
            }
            if (outputFormat == OutputFormat.Xml) Console.Out.Write("<?xml version=\"1.0\" encoding=\"utf-8\"?>\n" + result.ToString().Replace("\r\n", "\n", StringComparison.Ordinal) + "\n");
            else
            {
                Console.Out.WriteLine(string.Create(CultureInfo.InvariantCulture, $"Task records: {result.Attribute("count")?.Value} of {result.Attribute("matching_records")?.Value} matching; truncated={result.Attribute("truncated")?.Value}"));
                foreach (var record in result.Elements("record")) Console.Out.WriteLine(record.ToString());
            }
            return 0;
        });
        return command;
    }
}
