using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace MasterDocumentation.Views;

public partial class ColorPickerDialog : Window
{
    private double _hue, _saturation, _value; private bool _updating;
    public Color? SelectedColor { get; private set; }
    public ColorPickerDialog(string title, Color initial, bool allowNoColor = true)
    {
        InitializeComponent(); Caption.Text = title; Title = title; if (!allowNoColor) NoColorButton.Visibility = Visibility.Collapsed;
        var hsv = ToHsv(initial); _hue = hsv.h; _saturation = hsv.s; _value = hsv.v;
        foreach (var hex in new[] { "#000000", "#444444", "#777777", "#B0B0B0", "#FFFFFF", "#E53935", "#FB8C00", "#FDD835", "#43A047", "#00ACC1", "#1E88E5", "#5E35B1", "#FCE4EC", "#FFF3E0", "#FFFDE7", "#E8F5E9", "#E0F7FA", "#E3F2FD", "#EDE7F6", "#F3E5F5", "#263238", "#6D4C41", "#C62828", "#1565C0" }) { var c = (Color)ColorConverter.ConvertFromString(hex); var b = new Button { Background = new SolidColorBrush(c), Height = 28, Margin = new Thickness(2), ToolTip = hex, BorderThickness = new Thickness(1) }; b.Click += (_, _) => SetColor(c); Swatches.Children.Add(b); }
        Loaded += (_, _) => Refresh();
    }
    private void Palette_Mouse(object sender, MouseEventArgs e) { if (e.LeftButton != MouseButtonState.Pressed) return; var p = e.GetPosition(Palette); _saturation = Math.Clamp(p.X / Palette.ActualWidth, 0, 1); _value = 1 - Math.Clamp(p.Y / Palette.ActualHeight, 0, 1); Refresh(); }
    private void Hue_Mouse(object sender, MouseEventArgs e) { if (e.LeftButton != MouseButtonState.Pressed) return; var bar=(FrameworkElement)sender; _hue = Math.Clamp(e.GetPosition(bar).X / bar.ActualWidth, 0, 1) * 360; Refresh(); }
    private void Refresh() { var color = FromHsv(_hue, _saturation, _value); _updating = true; SelectedColor = color; HueSurface.Fill = new SolidColorBrush(FromHsv(_hue, 1, 1)); Preview.Background = new SolidColorBrush(color); HexBox.Text = $"#{color.R:X2}{color.G:X2}{color.B:X2}"; RBox.Text = color.R.ToString(); GBox.Text = color.G.ToString(); BBox.Text = color.B.ToString(); Canvas.SetLeft(PaletteMarker, _saturation * 360 - 7); Canvas.SetTop(PaletteMarker, (1 - _value) * 240 - 7); HueMarker.Margin = new Thickness(_hue / 360 * 550 - 4, 0, 0, 0); _updating = false; }
    private void SetColor(Color color) { var hsv = ToHsv(color); _hue = hsv.h; _saturation = hsv.s; _value = hsv.v; Refresh(); }
    private void Hex_TextChanged(object sender, TextChangedEventArgs e) { if (_updating || HexBox.Text.Length is not (7 or 9)) return; try { SetColor((Color)ColorConverter.ConvertFromString(HexBox.Text)); } catch { } }
    private void Rgb_TextChanged(object sender, TextChangedEventArgs e) { if (_updating || !byte.TryParse(RBox.Text, out var r) || !byte.TryParse(GBox.Text, out var g) || !byte.TryParse(BBox.Text, out var b)) return; SetColor(Color.FromRgb(r, g, b)); }
    private void NoColor_Click(object sender, RoutedEventArgs e) { SelectedColor = null; DialogResult = true; }
    private void Ok_Click(object sender, RoutedEventArgs e) { DialogResult = true; }
    private static Color FromHsv(double h, double s, double v) { var c=v*s; var x=c*(1-Math.Abs(h/60%2-1)); var m=v-c; var (r,g,b)=h switch { <60=>(c,x,0d), <120=>(x,c,0d), <180=>(0d,c,x), <240=>(0d,x,c), <300=>(x,0d,c), _=>(c,0d,x) }; return Color.FromRgb((byte)((r+m)*255),(byte)((g+m)*255),(byte)((b+m)*255)); }
    private static (double h,double s,double v) ToHsv(Color c) { var r=c.R/255d; var g=c.G/255d; var b=c.B/255d; var max=Math.Max(r,Math.Max(g,b)); var min=Math.Min(r,Math.Min(g,b)); var d=max-min; var h=d==0?0:max==r?60*((g-b)/d%6):max==g?60*((b-r)/d+2):60*((r-g)/d+4); if(h<0)h+=360; return(h,max==0?0:d/max,max); }
}
