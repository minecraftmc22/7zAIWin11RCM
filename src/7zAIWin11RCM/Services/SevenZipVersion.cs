using System.Text.RegularExpressions;

namespace SevenZipAIWin11RCM.Services;

/// <summary>
/// 7-Zip 版本号（主/次两段，例如 25.01、26.06）。
/// 7-Zip 的版本号始终是「年.月」两段，因此只比较前两段数字即可。
/// </summary>
public readonly record struct SevenZipVersion(int Major, int Minor) : IComparable<SevenZipVersion>
{
    private static readonly Regex Pattern = new(@"^(\d+)(?:[.\-_](\d+))?", RegexOptions.Compiled);

    /// <summary>解析 "26.06"、"25.01.0.0"、"9.20" 这类字符串；失败返回 null。</summary>
    public static SevenZipVersion? TryParse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var match = Pattern.Match(text.Trim());
        if (!match.Success || !int.TryParse(match.Groups[1].Value, out var major))
        {
            return null;
        }

        var minor = 0;
        if (match.Groups[2].Success && int.TryParse(match.Groups[2].Value, out var parsedMinor))
        {
            minor = parsedMinor;
        }

        return new SevenZipVersion(major, minor);
    }

    public int CompareTo(SevenZipVersion other) =>
        Major != other.Major ? Major.CompareTo(other.Major) : Minor.CompareTo(other.Minor);

    public static bool operator <(SevenZipVersion left, SevenZipVersion right) => left.CompareTo(right) < 0;

    public static bool operator >(SevenZipVersion left, SevenZipVersion right) => left.CompareTo(right) > 0;

    public static bool operator <=(SevenZipVersion left, SevenZipVersion right) => left.CompareTo(right) <= 0;

    public static bool operator >=(SevenZipVersion left, SevenZipVersion right) => left.CompareTo(right) >= 0;

    public override string ToString() => $"{Major}.{Minor:00}";
}
