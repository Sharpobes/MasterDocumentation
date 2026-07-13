using System.Windows;
namespace MasterDocumentation.Views;
public partial class TextPrompt : Window
{
    public string Value => Input.Text.Trim();
    public TextPrompt(string title, string initial = "") { InitializeComponent(); Title = title; Input.Text = initial; Input.SelectAll(); Loaded += (_,_) => Input.Focus(); }
    private void Ok_Click(object sender, RoutedEventArgs e) { if (!string.IsNullOrWhiteSpace(Value)) DialogResult = true; }
}
