using Melogold.Core.Domain;
using Microsoft.UI.Xaml.Data;

namespace Melogold.App;

/// <summary>Секунды → «3:45» (подсказка ползунка перемотки).</summary>
public sealed partial class TimeConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) =>
        value is double seconds ? Durations.Format(TimeSpan.FromSeconds(seconds)) : "";

    public object ConvertBack(object value, Type targetType, object parameter, string language) => throw new NotSupportedException();
}
