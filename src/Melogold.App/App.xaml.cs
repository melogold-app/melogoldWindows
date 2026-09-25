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
        InitializeComponent();
    }

    public static new App? Current => Application.Current as App;

    public MainWindow? Window => _window;

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _window = new MainWindow();
        _window.Activate();
    }

    /// <summary>Второй запуск передал активацию сюда: показать окно (и открыть ссылку, если она была).</summary>
    public void OnRedirectedActivation(AppActivationArguments args)
    {
        _window?.DispatcherQueue.TryEnqueue(() => _window.BringToFront());
    }
}
