using System.Text;
using System.Text.RegularExpressions;

namespace Linksta.Util;

public static class PathUtil
{
    private static readonly HashSet<string> ReservedWindowsNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    private const string InvalidChars = "<>:\"/\\|?*";

    /// <summary>Macht aus einem ILIAS-Titel einen Datei-/Ordnernamen, der auf Windows, macOS und Linux gültig ist.</summary>
    public static string SanitizeSegment(string name)
    {
        var sb = new StringBuilder(name.Length);
        foreach (var ch in name.Normalize(NormalizationForm.FormC))
            sb.Append(ch < 32 || InvalidChars.Contains(ch) ? '_' : ch);

        var result = sb.ToString().Trim().TrimEnd('.', ' ');
        if (result.Length == 0 || result == "." || result == "..")
            result = "_";

        var stem = result.Split('.')[0];
        if (ReservedWindowsNames.Contains(stem))
            result = "_" + result;

        if (result.Length > 180)
        {
            var ext = Path.GetExtension(result);
            if (ext.Length > 20) ext = "";
            result = result[..(180 - ext.Length)].TrimEnd() + ext;
        }

        return result;
    }

    /// <summary>Hängt " (2)", " (3)" … vor die Dateiendung, bis der Name eindeutig ist.</summary>
    public static string MakeUnique(string name, ISet<string> taken, bool isFile)
    {
        if (taken.Add(name))
            return name;

        var ext = isFile ? Path.GetExtension(name) : "";
        var stem = isFile ? Path.GetFileNameWithoutExtension(name) : name;
        for (var i = 2; ; i++)
        {
            var candidate = $"{stem} ({i}){ext}";
            if (taken.Add(candidate))
                return candidate;
        }
    }

    public static string ToLocal(string root, string relPath) =>
        Path.Combine(new[] { root }.Concat(relPath.Split('/')).ToArray());

    public static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double v = bytes;
        var u = 0;
        while (v >= 1024 && u < units.Length - 1)
        {
            v /= 1024;
            u++;
        }
        return u == 0 ? $"{bytes} B" : $"{v:0.#} {units[u]}";
    }
}

/// <summary>
/// Einfache Glob-Muster: <c>*</c>, <c>**</c>, <c>?</c>.
/// Muster ohne "/" prüfen jeden Pfadbestandteil (z. B. "*.mp4" oder "Videos"),
/// Muster mit "/" den ganzen relativen Pfad.
/// </summary>
public sealed class GlobMatcher
{
    private readonly List<(Regex Regex, bool PathPattern)> _patterns;

    public GlobMatcher(IEnumerable<string> patterns)
    {
        _patterns = patterns
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => p.Trim().Replace('\\', '/').Trim('/'))
            .Select(p => (ToRegex(p), p.Contains('/')))
            .ToList();
    }

    public bool IsEmpty => _patterns.Count == 0;

    public bool IsMatch(string relPath)
    {
        if (_patterns.Count == 0)
            return false;

        var segments = relPath.Split('/');
        foreach (var (regex, pathPattern) in _patterns)
        {
            if (pathPattern)
            {
                if (regex.IsMatch(relPath))
                    return true;
                // Treffer auf einen Ordner schließt auch alles darunter aus
                for (var i = 1; i < segments.Length; i++)
                    if (regex.IsMatch(string.Join('/', segments.Take(i))))
                        return true;
            }
            else if (segments.Any(regex.IsMatch))
            {
                return true;
            }
        }
        return false;
    }

    private static Regex ToRegex(string glob)
    {
        var sb = new StringBuilder("^");
        for (var i = 0; i < glob.Length; i++)
        {
            var c = glob[i];
            switch (c)
            {
                case '*' when i + 1 < glob.Length && glob[i + 1] == '*':
                    i++;
                    if (i + 1 < glob.Length && glob[i + 1] == '/')
                    {
                        sb.Append("(?:.*/)?"); // "**/" passt auch auf null Ordner
                        i++;
                    }
                    else
                    {
                        sb.Append(".*");
                    }
                    break;
                case '*':
                    sb.Append("[^/]*");
                    break;
                case '?':
                    sb.Append("[^/]");
                    break;
                default:
                    sb.Append(Regex.Escape(c.ToString()));
                    break;
            }
        }
        sb.Append('$');
        return new Regex(sb.ToString(), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }
}
