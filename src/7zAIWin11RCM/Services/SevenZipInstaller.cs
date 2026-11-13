using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;

namespace SevenZipAIWin11RCM.Services;

/// <summary>下载进度快照。</summary>
public readonly record struct DownloadProgress(long Received, long Total, double BytesPerSecond)
{
    public double Percent => Total > 0 ? Math.Clamp(Received * 100.0 / Total, 0, 100) : 0;

    public string DetailText
    {
        get
        {
            var receivedText = SevenZipAsset.FormatSize(Received);
            var totalText = Total > 0 ? SevenZipAsset.FormatSize(Total) : "未知";
            var speedText = BytesPerSecond > 0 ? $" · {SevenZipAsset.FormatSize((long)BytesPerSecond)}/s" : string.Empty;
            return $"{receivedText} / {totalText}{speedText}";
        }
    }
}

/// <summary>安装结果。</summary>
public sealed record SevenZipInstallResult(bool Success, string Message, string? Directory = null);

/// <summary>下载并静默安装 7-Zip 安装包。</summary>
public static class SevenZipInstaller
{
    private static readonly TimeSpan InstallTimeout = TimeSpan.FromMinutes(5);

    public static string DownloadDirectory => Path.Combine(SettingsStore.DataDirectory, "downloads");

    /// <summary>默认安装根目录（C:\Program Files）。</summary>
    public static string DefaultInstallRoot =>
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);

    /// <summary>
    /// 把用户选中的目录换算成真正的安装目录：
    /// 若已直接选中 7-Zip / 7zip 目录则原样使用，否则在其下追加 7-Zip 子目录。
    /// </summary>
    public static string ResolveTargetDirectory(string pickedDirectory)
    {
        var picked = Path.GetFullPath(pickedDirectory.Trim().Trim('"'));

        // 用 TrimEndingDirectorySeparator：盘符根目录（C:\）不会被削成 "C:"
        picked = Path.TrimEndingDirectorySeparator(picked);

        if (picked.Length == 0)
        {
            return Path.Combine(DefaultInstallRoot, "7-Zip");
        }

        var leaf = Path.GetFileName(picked);
        if (string.Equals(leaf, "7-Zip", StringComparison.OrdinalIgnoreCase)
            || string.Equals(leaf, "7zip", StringComparison.OrdinalIgnoreCase))
        {
            return picked;
        }

        return Path.Combine(picked, "7-Zip");
    }

    /// <summary>下载安装包到本地缓存目录，返回文件路径。</summary>
    public static async Task<string> DownloadAsync(
        SevenZipAsset asset,
        IProgress<DownloadProgress>? progress,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(DownloadDirectory);
        var targetPath = Path.Combine(DownloadDirectory, asset.Name);
        var temporaryPath = targetPath + ".part";

        using var response = await SevenZipReleaseCatalog.SharedClient
            .GetAsync(asset.Url, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var total = response.Content.Headers.ContentLength ?? asset.Size;

        await using (var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
        await using (var destination = new FileStream(
                         temporaryPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
        {
            var buffer = new byte[81920];
            long received = 0;
            var stopwatch = Stopwatch.StartNew();
            var lastReportSeconds = -1.0;

            int read;
            while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
            {
                await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                received += read;

                var elapsed = stopwatch.Elapsed.TotalSeconds;
                if (elapsed - lastReportSeconds >= 0.15 || (total > 0 && received >= total))
                {
                    lastReportSeconds = elapsed;
                    progress?.Report(new DownloadProgress(
                        received, total, elapsed > 0 ? received / elapsed : 0));
                }
            }

            progress?.Report(new DownloadProgress(received, total > 0 ? total : received, 0));
        }

        if (File.Exists(targetPath))
        {
            File.Delete(targetPath);
        }

        File.Move(temporaryPath, targetPath);
        Log.Write($"已下载 7-Zip 安装包：{targetPath}");
        return targetPath;
    }

    /// <summary>静默安装到指定目录（需要管理员权限，会弹出 UAC）。</summary>
    public static async Task<SevenZipInstallResult> InstallAsync(
        string installerPath,
        string targetDirectory,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(installerPath))
        {
            return new SevenZipInstallResult(false, "安装包不存在，请重新下载。");
        }

        // NSIS 约定：/S 静默安装，/D= 必须是最后一个参数且不加引号
        var info = new ProcessStartInfo(installerPath)
        {
            Arguments = $"/S /D={targetDirectory}",
            UseShellExecute = true,
            Verb = "runas",
        };

        Process? process;
        try
        {
            process = Process.Start(info);
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            return new SevenZipInstallResult(false, "已取消管理员授权，安装未开始。");
        }
        catch (Win32Exception ex)
        {
            Log.Write("启动 7-Zip 安装程序失败", ex);
            return new SevenZipInstallResult(false, $"无法启动安装程序：{ex.Message}");
        }

        if (process is null)
        {
            return new SevenZipInstallResult(false, "无法启动安装程序。");
        }

        using (process)
        {
            var exitTask = process.WaitForExitAsync(cancellationToken);
            var completed = await Task.WhenAny(exitTask, Task.Delay(InstallTimeout, cancellationToken))
                .ConfigureAwait(false);

            if (completed != exitTask)
            {
                return new SevenZipInstallResult(false, "安装超时（超过 5 分钟），请手动确认安装状态。");
            }
        }

        // 安装程序退出后文件可能还需要一点时间落地
        for (var attempt = 0; attempt < 10; attempt++)
        {
            var installation = SevenZipLocator.Validate(targetDirectory);
            if (installation is not null)
            {
                Log.Write($"7-Zip 安装完成：{installation.Directory}（{installation.Version}）");
                return new SevenZipInstallResult(
                    true,
                    string.IsNullOrEmpty(installation.Version)
                        ? $"7-Zip 已安装到 {installation.Directory}。"
                        : $"7-Zip {installation.Version} 已安装到 {installation.Directory}。",
                    installation.Directory);
            }

            await Task.Delay(300, cancellationToken).ConfigureAwait(false);
        }

        return new SevenZipInstallResult(
            false,
            $"安装程序已退出，但在 {targetDirectory} 中没有找到 7zG.exe / 7zFM.exe，请检查是否有安全软件拦截。");
    }
}
