using Melogold.App.Services;
using Melogold.Core.Data;
using Melogold.Core.Domain;
using Melogold.Server;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace Melogold.App.Controls;

/// <summary>
/// Чьи прослушивания показать (tasks/0002 §3.5) — История и «Итоги» (tasks/0015): «Все устройства · Это устройство ·
/// имя…»; устройство, которого уже нет в аккаунте, — «Другое устройство». Виден с аккаунтом, когда есть прослушивания
/// других устройств.
/// </summary>
public sealed partial class HistoryDeviceFilter : ComboBox
{
    private readonly Library _library = App.Services.GetRequiredService<Library>();
    private readonly AccountService _account = App.Services.GetRequiredService<AccountService>();
    private Dictionary<string, DeviceDto>? _names;

    public HistoryDeviceFilter()
    {
        MinWidth = 200;
        VerticalAlignment = VerticalAlignment.Top;
        Visibility = Visibility.Collapsed;
        AutomationProperties.SetName(this, Loc.Get("HistoryDeviceChoose"));
        ToolTipService.SetToolTip(this, Loc.Get("HistoryDeviceChoose"));
        SelectionChanged += (_, _) =>
        {
            if (SelectedItem is not ComboBoxItem { Tag: HistoryDevice filter } || filter == Filter) return;
            Filter = filter;
            FilterChanged?.Invoke();
        };
    }

    public HistoryDevice Filter { get; private set; } = HistoryDevice.All;

    public event Action? FilterChanged;

    private string? CurrentDeviceId => _account.State is AccountState.SignedIn signedIn ? signedIn.DeviceId : null;

    /// <summary>Перечитать устройства с прослушиваниями; без аккаунта или без чужих прослушиваний фильтра нет.</summary>
    public async Task RefreshAsync()
    {
        var current = CurrentDeviceId;
        var others = current is null ? [] : (await Task.Run(_library.HistoryDeviceIds)).Where(id => id != current).ToList();
        if (others.Count == 0)
        {
            Visibility = Visibility.Collapsed;
            Filter = HistoryDevice.All;
            return;
        }
        if (_names is null)
        {
            try
            {
                _names = (await _account.DevicesAsync()).Devices.ToDictionary(d => d.Id);
            }
            catch (Exception e) when (e is ApiException or HttpRequestException or TaskCanceledException)
            {
                Log.Warn("Device names unavailable", e);
            }
        }
        var options = new List<(string Text, string? Glyph, HistoryDevice Filter)>
        {
            (Loc.Get("HistoryDeviceAll"), null, HistoryDevice.All),
            (Loc.Get("HistoryDeviceThis"), DeviceSymbols.Glyph("windows"), HistoryDevice.This(current)),
        };
        options.AddRange(others
            .Select(id => _names?.GetValueOrDefault(id) is { } d
                ? (d.Name, DeviceSymbols.Glyph(d.Platform), HistoryDevice.Other(id))
                : (Loc.Get("HistoryDeviceOther"), (string?)DeviceSymbols.Glyph(null), HistoryDevice.Other(id)))
            .OrderBy(o => o.Item1, StringComparer.CurrentCulture));
        var selected = Filter;
        Items.Clear();
        foreach (var (text, glyph, filter) in options)
        {
            var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            if (glyph is not null) content.Children.Add(new FontIcon { Glyph = glyph, FontSize = 14 });
            content.Children.Add(new TextBlock { Text = text });
            var item = new ComboBoxItem { Content = content, Tag = filter };
            AutomationProperties.SetName(item, text);
            Items.Add(item);
        }
        SelectedIndex = Math.Max(0, options.FindIndex(o => o.Filter == selected));
        Filter = options[SelectedIndex].Filter;
        Visibility = Visibility.Visible;
    }
}
