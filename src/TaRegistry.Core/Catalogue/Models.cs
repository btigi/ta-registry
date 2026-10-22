namespace TaRegistry.Core.Catalogue;

public sealed class PublicKeyRecord
{
    public required string Id { get; init; }
    public required string Public { get; init; }
}

public sealed class CataloguePackage
{
    public required string Kind { get; init; }
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required int Release { get; init; }
    public required long Size { get; init; }
    public required string Sha256 { get; init; }
    public string? Version { get; init; }
    public int? Revision { get; init; }
    public string? Publisher { get; init; }
    public string? Author { get; init; }
    public string? Summary { get; init; }
    public string? Notes { get; init; }
    public string? Homepage { get; init; }
    public IReadOnlyList<string>? Tags { get; init; }
    public string? Badge { get; init; }
    public string? Published { get; init; }
    public string? DetailsJson { get; init; }
}

public sealed class CatalogueDraft
{
    public required string RegistryId { get; init; }
    public required long Sequence { get; init; }
    public required DateTimeOffset GeneratedUtc { get; init; }
    public required DateTimeOffset ExpiresUtc { get; init; }
    public string? Downloads { get; init; }
    public required IReadOnlyList<PublicKeyRecord> Keys { get; init; }
    public required IReadOnlyList<CataloguePackage> Packages { get; init; }
}

public sealed class DescriptorModel
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public string? Title { get; init; }
    public string? Description { get; init; }
    public string? Author { get; init; }
    public string? Homepage { get; init; }
    public required string CatalogueUrl { get; init; }
    public required string ApiUrl { get; init; }
    public required bool InstallIdRequired { get; init; }
    public required IReadOnlyList<PublicKeyRecord> Keys { get; init; }
}
