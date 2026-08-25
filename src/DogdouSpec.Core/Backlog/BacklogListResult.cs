using System.Globalization;
using System.Text;
using System.Xml;
using DogdouSpec.Core.Formatting;

namespace DogdouSpec.Core.Backlog;

public sealed record BacklogItemSummary(string Id, string Status, string Summary, string Kind, string? Severity);

public sealed class BacklogListResult
{
    public int Revision { get; }
    public IReadOnlyList<BacklogItemSummary> Items { get; }

    public BacklogListResult(int revision, IReadOnlyList<BacklogItemSummary> items)
    {
        Revision = revision;
        Items = items;
    }

    public string Format(OutputFormat format) => format == OutputFormat.Xml ? ToXmlString() : ToHumanString();

    public string ToXmlString()
    {
        var settings = new XmlWriterSettings
        {
            Indent = true,
            IndentChars = "  ",
            OmitXmlDeclaration = false,
            Encoding = new UTF8Encoding(false),
            NewLineHandling = NewLineHandling.Replace,
            NewLineChars = "\n"
        };
        using var stream = new MemoryStream();
        using (var writer = XmlWriter.Create(stream, settings))
        {
            writer.WriteStartDocument();
            writer.WriteStartElement("backlog-list");
            writer.WriteAttributeString("revision", Revision.ToString(CultureInfo.InvariantCulture));
            foreach (var item in Items)
            {
                writer.WriteStartElement("item");
                writer.WriteAttributeString("id", item.Id);
                writer.WriteAttributeString("status", item.Status);
                writer.WriteAttributeString("kind", item.Kind);
                if (!string.IsNullOrEmpty(item.Severity))
                {
                    writer.WriteAttributeString("severity", item.Severity);
                }
                writer.WriteElementString("summary", item.Summary);
                writer.WriteEndElement();
            }
            writer.WriteEndElement();
            writer.WriteEndDocument();
        }
        return Encoding.UTF8.GetString(stream.ToArray()) + "\n";
    }

    public string ToHumanString()
    {
        var sb = new StringBuilder();
        sb.AppendLine(CultureInfo.InvariantCulture, $"Backlog (Revision: {Revision})");
        foreach (var item in Items)
        {
            sb.AppendLine(CultureInfo.InvariantCulture,
                $"- {item.Id} [{item.Status}] {item.Kind}{(string.IsNullOrEmpty(item.Severity) ? string.Empty : "/" + item.Severity)}: {item.Summary}");
        }
        return sb.ToString();
    }
}
