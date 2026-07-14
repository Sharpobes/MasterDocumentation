using System.Globalization;
using System.Windows.Data;
namespace MasterDocumentation.Utilities;
public sealed class NodeIconConverter : IValueConverter { public object Convert(object value, Type t, object p, CultureInfo c) => value is true ? "📁" : "📄"; public object ConvertBack(object value, Type t, object p, CultureInfo c) => throw new NotSupportedException(); }
