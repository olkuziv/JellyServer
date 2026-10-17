namespace JellyServer.Catalog.Contracts;

/// <summary>A library and all its items were removed: modules keeping data about them clean up.</summary>
public sealed record LibraryRemoved(Guid LibraryId, IReadOnlyList<Guid> ItemIds);
