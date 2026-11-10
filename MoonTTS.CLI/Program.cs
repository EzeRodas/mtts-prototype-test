using System.CommandLine;
using Microsoft.Extensions.DependencyInjection;
using MoonTTS.CLI.commands;
using MoonTTS.CLI.commands.Daemon;
using MoonTTS.CLI.commands.Output;
using MoonTTS.CLI.commands.Output.Main;
using MoonTTS.CLI.commands.Output.Monitor;
using MoonTTS.Core.Services;
using MoonTTS.Domain.Interfaces;
using MoonTTS.Infrastructure.Audio;
using MoonTTS.Infrastructure.Daemon;
using MoonTTS.Infrastructure.Persistence;
using MoonTTS.Infrastructure.Tts;

namespace MoonTTS.CLI;

/* File Structure:
        - Main(string[] args) : Task<int>
        - ConfigureServices(IServiceCollection services) : void
        - BuildRootCommand(IServiceProvider sp) : RootCommand 
*/

/* CLI application entry point. Configures DI container and runs System.CommandLine parser */

class Program
{
    static async Task<int> Main(string[] args)
    {
        var services = new ServiceCollection();
        ConfigureServices(services);
        using var serviceProvider = services.BuildServiceProvider();

        /* Ensures base configuration directory is initialized */
        var configRepo = serviceProvider.GetRequiredService<IConfigurationRepository>();
        configRepo.LoadAll();

        var rootCommand = BuildRootCommand(serviceProvider);
        var parseResult = rootCommand.Parse(args);
        return await parseResult.InvokeAsync();
    }
    
    private static void ConfigureServices(IServiceCollection services)
    {
        services.AddSingleton<IAudioPlayer>(_ => AudioPlayerManager.Create());
        services.AddSingleton<IConfigurationRepository, JsonConfigurationRepository>();
        services.AddSingleton<SettingsManager>();
        services.AddSingleton<IModelRegistry>(_ => new EngineCatalog());
        services.AddSingleton<EngineVoiceStore>();
        services.AddSingleton<IModelDownloader, HttpModelDownloader>();
        services.AddSingleton<DownloadManager>();
        services.AddSingleton<HistoryManager>();
        services.AddSingleton<DictionaryManager>();
        services.AddSingleton<HttpClient>();
        services.AddSingleton<DaemonClient>();
        services.AddSingleton<DaemonServer>();
        services.AddSingleton<ITtsEngineFactory, TtsEngineFactory>();
        services.AddSingleton<ITtsEngineProvider, TtsEngineProvider>();
        services.AddSingleton<ISpeechPipeline, SpeechPipeline>();
    }

    /* Constructs the complete hierarchical command tree for Moon-TTS */
    private static RootCommand BuildRootCommand(IServiceProvider sp)
    {
        var root = new RootCommand("Moon-TTS: High-performance local speech synthesis CLI.");

        /* Playback commands */
        root.Add(PlayCommand.Build(sp));
        root.Add(PauseCommand.Build(sp));
        root.Add(StopCommand.Build(sp));

        /* Model branch */
        var modelBranch = new Command("model", "TTS model management.");
        modelBranch.Add(ListModelsCommand.Build(sp));
        modelBranch.Add(SetModelCommand.Build(sp));
        modelBranch.Add(DownloadModelCommand.Build(sp));
        modelBranch.Add(DeleteModelCommand.Build(sp));
        root.Add(modelBranch);
        
        /* Voice branch */
        var voiceBranch = new Command("voice", "Voice selection");
        voiceBranch.Add(ListVoicesCommand.Build(sp));
        voiceBranch.Add(SetVoiceCommand.Build(sp));
        root.Add(voiceBranch);
        
        /* Output branch */
        var outputBranch = new Command("output", "Management of audio output devices.");
        outputBranch.Add(ListOutputsCommand.Build(sp));

        var mainOutputBranch = new Command("main", "Primary audio output configuration.");
        mainOutputBranch.Add(SetMainOutputCommand.Build(sp));
        mainOutputBranch.Add(EnableMainOutputCommand.Build(sp));
        mainOutputBranch.Add(DisableMainOutputCommand.Build(sp));
        mainOutputBranch.Add(SetMainVolumeCommand.Build(sp));
        outputBranch.Add(mainOutputBranch);

        var monitorOutputBranch = new Command("monitor", "Monitor audio output configuration.");
        monitorOutputBranch.Add(SetMonitorOutputCommand.Build(sp));
        monitorOutputBranch.Add(EnableMonitorOutputCommand.Build(sp));
        monitorOutputBranch.Add(DisableMonitorOutputCommand.Build(sp));
        monitorOutputBranch.Add(SetMonitorVolumeCommand.Build(sp));
        outputBranch.Add(monitorOutputBranch);

        root.Add(outputBranch);
        
        /* Mic branch (ONLY LINUX) */
        if (OperatingSystem.IsLinux())
        {
            var micBranch = new Command("mic", "Create or check status of the virtual microphone. (ONLY LINUX)");
            micBranch.Add(VirtualMicCreateCommand.Build(sp));
            micBranch.Add(VirtualMicStatusCommand.Build(sp));

            root.Add(micBranch);
        }
        
        /* Daemon branch */
        var daemonBranch = new Command("daemon", "Manages the background TTS model daemon.");
        daemonBranch.Add(DaemonStartCommand.Build(sp));
        daemonBranch.Add(DaemonKillCommand.Build(sp));
        daemonBranch.Add(DaemonStatusCommand.Build(sp));
        daemonBranch.Add(DaemonRunCommand.Build(sp));
        root.Add(daemonBranch);

        return root;
    }
}