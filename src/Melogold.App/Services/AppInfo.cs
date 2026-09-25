using System.Reflection;
using System.Runtime.InteropServices;

namespace Melogold.App.Services;

/// <summary>Версия и архитектура сборки: версия задана в Directory.Build.props, без суффиксов.</summary>
public static class AppInfo
{
    public static string Version { get; } =
        typeof(AppInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "0.0.0";

    /// <summary><c>x64</c> или <c>arm64</c> — ключ файла в <c>update.json</c>.</summary>
    public static string Architecture => RuntimeInformation.ProcessArchitecture == System.Runtime.InteropServices.Architecture.Arm64 ? "arm64" : "x64";

    public const string RepositoryUrl = "https://github.com/melogold-app/melogoldWindows";

    /// <summary>User-Agent для lrclib, kugou и GitHub (docs/PROMPT.md §3).</summary>
    public static string ToolUserAgent => $"Melogold/{Version} (+{RepositoryUrl})";

    public static string ExecutablePath => Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "Melogold.exe");
}
