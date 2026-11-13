using System.Text.Json;

namespace SevenZipAIWin11RCM.Services;

/// <summary>把设置读写到 %LOCALAPPDATA%\7zAIWin11RCM\settings.json。</summary>
public static class SettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static string DataDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "7zAIWin11RCM");

    public static string SettingsFile => Path.Combine(DataDirectory, "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (!File.Exists(SettingsFile))
            {
                return AppSettings.CreateDefault();
            }

            var json = File.ReadAllText(SettingsFile);
            var settings = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions);
            return settings ?? AppSettings.CreateDefault();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            Log.Write("读取设置失败，使用默认值", ex);
            return AppSettings.CreateDefault();
        }
    }

    public static bool Save(AppSettings settings)
    {
        try
        {
            Directory.CreateDirectory(DataDirectory);
            settings.LastSavedAt = DateTimeOffset.Now;
            var json = JsonSerializer.Serialize(settings, JsonOptions);
            File.WriteAllText(SettingsFile, json);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Write("保存设置失败", ex);
            return false;
        }
    }
}

/// <summary>极简日志，写到 %LOCALAPPDATA%\7zAIWin11RCM\app.log，方便排查右键菜单调用失败。</summary>
public static class Log
{
    private static readonly object Gate = new();

    public static string LogFile => Path.Combine(SettingsStore.DataDirectory, "app.log");

    public static void Write(string message, Exception? ex = null)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(SettingsStore.DataDirectory);
                var line = $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss}] {message}";
                if (ex is not null)
                {
                    line += Environment.NewLine + ex;
                }

                File.AppendAllText(LogFile, line + Environment.NewLine);
            }
        }
        catch
        {
            // 记日志本身不应影响主流程
        }
    }
}
