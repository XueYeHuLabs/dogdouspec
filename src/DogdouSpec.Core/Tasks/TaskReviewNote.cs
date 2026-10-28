using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using DogdouSpec.Core.Diagnostics;
using DogdouSpec.Core.Serialization;
using DogdouSpec.Core.Time;
using DogdouSpec.Core.Transactions;
using DogdouSpec.Core.Validation;

namespace DogdouSpec.Core.Tasks;

public sealed record TaskReviewNoteInput(string TaskId, string Summary, string? IterationId = null,
    string? Result = null, string? Actor = null, int? ExpectedRevision = null, string? OperationId = null, string? OccurredAt = null);

public sealed record TaskReviewNoteResult(MutationEnvelope Envelope, string IterationId, string TaskId,
    string OperationId, string RecordId, string Actor, string OccurredAt)
{
    public XElement Identity() => new("review-identity", new XAttribute("iteration", IterationId), new XAttribute("task", TaskId),
        new XAttribute("operation_id", OperationId), new XAttribute("record_id", RecordId),
        new XAttribute("actor", Actor), new XAttribute("occurred_at", OccurredAt));
}

public static class TaskReviewNote
{
    public static (bool Success, TaskReviewNoteResult? Result, IReadOnlyList<Diagnostic> Diagnostics) Record(
        string workspaceRoot, TaskReviewNoteInput input, bool dryRun = false, IClock? clock = null)
    {
        clock ??= SystemClock.Instance;
        var actor = input.Actor ?? "agent";
        if (string.IsNullOrWhiteSpace(input.Summary) || string.IsNullOrWhiteSpace(actor) ||
            input.Result is not (null or "approved" or "changes-requested"))
            return Failure("Supply a non-empty summary/actor and an optional result of approved or changes-requested.");
        try { XmlConvert.VerifyXmlChars(input.Summary); XmlConvert.VerifyXmlChars(actor); }
        catch (XmlException) { return Failure("Review text contains unsupported XML characters."); }
        var (loaded, snapshot, diagnostics) = TaskReviewHistory.Load(workspaceRoot, input.TaskId, input.IterationId);
        if (!loaded || snapshot == null) return (false, null, diagnostics);
        DateTimeOffset timestamp;
        if (input.OccurredAt != null)
        {
            if (!DateTimeOffset.TryParse(input.OccurredAt, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out timestamp))
                return Failure("--occurred-at must be an ISO 8601 timestamp.");
        }
        else if (input.OperationId != null)
        {
            if (input.OperationId.Length < 16 || !DateTimeOffset.TryParseExact(input.OperationId[..16], "yyyyMMddTHHmmssZ",
                CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out timestamp))
                return Failure("An explicit --operation-id needs a UTC YYYYMMDDTHHmmssZ prefix or an explicit --occurred-at for deterministic retries.");
        }
        else timestamp = TaskTimestampPolicy.Automatic(snapshot.Task, clock.UtcNow);
        var at = timestamp.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
        var operation = input.OperationId ?? timestamp.UtcDateTime.ToString("yyyyMMddTHHmmssZ", CultureInfo.InvariantCulture) + "-reviewnote-" + Guid.NewGuid().ToString("N");
        var recordId = operation + "-record";
        if (!ProjectSemanticIndex.IsValidTimeFirstId(operation) || !ProjectSemanticIndex.IsValidTimeFirstId(recordId))
            return Failure("--operation-id must use the time-first ID grammar.");
        var record = new XElement("record", new XAttribute("id", recordId), new XAttribute("kind", "discussion"),
            new XAttribute("status", "informational"), new XAttribute("created_at", at), new XAttribute("actor", actor),
            new XAttribute("operation_id", operation),
            new XElement("index", new XElement("summary", "Recorded review."),
                new XElement("term", new XAttribute("key", "activity"), new XAttribute("value", "review")),
                new XElement("term", new XAttribute("key", "review-summary-sha256"),
                    new XAttribute("value", Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(input.Summary))).ToLowerInvariant())),
                input.Result == null ? null : new XElement("term", new XAttribute("key", "review-result"), new XAttribute("value", input.Result))),
            new XElement("summary", input.Summary));
        var request = new XElement("task-update", new XAttribute("id", operation), new XAttribute("actor", actor),
            new XAttribute("occurred_at", at), new XElement("records", record));
        var (success, envelope, errors) = TaskUpdater.Update(snapshot.Root, snapshot.IterationId, input.TaskId,
            input.ExpectedRevision ?? snapshot.Revision, ManagedDocumentSerializer.Serialize(request), clock: clock, dryRun: dryRun);
        return success && envelope != null
            ? (true, new TaskReviewNoteResult(new MutationEnvelope("task review record", envelope.Documents,
                envelope.AlreadyApplied, envelope.DryRun, envelope.Transitions, envelope.Partial),
                snapshot.IterationId, input.TaskId, operation, recordId, actor, at), errors)
            : (false, null, errors);
    }

    private static (bool, TaskReviewNoteResult?, IReadOnlyList<Diagnostic>) Failure(string message) =>
        (false, null, new[] { Diagnostic.Error(DiagnosticCodes.InvalidArgument, message) });
}
