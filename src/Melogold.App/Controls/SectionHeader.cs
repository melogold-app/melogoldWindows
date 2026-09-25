using Melogold.App.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;

namespace Melogold.App.Controls;

/// <summary>Заголовок секции внутри списка: название и «Ещё ›».</summary>
public sealed record SectionVm(string Title, Action? More);

public sealed partial class SectionHeader : Grid
{
    public static readonly DependencyProperty SectionProperty = DependencyProperty.Register(
        nameof(Section), typeof(SectionVm), typeof(SectionHeader), new PropertyMetadata(null, (d, _) => ((SectionHeader)d).Build()));

    public SectionHeader()
    {
        InitializeComponent();
        Margin = new Thickness(0, 20, 0, 4);
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
    }

    public SectionVm? Section
    {
        get => (SectionVm?)GetValue(SectionProperty);
        set => SetValue(SectionProperty, value);
    }

    private void Build()
    {
        Children.Clear();
        if (Section is not { } section) return;
        var title = new TextBlock { Text = section.Title, Style = (Style)Application.Current.Resources["SubtitleTextBlockStyle"] };
        AutomationProperties.SetHeadingLevel(title, AutomationHeadingLevel.Level2);
        Children.Add(title);
        if (section.More is { } more)
        {
            var button = new HyperlinkButton { Content = Loc.Get("ResultsMore") + " ›", VerticalAlignment = VerticalAlignment.Center };
            button.Click += (_, _) => more();
            SetColumn(button, 1);
            Children.Add(button);
        }
    }
}
