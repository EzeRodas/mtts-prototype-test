using System.Text;

namespace MoonTTS.Core.Utilities;

/* File Structure:
        - LogDirectory : string
        - Info(string source, string message) : void
        - Warning(string source, string message) : void
        - Error(string source, string message, Exception? ex) : void
        - WriteEntry(string level, string source, string message, Exception? ex) : void
        - RotateIfNeeded(string logPath) : void
*/

/* Lightweight, zero-dependency, AOT-safe rotating file logger writing to <ConfigDir>/logs/moontts.log.
   Ensures diagnostics are preserved even on Windows WinExe builds where Console output is invisible */
public static class AppLogger
{
    private const long MaxLogFileBytes = 2 * 1024 * 1024;
    private const int MaxArchivedFiles = 3;
    private const string LogFileName = "moontts.log";

    private static readonly Lock WriteLock = new();
    private static readonly Lazy<string> LazyLogDir = new(PathResolver.GetLogsDirectory);

    /* Returns the directory holding application log files */
    public static string LogDirectory => LazyLogDir.Value;

    /* Writes an informational diagnostic entry */
    public static void Info(string source, string message) =>
        WriteEntry("INFO", source, message, null);

    /* Writes a warning diagnostic entry */
    public static void Warning(string source, string message) =>
        WriteEntry("WARN", source, message, null);

    /* Writes an error diagnostic entry with optional exception details */
    public static void Error(string source, string message, Exception? ex = null) =>
        WriteEntry("ERROR", source, message, ex);

    /* Formats and appends one log entry under a process-local lock, rotating files when the size cap is reached */
    private static void WriteEntry(string level, string source, string message, Exception? ex)
    {
        string timestamp = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss.fff");
        var sb = new StringBuilder(128);
        sb.Append('[').Append(timestamp).Append(" UTC] [").Append(level).Append("] [").Append(source).Append("] ").Append(message);
        if (ex != null)
        {
            sb.AppendLine();
            sb.Append(ex);
        }
        sb.AppendLine();

        string line = sb.ToString();

        if (level is "WARN" or "ERROR")
        {
            try { Console.Error.Write(line); } catch { /* Ignore closed console handles */ }
        }

        try
        {
            lock (WriteLock)
            {
                string logDir = LazyLogDir.Value;
                Directory.CreateDirectory(logDir);
                string logPath = Path.Combine(logDir, LogFileName);

                RotateIfNeeded(logPath);
                File.AppendAllText(logPath, line, Encoding.UTF8);
            }
        }
        catch
        {
            /* Logging must never crash the host application */
        }
    }

    /* Rotates moontts.log -> moontts.log.1 .. moontts.log.3 once the active file exceeds MaxLogFileBytes */
    private static void RotateIfNeeded(string logPath)
    {
        if (!File.Exists(logPath))
            return;

        var info = new FileInfo(logPath);
        if (info.Length < MaxLogFileBytes)
            return;

        string oldest = $"{logPath}.{MaxArchivedFiles}";
        if (File.Exists(oldest))
            File.Delete(oldest);

        for (int i = MaxArchivedFiles - 1; i >= 1; i--)
        {
            string src = $"{logPath}.{i}";
            string dst = $"{logPath}.{i + 1}";
            if (File.Exists(src))
                File.Move(src, dst, overwrite: true);
        }

        File.Move(logPath, $"{logPath}.1", overwrite: true);
    }
}
