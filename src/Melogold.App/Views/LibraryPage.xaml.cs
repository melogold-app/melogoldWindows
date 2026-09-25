using Melogold.App.Services;
using Microsoft.UI.Xaml.Controls;

namespace Melogold.App.Views;

public sealed partial class LibraryPage : Page, IScrollToTop
{
    public LibraryPage()
    {
        InitializeComponent();
    }

    public void ScrollToTop() => Scroller.ChangeView(null, 0, null);
}
