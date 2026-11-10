using MoonTTS.Domain.Common;

namespace MoonTTS.Infrastructure.Audio.Linux;

/* Manages a virtual mic on Linux for outputting the audio */
/* This is only on Linux as Windows requires a digitally signed certification to allow you to make the custom
   C++ kernel drivers needed to be able to create one. You need to pay for it. Similar situation on Mac */

public class LinuxVirtualMicService
{
    public const string SinkName = "Moon-TTS_Sink";
    public const string SourceName = "Moon-TTS_Mic";
    public const string VirtualDeviceId = "mtts_mic";

    /* Checks if the virtual mic is already created */
    public static bool Exists()
    {
        /* Checks using PulseAudio */
        var pactlRes = LinuxProcessRunner.RunCommand("pactl", "list sinks short");
        if (pactlRes.IsSuccess && pactlRes.Value.Contains(SinkName, StringComparison.OrdinalIgnoreCase))
            return true;
        
        /* If that didn't work, checks with WirePlumber */
        var wpctlRes = LinuxProcessRunner.RunCommand("wpctl", "status");
        if (wpctlRes.IsSuccess && wpctlRes.Value.Contains(SinkName, StringComparison.OrdinalIgnoreCase))
            return true;
        
        return false;
    }

    public static Result EnsureCreated()
    {
        if (Exists()) return Result.Success();
        
        /* Creates the null sink */
        var nullSinkRes = LinuxProcessRunner.RunCommand("pactl", $"load-module module-null-sink sink_name={SinkName} sink_properties=device.description='\"Moon-TTS\\ Virtual\\ Sink\"'");
        if (nullSinkRes.IsSuccess)
        {
            /* Remaps the sink monitor to appear as an input */
            var remapRes = LinuxProcessRunner.RunCommand("pactl",
                $"load-module module-remap-source master={SinkName}.monitor source_name={SourceName} source_properties=device.description='\"Moon-TTS\\ Virtual\\ Microphone\"'");
            return remapRes.IsSuccess
                ? Result.Success()
                : Result.Failure($"Failed to remap virtual microphone source: {remapRes.ErrorMessage}");
        }
        
        /* If PulseAudio doesn't work, tries with wpctl */
        var pwRes =LinuxProcessRunner.RunCommand("pw-cli",
            $"create-node adapter '{{ factory.name=support.null-audio-sink node.name=\"{SinkName}\" node.description='\"Moon-TTS\\ Virtual\\ Sink\"' media.class=\"Audio/Sink\" object.linger=true }}'");
        return pwRes.IsSuccess
            ? Result.Success()
            : Result.Failure($"Failed to create virtual sink via pactl and pw-cli: {pwRes.ErrorMessage}");
    }
}