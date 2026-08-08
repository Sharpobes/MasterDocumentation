using System.Windows;
using System.Windows.Controls;
using MasterDocumentation.Models;

namespace MasterDocumentation.Views;

public sealed record ParentChoice(long? Id,string Name);
public sealed record TemplateChoice(long? Id,string Title);
public partial class NewItemDialog : Window
{
    private readonly bool _isFolder;
    public string ItemTitle=>TitleBox.Text.Trim();public bool IsFolder=>_isFolder;public long? ParentId=>(ParentBox.SelectedItem as ParentChoice)?.Id;public long? TemplateId=>_isFolder?null:(TemplateBox.SelectedItem as TemplateChoice)?.Id;
    /// <summary>Приватная страница видна только своему создателю, остальные — всем пользователям базы данных.</summary>
    public bool IsPrivate=>!_isFolder&&PrivateCheck.IsChecked==true;
    /// <param name="asTemplate">Создаётся шаблон: то же самое окно, но подписи говорят о шаблоне, а не о документе.</param>
    public NewItemDialog(IEnumerable<NodeItem> roots,IEnumerable<NodeItem> templates,bool folder,long? selectedParent,string? storageDescription=null,bool asTemplate=false)
    {InitializeComponent();_isFolder=folder;Title=folder?"Новая папка":asTemplate?"Новый шаблон":"Новый документ";HeadingText.Text=folder?"Создать папку":asTemplate?"Создать шаблон":"Создать документ";KindText.Text=folder?"Папка":asTemplate?"Шаблон":"Документ";TemplatePanel.Visibility=folder?Visibility.Collapsed:Visibility.Visible;var parents=new List<ParentChoice>{new(null,"Корень библиотеки")};AddFolders(roots,parents,"");ParentBox.ItemsSource=parents;ParentBox.SelectedItem=parents.FirstOrDefault(x=>x.Id==selectedParent)??parents[0];TemplateBox.ItemsSource=new[]{new TemplateChoice(null,"Без шаблона")}.Concat(templates.Select(x=>new TemplateChoice(x.Id,x.Title))).ToList();TemplateBox.SelectedIndex=0;if(!string.IsNullOrWhiteSpace(storageDescription))PrivateHintText.Text=storageDescription+"\n"+PrivateHintText.Text;Loaded+=(_,_)=>TitleBox.Focus();}
    private static void AddFolders(IEnumerable<NodeItem> nodes,List<ParentChoice> result,string prefix){foreach(var node in nodes.Where(x=>x.IsFolder)){result.Add(new(node.Id,prefix+node.Title));AddFolders(node.Children,result,prefix+node.Title+" / ");}}
    private void Create_Click(object sender,RoutedEventArgs e){if(ItemTitle.Length==0){MessageBox.Show(this,"Введите название.");return;}DialogResult=true;}
}
