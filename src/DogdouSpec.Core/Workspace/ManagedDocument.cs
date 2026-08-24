namespace DogdouSpec.Core.Workspace;

/// <summary>
/// Represents a managed XML document within a DogdouSpec workspace.
/// </summary>
public sealed record ManagedDocument(
    string RelativePath,
    string FullPath,
    string? IterationId = null)
{
    public string FileName => Path.GetFileName(FullPath);
}
