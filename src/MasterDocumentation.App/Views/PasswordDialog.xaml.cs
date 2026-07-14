using System.Windows;

namespace MasterDocumentation.Views;

public partial class PasswordDialog : Window
{
    public string Value => Password.Password;
    public PasswordDialog(string caption){InitializeComponent();Caption.Text=caption;Loaded+=(_,_)=>Password.Focus();}
    private void Ok_Click(object sender,RoutedEventArgs e){if(Password.Password.Length<8){MessageBox.Show(this,"Пароль должен содержать не менее 8 символов.");return;}DialogResult=true;}
}
