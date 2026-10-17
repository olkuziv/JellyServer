using System.Security.Cryptography;
using System.Text;

namespace JellyServer.Kernel;

public static class Ids
{
    /// <summary>
    /// Stable ID derived from a key so rescans keep item IDs. Big-endian layout makes
    /// <c>Guid.ToString("N")</c> equal PostgreSQL's <c>md5(key)::uuid</c>.
    /// </summary>
    public static Guid FromKey(params string[] parts) =>
        new(MD5.HashData(Encoding.UTF8.GetBytes(string.Join('\0', parts))), bigEndian: true);

    public static string N(this Guid id) => id.ToString("N");

    public static Guid? Parse(string? s) => Guid.TryParse(s, out var g) ? g : null;

    public static Guid GenreId(string name) => FromKeyRaw("genre:" + name);

    /// <summary>md5 of the raw string, matching SQL <c>md5('genre:' || name)::uuid</c>.</summary>
    public static Guid FromKeyRaw(string key) => new(MD5.HashData(Encoding.UTF8.GetBytes(key)), bigEndian: true);
}
