using System.Globalization;
using System.Text;
using System.Xml.Linq;
using DogdouSpec.Core.Formatting;
using DogdouSpec.Core.Serialization;

namespace DogdouSpec.Core.Backlog;

public sealed record BacklogRecordsResult(string Id, string Status, int Revision, int TotalRecords, IReadOnlyList<XElement> Records)
{
    public string Format(OutputFormat format) => format == OutputFormat.Xml
        ? ManagedDocumentSerializer.Serialize(new XElement("backlog-records", new XAttribute("id", Id),
            new XAttribute("status", Status), new XAttribute("revision", Revision),
            new XAttribute("total_records", TotalRecords), new XAttribute("count", Records.Count),
            new XAttribute("omitted_records", TotalRecords - Records.Count), Records.Select(record => new XElement(record))))
        : ToHumanString();

    private string ToHumanString()
    {
        var text = new StringBuilder();
        text.AppendLine(CultureInfo.InvariantCulture, $"Backlog {Id} [{Status}] (Revision: {Revision})");
        text.AppendLine(CultureInfo.InvariantCulture, $"Records: {Records.Count} of {TotalRecords}");
        foreach (var record in Records)
        {
            text.AppendLine(CultureInfo.InvariantCulture, $"- {(string?)record.Attribute("id")} [{(string?)record.Attribute("kind")}] {(string?)record.Attribute("created_at")} ({(string?)record.Attribute("actor")})");
            text.AppendLine(record.Element("summary")?.Value);
            foreach (var check in record.Element("checks")?.Elements("check") ?? [])
            {
                text.AppendLine(CultureInfo.InvariantCulture, $"  Check [{(string?)check.Attribute("result")}]: {check.Element("summary")?.Value}");
                if (check.Attribute("command") != null) text.AppendLine(CultureInfo.InvariantCulture, $"  Command: {(string?)check.Attribute("command")}");
                if (check.Attribute("exit_code") != null) text.AppendLine(CultureInfo.InvariantCulture, $"  Exit code: {(string?)check.Attribute("exit_code")}");
            }
            foreach (var source in record.Element("sources")?.Elements("ref") ?? [])
                text.AppendLine(CultureInfo.InvariantCulture, $"  {(string?)source.Attribute("relation")}: {(string?)source.Attribute("target")}");
        }
        return text.ToString();
    }
}
