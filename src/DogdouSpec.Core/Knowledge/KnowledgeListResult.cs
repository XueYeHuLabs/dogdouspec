using System.Globalization;
using System.Text;
using System.Xml;
using DogdouSpec.Core.Formatting;

namespace DogdouSpec.Core.Knowledge;

public sealed record KnowledgeItemSummary(string Id, string Status, string Summary, string Topic);

public sealed class KnowledgeListResult
{
    public int Revision { get; }
    public IReadOnlyList<KnowledgeItemSummary> Items { get; }

    public KnowledgeListResult(int revision, IReadOnlyList<KnowledgeItemSummary> items)
    {
        Revision = revision;
        Items = items ?? Array.Empty<KnowledgeItemSummary>();
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
            writer.WriteStartElement("knowledge-list");
            writer.WriteAttributeString("revision", Revision.ToString(CultureInfo.InvariantCulture));
            foreach (var item in Items)
            {
                writer.WriteStartElement("entry");
                writer.WriteAttributeString("id", item.Id);
                writer.WriteAttributeString("status", item.Status);
                writer.WriteAttributeString("topic", item.Topic);
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
        sb.AppendLine(CultureInfo.InvariantCulture, $"Knowledge (Revision: {Revision})");
        foreach (var item in Items)
        {
            sb.AppendLine(CultureInfo.InvariantCulture,
                $"- {item.Id} [{item.Status}] {item.Topic}: {item.Summary}");
        }
        return sb.ToString();
    }
}