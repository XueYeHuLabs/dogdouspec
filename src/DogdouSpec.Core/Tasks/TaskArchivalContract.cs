using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;

namespace DogdouSpec.Core.Tasks;

/// <summary>
/// Metadata descriptor representing the archival status and partition location for a terminal task's records.
/// </summary>
public sealed record TaskArchivalDescriptor(
    string TaskId,
    bool IsArchived,
    int TotalCount,
    int ArchivedCount,
    int InlineCount,
    string? ArchivePath,
    string? ArchiveSha256)
{
    /// <summary>
    /// Parses an archival descriptor from a task's XML element.
    /// </summary>
    public static TaskArchivalDescriptor FromTaskElement(XElement taskElement)
    {
        ArgumentNullException.ThrowIfNull(taskElement);

        var taskId = taskElement.Attribute("id")?.Value ?? string.Empty;
        var recordsElement = taskElement.Element("records");

        if (recordsElement == null)
        {
            return new TaskArchivalDescriptor(taskId, false, 0, 0, 0, null, null);
        }

        var isArchived = string.Equals(recordsElement.Attribute("archived")?.Value, "true", StringComparison.OrdinalIgnoreCase);
        var totalCountStr = recordsElement.Attribute("total_count")?.Value;
        var totalCount = int.TryParse(totalCountStr, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var parsed) ? parsed : 0;

        var inlineRecords = recordsElement.Elements("record").ToList();
        var inlineCount = inlineRecords.Count;

        var archivePath = recordsElement.Attribute("archive_path")?.Value;
        var archiveSha256 = recordsElement.Attribute("archive_sha256")?.Value;

        var archivedCount = isArchived && totalCount >= inlineCount ? totalCount - inlineCount : 0;

        return new TaskArchivalDescriptor(
            taskId,
            isArchived,
            isArchived ? totalCount : inlineCount,
            archivedCount,
            inlineCount,
            archivePath,
            archiveSha256);
    }

    /// <summary>
    /// Computes canonical SHA-256 digest of record XML blocks for tamper-evident archival receipts.
    /// </summary>
    public static string ComputeRecordSetDigest(IEnumerable<XElement> records)
    {
        ArgumentNullException.ThrowIfNull(records);

        var sb = new StringBuilder();
        foreach (var rec in records)
        {
            sb.Append(rec.ToString(SaveOptions.DisableFormatting));
        }

        var utf8Bytes = Encoding.UTF8.GetBytes(sb.ToString());
        var hashBytes = SHA256.HashData(utf8Bytes);
        return Convert.ToHexStringLower(hashBytes);
    }
}
