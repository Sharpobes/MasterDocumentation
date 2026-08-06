using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using MasterDocumentation.Utilities;
namespace MasterDocumentation.Views;
public partial class TextPrompt : Window
{
    private readonly bool _allowEmpty;
    public string Value => Input.Text.Trim();
    public TextPrompt(string title,string initial="",bool multiline=false,bool allowEmpty=false)
    {
        InitializeComponent();Title=LocalizationService.T(title);_allowEmpty=allowEmpty;Input.Text=initial;Input.AcceptsReturn=multiline;Input.VerticalContentAlignment=multiline?VerticalAlignment.Top:VerticalAlignment.Center;
        if(multiline){Width=680;Height=460;MinHeight=320;Input.TextWrapping=TextWrapping.Wrap;Input.HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled;Input.VerticalScrollBarVisibility=ScrollBarVisibility.Auto;}
        Input.SelectAll();Loaded+=(_,_)=>{Input.Focus();Keyboard.Focus(Input);};
    }
    private void Ok_Click(object sender,RoutedEventArgs e){if(_allowEmpty||!string.IsNullOrWhiteSpace(Value))DialogResult=true;}
}
