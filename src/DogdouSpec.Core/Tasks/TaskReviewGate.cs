using System.Xml.Linq;

namespace DogdouSpec.Core.Tasks;

public sealed record TaskReviewEvaluation(bool Required, bool Satisfied, string Reason);

public static class TaskReviewGate
{
    public static TaskReviewEvaluation Evaluate(XElement task)
    {
        var review = task.Element("review");
        var required = review != null &&
                       bool.TryParse((string?)review.Attribute("required"), out var requiredValue) &&
                       requiredValue;
        if (!required)
        {
            return new TaskReviewEvaluation(false, true, "Review is not required.");
        }

        var implementer = (string?)task.Attribute("agent");
        if (string.IsNullOrWhiteSpace(implementer))
        {
            return new TaskReviewEvaluation(true, false, "Review-required task has no declared implementer attribution in @agent.");
        }

        var latest = review!.Elements("submission").LastOrDefault();
        if (latest == null || !string.Equals((string?)latest.Attribute("disposition"), "approved", StringComparison.Ordinal))
        {
            return new TaskReviewEvaluation(true, false, "Latest structured review submission is not approved.");
        }
        var reviewer = (string?)latest.Attribute("actor");
        if (string.IsNullOrWhiteSpace(reviewer) || string.Equals(reviewer, implementer, StringComparison.Ordinal))
        {
            return new TaskReviewEvaluation(true, false, "Approval actor must differ from the declared implementer attribution in @agent.");
        }
        var recordId = (string?)latest.Attribute("record");
        var linkedRecords = task.Element("records")?.Elements("record")
            .Where(r => string.Equals((string?)r.Attribute("id"), recordId, StringComparison.Ordinal)).ToList()
            ?? new List<XElement>();
        var linked = linkedRecords.FirstOrDefault();
        var dispositionTerm = linked?.Element("index")?.Elements("term")
            .FirstOrDefault(t => string.Equals((string?)t.Attribute("key"), "review-disposition", StringComparison.Ordinal))
            ?.Attribute("value")?.Value;
        var fingerprintTerm = linked?.Element("index")?.Elements("term")
            .FirstOrDefault(t => string.Equals((string?)t.Attribute("key"), "request-sha256", StringComparison.Ordinal))
            ?.Attribute("value")?.Value;
        if (linkedRecords.Count != 1 || linked == null ||
            !string.Equals((string?)linked.Attribute("kind"), "decision", StringComparison.Ordinal) ||
            !string.Equals((string?)linked.Attribute("status"), "informational", StringComparison.Ordinal) ||
            !string.Equals((string?)linked.Attribute("actor"), reviewer, StringComparison.Ordinal) ||
            !string.Equals((string?)linked.Attribute("created_at"), (string?)latest.Attribute("reviewed_at"), StringComparison.Ordinal) ||
            !string.Equals(dispositionTerm, "approved", StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(fingerprintTerm))
        {
            return new TaskReviewEvaluation(true, false, "Latest approval does not link to a matching local decision record.");
        }
        return new TaskReviewEvaluation(true, true, "Latest review is an independently attributed approval.");
    }
}
