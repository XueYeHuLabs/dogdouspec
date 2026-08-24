namespace DogdouSpec.Core.Validation;

/// <summary>
/// Represents a prospective document candidate for pre-publication validation.
/// </summary>
public sealed record ProspectiveDocument(
    string RelativePath,
    string Content,
    bool IsNew = false,
    int? ExpectedRevision = null);
