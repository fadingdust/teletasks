namespace TeleTasks.Configuration;

/// <summary>
/// Single source of truth for where the wizard writes config, where discover writes
/// tasks.json, and where the bot reads them from. Resolves in this order:
///   1. <c>$TELETASKS_CONFIG_DIR</c>
///   2. <c>$XDG_CONFIG_HOME/teletasks</c>
///   3. <c>$HOME/.config/teletasks</c> (Linux/macOS default)
///   4. <c>%APPDATA%\teletasks</c> on Windows when HOME is unset
///   5. <see cref="AppContext.BaseDirectory"/> as a last-resort fallback
/// </summary>
public static class UserConfigDirectory
{
    public const string EnvVar = "TELETASKS_CONFIG_DIR";

    public static string Resolve()
    {
        var explicitOverride = Environment.GetEnvironmentVariable(EnvVar);
        if (!string.IsNullOrWhiteSpace(explicitOverride)) return explicitOverride;

        var xdg = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        if (!string.IsNullOrWhiteSpace(xdg)) return Path.Combine(xdg, "teletasks");

        // GetFolderPath resolves correctly even when $HOME is empty in the
        // environment: on Linux/macOS it falls through to getpwuid(). Returns
        // ~/.config on Linux, %APPDATA% on Windows, ~/Library/Application Support
        // on macOS — all the right place for per-user app config.
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (!string.IsNullOrWhiteSpace(appData))
            return Path.Combine(appData, "teletasks");

        var home = Environment.GetEnvironmentVariable("HOME");
        if (!string.IsNullOrWhiteSpace(home))
            return Path.Combine(home, ".config", "teletasks");

        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrWhiteSpace(profile))
            return Path.Combine(profile, ".config", "teletasks");

        return AppContext.BaseDirectory;
    }

    public static string EnsureExists()
    {
        var dir = Resolve();
        Directory.CreateDirectory(dir);
        return dir;
    }

    public static string LocalSettingsPath => Path.Combine(Resolve(), "appsettings.Local.json");

    public static string TasksPath => Path.Combine(Resolve(), "tasks.json");
}
