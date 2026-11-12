using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Microsoft.Extensions.DependencyInjection;
using MoonTTS.Avalonia.Services;
using MoonTTS.Avalonia.ViewModels;
using MoonTTS.Avalonia.Views;
using MoonTTS.Core.Services;
using MoonTTS.Domain.Interfaces;
using MoonTTS.Domain.ValueObjects;
using MoonTTS.Infrastructure.Audio;
using MoonTTS.Infrastructure.Audio.Linux;
using MoonTTS.Infrastructure.OS;
using MoonTTS.Infrastructure.Persistence;
using MoonTTS.Infrastructure.Tts;
using UIDispatcher = Avalonia.Threading.Dispatcher;

namespace MoonTTS.Avalonia;

public partial class App : Application
{
    private static IServiceProvider Services { get; set; } = null!;

    /* Loads the application XAML markup */
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    /* Sets up dependency injection, builds the service provider, and configures the desktop lifetime */
    public override void OnFrameworkInitializationCompleted()
    {
        var collection = new ServiceCollection();
        ConfigureServices(collection);
        Services = collection.BuildServiceProvider();
        
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            /* Prevents the app from exiting when the window is hidden */
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;

            /* Disposes singletons on exit so engine workers are shut down cleanly */
            desktop.Exit += (_, _) => (Services as IDisposable)?.Dispose();

            
            /* Creates thw window and service */
            var mainWindow = new MainWindow();
            var settingsManager = Services.GetRequiredService<SettingsManager>();
            var audioPlayer = Services.GetRequiredService<IAudioPlayer>();
            var downloadManager = Services.GetRequiredService<DownloadManager>();
            var dictionaryManager = Services.GetRequiredService<DictionaryManager>();
            var hotkeyManager = Services.GetRequiredService<HotkeyManager>();
            var historyManager = Services.GetRequiredService<HistoryManager>();
            var profileManager = Services.GetRequiredService<ProfileManager>();
            var engineProvider = Services.GetRequiredService<ITtsEngineProvider>();
            var overlayHelper = Services.GetRequiredService<IWindowOverlayHelper>();

            var windowService = new WindowService(
                mainWindow,
                desktop,
                settingsManager,
                audioPlayer,
                downloadManager,
                dictionaryManager,
                hotkeyManager,
                historyManager,
                engineProvider,
                overlayHelper,
                profileManager);
            var speechPipeline = Services.GetRequiredService<ISpeechPipeline>();
            var shortcutManager = Services.GetRequiredService<GlobalShortcutManager>();

            var mainViewModel = new MainViewModel(speechPipeline, windowService, hotkeyManager, historyManager);

            /* Sets the datacontext on the window and application */
            mainWindow.DataContext = mainViewModel;
            DataContext = mainViewModel;

            if (!settingsManager.CurrentAppConfig.OpenOnStartup)
            {
                desktop.MainWindow = mainWindow;
            }
            
            /* Listens for secondary launch instances and toggles window visibility */
            SingleInstanceService.StartServer(() =>
            {
                UIDispatcher.UIThread.Post(windowService.ShowMainWindow);
            }, CancellationToken.None);
            
            /* Creates a virtual microphone for linux on startup */
            var config = settingsManager.CurrentAppConfig;
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux) && config.CreateMicOnStartup)
            {
                LinuxVirtualMicService.EnsureCreated();
            }

            /* Initializes global OS shortcut hook asynchronously without blocking appstartup */
            _ = InitializeGlobalShortcutsAsync(shortcutManager, hotkeyManager, windowService);
        }
        base.OnFrameworkInitializationCompleted();
        
        GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
    }

    /* Initializes the global shortcut manager, registers the toggle hotkey, and wireschange notifications */
    private static async Task InitializeGlobalShortcutsAsync(
        GlobalShortcutManager shortcutManager,
        HotkeyManager hotkeyManager,
        IWindowService windowService)
    {
        try
        {
            await shortcutManager.InitializeAsync();

            /* Toggles window visibility on UI thread when OS hotkey fires */
            shortcutManager.OnShortcutTriggered += (id) =>
            {
                if (id == "toggle_app")
                {
                    UIDispatcher.UIThread.Post(windowService.ShowMainWindow);
                }
                const string phrasePrefix = "phrase_";
                if (id.StartsWith(phrasePrefix, StringComparison.Ordinal)
                    && Guid.TryParse(id[phrasePrefix.Length..], out Guid entryId))
                {
                    UIDispatcher.UIThread.Post(() => _ = PlayPhraseShortcutAsync(hotkeyManager, entryId));
                }
            };

            hotkeyManager.OnToggleHotkeyChanged += newkey =>
            {
                _ = RefreshGlobalShortcutsAsync(shortcutManager, hotkeyManager);
            };
            hotkeyManager.OnHotkeysChanged += () =>
            {
                _ = RefreshGlobalShortcutsAsync(shortcutManager, hotkeyManager);
            };

            await RefreshGlobalShortcutsAsync(shortcutManager, hotkeyManager);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"/* Failed to initialize global hotkey hook: {ex.Message} */");
        }
    }
    
    private static async Task RefreshGlobalShortcutsAsync(
        GlobalShortcutManager shortcutManager,
        HotkeyManager hotkeyManager)
    {
        try
        {
            ShortcutCombo? toggleShortcut =
                ShortcutCombo.TryParse(hotkeyManager.GetToggleHotkey(), out var parsedToggle)
                    ? parsedToggle
                    : null;

            var phraseShortcuts = new Dictionary<string, ShortcutCombo>();
            foreach (var entry in hotkeyManager.GetHotkeys())
            {
                if (ShortcutCombo.TryParse(entry.Hotkey, out var phraseCombo) && phraseCombo != null)
                {
                    phraseShortcuts[entry.Id.ToString("D")] = phraseCombo;
                }
            }

            await shortcutManager.UpdateShortcutAsync(toggleShortcut, phraseShortcuts);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to update global shortcuts: {ex.Message}");
        }
    }

    /* Plays a registered phrase after the platform hook reports its entry ID */
    private static async Task PlayPhraseShortcutAsync(HotkeyManager hotkeyManager, Guid entryId)
    {
        try
        {
            var result = await hotkeyManager.PlayHotkeyAsync(entryId);
            if (result.IsFailure)
            {
                Console.WriteLine($"Phrase shortcut failed: {result.ErrorMessage}");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Phrase shortcut failed: {ex.Message}");
        }
    }

    /* Registers application services, repositories, players, and engines in the DI container */
    private static void ConfigureServices(IServiceCollection services)
    {
        services.AddSingleton<IConfigurationRepository, JsonConfigurationRepository>();
        services.AddSingleton<SettingsManager>();
        services.AddSingleton<DictionaryManager>();
        services.AddSingleton<HistoryManager>();
        services.AddSingleton<ProfileManager>();
        services.AddSingleton<HttpClient>();
        services.AddSingleton<GlobalShortcutManager>();
        services.AddSingleton<IWindowOverlayHelper>(_ => WindowOverlayManager.Create());
        services.AddSingleton<IModelRegistry>(_ => new EngineCatalog());
        services.AddSingleton<EngineVoiceStore>();
        services.AddSingleton<IModelDownloader, HttpModelDownloader>();
        services.AddSingleton<DownloadManager>();
        services.AddSingleton<IAudioPlayer>(_ => AudioPlayerManager.Create());
        services.AddSingleton<ITtsEngineFactory, TtsEngineFactory>();
        services.AddSingleton<ITtsEngineProvider, TtsEngineProvider>();
        services.AddSingleton<ISpeechPipeline, SpeechPipeline>();
        services.AddSingleton<HotkeyManager>();
    }
}
