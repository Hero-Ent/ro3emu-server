using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Ro3Emu.Launcher;

/// <summary>
/// launcher.json: built into the exe (LauncherConfigFile in the csproj); a file beside the exe wins. Two modes:
/// "operator" (a server's own launcher: its client repository, news, links) and "ro3emu" (RO3Emu's own app: the game
/// from the official servers, a local server, updates from our GitHub releases). Without "mode", a config that names a
/// "repository" is an operator's, anything else is RO3Emu's.
/// </summary>
public sealed class LauncherConfig
{
    [JsonPropertyName("mode")] public string? Mode { get; set; }
    [JsonPropertyName("repository")] public string? Repository { get; set; }
    [JsonPropertyName("channel")] public string Channel { get; set; } = "test";
    [JsonPropertyName("server")] public string? Server { get; set; }
    /// <summary>The name on the title bar, the built-in hero, the tray and the shortcuts.</summary>
    [JsonPropertyName("serverName")] public string ServerName { get; set; } = "RO3Emu";
    /// <summary>
    /// The hero picture: a file beside the exe, or a URL relative to the repository (an absolute URL works too).
    /// Null = &lt;repository&gt;/hero.jpg when it exists, else the page's built-in sky.
    /// </summary>
    [JsonPropertyName("hero")] public string? Hero { get; set; }
    /// <summary>The title bar's links (Website, Discord, Wiki…); none by default.</summary>
    [JsonPropertyName("links")] public List<LauncherLink> Links { get; set; } = [];

    /// <summary>RO3Emu's own app rather than an operator's launcher (see the class summary).</summary>
    [JsonIgnore]
    public bool IsRo3EmuMode => Mode is { } mode
        ? string.Equals(mode, "ro3emu", StringComparison.OrdinalIgnoreCase)
        : string.IsNullOrWhiteSpace(Repository);

    /// <summary>The operator's repository; the old default for a config that names none in operator mode.</summary>
    [JsonIgnore]
    public string RepositoryUrl => string.IsNullOrWhiteSpace(Repository) ? "http://localhost:8790/" : Repository;

    static readonly JsonSerializerOptions Options = new() { ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

    public static LauncherConfig Load(string besideExe)
    {
        try
        {
            if (File.Exists(besideExe)) return JsonSerializer.Deserialize<LauncherConfig>(File.ReadAllBytes(besideExe), Options) ?? new();
            using var stream = typeof(LauncherConfig).Assembly.GetManifestResourceStream("launcher.json");
            return stream is null ? new() : JsonSerializer.Deserialize<LauncherConfig>(stream, Options) ?? new();
        }
        catch (JsonException)
        {
            return new();
        }
    }
}

/// <summary>One link of the title bar: an http(s) URL opened in the player's browser.</summary>
public sealed class LauncherLink
{
    [JsonPropertyName("label")] public string Label { get; set; } = "";
    [JsonPropertyName("url")] public string Url { get; set; } = "";
    /// <summary>Optional: globe, chat, book, trophy, flag, code or link; otherwise the page picks one from the label.</summary>
    [JsonPropertyName("icon")] public string? Icon { get; set; }
}

/// <summary>Per-user choices (%APPDATA%\RO3Emu\launcher-settings.json).</summary>
public sealed class UserSettings
{
    [JsonPropertyName("clientDirectory")] public string? ClientDirectory { get; set; }
    /// <summary>The Desktop / Start menu shortcut question was answered (asked once, on the first run).</summary>
    [JsonPropertyName("shortcutsAsked")] public bool ShortcutsAsked { get; set; }
    /// <summary>The game version whose "What's new" the player has seen; a newer installed version shows its notes once.</summary>
    [JsonPropertyName("notesSeenVersion")] public string? NotesSeenVersion { get; set; }
    /// <summary>
    /// The game folder this launcher created by installing into an empty or new folder: the only one Settings →
    /// Uninstall removes. A folder that already held a client (for example an official ROO_PC) is never deleted.
    /// </summary>
    [JsonPropertyName("ownedDirectory")] public string? OwnedDirectory { get; set; }
    /// <summary>RO3Emu mode: the app version whose release notes were shown; a newer running version shows its notes once.</summary>
    [JsonPropertyName("appNotesSeenVersion")] public string? AppNotesSeenVersion { get; set; }

    static string FilePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RO3Emu", "launcher-settings.json");

    public static UserSettings Load()
    {
        try
        {
            return File.Exists(FilePath) ? JsonSerializer.Deserialize<UserSettings>(File.ReadAllBytes(FilePath)) ?? new() : new();
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            return new();
        }
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
}
