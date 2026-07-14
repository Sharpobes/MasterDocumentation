using System.Windows;
using System.Windows.Controls;

namespace MasterDocumentation.Views;

public partial class ListFormatDialog : Window
{
    private static readonly (string Label,string Value)[] Ordered=[("1, 2, 3","decimal"),("a, b, c","lower-alpha"),("A, B, C","upper-alpha"),("i, ii, iii","lower-roman"),("I, II, III","upper-roman")];
    private static readonly (string Label,string Value)[] Bullets=[("● Круг","disc"),("○ Окружность","circle"),("■ Квадрат","square")];
    public string Kind { get; private set; }="ordered";
    public string ListStyle { get; private set; }="decimal";
    public int Start { get; private set; }=1;
    public ListFormatDialog(){InitializeComponent();KindBox.SelectedIndex=0;FillStyles();}
    private void Kind_Changed(object sender,SelectionChangedEventArgs e){if(StyleBox is not null)FillStyles();}
    private void FillStyles(){var ordered=(KindBox.SelectedItem as ComboBoxItem)?.Tag?.ToString()!="bullet";StyleBox.ItemsSource=(ordered?Ordered:Bullets).Select(x=>new ComboBoxItem{Content=x.Label,Tag=x.Value});StyleBox.SelectedIndex=0;StartBox.IsEnabled=ordered;}
    private void Apply_Click(object sender,RoutedEventArgs e){Kind=(KindBox.SelectedItem as ComboBoxItem)?.Tag?.ToString()??"ordered";ListStyle=(StyleBox.SelectedItem as ComboBoxItem)?.Tag?.ToString()??(Kind=="ordered"?"decimal":"disc");if(Kind=="ordered"&&(!int.TryParse(StartBox.Text,out var start)||start is <1 or >100000)){MessageBox.Show(this,"Начальное значение должно быть от 1 до 100000.","Параметры списка",MessageBoxButton.OK,MessageBoxImage.Warning);return;}Start=Kind=="ordered"?int.Parse(StartBox.Text):1;DialogResult=true;}
}
