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
using MoonTTS.Core.Utilities;
using MoonTTS.Domain.Interfaces;
using MoonTTS.Domain.ValueObjects;
using MoonTTS.Infrastructure.Audio;
using MoonTTS.Infrastructure.Audio.Linux;
using MoonTTS.Infrastructure.OS;
using MoonTTS.Infrastructure.Persistence;
using MoonTTS.Infrastructure.Tts;
using UIDispatcher = Avalonia.Threading.Dispatcher;

namespace MoonTTS.Avalonia;

/* File Structure:
        - Initialize() : void
        - OnFrameworkInitializationCompleted() : void
        - InitializeGlobalShortcutsAsync(GlobalShortcutManager, HotkeyManager, IWindowService) : Task
        - RefreshGlobalShortcutsAsync(GlobalShortcutManager, HotkeyManager) : Task
        - PlayPhraseShortcutAsync(HotkeyManager, Guid) : Task
        - ConfigureServices(IServiceCollection, IClassicDesktopStyleApplicationLifetime) : void
*/

public partial class App : Application
{
    private static IServiceProvider Services { get; set; } = null!;
    private CancellationTokenSource? _singleInstanceCts;

    /* Loads the application XAML markup */
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    /* Sets up dependency injection, crash logging, desktop lifetime, and background OS integrations */
    public override void OnFrameworkInitializationCompleted()
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            AppLogger.Error("AppDomain", "Unhandled exception", e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            AppLogger.Error("TaskScheduler", "Unobserved task exception", e.Exception);
            e.SetObserved();
        };

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var collection = new ServiceCollection();
            ConfigureServices(collection, desktop);
            Services = collection.BuildServiceProvider();

            /* Prevents the app from exiting when the window is hidden */
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;

            _singleInstanceCts = new CancellationTokenSource();

            /* Cancels background listeners and disposes singletons on exit so engine workers shut down cleanly */
            desktop.Exit += (_, _) =>
            {
                _singleInstanceCts?.Cancel();
                _singleInstanceCts?.Dispose();
                _singleInstanceCts = null;
                (Services as IDisposable)?.Dispose();
            };

            var settingsManager = Services.GetRequiredService<SettingsManager>();
            LocalizationService.Instance.SetLanguage(settingsManager.CurrentAppConfig.Language);

            var mainWindow = Services.GetRequiredService<MainWindow>();
            var windowService = Services.GetRequiredService<IWindowService>();
            var mainViewModel = Services.GetRequiredService<MainViewModel>();
            var hotkeyManager = Services.GetRequiredService<HotkeyManager>();
            var shortcutManager = Services.GetRequiredService<GlobalShortcutManager>();

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
            }, _singleInstanceCts.Token);

            /* Creates a virtual microphone for Linux in the background so UI startup never blocks on subprocesses */
            var config = settingsManager.CurrentAppConfig;
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux) && config.CreateMicOnStartup)
            {
                _ = Task.Run(() =>
                {
                    var micResult = LinuxVirtualMicService.EnsureCreated();
                    if (micResult.IsFailure)
                        AppLogger.Warning("App", $"Startup virtual mic creation failed: {micResult.ErrorMessage}");
                });
            }

            /* Initializes global OS shortcut hook asynchronously without blocking app startup */
            _ = InitializeGlobalShortcutsAsync(shortcutManager, hotkeyManager, windowService);
        }

        base.OnFrameworkInitializationCompleted();
    }

    /* Initializes the global shortcut manager, registers the toggle hotkey, and wires change notifications */
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

            hotkeyManager.OnToggleHotkeyChanged += newKey =>
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
            AppLogger.Error("App", "Failed to initialize global hotkey hook", ex);
        }
    }

    /* Synchronizes all active toggle and phrase shortcuts with the OS shortcut hook */
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
            AppLogger.Error("App", "Failed to update global shortcuts", ex);
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
                AppLogger.Warning("App", $"Phrase shortcut failed: {result.ErrorMessage}");
            }
        }
        catch (Exception ex)
        {
            AppLogger.Error("App", "Phrase shortcut failed", ex);
        }
    }

    /* Registers application services, repositories, players, engines, windows, and view models in DI */
    private static void ConfigureServices(
        IServiceCollection services,
        IClassicDesktopStyleApplicationLifetime desktop)
    {
        services.AddSingleton(desktop);
        services.AddSingleton<IConfigurationRepository, JsonConfigurationRepository>();
        services.AddSingleton<SettingsManager>();
        services.AddSingleton<DictionaryManager>();
        services.AddSingleton<HistoryManager>();
        services.AddSingleton<ProfileManager>();
        services.AddSingleton(_ => new HttpClient(new SocketsHttpHandler
        {
            ConnectTimeout = TimeSpan.FromSeconds(15),
            PooledConnectionLifetime = TimeSpan.FromMinutes(2)
        })
        {
            Timeout = Timeout.InfiniteTimeSpan
        });
        services.AddSingleton<GlobalShortcutManager>();
        services.AddSingleton<IWindowOverlayHelper>(_ => WindowOverlayManager.Create());
        services.AddSingleton<IModelRegistry>(_ => new EngineCatalog());
        services.AddSingleton<EngineVoiceStore>();
        services.AddSingleton<IModelDownloader, HttpModelDownloader>();
        services.AddSingleton<DownloadManager>();
        services.AddSingleton<IAudioPlayer>(_ => AudioPlayerManager.Create());
        services.AddSingleton<ITtsEngineFactory, TtsEngineFactory>();
        services.AddSingleton<ITtsEngineProvider, TtsEngineProvider>();
        services.AddSingleton<TtsEngineProvider>(sp => (TtsEngineProvider)sp.GetRequiredService<ITtsEngineProvider>());
        services.AddSingleton<ISpeechPipeline, SpeechPipeline>();
        services.AddSingleton<HotkeyManager>();

        /* Presentation services and view models */
        services.AddSingleton<MainWindow>();
        services.AddSingleton<IWindowService, WindowService>();
        services.AddSingleton<MainViewModel>();
        services.AddTransient<SettingsViewModel>();
        services.AddSingleton<Func<SettingsViewModel>>(sp => () => sp.GetRequiredService<SettingsViewModel>());
    }
}
