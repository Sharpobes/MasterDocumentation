using System.Globalization;
using System.Windows.Data;
using MasterDocumentation.UI;
namespace MasterDocumentation.Utilities;
public sealed class NodeIconConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is true ? AppIconKind.Folder : AppIconKind.Document;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
