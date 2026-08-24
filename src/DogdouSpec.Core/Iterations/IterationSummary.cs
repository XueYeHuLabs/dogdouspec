using System.Xml.Linq;

namespace DogdouSpec.Core.Iterations;

/// <summary>
/// Compact metadata summary of an iteration discovered in the workspace.
/// </summary>
public sealed record IterationSummary(
    string Id,
    string RelativePath,
    string Kind,
    string Status,
    int SpecRevision,
    int TasksRevision,
    XElement? IndexElement);
