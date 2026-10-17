namespace JellyServer.People.Contracts;

/// <summary>
/// Who is credited on catalog items. Each person is stored once (name, portrait, provider id); an item keeps only the list of its
/// credits. Lives apart from the catalog: schema <c>people</c>, its own database with <c>ConnectionStrings:people</c>.
/// </summary>
public interface IPeopleStore
{
    /// <summary>The credits of each item, in billing order (items without any are not returned).</summary>
    Task<Dictionary<Guid, List<Person>>> GetCreditsAsync(IReadOnlyCollection<Guid> itemIds, CancellationToken ct = default);

    /// <summary>Replaces the credits of these items; null or empty removes them.</summary>
    Task SetCreditsAsync(IReadOnlyList<(Guid ItemId, IReadOnlyList<Person>? People)> credits, CancellationToken ct = default);

    /// <summary>Items crediting any of these people (with one of <paramref name="kinds"/>, when given).</summary>
    Task<Guid[]> ItemsWithAsync(IReadOnlyCollection<Guid> personIds, string[]? kinds, CancellationToken ct = default);

    Task<List<PersonInfo>> GetPersonsAsync(IReadOnlyCollection<Guid> personIds, CancellationToken ct = default);

    /// <summary>A page of people ordered by name, with the total.</summary>
    Task<(List<PersonInfo> People, int Total)> QueryAsync(PersonQuery query, CancellationToken ct = default);
}
