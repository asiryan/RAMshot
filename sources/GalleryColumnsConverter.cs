using System.Globalization;
using System.Windows.Data;

namespace RAMshot;

// Turns the gallery width into a column count so the fluid image grid always fills the panel.
internal sealed class GalleryColumnsConverter : IValueConverter
{
    private const double TargetCardWidth = 160;

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        double width = value is double d && !double.IsNaN(d) && !double.IsInfinity(d) ? d : 0;
        return Math.Clamp((int)(width / TargetCardWidth), 1, 16);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}
