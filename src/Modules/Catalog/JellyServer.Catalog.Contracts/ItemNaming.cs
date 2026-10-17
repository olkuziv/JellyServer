using System.Text;
using System.Text.RegularExpressions;

namespace JellyServer.Catalog.Contracts;

/// <summary>How item names are sorted and searched. Scanners store these, the API applies the same search normalisation to queries.</summary>
public static partial class ItemNaming
{
    [GeneratedRegex(@"\s{2,}")] private static partial Regex MultiSpace();

    private static readonly string[] Articles = ["the ", "a ", "an "];

    public static string SortName(string name)
    {
        var s = name.Trim().ToLowerInvariant();
        foreach (var a in Articles)
        {
            if (s.StartsWith(a, StringComparison.Ordinal) && s.Length > a.Length)
            {
                s = s[a.Length..];
                break;
            }
        }
        return s.TrimStart(' ', '.', '-', '_', '\'', '"', '«', '[', '(');
    }

    /// <summary>
    /// Rejects NFO sort titles that chop characters off mid-word (title "Ґанц",
    /// sorttitle "анц"); dropping whole leading words ("The Matrix" → "Matrix") is fine.
    /// </summary>
    public static bool IsValidSortTitle(string title, string? sortTitle)
    {
        var st = sortTitle?.Trim();
        if (string.IsNullOrEmpty(st)) return false;
        var t = title.Trim();
        if (t != st && t.EndsWith(st, StringComparison.Ordinal))
            return t[..^st.Length].EndsWith(' ');
        return true;
    }

    /// <summary>Normalises text for trigram search. The API applies the same function to queries.</summary>
    public static string SearchText(params string?[] parts)
    {
        var sb = new StringBuilder();
        foreach (var part in parts)
        {
            if (string.IsNullOrEmpty(part)) continue;
            foreach (var ch in part.ToLowerInvariant())
            {
                switch (ch)
                {
                    case 'ґ': sb.Append('г'); break;
                    case 'ё': sb.Append('е'); break;
                    case '\'' or '’' or '`': break;
                    case ':' or '.' or ',' or '-' or '_' or '!' or '?': sb.Append(' '); break;
                    default: sb.Append(ch); break;
                }
            }
            sb.Append(' ');
        }
        return MultiSpace().Replace(sb.ToString(), " ").Trim();
    }
}
