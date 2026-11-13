using System.Diagnostics;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace SevenZipAIWin11RCM.Services;

/// <summary>7-Zip 安装包（某个版本、某个架构的 exe）。</summary>
public sealed record SevenZipAsset(string Name, string Url, long Size)
{
    public string SizeText => Size > 0 ? FormatSize(Size) : "大小未知";

    public static string FormatSize(long bytes) => bytes switch
    {
        >= 1024 * 1024 => $"{bytes / 1024.0 / 1024.0:0.0} MB",
        >= 1024 => $"{bytes / 1024.0:0.0} KB",
        _ => $"{bytes} B",
    };
}

/// <summary>一个 7-Zip 版本，含各架构的安装包。</summary>
public sealed record SevenZipRelease(string Version, DateTimeOffset? PublishedAt, IReadOnlyList<SevenZipAsset> Assets)
{
    private static readonly System.Text.RegularExpressions.Regex AssetNamePattern = new(
        @"^7z\d+(-x64|-arm64|-arm)?\.exe$",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);

    public static bool IsWindowsInstaller(string name) => AssetNamePattern.IsMatch(name);

    /// <summary>按当前系统架构挑选最合适的安装包。</summary>
    public SevenZipAsset? PickForCurrentArchitecture()
    {
        var suffix = RuntimeInformation.OSArchitecture switch
        {
            Architecture.Arm64 => "-arm64.exe",
            Architecture.X64 => "-x64.exe",
            _ => null,
        };

        if (suffix is not null)
        {
            var match = Assets.FirstOrDefault(a => a.Name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));
            if (match is not null)
            {
                return match;
            }
        }

        // 32 位包在所有架构上都能跑，作为兜底
        return Assets.FirstOrDefault(a => !a.Name.Contains("-x64", StringComparison.OrdinalIgnoreCase)
                                          && !a.Name.Contains("-arm", StringComparison.OrdinalIgnoreCase))
               ?? Assets.FirstOrDefault();
    }

    public string DisplayName
    {
        get
        {
            var date = PublishedAt is { } published ? $"（{published:yyyy-MM-dd}）" : string.Empty;
            var asset = PickForCurrentArchitecture();
            var size = asset?.Size > 0 ? $" · {SevenZipAsset.FormatSize(asset.Size)}" : string.Empty;
            var arch = asset is null ? string.Empty : $" · {DescribeArchitecture(asset.Name)}";
            return $"7-Zip {Version}{date}{arch}{size}";
        }
    }

    private static string DescribeArchitecture(string assetName)
    {
        if (assetName.Contains("-arm64", StringComparison.OrdinalIgnoreCase))
        {
            return "ARM64";
        }

        if (assetName.Contains("-x64", StringComparison.OrdinalIgnoreCase))
        {
            return "64 位";
        }

        return "32 位";
    }
}

/// <summary>版本列表获取结果（可能带一条提示，例如"网络不可用，已用内置列表"）。</summary>
public sealed record ReleaseCatalogResult(IReadOnlyList<SevenZipRelease> Releases, string? Warning);

/// <summary>
/// 从 7-Zip 官方 GitHub Releases（ip7z/7zip）获取版本列表：
/// 这里能同时拿到版本号、发布日期、各架构安装包的真实大小与下载直链。
/// </summary>
public static class SevenZipReleaseCatalog
{
    private const string ReleasesApi = "https://api.github.com/repos/ip7z/7zip/releases?per_page=12";
    private const int MaxReleases = 8;

    private static readonly HttpClient Http = CreateHttpClient();

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("7zAIWin11RCM/1.0 (+https://github.com/Minecraftmc22/7zAIWin11RCM)");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return client;
    }

    public static HttpClient SharedClient => Http;

    /// <summary>拉取版本列表；失败时回退到内置列表。</summary>
    public static async Task<ReleaseCatalogResult> FetchAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await Http.GetAsync(ReleasesApi, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);

            var releases = new List<SevenZipRelease>();

            foreach (var item in document.RootElement.EnumerateArray())
            {
                if (IsTrue(item, "draft") || IsTrue(item, "prerelease"))
                {
                    continue;
                }

                var tag = item.TryGetProperty("tag_name", out var tagElement) ? tagElement.GetString() : null;
                if (string.IsNullOrWhiteSpace(tag))
                {
                    continue;
                }

                var version = tag.Trim().TrimStart('v', 'V');
                DateTimeOffset? published = null;
                if (item.TryGetProperty("published_at", out var publishedElement)
                    && publishedElement.ValueKind == JsonValueKind.String
                    && DateTimeOffset.TryParse(publishedElement.GetString(), out var parsed))
                {
                    published = parsed;
                }

                var assets = new List<SevenZipAsset>();
                if (item.TryGetProperty("assets", out var assetsElement) && assetsElement.ValueKind == JsonValueKind.Array)
                {
                    foreach (var asset in assetsElement.EnumerateArray())
                    {
                        var name = asset.TryGetProperty("name", out var nameElement) ? nameElement.GetString() : null;
                        var url = asset.TryGetProperty("browser_download_url", out var urlElement) ? urlElement.GetString() : null;
                        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(url) || !SevenZipRelease.IsWindowsInstaller(name))
                        {
                            continue;
                        }

                        var size = asset.TryGetProperty("size", out var sizeElement) && sizeElement.TryGetInt64(out var parsedSize)
                            ? parsedSize
                            : 0L;
                        assets.Add(new SevenZipAsset(name, url, size));
                    }
                }

                if (assets.Count == 0)
                {
                    continue;
                }

                releases.Add(new SevenZipRelease(version, published, assets));
            }

            if (releases.Count == 0)
            {
                return new ReleaseCatalogResult(BuiltInReleases, "在线版本列表为空，已使用内置列表。");
            }

            var ordered = releases
                .OrderByDescending(r => r.PublishedAt ?? DateTimeOffset.MinValue)
                .Take(MaxReleases)
                .ToList();

            return new ReleaseCatalogResult(ordered, null);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or NotSupportedException)
        {
            Log.Write("获取 7-Zip 版本列表失败，使用内置列表", ex);
            return new ReleaseCatalogResult(BuiltInReleases, "无法获取在线版本列表（网络不可用或被限流），已回退到内置版本。");
        }
    }

    /// <summary>离线兜底列表。</summary>
    public static IReadOnlyList<SevenZipRelease> BuiltInReleases { get; } = new[]
    {
        ("26.06", 1770000000L),
        ("25.01", 1754000000L),
        ("24.09", 1732800000L),
    }.Select(item => BuildBuiltIn(item.Item1, item.Item2)).ToArray();

    private static SevenZipRelease BuildBuiltIn(string version, long unixTime)
    {
        var code = version.Replace(".", string.Empty);
        var baseUrl = $"https://github.com/ip7z/7zip/releases/download/{version}";
        var assets = new List<SevenZipAsset>
        {
            new($"7z{code}-x64.exe", $"{baseUrl}/7z{code}-x64.exe", 0),
            new($"7z{code}.exe", $"{baseUrl}/7z{code}.exe", 0),
        };

        if (RuntimeInformation.OSArchitecture is Architecture.Arm64 or Architecture.X64)
        {
            assets.Add(new($"7z{code}-arm64.exe", $"{baseUrl}/7z{code}-arm64.exe", 0));
        }

        return new SevenZipRelease(version, DateTimeOffset.FromUnixTimeSeconds(unixTime), assets);
    }

    private static bool IsTrue(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.True;
}
