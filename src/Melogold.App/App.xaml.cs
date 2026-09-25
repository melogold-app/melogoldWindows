using Melogold.App.Services;
using Melogold.Core.Data;
using Melogold.InnerTube;
using Melogold.Playback;
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
        return services.BuildServiceProvider();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        ProtocolRegistration.Ensure();
        _window = new MainWindow();
        _window.Activate();
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
