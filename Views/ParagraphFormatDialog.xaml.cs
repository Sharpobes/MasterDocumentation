using System.Globalization;
using System.Windows;
using System.Windows.Controls;

namespace MasterDocumentation.Views;

public partial class ParagraphFormatDialog : Window
{
    public double? SpaceBefore { get; private set; }
    public double? SpaceAfter { get; private set; }
    public double? FirstIndent { get; private set; }
    public double? LeftIndent { get; private set; }
    public double? RightIndent { get; private set; }
    public string? TextDirection { get; private set; }

    public ParagraphFormatDialog(double? before,double? after,double? firstIndent,double? leftIndent,double? rightIndent,string? direction)
    {
        InitializeComponent();BeforeBox.Text=Display(before);AfterBox.Text=Display(after);FirstIndentBox.Text=Display(firstIndent);LeftIndentBox.Text=Display(leftIndent);RightIndentBox.Text=Display(rightIndent);DirectionBox.SelectedItem=DirectionBox.Items.OfType<ComboBoxItem>().FirstOrDefault(x=>(x.Tag?.ToString()??"")==direction)??DirectionBox.Items[0];
    }

    private static string Display(double? value)=>value?.ToString("0.##",CultureInfo.CurrentCulture)??"";
    private static bool Read(TextBox box,string label,out double? value)
    {
        value=null;if(string.IsNullOrWhiteSpace(box.Text))return true;if((double.TryParse(box.Text,NumberStyles.Float,CultureInfo.CurrentCulture,out var parsed)||double.TryParse(box.Text,NumberStyles.Float,CultureInfo.InvariantCulture,out parsed))&&parsed is >=-500 and <=2000){value=parsed;return true;}MessageBox.Show($"Поле «{label}» должно содержать число от -500 до 2000.","Параметры абзаца",MessageBoxButton.OK,MessageBoxImage.Warning);box.Focus();box.SelectAll();return false;
    }
    private void Apply_Click(object sender,RoutedEventArgs e)
    {
        if(!Read(BeforeBox,"Интервал перед",out var before)||!Read(AfterBox,"Интервал после",out var after)||!Read(FirstIndentBox,"Отступ первой строки",out var first)||!Read(LeftIndentBox,"Левый отступ",out var left)||!Read(RightIndentBox,"Правый отступ",out var right))return;SpaceBefore=before;SpaceAfter=after;FirstIndent=first;LeftIndent=left;RightIndent=right;TextDirection=(DirectionBox.SelectedItem as ComboBoxItem)?.Tag?.ToString();DialogResult=true;
    }
    private void Reset_Click(object sender,RoutedEventArgs e){SpaceBefore=null;SpaceAfter=null;FirstIndent=null;LeftIndent=null;RightIndent=null;TextDirection=null;DialogResult=true;}
}
