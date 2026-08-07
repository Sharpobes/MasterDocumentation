using System.Globalization;
using System.Windows;
using System.Windows.Controls;

namespace MasterDocumentation.Views;

/// <summary>
/// Интервалы и отступы абзаца. Значения показываются в единицах измерения из настроек
/// приложения — «пиксели» в диалоге ничего не говорили тому, кто верстает документ.
/// Пустые поля заполняются значениями по умолчанию, чтобы у нового абзаца была понятная
/// отправная точка, а кнопка сброса убирает прямые настройки целиком.
/// </summary>
public partial class ParagraphFormatDialog : Window
{
    private const double DefaultSpaceBefore=0;
    private const double DefaultSpaceAfter=10;
    private const double DefaultFirstIndent=0;
    private const double DefaultLeftIndent=0;
    private const double DefaultRightIndent=0;

    private readonly double _perUnit;
    private readonly string _unit;
    private readonly int _decimals;

    public double? SpaceBefore { get; private set; }
    public double? SpaceAfter { get; private set; }
    public double? FirstIndent { get; private set; }
    public double? LeftIndent { get; private set; }
    public double? RightIndent { get; private set; }
    public string? TextDirection { get; private set; }

    public ParagraphFormatDialog(double? before,double? after,double? firstIndent,double? leftIndent,double? rightIndent,string? direction,string measurementUnits="Пиксели")
    {
        InitializeComponent();
        // 96 точек на дюйм — сетка, в которой редактор хранит отступы.
        (_unit,_perUnit,_decimals)=measurementUnits switch
        {
            not null when measurementUnits.StartsWith("Миллиметры",StringComparison.OrdinalIgnoreCase)=>("мм",96d/25.4,1),
            not null when measurementUnits.StartsWith("Дюймы",StringComparison.OrdinalIgnoreCase)=>("дюйм",96d,2),
            _=>("точки экрана",1d,0)
        };
        BeforeUnit.Text=AfterUnit.Text=FirstIndentUnit.Text=LeftIndentUnit.Text=RightIndentUnit.Text=_unit;
        UnitsHintText.Text=$"Значения указываются в выбранных в настройках единицах ({_unit}). Пустое поле возвращает значение по умолчанию.";
        BeforeBox.Text=Display(before??DefaultSpaceBefore);
        AfterBox.Text=Display(after??DefaultSpaceAfter);
        FirstIndentBox.Text=Display(firstIndent??DefaultFirstIndent);
        LeftIndentBox.Text=Display(leftIndent??DefaultLeftIndent);
        RightIndentBox.Text=Display(rightIndent??DefaultRightIndent);
        DirectionBox.SelectedItem=DirectionBox.Items.OfType<ComboBoxItem>().FirstOrDefault(x=>(x.Tag?.ToString()??"")==direction)??DirectionBox.Items[0];
    }

    private string Display(double? pixels)=>pixels is null?"":(pixels.Value/_perUnit).ToString("0."+new string('#',Math.Max(1,_decimals)),CultureInfo.CurrentCulture);
    private bool Read(TextBox box,string label,double fallback,out double? pixels)
    {
        pixels=fallback;
        if(string.IsNullOrWhiteSpace(box.Text))return true;
        if((double.TryParse(box.Text,NumberStyles.Float,CultureInfo.CurrentCulture,out var parsed)||double.TryParse(box.Text,NumberStyles.Float,CultureInfo.InvariantCulture,out parsed))&&parsed*_perUnit is >=-500 and <=2000)
        {
            pixels=parsed*_perUnit;
            return true;
        }
        var limit=(2000/_perUnit).ToString("0.#",CultureInfo.CurrentCulture);
        MessageBox.Show($"Поле «{label}» должно содержать число не больше {limit} {_unit}.","Параметры абзаца",MessageBoxButton.OK,MessageBoxImage.Warning);
        box.Focus();box.SelectAll();
        return false;
    }
    private void Apply_Click(object sender,RoutedEventArgs e)
    {
        if(!Read(BeforeBox,"Интервал перед абзацем",DefaultSpaceBefore,out var before)
           ||!Read(AfterBox,"Интервал после абзаца",DefaultSpaceAfter,out var after)
           ||!Read(FirstIndentBox,"Отступ первой строки",DefaultFirstIndent,out var first)
           ||!Read(LeftIndentBox,"Левый отступ",DefaultLeftIndent,out var left)
           ||!Read(RightIndentBox,"Правый отступ",DefaultRightIndent,out var right))return;
        SpaceBefore=before;SpaceAfter=after;FirstIndent=first;LeftIndent=left;RightIndent=right;
        TextDirection=(DirectionBox.SelectedItem as ComboBoxItem)?.Tag?.ToString();
        DialogResult=true;
    }
    private void Reset_Click(object sender,RoutedEventArgs e){SpaceBefore=null;SpaceAfter=null;FirstIndent=null;LeftIndent=null;RightIndent=null;TextDirection=null;DialogResult=true;}
}
