using System.ComponentModel;
using System.Runtime.CompilerServices;
using Melogold.App.Services;
using Melogold.App.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace Melogold.App.Controls;

/// <summary>Строка трека или коллекции (§5.3), около 56 px.</summary>
public sealed partial class MusicRow : UserControl, INotifyPropertyChanged
{
    public static readonly DependencyProperty ItemProperty = DependencyProperty.Register(
        nameof(Item), typeof(RowVm), typeof(MusicRow), new PropertyMetadata(null, (d, e) => ((MusicRow)d).OnItemChanged()));

    private bool _hovered;

    public MusicRow()
    {
        InitializeComponent();
    }

    public RowVm Item
    {
        get => (RowVm)GetValue(ItemProperty);
        set => SetValue(ItemProperty, value);
    }

    /// <summary>Указатель над строкой: видны ♡, «…» и play на обложке.</summary>
    public bool Hovered
    {
        get => _hovered;
        private set
        {
            if (_hovered == value) return;
            _hovered = value;
            OnPropertyChanged();
            PlayOverlay.Opacity = value ? 1 : 0;
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    private void OnItemChanged()
    {
        Hovered = false;
        if (Item is null) return;
        AutomationProperties.SetName(this, Item.AccessibleName);
        Bindings.Update();
    }

    private void OnPointerEntered(object sender, PointerRoutedEventArgs e) => Hovered = true;

    private void OnPointerExited(object sender, PointerRoutedEventArgs e) => Hovered = false;

    public static CornerRadius Corner(bool round) => round ? new CornerRadius(20) : new CornerRadius(4);

    public static string HeartGlyph(bool liked) => liked ? "" : "";

    public static double LikeOpacity(bool liked, bool hovered) => liked || hovered ? 1 : 0;

    public static double HoverOpacity(bool hovered) => hovered ? 1 : 0;

    public static Brush LikeBrush(bool liked) => (Brush)Application.Current.Resources[liked ? "AccentTextFillColorPrimaryBrush" : "TextFillColorSecondaryBrush"];

    public static Brush TitleBrush(bool current, bool unavailable) => (Brush)Application.Current.Resources[
        unavailable ? "TextFillColorDisabledBrush" : current ? "AccentTextFillColorPrimaryBrush" : "TextFillColorPrimaryBrush"];

    public static string ExplicitText() => Loc.Get("Explicit");

    private void OnPlayClick(object sender, RoutedEventArgs e) => MusicListView.Activate(Item);

    private void OnLikeClick(object sender, RoutedEventArgs e)
    {
        if (Item.Track is not { } track) return;
        App.Services.GetRequiredService<TrackActions>().ToggleLike(track);
    }

    private void OnMenuClick(object sender, RoutedEventArgs e) => MusicListView.ShowMenu(Item, MenuButton, null);
}
