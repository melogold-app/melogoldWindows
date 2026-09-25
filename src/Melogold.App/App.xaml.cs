using Melogold.App.Services;
using Melogold.App.ViewModels;
using Melogold.App.Views;
using Melogold.Core.Data;
using Melogold.InnerTube;
using Melogold.InnerTube.Lyrics;
using Melogold.Playback;
using Melogold.Server;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;

namespace Melogold.App;

public partial class App : Application
{
    private readonly string[] _args;
    private MainWindow? _window;

    public App(string[] args)
    {
        _args = args;
        AppPaths.Ensure();
        Log.Start();
        UnhandledException += (_, e) =>
        {
            Log.Crash(e.Exception, "UI");
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            if (e.ExceptionObject is Exception error) Log.Crash(error, "AppDomain");
        };
        if (Environment.GetEnvironmentVariable("MELOGOLD_TRACE") == "1")
        {
            // Отладка: каждое исключение первого шанса — в журнал
            AppDomain.CurrentDomain.FirstChanceException += (_, e) =>
                Log.Warn($"First chance {e.Exception.GetType().Name}: {e.Exception.Message}\n{Environment.StackTrace}");
        }
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Log.Warn("Unobserved task", e.Exception);
            e.SetObserved();
        };
        Services = ConfigureServices();
        InitializeComponent();
    }

    /// <summary>Контейнер сервисов: страницы и модели берут зависимости отсюда (docs/PROMPT.md §3).</summary>
    public static IServiceProvider Services { get; private set; } = null!;

    public static new App? Current => Application.Current as App;

    public MainWindow? Window => _window;

    private static ServiceProvider ConfigureServices()
    {
        var services = new ServiceCollection();
        services.AddSingleton(new SettingsStore(AppPaths.Settings));
        services.AddSingleton<Navigator>();
        services.AddSingleton<LinkRouter>();
        services.AddSingleton(_ => new LibraryDatabase(AppPaths.Database));
        services.AddSingleton<Library>();
        services.AddSingleton(_ =>
        {
            var client = new InnerTubeClient();
            Log.Info($"InnerTube: hl={client.Language} gl={client.Region}");
            return client;
        });
        services.AddSingleton<YouTubeMusic>();
        services.AddSingleton<StreamResolver>();
        services.AddSingleton(sp => new StreamClients(sp.GetRequiredService<StreamResolver>(), Path.Combine(AppPaths.DataDirectory, "stream-clients.json"),
            (message, error) => Log.Warn(message, error)));
        services.AddSingleton<CatalogCache>();
        services.AddSingleton(sp => new AccountService(new DpapiSessionStore(AppPaths.Account), new WindowsDeviceIdentity(),
            sp.GetRequiredService<SettingsStore>().ServerUrl));
        services.AddSingleton(sp => new SyncStore(sp.GetRequiredService<Library>()));
        services.AddSingleton(sp => new LibrarySync(sp.GetRequiredService<AccountService>(), sp.GetRequiredService<SyncStore>(),
            (message, error) => Log.Warn(message, error)));
        services.AddSingleton<ForYouBuilder>();
        // Всё, что ниже, создаётся в потоке интерфейса: плеер запоминает его контекст, плашка — его очередь
        services.AddSingleton<Snackbar>();
        services.AddSingleton(sp => new PlayerEngine(sp.GetRequiredService<StreamResolver>(), sp.GetRequiredService<YouTubeMusic>(),
            sp.GetRequiredService<Library>(), sp.GetRequiredService<SettingsStore>()));
        services.AddSingleton<PlayerViewModel>();
        services.AddSingleton<TrackActions>();
        services.AddSingleton<CollectionMenu>();
        services.AddSingleton(sp => new LyricsFetcher(sp.GetRequiredService<YouTubeMusic>(), new LrcLib(LrcLib.CreateClient(AppInfo.ToolUserAgent)), new KuGou(KuGou.CreateClient()))
        {
            // Свой или общий текст с сервера Melogold, когда провайдеры не нашли синхронный (docs/LYRICS-SYNC.md §3.5)
            Community = sp.GetRequiredService<LibrarySync>().LookupLyricsAsync,
        });
        services.AddSingleton<LyricsService>();
        services.AddSingleton<AudioLevels>();
        services.AddSingleton<UpdateService>();
        return services.BuildServiceProvider();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        ProtocolRegistration.Ensure();
        Services.GetRequiredService<StreamClients>().Start(AppInfo.ToolUserAgent);
        // Прогрев: visitorData и соединение с YouTube — до первого нажатия, а не после
        _ = Task.Run(async () =>
        {
            try
            {
                await Services.GetRequiredService<InnerTubeClient>().EnsureVisitorDataAsync();
            }
            catch (Exception e)
            {
                Log.Warn("Warm-up failed", e);
            }
        });
        _window = new MainWindow();
        _window.Activate();
        _window.Closed += (_, _) =>
        {
            Services.GetRequiredService<PlayerEngine>().Dispose();
            Services.GetRequiredService<AudioLevels>().Dispose();
            // Правки последних двух секунд — на сервер до выхода
            Services.GetRequiredService<LibrarySync>().Flush(TimeSpan.FromSeconds(3));
        };
        Services.GetRequiredService<LibrarySync>().Start();
        // Обновления: при старте не чаще раза в 6 часов, чуть позже — окно и воспроизведение важнее
        var updates = Services.GetRequiredService<UpdateService>();
        _ = Task.Delay(TimeSpan.FromSeconds(5)).ContinueWith(_ => updates.CheckAsync(force: false), TaskScheduler.FromCurrentSynchronizationContext());
        if (_args.Length > 0) Services.GetRequiredService<LinkRouter>().OpenArguments(_args);
    }

    /// <summary>Второй запуск передал активацию сюда: поднять окно и открыть ссылку, если она была.</summary>
    public void OnRedirectedActivation(AppActivationArguments args)
    {
        _window?.DispatcherQueue.TryEnqueue(() =>
        {
            _window.BringToFront();
            string? text = args.Data switch
            {
                Windows.ApplicationModel.Activation.ILaunchActivatedEventArgs launch => launch.Arguments,
                Windows.ApplicationModel.Activation.IProtocolActivatedEventArgs protocol => protocol.Uri.OriginalString,
                _ => null,
            };
            if (!string.IsNullOrWhiteSpace(text)) Services.GetRequiredService<LinkRouter>().OpenArguments(SplitArguments(text));
        });
    }

    /// <summary>Строка аргументов второго экземпляра без пути к exe.</summary>
    private static string[] SplitArguments(string commandLine)
    {
        var parts = new List<string>();
        var current = new System.Text.StringBuilder();
        var quoted = false;
        foreach (var ch in commandLine)
        {
            if (ch == '"') quoted = !quoted;
            else if (char.IsWhiteSpace(ch) && !quoted)
            {
                if (current.Length > 0) parts.Add(current.ToString());
                current.Clear();
            }
            else current.Append(ch);
        }
        if (current.Length > 0) parts.Add(current.ToString());
        if (parts.Count > 0 && parts[0].EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) parts.RemoveAt(0);
        return [.. parts];
    }
}
