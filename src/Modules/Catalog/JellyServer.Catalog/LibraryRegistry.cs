using JellyServer.Catalog.Contracts;

namespace JellyServer.Catalog;

/// <summary>In-memory list of configured libraries (they change only on restart).</summary>
public sealed class LibraryRegistry(LibraryStore store) : ILibraryDirectory
{
    private volatile List<Library> _libraries = [];

    public IReadOnlyList<Library> All => _libraries;

    public async Task ReloadAsync(CancellationToken ct) => _libraries = await store.LoadLibrariesAsync(ct);

    public async Task SetImagesAsync(Library lib, ItemImages images, CancellationToken ct)
    {
        await store.SetLibraryImagesAsync(lib.Id, images, ct);
        lib.Images = images;
    }

    public Library? Find(Guid id) => _libraries.FirstOrDefault(l => l.Id == id);
}
