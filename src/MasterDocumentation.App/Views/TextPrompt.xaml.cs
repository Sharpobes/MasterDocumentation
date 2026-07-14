using System.Windows;
namespace MasterDocumentation.Views;
public partial class TextPrompt : Window
{
    public string Value => Input.Text.Trim();
    public TextPrompt(string title, string initial = "",bool multiline=false) { InitializeComponent(); Title = title; Input.Text = initial; Input.AcceptsReturn=multiline;Input.VerticalContentAlignment=multiline?VerticalAlignment.Top:VerticalAlignment.Center;if(multiline){Width=640;Height=430;ResizeMode=ResizeMode.CanResize;}Input.SelectAll(); Loaded += (_,_) => Input.Focus(); }
    private void Ok_Click(object sender, RoutedEventArgs e) { if (!string.IsNullOrWhiteSpace(Value)) DialogResult = true; }
}
