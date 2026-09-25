using Melogold.App.Views;
using Melogold.Core.Domain;

namespace Melogold.App.Services;

/// <summary>
/// Разбор входов (docs/PROMPT.md §3, REWRITE §2.3 Android): аргументы запуска и второго экземпляра, ссылки
/// <c>melogold://</c> (API §7.2), ссылки YouTube и текст из поля поиска. Автоматического входа или одобрения не бывает.
/// </summary>
public sealed class LinkRouter(Navigator navigator)
{
    /// <summary>Аргументы командной строки: первая строка, похожая на ссылку, или весь текст.</summary>
    public void OpenArguments(IReadOnlyList<string> args)
    {
        var text = args.FirstOrDefault(a => a.Contains("://", StringComparison.Ordinal)) ?? string.Join(' ', args);
        if (!string.IsNullOrWhiteSpace(text)) OpenText(text);
    }

    public void OpenText(string text)
    {
        text = text.Trim();
        Log.Info($"Open: {(text.StartsWith("melogold:", StringComparison.OrdinalIgnoreCase) ? "melogold link" : "text")}");
        if (text.StartsWith("melogold://", StringComparison.OrdinalIgnoreCase))
        {
            OpenMelogold(text);
            return;
        }

        switch (YouTubeLinkParser.Parse(text))
        {
            case LinkTarget.Search search:
                navigator.Open(typeof(SearchPage), search.Query);
                break;
            case LinkTarget.Unsupported { Reason: LinkTarget.ReasonEmpty }:
                break;
            default:
                // Ссылки YouTube получат свои экраны в срезах 2–3; до тех пор — выдача по тексту
                navigator.Open(typeof(SearchPage), text);
                break;
        }
    }

    private void OpenMelogold(string link)
    {
        // melogold://server?… → «Сервер» с заполненным адресом; melogold://link?… — после экранов аккаунта (срез 5)
        navigator.Show("settings");
    }
}
