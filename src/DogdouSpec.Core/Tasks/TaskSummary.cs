using DogdouSpec.Core.Diagnostics;
using DogdouSpec.Core.Progression;

namespace DogdouSpec.Core.Tasks;

public static class TaskSummary
{
    public static (bool Success, TaskSummaryResult? Result, IReadOnlyList<Diagnostic> Diagnostics) Summarize(
        string workspaceRoot,
        string? requestedIterationId = null)
    {
        var (assessSuccess, assessResult, diagnostics) = ProgressionEngine.Assess(
            workspaceRoot,
            requestedIterationId);

        if (!assessSuccess || assessResult == null)
        {
            return (false, null, diagnostics);
        }

        var facts = assessResult.Facts;
        var result = new TaskSummaryResult(
            assessResult.IterationId,
            assessResult.TasksRevision,
            facts.TotalTasks,
            facts.PendingTasks,
            facts.InProgressTasks,
            facts.VerificationTasks,
            facts.DoneTasks,
            facts.BlockedTasks,
            facts.TransferredTasks,
            facts.SupersededTasks,
            facts.CancelledTasks);

        return (true, result, Array.Empty<Diagnostic>());
    }
}
