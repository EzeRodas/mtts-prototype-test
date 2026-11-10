using MoonTTS.Domain.Common;

namespace MoonTTS.Infrastructure.Audio.Linux;

/* File Structure:
        - SinkName : string
        - SourceName : string
        - VirtualDeviceId : string
        - Exists(ILinuxCommandRunner? commandRunner) : bool
        - EnsureCreated(ILinuxCommandRunner? commandRunner) : Result
*/

/* Manages a virtual mic on Linux for outputting the audio */
/* This is only on Linux as Windows requires a digitally signed certification to allow you to make the custom
   C++ kernel drivers needed to be able to create one. You need to pay for it. Similar situation on Mac */

public class LinuxVirtualMicService
{
    public const string SinkName = "Moon-TTS_Sink";
    public const string SourceName = "Moon-TTS_Mic";
    public const string VirtualDeviceId = "mtts_mic";

    /* Checks if the virtual mic is already created */
    public static bool Exists(ILinuxCommandRunner? commandRunner = null)
    {
        var runner = commandRunner ?? LinuxProcessRunner.Instance;

        /* Checks using PulseAudio */
        var pactlRes = runner.RunCommand("pactl", "list sinks short");
        if (pactlRes.IsSuccess && pactlRes.Value != null && pactlRes.Value.Contains(SinkName, StringComparison.OrdinalIgnoreCase))
            return true;
        
        /* If that didn't work, checks with WirePlumber */
        var wpctlRes = runner.RunCommand("wpctl", "status");
        if (wpctlRes.IsSuccess && wpctlRes.Value != null && wpctlRes.Value.Contains(SinkName, StringComparison.OrdinalIgnoreCase))
            return true;
        
        return false;
    }

    /* Ensures the virtual null-sink and remapped microphone source exist in PulseAudio or PipeWire */
    public static Result EnsureCreated(ILinuxCommandRunner? commandRunner = null)
    {
        var runner = commandRunner ?? LinuxProcessRunner.Instance;

        if (Exists(runner)) return Result.Success();
        
        /* Creates the null sink */
        var nullSinkRes = runner.RunCommand("pactl", $"load-module module-null-sink sink_name={SinkName} sink_properties=device.description='\"Moon-TTS\\ Virtual\\ Sink\"'");
        if (nullSinkRes.IsSuccess)
        {
            /* Remaps the sink monitor to appear as an input */
            var remapRes = runner.RunCommand("pactl",
                $"load-module module-remap-source master={SinkName}.monitor source_name={SourceName} source_properties=device.description='\"Moon-TTS\\ Virtual\\ Microphone\"'");
            return remapRes.IsSuccess
                ? Result.Success()
                : Result.Failure($"Failed to remap virtual microphone source: {remapRes.ErrorMessage}");
        }
        
        /* If PulseAudio doesn't work, tries with wpctl */
        var pwRes = runner.RunCommand("pw-cli",
            $"create-node adapter '{{ factory.name=support.null-audio-sink node.name=\"{SinkName}\" node.description='\"Moon-TTS\\ Virtual\\ Sink\"' media.class=\"Audio/Sink\" object.linger=true }}'");
        return pwRes.IsSuccess
            ? Result.Success()
            : Result.Failure($"Failed to create virtual sink via pactl and pw-cli: {pwRes.ErrorMessage}");
    }
}
