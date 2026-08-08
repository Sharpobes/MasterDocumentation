using System.Globalization;
using System.Windows.Data;

namespace MasterDocumentation.Utilities;

/// <summary>
/// Ширина карточки вкладки документа: доступная ширина полосы делится на количество открытых
/// вкладок. Раньше вкладки имели фиксированную ширину, и начиная с четвёртой последняя просто
/// обрезалась краем полосы; теперь они сжимаются, как в браузере, до разумного минимума —
/// дальше остаток доступен через меню открытых вкладок.
/// </summary>
public sealed class TabWidthConverter : IMultiValueConverter
{
    private const double Minimum = 96;
    private const double Maximum = 235;
    /// <summary>Внешние отступы карточки (Margin="3,0") — их надо вычесть, иначе строка переполнится.</summary>
    private const double Spacing = 6;

    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values.Length < 2 || values[0] is not double available || values[1] is not int count || count <= 0 || double.IsNaN(available) || available <= 0)
            return Maximum;
        return Math.Clamp(available / count - Spacing, Minimum, Maximum);
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) => throw new NotSupportedException();
}
