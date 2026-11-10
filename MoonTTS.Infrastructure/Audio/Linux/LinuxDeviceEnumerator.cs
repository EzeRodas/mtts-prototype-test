using System.Text.RegularExpressions;

namespace MoonTTS.Infrastructure.Audio.Linux;

/* File Structure:
        - LinuxSink(string RealName, string DisplayName)
        - GetSinks(ILinuxCommandRunner? commandRunner) : List<LinuxSink>
        - ParsePactlSinks(string output) : List<LinuxSink>
        - ParseWpctlSinks(string output) : List<LinuxSink>
        - WpctlSinkLineRegex() : Regex
*/

public record LinuxSink(string RealName, string DisplayName);

public partial class LinuxDeviceEnumerator
{
    /* Discovers available Linux audio sinks via pactl with wpctl fallback */
    public static List<LinuxSink> GetSinks(ILinuxCommandRunner? commandRunner = null)
    {
        var runner = commandRunner ?? LinuxProcessRunner.Instance;

        /* Tries first with pactl */
        var pactlRes = runner.RunCommand("pactl", "list sinks");
        if (pactlRes.IsSuccess && !string.IsNullOrWhiteSpace(pactlRes.Value))
        {
            var sinks = ParsePactlSinks(pactlRes.Value);
            if (sinks.Count > 0) return sinks;
        }
        
        /* Fallback with wpctl */
        var wpctlRes = runner.RunCommand("wpctl", "status");
        if (wpctlRes.IsSuccess && !string.IsNullOrWhiteSpace(wpctlRes.Value))
        {
            var sinks = ParseWpctlSinks(wpctlRes.Value);
            if (sinks.Count > 0) return sinks;
        }

        return [];
    }

    /* Parses PulseAudio / pipewire-pulse sink blocks from 'pactl list sinks' output */
    public static List<LinuxSink> ParsePactlSinks(string output)
    {
        var sinks = new List<LinuxSink>();
        string? currentName = null;
        string? currentDesc = null;

        using var reader = new StringReader(output);
        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            line = line.Trim();
            if (line.StartsWith("Sink #", StringComparison.Ordinal))
            {
                if (!string.IsNullOrEmpty(currentName)) 
                    sinks.Add(new LinuxSink(currentName, currentDesc ?? currentName));
                currentName = null;
                currentDesc = null;
            }
            else if (line.StartsWith("Name:", StringComparison.Ordinal))
            {
                currentName = line.Substring(5).Trim();
            }
            else if (line.StartsWith("Description:", StringComparison.Ordinal))
            {
                currentDesc = line.Substring(12).Trim();
            }
        }
        if (!string.IsNullOrEmpty(currentName))
            sinks.Add(new LinuxSink(currentName, currentDesc ?? currentName));
        return sinks;
    }

    /* Parses PipeWire / WirePlumber sink lines from the Sinks section of 'wpctl status' output */
    public static List<LinuxSink> ParseWpctlSinks(string output)
    {
        var sinks = new List<LinuxSink>();
        if (!output.Contains("Sinks:", StringComparison.Ordinal)) return sinks;

        string sinksPart = output.Split("Sinks:")[1].Split("Sources:")[0];
        using var reader = new StringReader(sinksPart);
        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            var match = WpctlSinkLineRegex().Match(line);
            if (match.Success)
            {
                string id = match.Groups[1].Value.Trim();
                string desc = match.Groups[2].Value.Trim();
                if (!string.IsNullOrEmpty(id))
                {
                    sinks.Add(new LinuxSink(id, string.IsNullOrEmpty(desc) ? id : desc));
                }
            }
        }
        return sinks;
    }

    /* Source-generated AOT-safe regex matching a WirePlumber sink entry ('*  46. Built-in Audio [vol: 1.00]') */
    [GeneratedRegex(@"^[│├└─\s*]*(\d+)\.\s+(.+?)(?:\s+\[vol:.*)?$")]
    private static partial Regex WpctlSinkLineRegex();
}
