using System.Runtime.InteropServices;

namespace MoonTTS.Core.Utilities;

/* File Structure:
        - GetConfigDirectory() : string
        - GetLogsDirectory() : string
        - GetModelsDirectory() : string
        - GetEnginesDirectory() : string
        - GetXdgDataHome() : string
*/

public static class PathResolver
{
    /* Resolves the platform-specific configuration directory */
    public static string GetConfigDirectory()
    {
        /* On windows returns AppData/Moon-TTS */
        if (OperatingSystem.IsWindows())
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Moon-TTS");
        }

        /* On macOS saves to ~/Library/Application Support/Moon-TTS */
        if (OperatingSystem.IsMacOS() || OperatingSystem.IsMacCatalyst())
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library",
                "Application Support", "Moon-TTS");
        }

        /* On Linux / POSIX follows XDG_CONFIG_HOME, falling back to ~/.config */
        string? configHome = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        if (string.IsNullOrEmpty(configHome) || !Path.IsPathRooted(configHome))
        {
            configHome = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
        }
        return Path.Combine(configHome, "Moon-TTS");
    }

    /* Resolves and creates the directory used for rotating diagnostic logs */
    public static string GetLogsDirectory()
    {
        string logsPath = Path.Combine(GetConfigDirectory(), "logs");
        Directory.CreateDirectory(logsPath);
        return logsPath;
    }

    /* Resolves the default directory where downloaded TTS model weights are stored */
    public static string GetModelsDirectory()
    {
        string defaultPath;

        if (OperatingSystem.IsWindows())
        {
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            defaultPath = Path.Combine(appData, "Moon-TTS", "models");
        }
        else if (OperatingSystem.IsMacOS() || OperatingSystem.IsMacCatalyst())
        {
            defaultPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "Library", "Application Support", "Moon-TTS", "models");
        }
        else
        {
            defaultPath = Path.Combine(GetXdgDataHome(), "Moon-TTS", "models");
        }

        Directory.CreateDirectory(defaultPath);
        return defaultPath;
    }

    /* Resolves the folder engines are installed into (one sub folder per engine, each with an engine.json) */
    public static string GetEnginesDirectory()
    {
        string enginesPath;

        if (OperatingSystem.IsWindows())
        {
            enginesPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Moon-TTS", "engines");
        }
        else if (OperatingSystem.IsMacOS() || OperatingSystem.IsMacCatalyst())
        {
            enginesPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library",
                "Application Support", "Moon-TTS", "engines");
        }
        else
        {
            enginesPath = Path.Combine(GetXdgDataHome(), "Moon-TTS", "engines");
        }

        Directory.CreateDirectory(enginesPath);
        return enginesPath;
    }

    /* Resolves XDG_DATA_HOME on Linux / POSIX, falling back to ~/.local/share */
    private static string GetXdgDataHome()
    {
        string? dataHome = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        if (string.IsNullOrEmpty(dataHome) || !Path.IsPathRooted(dataHome))
        {
            dataHome = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share");
        }
        return dataHome;
    }
}