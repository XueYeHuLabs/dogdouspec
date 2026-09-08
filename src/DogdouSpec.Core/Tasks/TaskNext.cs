using DogdouSpec.Core.Diagnostics;
using DogdouSpec.Core.Progression;

namespace DogdouSpec.Core.Tasks;

/// <summary>
/// Public read-only helper for deriving the next actionable task in an iteration,
/// properly accounting for same-document and cross-iteration dependencies,
/// review gates, active findings, and agent filtering.
/// </summary>
public static class TaskNext
{
    public static (bool Success, TaskNextResult? Result, IReadOnlyList<Diagnostic> Diagnostics) SelectNext(
        string workspaceRoot,
        string? requestedIterationId = null,
        string? agentFilter = null,
        string? requestedTaskId = null)
    {
        var (assessSuccess, assessResult, diagnostics) = ProgressionEngine.Assess(
            workspaceRoot,
            requestedIterationId,
            agentFilter,
            requestedTaskId);

        if (!assessSuccess || assessResult == null)
        {
            return (false, null, diagnostics);
        }

        var result = new TaskNextResult(
            assessResult.IterationId,
            assessResult.TasksRevision,
            assessResult.PrimaryTask,
            assessResult.RecommendedAction.Reason,
            assessResult.RecommendedAction.ActionCategory,
            assessResult.RecommendedAction.ReasonCode,
            assessResult.ActionableCandidates,
            assessResult);

        return (true, result, Array.Empty<Diagnostic>());
    }
}
