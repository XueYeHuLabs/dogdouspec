using System.Globalization;
using System.Xml.Linq;
using DogdouSpec.Core.Diagnostics;

namespace DogdouSpec.Core.Time;

/// <summary>Bounds new timestamps while allowing monotonic continuation of existing task state.</summary>
public static class TaskTimestampPolicy
{
    public static readonly TimeSpan AllowedClockSkew = TimeSpan.FromMinutes(5);

    public static DateTimeOffset Automatic(XElement task, DateTimeOffset now)
    {
        foreach (var name in new[] { "created_at", "updated_at" })
            if (DateTimeOffset.TryParse((string?)task.Attribute(name), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var existing) && existing > now)
                now = existing;
        return now.ToUniversalTime();
    }

    public static Diagnostic? ValidateRequest(XElement request, IClock clock, string document, XElement? existingTask = null)
    {
        var latest = new DateTimeOffset(clock.UtcNow).ToUniversalTime().Add(AllowedClockSkew);
        if (existingTask != null) latest = Automatic(existingTask, latest);
        foreach (var attribute in request.DescendantsAndSelf().Attributes())
        {
            if (attribute.Name.LocalName is not ("occurred_at" or "created_at" or "updated_at" or "started_at" or "completed_at")) continue;
            if (DateTimeOffset.TryParse(attribute.Value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var timestamp) && timestamp > latest)
                return Diagnostic.Error(DiagnosticCodes.InvalidArgument,
                    $"Timestamp @{attribute.Name.LocalName} '{attribute.Value}' exceeds the allowed clock skew of {AllowedClockSkew.TotalMinutes} minutes and existing task timestamps. Latest allowed timestamp is '{latest:yyyy-MM-ddTHH:mm:ssZ}'.", document);
        }
        return null;
    }
}
