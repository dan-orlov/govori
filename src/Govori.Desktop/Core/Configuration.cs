using System.IO;
using System.Text.Json;
using System.Security.Cryptography;
using System.Text.Json.Serialization;

namespace Govori;

public sealed class Settings
{
    // macOS uses Ctrl+Space to switch input sources and cannot suppress it.
    public string Hotkey { get; set; } = OperatingSystem.IsMacOS() ? "RightAlt" : "Ctrl+Space";
    public string HistoryHotkey { get; set; } = "Ctrl+Alt+J";
    public string Model { get; set; } = "gemini-2.5-flash";
    public string Provider { get; set; } = Providers.Gemini;
    public string LiteLlmBaseUrl { get; set; } = "";
    public string LiteLlmModel { get; set; } = "";
    [JsonIgnore] public string ActiveModel => Provider == Providers.LiteLlm ? LiteLlmModel : Model;
    public string Vocabulary { get; set; } = "";
    public string Language { get; set; } = "system";
    public bool RestoreClipboard { get; set; } = true;
    public bool StartWithSystem { get; set; }
    public int MaxRecordingSeconds { get; set; } = 180;
    public int MicrophoneDevice { get; set; } = -1;
    public string IndicatorEdge { get; set; } = "Right";
    public Settings Clone() => (Settings)MemberwiseClone();
}

public static class Configuration
{
    public static string DefaultDataDirectory { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Dictophone.CrossPlatform");
    public static string DataDirectory { get; private set; } = DefaultDataDirectory;
    public static string CredentialsPath => Path.Combine(DefaultDataDirectory, ".env");
    public static string SettingsPath => Path.Combine(DataDirectory, "settings.json");
    public static string AudioDirectory => Path.Combine(DataDirectory, "recordings");
    internal static void UseDataDirectory(string path) => DataDirectory = Path.GetFullPath(path);
    public static string EnvPath { get; private set; } = "";
    private static Dictionary<string, string> env = new(StringComparer.OrdinalIgnoreCase);

    public static void LoadEnvironment()
    {
        // Credentials and user data stay outside the source tree. Portable
        // installations can still supply a local, ignored .env file.
        var candidates = new List<string> { CredentialsPath };
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory != null && candidates.Count < 6; directory = directory.Parent) candidates.Add(Path.Combine(directory.FullName, ".env"));
        candidates.Add(Path.Combine(Environment.CurrentDirectory, ".env"));
        EnvPath = candidates.Select(Path.GetFullPath).FirstOrDefault(File.Exists) ?? CredentialsPath;
        env = File.Exists(EnvPath) ? ParseEnv(File.ReadAllText(EnvPath)) : new(StringComparer.OrdinalIgnoreCase);
        Directory.CreateDirectory(DataDirectory);
        Directory.CreateDirectory(AudioDirectory);
    }

    public static Dictionary<string, string> ParseEnv(string text)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim().TrimStart('\uFEFF');
            if (line.StartsWith("export ")) line = line[7..].Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            int eq = line.IndexOf('=');
            if (eq < 1) continue;
            var value = line[(eq + 1)..].Trim();
            if (value.Length >= 2 && (value[0] == '"' || value[0] == '\'') && value[^1] == value[0]) value = value[1..^1];
            else { int comment = value.IndexOf(" #", StringComparison.Ordinal); if (comment >= 0) value = value[..comment].TrimEnd(); }
            result[line[..eq].Trim()] = value;
        }
        return result;
    }

    public static string? ApiKeyStorageError { get; private set; }
    internal static bool HasSavedApiKey => HasSavedKey(Providers.Gemini);
    internal static bool HasSavedKey(string provider) => File.Exists(ApiKeyStore.PathFor(provider));
    public static string ApiKey => GetApiKey(Providers.Gemini);
    public static string GetApiKey(string provider)
    {
        ApiKeyStorageError = null;
        try
        {
            string saved = ApiKeyStore.Read(provider);
            if (!string.IsNullOrWhiteSpace(saved)) return saved;
        }
        catch (Exception ex) when (ex is CryptographicException or IOException or UnauthorizedAccessException)
        {
            ApiKeyStorageError = Localization.T("ApiKeyReadError");
        }
        if (provider == Providers.LiteLlm) return EnvironmentValue("LITELLM_API_KEY");
        return EnvironmentValue("GEMINI_API_KEY");
    }
    internal static string EnvironmentValue(string name)
    {
        string? value = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrWhiteSpace(value) && OperatingSystem.IsWindows()) value = Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.User);
        if (string.IsNullOrWhiteSpace(value) && OperatingSystem.IsWindows()) value = Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.Machine);
        return !string.IsNullOrWhiteSpace(value) ? value.Trim() : env.GetValueOrDefault(name, "");
    }
    public static Settings LoadSettings()
    {
        if (File.Exists(SettingsPath))
        {
            try
            {
                var settings = JsonSerializer.Deserialize<Settings>(File.ReadAllText(SettingsPath)) ?? new();
                if (!Localization.IsSupported(settings.Language)) settings.Language = "system";
                if (!Providers.IsSupported(settings.Provider)) settings.Provider = Providers.Gemini;
                if (string.IsNullOrWhiteSpace(settings.LiteLlmBaseUrl)) settings.LiteLlmBaseUrl = EnvironmentValue("LITELLM_BASE_URL");
                return settings;
            }
            catch (JsonException) { File.Copy(SettingsPath, SettingsPath + ".damaged", true); }
        }
        return new Settings { Model = env.GetValueOrDefault("GEMINI_MODEL", "gemini-2.5-flash"), LiteLlmBaseUrl = EnvironmentValue("LITELLM_BASE_URL"), Vocabulary = env.GetValueOrDefault("DICTATION_CONTEXT", "") };
    }
    public static void SaveSettings(Settings settings) => AtomicWrite(SettingsPath, JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }));
    public static void AtomicWrite(string path, string text)
    {
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, text);
        File.Move(tmp, path, true);
    }
}
