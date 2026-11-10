using System.Text.RegularExpressions;

namespace MoonTTS.Infrastructure.Audio.Linux;

public record LinuxSink(string RealName, string DisplayName);

public class LinuxDeviceEnumerator
{
    public static List<LinuxSink> GetSinks()
    {
        /* Tries first with pactl */
        var pactlRes = LinuxProcessRunner.RunCommand("pactl", "list sinks");
        if (pactlRes.IsSuccess)
        {
            var sinks = ParsePactlSinks(pactlRes.Value);
            if (sinks.Count > 0) return sinks;
        }
        
        /* Fallback with wpctl */
        var wpctlRes = LinuxProcessRunner.RunCommand("wpctl", "status");
        if (wpctlRes.IsSuccess)
        {
            var sinks = ParseWpctlSinks(wpctlRes.Value);
            if (sinks.Count > 0) return sinks;
        }

        return [];
    }

    private static List<LinuxSink> ParsePactlSinks(string output)
    {
        var sinks = new List<LinuxSink>();
        string? currentName = null;
        string? currentDesc = null;

        using var reader = new StringReader(output);
        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            line = line.Trim();
            if (line.StartsWith("Sink #"))
            {
                if (!string.IsNullOrEmpty(currentName)) 
                    sinks.Add(new LinuxSink(currentName, currentDesc ?? currentName));
                currentName = null;
                currentDesc = null;
            }
            else if (line.StartsWith("Name:"))
            {
                currentName = line.Substring(5).Trim();
            }
            else if (line.StartsWith("Description:"))
            {
                currentDesc = line.Substring(12).Trim();
            }
        }
        if (!string.IsNullOrEmpty(currentName))
            sinks.Add(new LinuxSink(currentName, currentDesc ?? currentName));
        return sinks;
    }

    private static List<LinuxSink> ParseWpctlSinks(string output)
    {
        var sinks = new List<LinuxSink>();
        if (!output.Contains("Sinks:")) return sinks;

        string sinksPart = output.Split("Sinks:")[1].Split("Sources:")[0];
        using var reader = new StringReader(sinksPart);
        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            var match = Regex.Match(
                line,
                @"(?:[*\s]*?)\d+\.\s+(.*?)(?:\s+\[vol|$)");
            if (match.Success)
            {
                string id = match.Groups[1].Value.Trim();
                string desc = match.Groups[2].Value.Trim();
                sinks.Add(new LinuxSink(id, desc));
            }
        }
        return sinks;
    }
    
}