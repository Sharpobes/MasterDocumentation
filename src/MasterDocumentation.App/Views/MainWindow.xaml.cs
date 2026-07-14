using Microsoft.Win32;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Text.RegularExpressions;
using System.Collections.ObjectModel;
using System.Text.Json;
using DocumentFormat.OpenXml.Packaging;
using OpenXmlParagraph = DocumentFormat.OpenXml.Wordprocessing.Paragraph;
using MasterDocumentation.Models;
using MasterDocumentation.Services;
using MasterDocumentation.Utilities;
using MasterDocumentation.ViewModels;
using MasterDocumentation.Editor;

namespace MasterDocumentation.Views;

public sealed class OutlineItem
{
    public EditorHeading Heading { get; }
    public string Text=>Heading.Text;
    public ObservableCollection<OutlineItem> Children { get; }=[];
    public OutlineItem(EditorHeading heading)=>Heading=heading;
}

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm; private readonly SettingsService _settingsService; private ApplicationSettings _settings; private readonly System.Windows.Threading.DispatcherTimer _saveTimer; private readonly System.Windows.Threading.DispatcherTimer _highlightTimer; private readonly ObservableCollection<AttachmentInfo> _attachments=[]; private SettingsView? _settingsView; private bool _loading; private bool _highlighting; private bool _closeInProgress; private bool _closeCommitted; private bool _resettingHeadingSelector; private bool _updatingToolbarState; private bool _modernImageSelected; private string _selectedImageSource=""; private string _selectedImageAlt=""; private string _selectedImageCaption=""; private string _selectedImageWrap="none"; private double? _spaceBefore; private double? _spaceAfter; private double? _firstIndent; private double? _leftIndent; private double? _rightIndent; private string? _textDirection; private string _outlineSignature=""; private long _outlineDocumentId; private double _zoom=1; private Point _dragStart;
    public MainWindow(MainViewModel viewModel, SettingsService settingsService)
    {
        InitializeComponent(); _vm = viewModel; _settingsService = settingsService; DataContext = _vm; AttachmentList.ItemsSource=_attachments;ModernEditor.DataFolder=AppPaths.Data;StorageLocationText.Text=AppPaths.Data;StorageLocationText.ToolTip=AppPaths.Data;
        FontFamilyBox.ItemsSource = Fonts.SystemFontFamilies.OrderBy(f => f.Source); FontFamilyBox.SelectedItem = new FontFamily("Segoe UI");
        FontSizeBox.ItemsSource = new[] { 8d, 9d, 10d, 11d, 12d, 13d, 14d, 16d, 18d, 20d, 24d, 28d, 32d, 40d, 48d, 64d }; FontSizeBox.SelectedItem = 13d;
        _settings = _settingsService.Load();
        _saveTimer = new() { Interval = TimeSpan.FromSeconds(_settings.AutoSaveDelaySeconds) }; _saveTimer.Tick += (_, _) => { _saveTimer.Stop(); SaveAllWithStatus(); };
        _highlightTimer = new() { Interval = TimeSpan.FromMilliseconds(450) }; _highlightTimer.Tick += (_, _) => { _highlightTimer.Stop(); HighlightCodeBlocks(); };
        UpdateNavigationCounts();
        RestoreWindow();RestorePanelLayout(); Loaded += (_, _) => { RecoverEmergencyDrafts(); RestoreSession(); AutoBackupIfNeeded(); }; Deactivated += (_, _) => _vm.SaveAll();
        AddHandler(Hyperlink.RequestNavigateEvent, new System.Windows.Navigation.RequestNavigateEventHandler((_, e) => { try { Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true }); } catch (Exception ex) { LogService.Error("Не удалось открыть ссылку", ex); } e.Handled = true; }));
    }

    private void Tree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e) => _vm.SelectedNode = e.NewValue as NodeItem;
    private void Tree_DoubleClick(object sender, MouseButtonEventArgs e) { if (_vm.SelectedNode is not null) _vm.Open(_vm.SelectedNode); }
    private void OpenNode_Click(object sender, RoutedEventArgs e) { if (_vm.SelectedNode is not null) _vm.Open(_vm.SelectedNode); }
    private long? TargetFolder() => _vm.SelectedNode is null ? null : _vm.SelectedNode.IsFolder ? _vm.SelectedNode.Id : _vm.SelectedNode.ParentId;
    private void CreateMenu_Click(object sender,RoutedEventArgs e){CreateMenuPopup.PlacementTarget=sender as UIElement;CreateMenuPopup.IsOpen=true;}
    private void NewDocument_Click(object sender, RoutedEventArgs e){CreateMenuPopup.IsOpen=false;CreateNode(false);}
    private void NewFolder_Click(object sender, RoutedEventArgs e){CreateMenuPopup.IsOpen=false;CreateNode(true);}
    private void NewFromTemplate_Click(object sender,RoutedEventArgs e){CreateMenuPopup.IsOpen=false;if(_vm.Database.LoadTemplates().Count==0){MessageBox.Show(this,"Сначала создайте шаблон из документа или через пункт «Новый шаблон».","Шаблоны");return;}CreateNode(false);}
    private void NewTemplate_Click(object sender,RoutedEventArgs e){CreateMenuPopup.IsOpen=false;var id=CreateNode(false);if(id is long value){_vm.Database.SetTemplate(value,true);_vm.ReloadTree();}}
    private void NewSection_Click(object sender,RoutedEventArgs e){CreateMenuPopup.IsOpen=false;var prompt=new TextPrompt("Новый раздел",""){Owner=this};if(prompt.ShowDialog()!=true)return;try{_vm.Database.Create(null,true,prompt.Value);_vm.ReloadTree();UpdateNavigationCounts();}catch(Exception ex){MessageBox.Show(this,ex.Message,"Не удалось создать раздел",MessageBoxButton.OK,MessageBoxImage.Warning);}}
    private void SectionAdd_Click(object sender,RoutedEventArgs e)=>SectionAddPopup.IsOpen=!SectionAddPopup.IsOpen;
    private void SectionAddDocument_Click(object sender,RoutedEventArgs e){SectionAddPopup.IsOpen=false;CreateNode(false);}
    private void SectionAddFolder_Click(object sender,RoutedEventArgs e){SectionAddPopup.IsOpen=false;CreateNode(true);}
    private long? CreateNode(bool folder)
    {
        var roots=_vm.Database.LoadTree();var dialog=new NewItemDialog(roots,_vm.Database.LoadTemplates(),folder,TargetFolder()){Owner=this};if(dialog.ShowDialog()!=true)return null;
        try
        {
            IReadOnlyDictionary<string,string>? values=null;
            if(dialog.TemplateId is long template)
            {
                var builtIn=new HashSet<string>(["Title","Date","Time","Author","Section"],StringComparer.OrdinalIgnoreCase);var custom=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
                foreach(var name in _vm.Database.GetTemplateVariables(template).Where(x=>!builtIn.Contains(x))){var prompt=new TextPrompt("Переменная шаблона: "+name,""){Owner=this};if(prompt.ShowDialog()!=true)return null;custom[name]=prompt.Value;}values=custom;
            }
            var id=dialog.TemplateId is long source?_vm.Database.CreateFromTemplate(source,dialog.ParentId,dialog.ItemTitle,values):_vm.Database.Create(dialog.ParentId,dialog.IsFolder,dialog.ItemTitle);_vm.ReloadTree();UpdateNavigationCounts();if(!dialog.IsFolder){var node=FindNode(_vm.Nodes,id);if(node is not null){_vm.Open(node);ShowSelectedTab();}}return id;
        }
        catch(Exception ex){MessageBox.Show(this,ex.Message,"Не удалось создать",MessageBoxButton.OK,MessageBoxImage.Warning);return null;}
    }
    private void ImportDocument_Click(object sender,RoutedEventArgs e)
    {
        CreateMenuPopup.IsOpen=false;var dialog=new OpenFileDialog{Title="Импорт документа",Filter="Поддерживаемые документы|*.docx;*.html;*.htm;*.md;*.markdown;*.txt;*.rtf;*.pdf|Word (*.docx)|*.docx|HTML (*.html;*.htm)|*.html;*.htm|Markdown (*.md;*.markdown)|*.md;*.markdown|Текст (*.txt)|*.txt|RTF (*.rtf)|*.rtf|PDF как вложение (*.pdf)|*.pdf"};if(dialog.ShowDialog(this)!=true)return;
        try
        {
            var parent=TargetFolder();var title=UniqueTitle(parent,Path.GetFileNameWithoutExtension(dialog.FileName));var id=_vm.Database.Create(parent,false,title);var extension=Path.GetExtension(dialog.FileName).ToLowerInvariant();string html;string plain;
            if(extension==".pdf") {var asset=StoreAsset(dialog.FileName);_vm.Database.RegisterAttachment(id,Path.GetFileName(dialog.FileName),asset.StoredName,"application/pdf",asset.Size,asset.Hash);plain=$"Вложение PDF: {Path.GetFileName(dialog.FileName)}";html=$"<p>Вложение PDF: <a href=\"https://assets.local/{asset.StoredName}\">{System.Net.WebUtility.HtmlEncode(Path.GetFileName(dialog.FileName))}</a></p>";}
            else if(extension==".docx") (html,plain)=ImportDocx(dialog.FileName);
            else if(extension is ".md" or ".markdown") {plain=File.ReadAllText(dialog.FileName);html=MarkdownToHtml(plain);}
            else if(extension is ".html" or ".htm") {html=SanitizeImportedHtml(File.ReadAllText(dialog.FileName));plain=HtmlToPlainText(html);}
            else if(extension==".rtf") {var document=new FlowDocument();using var stream=File.OpenRead(dialog.FileName);new TextRange(document.ContentStart,document.ContentEnd).Load(stream,DataFormats.Rtf);plain=new TextRange(document.ContentStart,document.ContentEnd).Text.TrimEnd();html="<p>"+System.Net.WebUtility.HtmlEncode(plain).Replace("\r\n","</p><p>").Replace("\n","</p><p>")+"</p>";}
            else {plain=File.ReadAllText(dialog.FileName);html="<p>"+System.Net.WebUtility.HtmlEncode(plain).Replace("\r\n","</p><p>").Replace("\n","</p><p>")+"</p>";}
            _vm.Database.SaveStructuredContent(id,"",html,plain);_vm.ReloadTree();UpdateNavigationCounts();var node=FindNode(_vm.Nodes,id);if(node is not null){_vm.Open(node);ShowSelectedTab();}MessageBox.Show(this,"Документ импортирован.","Импорт");
        }
        catch(Exception ex){LogService.Error("Ошибка импорта документа",ex);MessageBox.Show(this,"Не удалось импортировать документ: "+ex.Message,"Импорт",MessageBoxButton.OK,MessageBoxImage.Error);}
    }
    private string UniqueTitle(long? parent,string value){var title=string.IsNullOrWhiteSpace(value)?"Импортированный документ":value.Trim();if(!_vm.Database.TitleExists(parent,title))return title;for(var i=2;;i++){var candidate=$"{title} ({i})";if(!_vm.Database.TitleExists(parent,candidate))return candidate;}}
    private static (string Html,string Plain) ImportDocx(string path){using var document=WordprocessingDocument.Open(path,false);var paragraphs=document.MainDocumentPart?.Document?.Body?.Elements<OpenXmlParagraph>()??[];var html=new System.Text.StringBuilder();var plain=new System.Text.StringBuilder();foreach(var paragraph in paragraphs){var text=paragraph.InnerText;plain.AppendLine(text);var style=paragraph.ParagraphProperties?.ParagraphStyleId?.Val?.Value??"";var match=Regex.Match(style,@"Heading\s*([1-6])",RegexOptions.IgnoreCase);if(match.Success)html.Append($"<h{match.Groups[1].Value}>{System.Net.WebUtility.HtmlEncode(text)}</h{match.Groups[1].Value}>");else html.Append($"<p>{System.Net.WebUtility.HtmlEncode(text)}</p>");}return(html.ToString(),plain.ToString().TrimEnd());}
    private static string MarkdownToHtml(string markdown){var result=new System.Text.StringBuilder();var inCode=false;foreach(var raw in markdown.Replace("\r","").Split('\n')){if(raw.TrimStart().StartsWith("```")){result.Append(inCode?"</code></pre>":"<pre><code>");inCode=!inCode;continue;}if(inCode){result.AppendLine(System.Net.WebUtility.HtmlEncode(raw));continue;}var heading=Regex.Match(raw,@"^(#{1,6})\s+(.+)$");if(heading.Success){var level=heading.Groups[1].Value.Length;result.Append($"<h{level}>{System.Net.WebUtility.HtmlEncode(heading.Groups[2].Value)}</h{level}>");continue;}var bullet=Regex.Match(raw,@"^\s*[-*+]\s+(.+)$");if(bullet.Success){result.Append($"<ul><li>{System.Net.WebUtility.HtmlEncode(bullet.Groups[1].Value)}</li></ul>");continue;}result.Append(string.IsNullOrWhiteSpace(raw)?"<p></p>":$"<p>{System.Net.WebUtility.HtmlEncode(raw)}</p>");}if(inCode)result.Append("</code></pre>");return result.ToString();}
    private static string SanitizeImportedHtml(string html){html=Regex.Replace(html,@"<(script|iframe|object|embed)\b[^>]*>[\s\S]*?</\1>","",RegexOptions.IgnoreCase);html=Regex.Replace(html,@"\son\w+\s*=\s*(['""]).*?\1","",RegexOptions.IgnoreCase);return html;}
    private static string HtmlToPlainText(string html)=>System.Net.WebUtility.HtmlDecode(Regex.Replace(Regex.Replace(html,@"<(br|/p|/div|/h[1-6]|/li)>","\n",RegexOptions.IgnoreCase),"<[^>]+>"," ")).Trim();
    private void Rename_Click(object sender, RoutedEventArgs e)
    {
        var node = _vm.SelectedNode; if (node is null) return; var prompt = new TextPrompt("Переименовать", node.Title) { Owner = this };
        if (prompt.ShowDialog() != true) return;try{_vm.Database.Rename(node.Id, prompt.Value); node.Title = prompt.Value; var tab = _vm.Tabs.FirstOrDefault(t => t.DocumentId == node.Id); if (tab is not null) tab.Title = prompt.Value;}catch(Exception ex){MessageBox.Show(this,ex.Message,"Переименование",MessageBoxButton.OK,MessageBoxImage.Warning);}
    }
    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        var node = _vm.SelectedNode; if (node is null) return;
        if (_settingsService.Load().ConfirmDelete&&MessageBox.Show($"Удалить «{node.Title}»{(node.IsFolder ? " и всё содержимое" : "")}?", "Подтверждение", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        foreach (var tab in _vm.Tabs.Where(t => t.DocumentId == node.Id).ToList()) _vm.Tabs.Remove(tab); _vm.Database.Delete(node.Id); _vm.ReloadTree(); AllDocumentsCountText.Text = _vm.Database.CountDocuments().ToString(); ShowSelectedTab();
    }
    private static NodeItem? FindNode(IEnumerable<NodeItem> nodes, long id) { foreach (var n in nodes) { if (n.Id == id) return n; var child = FindNode(n.Children, id); if (child is not null) return child; } return null; }

    private void Tabs_SelectionChanged(object sender, SelectionChangedEventArgs e) { if (e.Source == Tabs) ShowSelectedTab(); }
    private void ShowSelectedTab()
    {
        _loading = true; if (_vm.SelectedTab is null) { Editor.Visibility = Visibility.Collapsed; ModernEditor.Visibility=Visibility.Collapsed; EmptyHint.Visibility = Visibility.Visible;LoadStartPage(); }
        else { Editor.Document = _vm.SelectedTab.Document;Editor.Visibility=Visibility.Collapsed;ModernEditor.Visibility=Visibility.Visible; EmptyHint.Visibility = Visibility.Collapsed;_outlineSignature="";_outlineDocumentId=_vm.SelectedTab.DocumentId;StructureTree.ItemsSource=null;HeadersList.ItemsSource=null;TocTree.ItemsSource=null;var html=_vm.SelectedTab.Html;if(string.IsNullOrWhiteSpace(html)){var text=new TextRange(Editor.Document.ContentStart,Editor.Document.ContentEnd).Text;html="<p>"+System.Net.WebUtility.HtmlEncode(text).Replace("\r\n","</p><p>").Replace("\n","</p><p>")+"</p>";}_=ModernEditor.SetContentAsync(_vm.SelectedTab.DocumentId,_vm.SelectedTab.EditorJson,html);_zoom=Math.Clamp(_vm.Database.GetZoom(_vm.SelectedTab.DocumentId),.5,2);ZoomText.Text=$"{_zoom*100:0}%";_=ModernEditor.ExecuteAsync("setZoom",new{value=_zoom});_=ModernEditor.ExecuteAsync("setTheme",new{theme=_vm.DarkTheme?"dark":"light"});LoadDocumentProperties(); } _loading = false; UpdateNavigationCounts(); UpdateDocumentStats();
    }
    private void LoadStartPage(){StartRecentList.ItemsSource=_vm.Database.LoadRecent(_settings.RecentFilesCount);StartStatisticsText.Text=$"Документов: {_vm.Database.CountDocuments()}   •   Избранных: {_vm.Database.CountFavorites()}   •   В корзине: {_vm.Database.CountTrash()}   •   Хранилище: {AppPaths.Data}";}
    private void StartRecent_DoubleClick(object sender,MouseButtonEventArgs e){if(StartRecentList.SelectedItem is not NodeItem node)return;_vm.Open(node);ShowSelectedTab();}
    private void GlobalSearch_PreviewKeyDown(object sender,KeyEventArgs e){if(e.Key!=Key.Enter)return;e.Handled=true;OpenGlobalSearch(GlobalSearchBox.Text);}
    private void OpenGlobalSearch(string initialQuery)
    {
        var dialog=new SearchWindow(_vm.Database,initialQuery){Owner=this};if(dialog.ShowDialog()!=true||dialog.SelectedDocumentId is not long id)return;var node=_vm.Database.FindNode(id);if(node is null){MessageBox.Show(this,"Документ находится в корзине. Восстановите его перед открытием.","Поиск",MessageBoxButton.OK,MessageBoxImage.Information);_vm.ShowTrash();return;}_vm.Open(node);ShowSelectedTab();
    }
    private void CloseTab_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not DocumentTab tab) return; _vm.Save(tab); _vm.Tabs.Remove(tab); if (_vm.SelectedTab == tab) _vm.SelectedTab = _vm.Tabs.LastOrDefault(); ShowSelectedTab(); e.Handled = true;
    }
    private void Editor_TextChanged(object sender, TextChangedEventArgs e) { if (_loading || _highlighting || _vm.SelectedTab is null) return; _vm.SelectedTab.IsDirty = true; _saveTimer.Stop(); _saveTimer.Start(); UpdateDocumentStats(); if (Editor.CaretPosition.Paragraph?.Tag as string == "CodeBlock") { _highlightTimer.Stop(); _highlightTimer.Start(); } }
    private void UpdateDocumentStats() { if (_vm.SelectedTab is null) { DocumentStatsText.Text = "Слов: 0   Символов: 0 / 0 без пробелов"; return; } var text = !string.IsNullOrEmpty(_vm.SelectedTab.PlainText)?_vm.SelectedTab.PlainText:new TextRange(Editor.Document.ContentStart, Editor.Document.ContentEnd).Text.TrimEnd(); var words = Regex.Matches(text, @"[\p{L}\p{N}_]+", RegexOptions.CultureInvariant).Count;var withoutSpaces=text.Count(x=>!char.IsWhiteSpace(x)); DocumentStatsText.Text = $"Слов: {words}   Символов: {text.Length} / {withoutSpaces} без пробелов"; }
    private void ModernEditor_ContentChanged(object? sender,EditorContent content)
    {
        if(content.DocumentId<=0)return;var tab=_vm.Tabs.FirstOrDefault(x=>x.DocumentId==content.DocumentId);if(tab is null)return;
        if(_vm.SelectedTab?.DocumentId==content.DocumentId)UpdateOutline(content.DocumentId,content.Headings);
        if(content.IsInitialLoad)return;
        ApplyEditorContent(tab,content,true);
    }
    private void ModernEditor_Ready(object? sender,EventArgs e){LogService.Info("TipTap-редактор готов");_=ModernEditor.ExecuteAsync("setTheme",new{theme=_vm.DarkTheme?"dark":"light"});}
    private async void ModernEditor_FileDropped(object? sender,EditorFileData file)
    {
        if(file.DocumentId<=0||_vm.SelectedTab?.DocumentId!=file.DocumentId||string.IsNullOrWhiteSpace(file.DataUrl))return;
        try
        {
            var comma=file.DataUrl.IndexOf(',');if(comma<0)throw new InvalidDataException("Некорректные данные файла.");var bytes=Convert.FromBase64String(file.DataUrl[(comma+1)..]);var asset=StoreAssetBytes(bytes,file.Name,file.MimeType);if(!_vm.Database.GetAttachments(file.DocumentId).Any(x=>x.Sha256.Equals(asset.Hash,StringComparison.OrdinalIgnoreCase)))_vm.Database.RegisterAttachment(file.DocumentId,file.Name,asset.StoredName,file.MimeType,asset.Size,asset.Hash);if(file.MimeType.StartsWith("image/",StringComparison.OrdinalIgnoreCase))await ModernEditor.ExecuteAsync("image",new{src="https://assets.local/"+asset.StoredName,alt=file.Name});LoadDocumentProperties();
        }
        catch(Exception ex){LogService.Error("Не удалось принять файл из буфера обмена или перетаскивания",ex);MessageBox.Show(this,"Не удалось добавить файл: "+ex.Message,"Вставка файла",MessageBoxButton.OK,MessageBoxImage.Error);}
    }
    private void ApplyEditorContent(DocumentTab tab,EditorContent content,bool restartAutoSave)
    {
        tab.EditorJson=content.Json;tab.Html=content.Html;tab.PlainText=content.PlainText;tab.IsDirty=true;SaveStatusText.Text="Изменено";SaveStatusText.Foreground=new SolidColorBrush(Color.FromRgb(230,190,96));
        if(_vm.SelectedTab?.DocumentId==tab.DocumentId){_loading=true;Editor.Document=new FlowDocument(new Paragraph(new Run(content.PlainText)));_loading=false;UpdateDocumentStats();}
        try{DraftRecoveryService.Write(tab);}catch(Exception ex){LogService.Error("Не удалось записать аварийный черновик",ex);}
        if(restartAutoSave){_saveTimer.Stop();_saveTimer.Start();}
    }
    private void UpdateOutline(long documentId,IReadOnlyList<EditorHeading> headings)
    {
        var signature=string.Join('\u001f',headings.Select(x=>$"{x.Level}:{x.Position}:{x.Text}"));if(_outlineDocumentId==documentId&&_outlineSignature==signature)return;_outlineDocumentId=documentId;_outlineSignature=signature;
        var roots=new ObservableCollection<OutlineItem>();var stack=new Stack<(int Level,OutlineItem Item)>();
        foreach(var heading in headings){while(stack.Count>0&&stack.Peek().Level>=heading.Level)stack.Pop();var item=new OutlineItem(heading);if(stack.Count==0)roots.Add(item);else stack.Peek().Item.Children.Add(item);stack.Push((heading.Level,item));}
        StructureTree.ItemsSource=roots;TocTree.ItemsSource=roots;HeadersList.ItemsSource=headings;
    }
    private void Heading_DoubleClick(object sender,MouseButtonEventArgs e){var heading=sender switch{TreeView tree when tree.SelectedItem is OutlineItem item=>item.Heading,ListBox list when list.SelectedItem is EditorHeading item=>item,_=>null};if(heading is not null)_=ModernEditor.ExecuteAsync("gotoHeading",new{pos=heading.Position});}
    private void StructureTab_Click(object sender,RoutedEventArgs e){StructureTab.IsChecked=true;HeadersTab.IsChecked=false;StructureTree.Visibility=Visibility.Visible;HeadersList.Visibility=Visibility.Collapsed;}
    private void HeadersTab_Click(object sender,RoutedEventArgs e){StructureTab.IsChecked=false;HeadersTab.IsChecked=true;StructureTree.Visibility=Visibility.Collapsed;HeadersList.Visibility=Visibility.Visible;}
    private void UpdateNavigationCounts() { AllDocumentsCountText.Text = _vm.Database.CountDocuments().ToString(); FavoritesCountText.Text = _vm.Database.CountFavorites().ToString(); RecentDocumentsCountText.Text = _vm.Database.CountRecent().ToString(); TrashCountText.Text = _vm.Database.CountTrash().ToString(); }
    private void AllDocuments_Click(object sender, RoutedEventArgs e) => _vm.ShowAll();
    private void Favorites_Click(object sender, RoutedEventArgs e) => _vm.ShowFavorites();
    private void Recent_Click(object sender, RoutedEventArgs e) => _vm.ShowRecent(_settingsService.Load().RecentFilesCount);
    private void Trash_Click(object sender, RoutedEventArgs e) => _vm.ShowTrash();
    private void FavoriteNode_Click(object sender, RoutedEventArgs e) { if (_vm.SelectedNode is null || _vm.SelectedNode.IsFolder) return; _vm.Database.ToggleFavorite(_vm.SelectedNode.Id); UpdateNavigationCounts(); }
    private void CopyDocumentLink_Click(object sender,RoutedEventArgs e){if(_vm.SelectedNode is null||_vm.SelectedNode.IsFolder)return;Clipboard.SetText("masterdoc://document/"+_vm.Database.GetDocumentGuid(_vm.SelectedNode.Id));}
    private void RestoreNode_Click(object sender, RoutedEventArgs e) { if (_vm.SelectedNode is null) return; _vm.Database.Restore(_vm.SelectedNode.Id); _vm.ShowTrash(); UpdateNavigationCounts(); }
    private void DuplicateSelectedNode_Click(object sender,RoutedEventArgs e){if(_vm.SelectedNode is null||_vm.SelectedNode.IsFolder)return;var id=_vm.Database.Duplicate(_vm.SelectedNode.Id);_vm.ReloadTree();UpdateNavigationCounts();var node=FindNode(_vm.Nodes,id);if(node is not null){_vm.Open(node);ShowSelectedTab();}}
    private void DeletePermanently_Click(object sender,RoutedEventArgs e){if(_vm.SelectedNode is null)return;if(MessageBox.Show(this,$"Окончательно удалить «{_vm.SelectedNode.Title}»? Это действие нельзя отменить.","Окончательное удаление",MessageBoxButton.YesNo,MessageBoxImage.Warning)!=MessageBoxResult.Yes)return;_vm.Database.DeletePermanently(_vm.SelectedNode.Id);_vm.ShowTrash();UpdateNavigationCounts();}
    private void EmptyTrash_Click(object sender,RoutedEventArgs e){if(MessageBox.Show(this,"Окончательно удалить всё содержимое корзины?","Очистить корзину",MessageBoxButton.YesNo,MessageBoxImage.Warning)!=MessageBoxResult.Yes)return;_vm.Database.EmptyTrash();_vm.ShowTrash();UpdateNavigationCounts();}
    private void CollapseAll_Click(object sender,RoutedEventArgs e){foreach(var node in _vm.Nodes)SetExpanded(node,false);SaveExpandedNodes();}
    private static void SetExpanded(NodeItem node,bool value){node.IsExpanded=value;foreach(var child in node.Children)SetExpanded(child,value);}
    private void Tree_ExpansionChanged(object sender,RoutedEventArgs e){Dispatcher.BeginInvoke(SaveExpandedNodes,System.Windows.Threading.DispatcherPriority.Background);}
    private void SaveExpandedNodes(){var ids=new List<long>();void Visit(NodeItem node){if(node.IsExpanded)ids.Add(node.Id);foreach(var child in node.Children)Visit(child);}foreach(var node in _vm.Nodes)Visit(node);_vm.Database.SetSetting("ExpandedNodeIds",string.Join(',',ids));}
    private void MakeTemplate_Click(object sender,RoutedEventArgs e){if(_vm.SelectedNode is null||_vm.SelectedNode.IsFolder){MessageBox.Show(this,"Шаблоном может быть только документ.");return;}_vm.Save(_vm.Tabs.FirstOrDefault(x=>x.DocumentId==_vm.SelectedNode.Id));_vm.Database.SetTemplate(_vm.SelectedNode.Id,true);MessageBox.Show(this,"Документ добавлен в шаблоны. При создании нового документа его можно выбрать в поле «Создать из шаблона».","Шаблон");}
    private void Templates_Click(object sender,RoutedEventArgs e)=>_vm.ShowTemplates();
    private void Applications_Click(object sender,RoutedEventArgs e)=>new ApplicationsWindow{Owner=this}.ShowDialog();
    private void StorageHealth_Click(object sender,RoutedEventArgs e){try{var result=_vm.Database.CheckIntegrity();MessageBox.Show(this,result.Equals("ok",StringComparison.OrdinalIgnoreCase)?"Хранилище исправно.":"Результат проверки: "+result,"Проверка хранилища",MessageBoxButton.OK,result.Equals("ok",StringComparison.OrdinalIgnoreCase)?MessageBoxImage.Information:MessageBoxImage.Warning);}catch(Exception ex){MessageBox.Show(this,"Не удалось проверить хранилище: "+ex.Message,"Проверка",MessageBoxButton.OK,MessageBoxImage.Error);}}
    private void ThemeQuickToggle_Click(object sender,RoutedEventArgs e){_vm.DarkTheme=!_vm.DarkTheme;_=ModernEditor.ExecuteAsync("setTheme",new{theme=_vm.DarkTheme?"dark":"light"});}
    private void OpenDataFolder_Click(object sender,RoutedEventArgs e){Directory.CreateDirectory(AppPaths.Data);Process.Start(new ProcessStartInfo("explorer.exe",$"\"{AppPaths.Data}\""){UseShellExecute=true});}
    private void LoadDocumentProperties(){_attachments.Clear();if(_vm.SelectedTab is null){AttachmentsHeaderText.Text="Вложения (0)";TagsButton.Content="＋ Добавить";return;}var status=_vm.Database.GetStatus(_vm.SelectedTab.DocumentId);StatusBox.ItemsSource=null;StatusBox.Items.Clear();foreach(var value in _vm.Database.GetStatuses())StatusBox.Items.Add(value);StatusBox.SelectedItem=status;var tags=_vm.Database.GetTags(_vm.SelectedTab.DocumentId);TagsButton.Content=tags.Count==0?"＋ Добавить":string.Join(", ",tags);foreach(var attachment in _vm.Database.GetAttachments(_vm.SelectedTab.DocumentId))_attachments.Add(attachment);AttachmentsHeaderText.Text=$"Вложения ({_attachments.Count})";}
    private void PropertiesTab_Click(object sender,RoutedEventArgs e){AttachmentsPanel.Visibility=Visibility.Collapsed;PropertiesTabIndicator.Visibility=Visibility.Visible;AttachmentsTabIndicator.Visibility=Visibility.Collapsed;}
    private void AttachmentsTab_Click(object sender,RoutedEventArgs e){AttachmentsPanel.Visibility=Visibility.Visible;PropertiesTabIndicator.Visibility=Visibility.Collapsed;AttachmentsTabIndicator.Visibility=Visibility.Visible;LoadDocumentProperties();}
    private void AddAttachment_Click(object sender,RoutedEventArgs e)
    {
        if(_vm.SelectedTab is null){MessageBox.Show(this,"Сначала откройте документ.","Вложения");return;}var dialog=new OpenFileDialog{Title="Добавить вложение",Filter="Все файлы|*.*",Multiselect=true};if(dialog.ShowDialog(this)!=true)return;
        try{foreach(var path in dialog.FileNames){var asset=StoreAsset(path);if(_vm.Database.GetAttachments(_vm.SelectedTab.DocumentId).Any(x=>x.Sha256.Equals(asset.Hash,StringComparison.OrdinalIgnoreCase)))continue;_vm.Database.RegisterAttachment(_vm.SelectedTab.DocumentId,Path.GetFileName(path),asset.StoredName,GuessMimeType(path),asset.Size,asset.Hash);}LoadDocumentProperties();}catch(Exception ex){LogService.Error("Не удалось добавить вложение",ex);MessageBox.Show(this,"Не удалось добавить вложение: "+ex.Message,"Вложения",MessageBoxButton.OK,MessageBoxImage.Error);}
    }
    private void OpenAttachment_Click(object sender,RoutedEventArgs e){if(AttachmentList.SelectedItem is not AttachmentInfo item)return;var path=Path.Combine(AppPaths.Assets,item.StoredName);if(!File.Exists(path)){MessageBox.Show(this,"Файл вложения не найден в локальном хранилище.","Вложения",MessageBoxButton.OK,MessageBoxImage.Warning);return;}Process.Start(new ProcessStartInfo(path){UseShellExecute=true});}
    private void SaveAttachment_Click(object sender,RoutedEventArgs e){if(AttachmentList.SelectedItem is not AttachmentInfo item)return;var source=Path.Combine(AppPaths.Assets,item.StoredName);if(!File.Exists(source)){MessageBox.Show(this,"Файл вложения не найден.","Вложения");return;}var dialog=new SaveFileDialog{Title="Сохранить вложение",FileName=item.FileName,Filter="Все файлы|*.*"};if(dialog.ShowDialog(this)==true)File.Copy(source,dialog.FileName,true);}
    private void RemoveAttachment_Click(object sender,RoutedEventArgs e){if(AttachmentList.SelectedItem is not AttachmentInfo item)return;if(MessageBox.Show(this,$"Удалить связь с файлом «{item.FileName}»?\nСам файл будет удалён только если больше нигде не используется.","Удаление вложения",MessageBoxButton.YesNo,MessageBoxImage.Warning)!=MessageBoxResult.Yes)return;try{var removed=_vm.Database.RemoveAttachment(item.Id);if(removed.IsUnused){var path=Path.Combine(AppPaths.Assets,removed.StoredName);if(File.Exists(path))File.Delete(path);}LoadDocumentProperties();}catch(Exception ex){MessageBox.Show(this,"Не удалось удалить вложение: "+ex.Message,"Вложения",MessageBoxButton.OK,MessageBoxImage.Error);}}
    private static string GuessMimeType(string path)=>Path.GetExtension(path).ToLowerInvariant() switch{".png"=>"image/png",".jpg" or ".jpeg"=>"image/jpeg",".gif"=>"image/gif",".svg"=>"image/svg+xml",".pdf"=>"application/pdf",".docx"=>"application/vnd.openxmlformats-officedocument.wordprocessingml.document",".xlsx"=>"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",".zip"=>"application/zip",".json"=>"application/json",".html" or ".htm"=>"text/html",".md"=>"text/markdown",".txt"=>"text/plain",_=>"application/octet-stream"};
    private void Status_Changed(object sender,SelectionChangedEventArgs e){if(_loading||_vm?.SelectedTab is null||StatusBox.SelectedItem is null)return;var value=StatusBox.SelectedItem is ComboBoxItem item?item.Content?.ToString():StatusBox.SelectedItem.ToString();if(!string.IsNullOrWhiteSpace(value))_vm.Database.SetStatus(_vm.SelectedTab.DocumentId,value);}
    private void Tags_Click(object sender,RoutedEventArgs e){if(_vm.SelectedTab is null)return;var current=string.Join(", ",_vm.Database.GetTags(_vm.SelectedTab.DocumentId));var prompt=new TextPrompt("Теги через запятую",current){Owner=this};if(prompt.ShowDialog()!=true)return;_vm.Database.SetTags(_vm.SelectedTab.DocumentId,prompt.Value.Split(',',StringSplitOptions.RemoveEmptyEntries|StringSplitOptions.TrimEntries));LoadDocumentProperties();}
    private void Editor_SelectionChanged(object sender, RoutedEventArgs e)
    {
        var family = Editor.Selection.GetPropertyValue(TextElement.FontFamilyProperty); if (family != DependencyProperty.UnsetValue) FontFamilyBox.SelectedItem = family;
        var size = Editor.Selection.GetPropertyValue(TextElement.FontSizeProperty); if (size is double d) FontSizeBox.Text = Math.Round(d).ToString();
    }
    private void Save_Executed(object sender, ExecutedRoutedEventArgs e)=>SaveAllWithStatus();
    private void SaveAllWithStatus(){SaveStatusText.Text="Сохранение…";SaveStatusText.Foreground=new SolidColorBrush(Color.FromRgb(142,153,168));var success=_vm.SaveAll();SaveStatusText.Text=success?"Сохранено":"Не удалось сохранить";SaveStatusText.Foreground=new SolidColorBrush(success?Color.FromRgb(84,215,138):Color.FromRgb(239,103,103));}
    private void Find_Executed(object sender, ExecutedRoutedEventArgs e){if(_vm.SelectedTab is null)return;var prompt=new TextPrompt("Поиск в текущем документе",""){Owner=this};if(prompt.ShowDialog()==true&&!string.IsNullOrWhiteSpace(prompt.Value))_=ModernEditor.ExecuteAsync("find",new{query=prompt.Value});}
    private void FontFamily_Changed(object sender, SelectionChangedEventArgs e) { if(_updatingToolbarState)return;if(FontFamilyBox.SelectedItem is not FontFamily f)return;if(ModernEditor.Visibility==Visibility.Visible){_=ModernEditor.ExecuteAsync("fontFamily",new{family=f.Source});return;}if(Editor?.Selection is not null)Editor.Selection.ApplyPropertyValue(TextElement.FontFamilyProperty,f); }
    private void FontSize_Changed(object sender, SelectionChangedEventArgs e) { if(_updatingToolbarState)return;if(FontSizeBox.SelectedItem is double d)ApplyFontSize(d); }
    private void FontSize_LostFocus(object sender, KeyboardFocusChangedEventArgs e) { if(_updatingToolbarState)return;if(double.TryParse(FontSizeBox.Text,out var d)&&d is>=6 and<=200)ApplyFontSize(d); }
    private void ApplyFontSize(double size){if(ModernEditor.Visibility==Visibility.Visible){_=ModernEditor.ExecuteAsync("fontSize",new{size});return;}Editor.Selection.ApplyPropertyValue(TextElement.FontSizeProperty,size);}
    private async void Heading_Changed(object sender, SelectionChangedEventArgs e) { if(_resettingHeadingSelector)return;if(ModernEditor is not null&&ModernEditor.Visibility==Visibility.Visible&&HeadingBox.SelectedItem is ComboBoxItem modern&&int.TryParse(modern.Tag?.ToString(),out var level)){await ModernEditor.ExecuteAsync(level==0?"paragraph":"heading",level==0?null:new{level});if(level>0){_resettingHeadingSelector=true;HeadingBox.SelectedIndex=0;_resettingHeadingSelector=false;}return;}if (Editor is null || HeadingBox.SelectedItem is not ComboBoxItem item || !double.TryParse(item.Tag?.ToString(), out var size)) return; ApplyFontSize(size); Editor.Selection.ApplyPropertyValue(TextElement.FontWeightProperty, size > 13 ? FontWeights.Bold : FontWeights.Normal); }
    private async void ModernCommand_Click(object sender,RoutedEventArgs e){if(sender is FrameworkElement{Tag:string command})await ModernEditor.ExecuteAsync(command);}
    private async void Callout_Click(object sender,RoutedEventArgs e){if(sender is not FrameworkElement{Tag:string value})return;var parts=value.Split('|',2);await ModernEditor.ExecuteAsync("callout",new{kind=parts[0],label=parts.Length>1?parts[1]:"Примечание"});BlocksToggle.IsChecked=false;}
    private async void Collapsible_Click(object sender,RoutedEventArgs e){var prompt=new TextPrompt("Заголовок сворачиваемой секции","Подробнее"){Owner=this};if(prompt.ShowDialog()!=true)return;await ModernEditor.ExecuteAsync("collapsible",new{title=prompt.Value});BlocksToggle.IsChecked=false;}
    private async void Formula_Click(object sender,RoutedEventArgs e){var prompt=new TextPrompt("Формула KaTeX","E = mc^2"){Owner=this};if(prompt.ShowDialog()!=true)return;await ModernEditor.ExecuteAsync("formula",new{latex=prompt.Value});BlocksToggle.IsChecked=false;}
    private async void Mermaid_Click(object sender,RoutedEventArgs e){var prompt=new TextPrompt("Код Mermaid","flowchart LR\n  A[Клиент] --> B[Сервис]\n  B --> C[(SQLite)]",true){Owner=this};if(prompt.ShowDialog()!=true)return;await ModernEditor.ExecuteAsync("mermaid",new{code=prompt.Value});BlocksToggle.IsChecked=false;}
    private async void InsertDateTime_Click(object sender,RoutedEventArgs e){await ModernEditor.ExecuteAsync("insertDateTime",new{value=DateTime.Now.ToString("dd.MM.yyyy HH:mm")});BlocksToggle.IsChecked=false;}
    private async void Anchor_Click(object sender,RoutedEventArgs e){var prompt=new TextPrompt("Имя якоря","раздел-1"){Owner=this};if(prompt.ShowDialog()!=true||string.IsNullOrWhiteSpace(prompt.Value))return;await ModernEditor.ExecuteAsync("anchor",new{name=prompt.Value});BlocksToggle.IsChecked=false;}
    private async void SafeHtml_Click(object sender,RoutedEventArgs e){var prompt=new TextPrompt("Безопасный HTML","<div>Локальный HTML-блок</div>",true){Owner=this};if(prompt.ShowDialog()!=true)return;await ModernEditor.ExecuteAsync("safeHtml",new{code=prompt.Value});BlocksToggle.IsChecked=false;}
    private void Strike_Click(object sender, RoutedEventArgs e)
    {
        var current = Editor.Selection.GetPropertyValue(Inline.TextDecorationsProperty); var struck = current is TextDecorationCollection c && c.Any(x => x.Location == TextDecorationLocation.Strikethrough);
        Editor.Selection.ApplyPropertyValue(Inline.TextDecorationsProperty, struck ? null : TextDecorations.Strikethrough);
    }
    private void TextColor_Click(object sender, RoutedEventArgs e) => PickColor("Цвет текста", TextElement.ForegroundProperty, Colors.Black, false);
    private void BackColor_Click(object sender, RoutedEventArgs e) => PickColor("Цвет фона текста", TextElement.BackgroundProperty, Color.FromRgb(255, 242, 168), true);
    private void PickColor(string title, DependencyProperty property, Color fallback, bool allowNoColor)
    {
        var value = Editor.Selection.GetPropertyValue(property); var initial = value is SolidColorBrush brush ? brush.Color : fallback;
        var picker = new ColorPickerDialog(title, initial, allowNoColor) { Owner = this }; if (picker.ShowDialog() != true) return;
        if(ModernEditor.Visibility==Visibility.Visible){var css=picker.SelectedColor is Color c?$"#{c.R:X2}{c.G:X2}{c.B:X2}":"transparent";_=ModernEditor.ExecuteAsync(property==TextElement.ForegroundProperty?"color":"highlight",new{color=css});return;}Editor.Selection.ApplyPropertyValue(property, picker.SelectedColor is Color color ? new SolidColorBrush(color) : null); Editor.Focus();
    }
    private void InlineCode_Click(object sender, RoutedEventArgs e) { if(ModernEditor.Visibility==Visibility.Visible){_=ModernEditor.ExecuteAsync("code");return;}var active = Editor.Selection.GetPropertyValue(TextElement.FontFamilyProperty) is FontFamily f && (f.Source.Contains("Consolas") || f.Source.Contains("Cascadia")); if (active) { Editor.Selection.ClearAllProperties(); } else { Editor.Selection.ApplyPropertyValue(TextElement.FontFamilyProperty, new FontFamily("Cascadia Mono, Consolas")); Editor.Selection.ApplyPropertyValue(TextElement.BackgroundProperty, new SolidColorBrush(Color.FromRgb(235, 237, 240))); Editor.Selection.ApplyPropertyValue(TextElement.ForegroundProperty, new SolidColorBrush(Color.FromRgb(190, 45, 55))); } }
    private void CodeBlock_Click(object sender, RoutedEventArgs e) { if(ModernEditor.Visibility==Visibility.Visible){_=ModernEditor.ExecuteAsync("codeBlock");return;}var paragraphs = SelectedParagraphs().ToList(); var turnOff = paragraphs.All(p => p.Tag as string == "CodeBlock"); foreach (var p in paragraphs) { if (turnOff) ResetBlock(p); else { p.Tag = "CodeBlock"; p.FontFamily = new FontFamily("Cascadia Mono, Consolas"); p.Background = new SolidColorBrush(Color.FromRgb(242, 244, 247)); p.BorderBrush = new SolidColorBrush(Color.FromRgb(205, 210, 218)); p.BorderThickness = new Thickness(1); p.Padding = new Thickness(12, 8, 12, 8); p.Margin = new Thickness(0, 6, 0, 6); } } if (!turnOff) HighlightCodeBlocks(); Editor.Focus(); }
    private void Quote_Click(object sender, RoutedEventArgs e) { if(ModernEditor.Visibility==Visibility.Visible){_=ModernEditor.ExecuteAsync("blockquote");return;}var paragraphs = SelectedParagraphs().ToList(); var turnOff = paragraphs.All(p => p.Tag as string == "Quote"); foreach (var p in paragraphs) { if (turnOff) ResetBlock(p); else { p.Tag = "Quote"; p.BorderBrush = new SolidColorBrush(Color.FromRgb(54, 103, 214)); p.BorderThickness = new Thickness(4, 0, 0, 0); p.Padding = new Thickness(12, 4, 8, 4); p.Margin = new Thickness(6, 6, 0, 6); p.Background = new SolidColorBrush(Color.FromArgb(20, 54, 103, 214)); } } Editor.Focus(); }
    private void Spoiler_Click(object sender, RoutedEventArgs e) {if(ModernEditor.Visibility==Visibility.Visible){_=ModernEditor.ExecuteAsync("spoiler");return;}var existing = FindSpoiler(Editor.CaretPosition); if (existing is not null) { existing.Tag = null; existing.ClearValue(TextElement.ForegroundProperty); existing.ClearValue(TextElement.BackgroundProperty); return; } if (Editor.Selection.IsEmpty) return; var spoiler = new Span(Editor.Selection.Start, Editor.Selection.End) { Tag = "SpoilerHidden", Background = new SolidColorBrush(Color.FromRgb(55, 57, 62)), Foreground = Brushes.Transparent }; Editor.CaretPosition = spoiler.ElementEnd; }
    private static Span? FindSpoiler(TextPointer position) { DependencyObject? item = position.Parent as DependencyObject; while (item is not null) { if (item is Span { Tag: string tag } span && tag.StartsWith("Spoiler")) return span; item = item is FrameworkContentElement f ? f.Parent : null; } return null; }
    private void Editor_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e) { var point = Editor.GetPositionFromPoint(e.GetPosition(Editor), true); if (point is null || FindSpoiler(point) is not Span spoiler) return; var hidden = spoiler.Tag as string == "SpoilerHidden"; spoiler.Tag = hidden ? "SpoilerShown" : "SpoilerHidden"; spoiler.Foreground = hidden ? new SolidColorBrush(Color.FromRgb(245, 245, 245)) : Brushes.Transparent; e.Handled = true; }
    private void Editor_PreviewKeyDown(object sender, KeyEventArgs e) { var p = Editor.CaretPosition.Paragraph; if (p?.Tag is not string tag || tag is not ("CodeBlock" or "Quote")) return; var empty = string.IsNullOrWhiteSpace(new TextRange(p.ContentStart, p.ContentEnd).Text); if ((e.Key == Key.Back && empty) || (e.Key == Key.Enter && empty)) { ResetBlock(p); e.Handled = true; Editor.Focus(); } }
    private static void ResetBlock(Paragraph p) { p.Tag = null; p.ClearValue(TextElement.FontFamilyProperty); p.ClearValue(TextElement.ForegroundProperty); p.ClearValue(Block.BackgroundProperty); p.ClearValue(Block.BorderBrushProperty); p.ClearValue(Block.BorderThicknessProperty); p.ClearValue(Block.PaddingProperty); p.ClearValue(Block.MarginProperty); p.ToolTip = null; }
    private void Subscript_Click(object sender, RoutedEventArgs e) {if(ModernEditor.Visibility==Visibility.Visible){_=ModernEditor.ExecuteAsync("subscript");return;}ToggleBaseline(BaselineAlignment.Subscript);}
    private void Superscript_Click(object sender, RoutedEventArgs e) {if(ModernEditor.Visibility==Visibility.Visible){_=ModernEditor.ExecuteAsync("superscript");return;}ToggleBaseline(BaselineAlignment.Superscript);}
    private void ToggleBaseline(BaselineAlignment alignment) { var current = Editor.Selection.GetPropertyValue(Inline.BaselineAlignmentProperty); Editor.Selection.ApplyPropertyValue(Inline.BaselineAlignmentProperty, current is BaselineAlignment a && a == alignment ? BaselineAlignment.Baseline : alignment); }
    private void ChangeCase_Click(object sender, RoutedEventArgs e) {if(ModernEditor.Visibility==Visibility.Visible){_=ModernEditor.ExecuteAsync("changeCase");return;}if (Editor.Selection.IsEmpty) return; var text = Editor.Selection.Text; Editor.Selection.Text = text.Any(char.IsLower) ? text.ToUpperInvariant() : text.ToLowerInvariant(); }
    private void ClearFormatting_Click(object sender, RoutedEventArgs e) {if(ModernEditor.Visibility==Visibility.Visible){_=ModernEditor.ExecuteAsync("clear");return;}Editor.Selection.ClearAllProperties(); foreach (var p in SelectedParagraphs()) ResetBlock(p); }
    private void LineSpacing_Changed(object sender, SelectionChangedEventArgs e) { if(_updatingToolbarState||ModernEditor is null||sender is not ComboBox { SelectedItem: ComboBoxItem item }||!double.TryParse(item.Tag?.ToString(),System.Globalization.NumberStyles.Any,System.Globalization.CultureInfo.InvariantCulture,out var factor))return;if(ModernEditor.Visibility==Visibility.Visible){_=ModernEditor.ExecuteAsync("lineHeight",new{value=factor});return;}if(Editor is null)return;var size=Editor.Selection.GetPropertyValue(TextElement.FontSizeProperty) is double d?d:13;foreach(var p in SelectedParagraphs()){p.LineHeight=size*factor;p.LineStackingStrategy=LineStackingStrategy.BlockLineHeight;} }
    private void CodeLanguage_Changed(object sender,SelectionChangedEventArgs e){if(_updatingToolbarState||ModernEditor is null||sender is not ComboBox{SelectedItem:ComboBoxItem item})return;_=ModernEditor.ExecuteAsync("codeLanguage",new{language=item.Tag?.ToString()??"auto"});}
    private void ModernEditor_SelectionChanged(object? sender,JsonElement state){_updatingToolbarState=true;try{BoldToggle.IsChecked=state.TryGetProperty("bold",out var bold)&&bold.GetBoolean();ItalicToggle.IsChecked=state.TryGetProperty("italic",out var italic)&&italic.GetBoolean();UnderlineToggle.IsChecked=state.TryGetProperty("underline",out var underline)&&underline.GetBoolean();StrikeToggle.IsChecked=state.TryGetProperty("strike",out var strike)&&strike.GetBoolean();if(state.TryGetProperty("fontFamily",out var family)&&!string.IsNullOrWhiteSpace(family.GetString()))FontFamilyBox.SelectedItem=Fonts.SystemFontFamilies.FirstOrDefault(x=>x.Source.Equals(family.GetString(),StringComparison.OrdinalIgnoreCase));if(state.TryGetProperty("fontSize",out var size)){var value=size.GetString()?.Replace("px","");if(double.TryParse(value,System.Globalization.NumberStyles.Any,System.Globalization.CultureInfo.InvariantCulture,out var parsed))FontSizeBox.Text=parsed.ToString();}_spaceBefore=JsonNumber(state,"spaceBefore");_spaceAfter=JsonNumber(state,"spaceAfter");_firstIndent=JsonNumber(state,"firstIndent");_leftIndent=JsonNumber(state,"leftIndent");_rightIndent=JsonNumber(state,"rightIndent");_textDirection=state.TryGetProperty("textDirection",out var direction)?direction.GetString():null;_modernImageSelected=state.TryGetProperty("imageSelected",out var selected)&&selected.GetBoolean();_selectedImageSource=state.TryGetProperty("imageSrc",out var source)?source.GetString()??"":"";_selectedImageAlt=state.TryGetProperty("imageAlt",out var alt)?alt.GetString()??"":"";_selectedImageCaption=state.TryGetProperty("imageCaption",out var caption)?caption.GetString()??"":"";_selectedImageWrap=state.TryGetProperty("imageWrap",out var wrap)?wrap.GetString()??"none":"none";}finally{_updatingToolbarState=false;}}
    private static double? JsonNumber(JsonElement state,string name)=>state.TryGetProperty(name,out var value)&&value.ValueKind==JsonValueKind.Number&&value.TryGetDouble(out var number)?number:null;
    private async void LetterSpacing_Changed(object sender,SelectionChangedEventArgs e){if(_updatingToolbarState||ModernEditor is null||sender is not ComboBox{SelectedItem:ComboBoxItem item})return;double? value=null;if(!string.IsNullOrWhiteSpace(item.Tag?.ToString())&&double.TryParse(item.Tag!.ToString(),System.Globalization.NumberStyles.Float,System.Globalization.CultureInfo.InvariantCulture,out var parsed))value=parsed;await ModernEditor.ExecuteAsync("letterSpacing",new{value});}
    private async void ParagraphFormat_Click(object sender,RoutedEventArgs e){var dialog=new ParagraphFormatDialog(_spaceBefore,_spaceAfter,_firstIndent,_leftIndent,_rightIndent,_textDirection){Owner=this};if(dialog.ShowDialog()!=true)return;await ModernEditor.ExecuteAsync("paragraphLayout",new{spaceBefore=dialog.SpaceBefore,spaceAfter=dialog.SpaceAfter,firstIndent=dialog.FirstIndent,leftIndent=dialog.LeftIndent,rightIndent=dialog.RightIndent,textDirection=dialog.TextDirection});AdvancedToggle.IsChecked=false;}
    private async void ListFormat_Click(object sender,RoutedEventArgs e){var dialog=new ListFormatDialog{Owner=this};if(dialog.ShowDialog()!=true)return;await ModernEditor.ExecuteAsync("listOptions",new{kind=dialog.Kind,style=dialog.ListStyle,start=dialog.Start});AdvancedToggle.IsChecked=false;}
    private async void TextDirection_Click(object sender,RoutedEventArgs e){if(sender is not FrameworkElement{Tag:string direction})return;await ModernEditor.ExecuteAsync("textDirection",new{direction});}
    private void HighlightCodeBlocks()
    {
        if (_highlighting) return; _highlighting = true;
        try { foreach (var p in Editor.Document.Blocks.OfType<Paragraph>().Where(p => p.Tag as string == "CodeBlock")) HighlightParagraph(p); }
        finally { _highlighting = false; }
    }
    private static void HighlightParagraph(Paragraph p)
    {
        var text = new TextRange(p.ContentStart, p.ContentEnd).Text.TrimEnd('\r', '\n'); var language = DetectLanguage(text); p.ToolTip = "Определён язык: " + language;
        new TextRange(p.ContentStart, p.ContentEnd).ApplyPropertyValue(TextElement.ForegroundProperty, new SolidColorBrush(Color.FromRgb(30, 30, 30)));
        var keywords = language switch
        {
            "C#" => @"\b(abstract|as|async|await|base|bool|break|byte|case|catch|char|class|const|continue|decimal|default|delegate|do|double|else|enum|event|explicit|extern|false|finally|fixed|float|for|foreach|if|implicit|in|int|interface|internal|is|lock|long|namespace|new|null|object|operator|out|override|params|private|protected|public|readonly|record|ref|required|return|sbyte|sealed|short|sizeof|stackalloc|static|string|struct|switch|this|throw|true|try|typeof|uint|ulong|unchecked|unsafe|ushort|using|var|virtual|void|volatile|while|yield)\b",
            "Python" => @"\b(and|as|assert|async|await|break|class|continue|def|del|elif|else|except|False|finally|for|from|global|if|import|in|is|lambda|None|nonlocal|not|or|pass|raise|return|True|try|while|with|yield)\b",
            "JavaScript/TypeScript" => @"\b(async|await|break|case|catch|class|const|continue|debugger|default|delete|do|else|export|extends|false|finally|for|from|function|get|if|import|in|instanceof|interface|let|new|null|of|return|set|static|super|switch|this|throw|true|try|typeof|undefined|var|void|while|yield)\b",
            "SQL" => @"\b(ALTER|AND|AS|ASC|BEGIN|BETWEEN|BY|CASE|CREATE|DATABASE|DELETE|DESC|DISTINCT|DROP|ELSE|END|EXISTS|FROM|GROUP|HAVING|IN|INDEX|INNER|INSERT|INTO|IS|JOIN|LEFT|LIKE|LIMIT|NOT|NULL|ON|OR|ORDER|OUTER|PRIMARY|RIGHT|SELECT|SET|TABLE|THEN|UNION|UNIQUE|UPDATE|VALUES|WHEN|WHERE)\b",
            _ => @"\b(true|false|null|return|class|function|public|private|static|new|if|else|for|while)\b"
        };
        ApplySyntax(p, @"//.*$|/\*[\s\S]*?\*/|#.*$|--.*$", Color.FromRgb(0, 128, 0), RegexOptions.Multiline);
        ApplySyntax(p, "\"(?:\\\\.|[^\"\\\\])*\"|'(?:\\\\.|[^'\\\\])*'", Color.FromRgb(163, 21, 21));
        ApplySyntax(p, @"\b\d+(\.\d+)?\b", Color.FromRgb(9, 134, 88));
        ApplySyntax(p, keywords, Color.FromRgb(0, 0, 255), language == "SQL" ? RegexOptions.IgnoreCase : RegexOptions.None);
        ApplySyntax(p, @"\b[A-Z][A-Za-z0-9_]*(?=\s*[<(])", Color.FromRgb(43, 145, 175));
    }
    private static void ApplySyntax(Paragraph paragraph, string pattern, Color color, RegexOptions options = RegexOptions.None)
    {
        var pointer = paragraph.ContentStart; while (pointer is not null && pointer.CompareTo(paragraph.ContentEnd) < 0) { var chunk = pointer.GetTextInRun(LogicalDirection.Forward); if (chunk.Length == 0) { pointer = pointer.GetNextContextPosition(LogicalDirection.Forward); continue; } foreach (Match match in Regex.Matches(chunk, pattern, options)) { var start = pointer.GetPositionAtOffset(match.Index); var end = start?.GetPositionAtOffset(match.Length); if (start is not null && end is not null) new TextRange(start, end).ApplyPropertyValue(TextElement.ForegroundProperty, new SolidColorBrush(color)); } pointer = pointer.GetPositionAtOffset(chunk.Length); }
    }
    private static string DetectLanguage(string text)
    {
        if (Regex.IsMatch(text, @"\b(using\s+System|namespace\s+\w+|Console\.Write|public\s+(class|record|interface)|string\[\]|async\s+Task)")) return "C#";
        if (Regex.IsMatch(text, @"(^|\n)\s*(def|import|from)\s+|:\s*(#.*)?$|\bprint\s*\(", RegexOptions.Multiline)) return "Python";
        if (Regex.IsMatch(text, @"\b(const|let|function|interface)\s+\w+|=>|console\.log|document\.")) return "JavaScript/TypeScript";
        if (Regex.IsMatch(text, @"\b(SELECT|INSERT|UPDATE|DELETE|CREATE\s+TABLE|FROM|WHERE)\b", RegexOptions.IgnoreCase)) return "SQL";
        if (Regex.IsMatch(text, @"<[/!?]?[A-Za-z][^>]*>")) return "HTML/XML";
        if (Regex.IsMatch(text, @"^\s*[\[{].*[:\]}]", RegexOptions.Singleline)) return "JSON";
        return "обычный код";
    }
    private IEnumerable<Paragraph> SelectedParagraphs() { var start = Editor.Selection.Start.Paragraph; var end = Editor.Selection.End.Paragraph; if (start is null) yield break; for (var p = start; p is not null; p = p.NextBlock as Paragraph) { yield return p; if (p == end) break; } }

    private void Image_Click(object sender, RoutedEventArgs e)
    {
        if (_vm.SelectedTab is null) return; var dialog = new OpenFileDialog { Filter = "Изображения|*.png;*.jpg;*.jpeg;*.gif;*.bmp;*.tif;*.tiff" }; if (dialog.ShowDialog(this) != true) return;
        try
        {
            var asset=StoreAsset(dialog.FileName);var name=asset.StoredName;var stored=Path.Combine(AppPaths.Assets,name);_vm.Database.RegisterAttachment(_vm.SelectedTab.DocumentId,Path.GetFileName(dialog.FileName),name,"image/"+Path.GetExtension(name).TrimStart('.'),asset.Size,asset.Hash);if(ModernEditor.Visibility==Visibility.Visible){_=ModernEditor.ExecuteAsync("image",new{src="https://assets.local/"+name,alt=Path.GetFileName(dialog.FileName)});LoadDocumentProperties();return;}
            var bitmap = new BitmapImage(); bitmap.BeginInit(); bitmap.UriSource = new Uri(stored); bitmap.CacheOption = BitmapCacheOption.OnLoad; bitmap.EndInit(); bitmap.Freeze();
            var image = new Image { Source = bitmap, Stretch = Stretch.Uniform, MaxWidth = 900 }; if (bitmap.PixelWidth > 700) image.Width = 700;
            new InlineUIContainer(image, Editor.CaretPosition); Editor.CaretPosition = Editor.CaretPosition.GetNextInsertionPosition(LogicalDirection.Forward) ?? Editor.CaretPosition; LoadDocumentProperties(); Editor.Focus();
        }
        catch (Exception ex) { LogService.Error("Ошибка вставки изображения", ex); MessageBox.Show("Не удалось вставить изображение: " + ex.Message); }
    }
    private (string StoredName,string Hash,long Size) StoreAsset(string source){using var input=File.OpenRead(source);var hash=Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(input));var existing=_vm.Database.FindStoredAttachmentByHash(hash);if(existing is not null&&File.Exists(Path.Combine(AppPaths.Assets,existing)))return(existing,hash,new FileInfo(Path.Combine(AppPaths.Assets,existing)).Length);var extension=Path.GetExtension(source).ToLowerInvariant();var name=hash.ToLowerInvariant()+extension;var target=Path.Combine(AppPaths.Assets,name);Directory.CreateDirectory(AppPaths.Assets);if(!File.Exists(target))File.Copy(source,target);return(name,hash,new FileInfo(target).Length);}
    private (string StoredName,string Hash,long Size) StoreAssetBytes(byte[] bytes,string fileName,string mime){var hash=Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes));var existing=_vm.Database.FindStoredAttachmentByHash(hash);if(existing is not null&&File.Exists(Path.Combine(AppPaths.Assets,existing)))return(existing,hash,bytes.LongLength);var extension=Path.GetExtension(fileName).ToLowerInvariant();if(!Regex.IsMatch(extension,@"^\.[a-z0-9]{1,10}$"))extension=mime.ToLowerInvariant() switch{"image/png"=>".png","image/jpeg"=>".jpg","image/gif"=>".gif","image/webp"=>".webp","application/pdf"=>".pdf",_=>".bin"};var name=hash.ToLowerInvariant()+extension;var target=Path.Combine(AppPaths.Assets,name);Directory.CreateDirectory(AppPaths.Assets);if(!File.Exists(target)){var temporary=target+".tmp";File.WriteAllBytes(temporary,bytes);File.Move(temporary,target,true);}return(name,hash,bytes.LongLength);}
    private Image? SelectedImage() => Editor.CaretPosition.GetAdjacentElement(LogicalDirection.Forward) as Image ?? Editor.CaretPosition.GetAdjacentElement(LogicalDirection.Backward) as Image;
    private void ImageSmaller_Click(object sender, RoutedEventArgs e) {if(ModernEditor.Visibility==Visibility.Visible){_=ModernEditor.ExecuteAsync("imageScale",new{factor=.8});return;}var i=SelectedImage();if(i is not null)i.Width=Math.Max(40,(double.IsNaN(i.Width)?i.ActualWidth:i.Width)*.8);}
    private void ImageLarger_Click(object sender, RoutedEventArgs e) {if(ModernEditor.Visibility==Visibility.Visible){_=ModernEditor.ExecuteAsync("imageScale",new{factor=1.2});return;}var i=SelectedImage();if(i is not null)i.Width=Math.Min(1600,(double.IsNaN(i.Width)?i.ActualWidth:i.Width)*1.2);}
    private async void ImageRotate_Click(object sender,RoutedEventArgs e){if(!EnsureModernImage()||sender is not FrameworkElement element||!int.TryParse(element.Tag?.ToString(),out var delta))return;await ModernEditor.ExecuteAsync("imageRotate",new{delta});}
    private async void ImageAlign_Click(object sender,RoutedEventArgs e){if(!EnsureModernImage()||sender is not FrameworkElement{Tag:string align})return;await ModernEditor.ExecuteAsync("imageAlign",new{align});}
    private async void ImageAlt_Click(object sender,RoutedEventArgs e){if(!EnsureModernImage())return;var prompt=new TextPrompt("Альтернативный текст изображения",_selectedImageAlt){Owner=this};if(prompt.ShowDialog()!=true)return;await ModernEditor.ExecuteAsync("imageAlt",new{alt=prompt.Value});_selectedImageAlt=prompt.Value;}
    private async void ImageCaption_Click(object sender,RoutedEventArgs e){if(!EnsureModernImage())return;var prompt=new TextPrompt("Подпись под изображением",_selectedImageCaption){Owner=this};if(prompt.ShowDialog()!=true)return;await ModernEditor.ExecuteAsync("imageCaption",new{caption=prompt.Value});_selectedImageCaption=prompt.Value;}
    private async void ImageWrap_Click(object sender,RoutedEventArgs e){if(!EnsureModernImage())return;var value=(sender as FrameworkElement)?.Tag?.ToString()??"none";await ModernEditor.ExecuteAsync("imageWrap",new{wrap=value});_selectedImageWrap=value;}
    private async void ImageCrop_Click(object sender,RoutedEventArgs e)
    {
        if(!EnsureModernImage())return;var prompt=new TextPrompt("Обрезка: сверху, справа, снизу, слева (%)","0, 0, 0, 0"){Owner=this};if(prompt.ShowDialog()!=true)return;var parts=prompt.Value.Split([',',';'],StringSplitOptions.TrimEntries);if(parts.Length!=4||parts.Any(x=>!double.TryParse(x,System.Globalization.NumberStyles.Float,System.Globalization.CultureInfo.CurrentCulture,out _))){MessageBox.Show(this,"Введите четыре числа от 0 до 45.","Обрезка");return;}var values=parts.Select(x=>double.Parse(x,System.Globalization.CultureInfo.CurrentCulture)).ToArray();if(values.Any(x=>x is <0 or >45)||values[0]+values[2]>=90||values[1]+values[3]>=90){MessageBox.Show(this,"Каждое значение должно быть от 0 до 45, изображение не может быть обрезано полностью.","Обрезка");return;}await ModernEditor.ExecuteAsync("imageCrop",new{top=values[0],right=values[1],bottom=values[2],left=values[3]});
    }
    private async void ImageCopy_Click(object sender,RoutedEventArgs e){if(!EnsureModernImage())return;await ModernEditor.ExecuteAsync("imageCopy");}
    private async void ImageCompress_Click(object sender,RoutedEventArgs e)
    {
        if(!EnsureModernImage()||_vm.SelectedTab is null)return;var prompt=new TextPrompt("Качество JPEG (30–100)","82"){Owner=this};if(prompt.ShowDialog()!=true)return;if(!int.TryParse(prompt.Value,out var quality)||quality is <30 or >100){MessageBox.Show(this,"Качество должно быть от 30 до 100.","Сжатие");return;}
        try
        {
            var source=ResolveAssetPath(_selectedImageSource)??throw new FileNotFoundException("Исходный файл изображения не найден.");BitmapDecoder decoder;using(var stream=File.OpenRead(source))decoder=BitmapDecoder.Create(stream,BitmapCreateOptions.PreservePixelFormat,BitmapCacheOption.OnLoad);var encoder=new JpegBitmapEncoder{QualityLevel=quality};encoder.Frames.Add(BitmapFrame.Create(decoder.Frames[0]));using var output=new MemoryStream();encoder.Save(output);var fileName=Path.GetFileNameWithoutExtension(source)+$"-q{quality}.jpg";var asset=StoreAssetBytes(output.ToArray(),fileName,"image/jpeg");if(!_vm.Database.GetAttachments(_vm.SelectedTab.DocumentId).Any(x=>x.Sha256.Equals(asset.Hash,StringComparison.OrdinalIgnoreCase)))_vm.Database.RegisterAttachment(_vm.SelectedTab.DocumentId,fileName,asset.StoredName,"image/jpeg",asset.Size,asset.Hash);await ModernEditor.ExecuteAsync("imageReplace",new{src="https://assets.local/"+asset.StoredName,alt=_selectedImageAlt});LoadDocumentProperties();
        }
        catch(Exception ex){LogService.Error("Не удалось сжать изображение",ex);MessageBox.Show(this,"Не удалось сжать изображение: "+ex.Message,"Сжатие",MessageBoxButton.OK,MessageBoxImage.Error);}
    }
    private static string? ResolveAssetPath(string source){if(!Uri.TryCreate(source,UriKind.Absolute,out var uri)||!uri.Host.Equals("assets.local",StringComparison.OrdinalIgnoreCase))return null;var file=Path.GetFileName(Uri.UnescapeDataString(uri.AbsolutePath));var root=Path.GetFullPath(AppPaths.Assets).TrimEnd(Path.DirectorySeparatorChar)+Path.DirectorySeparatorChar;var path=Path.GetFullPath(Path.Combine(AppPaths.Assets,file));return path.StartsWith(root,StringComparison.OrdinalIgnoreCase)&&File.Exists(path)?path:null;}
    private async void ImageReplace_Click(object sender,RoutedEventArgs e)
    {
        if(!EnsureModernImage()||_vm.SelectedTab is null)return;var dialog=new OpenFileDialog{Title="Заменить изображение",Filter="Изображения|*.png;*.jpg;*.jpeg;*.gif;*.bmp;*.tif;*.tiff;*.webp"};if(dialog.ShowDialog(this)!=true)return;
        try{var asset=StoreAsset(dialog.FileName);var fileName=Path.GetFileName(dialog.FileName);if(!_vm.Database.GetAttachments(_vm.SelectedTab.DocumentId).Any(x=>x.Sha256.Equals(asset.Hash,StringComparison.OrdinalIgnoreCase)))_vm.Database.RegisterAttachment(_vm.SelectedTab.DocumentId,fileName,asset.StoredName,GuessMimeType(dialog.FileName),asset.Size,asset.Hash);await ModernEditor.ExecuteAsync("imageReplace",new{src="https://assets.local/"+asset.StoredName,alt=string.IsNullOrWhiteSpace(_selectedImageAlt)?fileName:_selectedImageAlt});LoadDocumentProperties();}
        catch(Exception ex){LogService.Error("Не удалось заменить изображение",ex);MessageBox.Show(this,"Не удалось заменить изображение: "+ex.Message,"Изображение",MessageBoxButton.OK,MessageBoxImage.Error);}
    }
    private async void ImageOpen_Click(object sender,RoutedEventArgs e){if(!EnsureModernImage())return;await ModernEditor.ExecuteAsync("imageOpen");}
    private bool EnsureModernImage(){if(ModernEditor.Visibility==Visibility.Visible&&_modernImageSelected)return true;MessageBox.Show(this,"Сначала щёлкните по изображению в документе.","Изображение",MessageBoxButton.OK,MessageBoxImage.Information);return false;}
    private void ModernEditor_AssetOpenRequested(object? sender,string source)
    {
        if(!Uri.TryCreate(source,UriKind.Absolute,out var uri)||!uri.Host.Equals("assets.local",StringComparison.OrdinalIgnoreCase))return;var relative=Uri.UnescapeDataString(uri.AbsolutePath.TrimStart('/')).Replace('/',Path.DirectorySeparatorChar);var root=Path.GetFullPath(AppPaths.Assets).TrimEnd(Path.DirectorySeparatorChar)+Path.DirectorySeparatorChar;var path=Path.GetFullPath(Path.Combine(AppPaths.Assets,relative));if(!path.StartsWith(root,StringComparison.OrdinalIgnoreCase)||!File.Exists(path))return;Process.Start(new ProcessStartInfo(path){UseShellExecute=true});
    }
    private async void ModernEditor_LinkOpenRequested(object? sender,string value)
    {
        try
        {
            if(value.StartsWith("masterdoc://",StringComparison.OrdinalIgnoreCase))
            {
                if(!Uri.TryCreate(value,UriKind.Absolute,out var internalUri)||!internalUri.Host.Equals("document",StringComparison.OrdinalIgnoreCase)){MessageBox.Show(this,"Некорректная внутренняя ссылка.","Ссылка",MessageBoxButton.OK,MessageBoxImage.Warning);return;}
                var guid=internalUri.AbsolutePath.Trim('/');var node=_vm.Database.FindDocumentByGuid(guid);if(node is null){MessageBox.Show(this,"Связанный документ удалён или не существует.","Битая ссылка",MessageBoxButton.OK,MessageBoxImage.Warning);return;}_vm.Open(node);ShowSelectedTab();if(internalUri.Fragment.Length>1)await ModernEditor.ExecuteAsync("gotoFragment",new{fragment=Uri.UnescapeDataString(internalUri.Fragment[1..])});return;
            }
            if(!Uri.TryCreate(value,UriKind.Absolute,out var uri))return;
            if(uri.IsFile&&MessageBox.Show(this,$"Открыть локальный файл?\n{uri.LocalPath}","Внешний файл",MessageBoxButton.YesNo,MessageBoxImage.Warning)!=MessageBoxResult.Yes)return;
            if(uri.Scheme is not ("http" or "https" or "mailto" or "file")){MessageBox.Show(this,"Этот тип внешней ссылки заблокирован.","Безопасность",MessageBoxButton.OK,MessageBoxImage.Warning);return;}Process.Start(new ProcessStartInfo(uri.IsFile?uri.LocalPath:uri.AbsoluteUri){UseShellExecute=true});
        }
        catch(Exception ex){LogService.Error("Не удалось открыть ссылку",ex);MessageBox.Show(this,"Не удалось открыть ссылку: "+ex.Message,"Ссылка",MessageBoxButton.OK,MessageBoxImage.Error);}
    }
    private void ZoomOut_Click(object sender,RoutedEventArgs e)=>SetZoom(_zoom-.1);
    private void ZoomIn_Click(object sender,RoutedEventArgs e)=>SetZoom(_zoom+.1);
    private void ZoomReset_Click(object sender,MouseButtonEventArgs e)=>SetZoom(1);
    private void SetZoom(double value){if(_vm.SelectedTab is null)return;_zoom=Math.Clamp(Math.Round(value,1),.5,2);ZoomText.Text=$"{_zoom*100:0}%";_vm.Database.SetZoom(_vm.SelectedTab.DocumentId,_zoom);_=ModernEditor.ExecuteAsync("setZoom",new{value=_zoom});}
    private void Link_Click(object sender, RoutedEventArgs e)
    {
        if (ModernEditor.Visibility!=Visibility.Visible&&Editor.Selection.IsEmpty) { MessageBox.Show("Сначала выделите текст ссылки."); return; } var p = new TextPrompt("Адрес гиперссылки", "https://") { Owner = this }; if (p.ShowDialog() != true) return;
        if (!Uri.TryCreate(p.Value, UriKind.Absolute, out var uri)) { MessageBox.Show("Некорректный адрес."); return; }
        try { if(ModernEditor.Visibility==Visibility.Visible){_=ModernEditor.ExecuteAsync("link",new{href=uri.AbsoluteUri});return;}var link = new Hyperlink(Editor.Selection.Start, Editor.Selection.End) { NavigateUri = uri, Foreground = Brushes.RoyalBlue, TextDecorations = TextDecorations.Underline }; }
        catch (Exception ex) { MessageBox.Show("Не удалось создать ссылку: " + ex.Message); }
    }
    private void InternalLink_Click(object sender,RoutedEventArgs e)
    {
        if(_vm.SelectedTab is null)return;var picker=new DocumentPickerDialog(_vm.Database){Owner=this};if(picker.ShowDialog()!=true||picker.SelectedDocument is null)return;_=ModernEditor.ExecuteAsync("link",new{href="masterdoc://document/"+picker.SelectedDocument.Guid});
    }
    private void Table_Click(object sender, RoutedEventArgs e)
    {
        var p = new TextPrompt("Размер таблицы (например 3x4)", "3x3") { Owner = this }; if (p.ShowDialog() != true) return; var parts = p.Value.ToLowerInvariant().Split('x', 'х'); if (parts.Length != 2 || !int.TryParse(parts[0], out var rows) || !int.TryParse(parts[1], out var cols) || rows is < 1 or > 30 || cols is < 1 or > 20) { MessageBox.Show("Введите размер как строки x столбцы."); return; }
        if(ModernEditor.Visibility==Visibility.Visible){_=ModernEditor.ExecuteAsync("table",new{rows,cols});return;}var table = new Table { CellSpacing = 0, BorderBrush = Brushes.Gray, BorderThickness = new Thickness(1) }; for (var c = 0; c < cols; c++) table.Columns.Add(new TableColumn()); var group = new TableRowGroup(); table.RowGroups.Add(group);
        for (var r = 0; r < rows; r++) { var row = new TableRow(); for (var c = 0; c < cols; c++) row.Cells.Add(new TableCell(new Paragraph(new Run(r == 0 ? $"Столбец {c + 1}" : ""))) { BorderBrush = Brushes.Gray, BorderThickness = new Thickness(.5), Padding = new Thickness(5) }); group.Rows.Add(row); }
        var paragraph = Editor.CaretPosition.Paragraph; if (paragraph is not null) Editor.Document.Blocks.InsertAfter(paragraph, table); else Editor.Document.Blocks.Add(table);
    }

    private void Tree_MouseMove(object sender, MouseEventArgs e) { if (e.LeftButton != MouseButtonState.Pressed || _vm.SelectedNode is null || (e.GetPosition(Tree) - _dragStart).Length < 8) return; DragDrop.DoDragDrop(Tree, _vm.SelectedNode, DragDropEffects.Move); }
    protected override void OnPreviewMouseLeftButtonDown(MouseButtonEventArgs e) { _dragStart = e.GetPosition(Tree); base.OnPreviewMouseLeftButtonDown(e); }
    private void Tree_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(typeof(NodeItem)) is not NodeItem source) return; var element = Tree.InputHitTest(e.GetPosition(Tree)) as DependencyObject; while (element is not null && element is not TreeViewItem) element = VisualTreeHelper.GetParent(element); var target = (element as TreeViewItem)?.DataContext as NodeItem;
        var parent = target?.IsFolder == true ? target.Id : target?.ParentId; if (parent == source.Id || IsDescendant(source, parent)) { MessageBox.Show("Нельзя переместить папку внутрь самой себя."); return; } _vm.Database.Move(source.Id, parent); _vm.ReloadTree();
    }
    private static bool IsDescendant(NodeItem source, long? id) => id.HasValue && (source.Children.Any(c => c.Id == id) || source.Children.Any(c => IsDescendant(c, id)));

    private void Backup_Click(object sender, RoutedEventArgs e) { try { _vm.SaveAll();string? password=null;if(_settingsService.Load().EncryptManualBackups){var prompt=new PasswordDialog("Пароль для новой резервной копии"){Owner=this};if(prompt.ShowDialog()!=true)return;password=prompt.Value;}var path = _vm.Backups.CreateBackup(false,password); MessageBox.Show("Резервная копия создана:\n" + path); } catch (Exception ex) { LogService.Error("Ошибка резервного копирования", ex); MessageBox.Show("Не удалось создать копию: " + ex.Message); } }
    private void Restore_Click(object sender, RoutedEventArgs e) { var d = new OpenFileDialog { Filter = "Резервные копии (*.mdbackup)|*.mdbackup|Старые ZIP-копии (*.zip)|*.zip" }; if (d.ShowDialog(this) != true || MessageBox.Show("Текущие данные будут заменены. Продолжить?", "Восстановление", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return; try { string? password=null;if(BackupService.IsEncrypted(d.FileName)){var prompt=new PasswordDialog("Пароль зашифрованной резервной копии"){Owner=this};if(prompt.ShowDialog()!=true)return;password=prompt.Value;}_vm.SaveAll(); _vm.Backups.Restore(d.FileName,password); MessageBox.Show("Данные восстановлены. Приложение будет закрыто; запустите его снова."); Close(); } catch (Exception ex) { LogService.Error("Ошибка восстановления", ex); MessageBox.Show("Не удалось восстановить: " + ex.Message); } }
    private void Export_Click(object sender, RoutedEventArgs e) { var d = new OpenFolderDialog { Title = "Выберите папку экспорта" }; if (d.ShowDialog(this) != true) return; try { _vm.SaveAll(); var target = Path.Combine(d.FolderName, "MasterDocumentation-Export-" + DateTime.Now.ToString("yyyyMMdd-HHmmss")); _vm.Backups.Export(target); MessageBox.Show("Экспорт создан:\n" + target); } catch (Exception ex) { MessageBox.Show("Ошибка экспорта: " + ex.Message); } }
    private void ExportDocument_Click(object sender, RoutedEventArgs e) { if (_vm.SelectedTab is null) return; var dialog=new SaveFileDialog { Title="Экспорт документа", FileName=_vm.SelectedTab.Title, Filter="PDF (*.pdf)|*.pdf|Word (*.docx)|*.docx|HTML (*.html)|*.html|Markdown (*.md)|*.md|Текст (*.txt)|*.txt", InitialDirectory=AppPaths.Exports }; if(dialog.ShowDialog(this)!=true)return; try { _vm.Save(_vm.SelectedTab);var exporter=new DocumentExportService();if(!string.IsNullOrWhiteSpace(_vm.SelectedTab.Html))exporter.Export(_vm.SelectedTab.Html,_vm.SelectedTab.PlainText,_vm.SelectedTab.Title,dialog.FileName);else exporter.Export(Editor.Document,_vm.SelectedTab.Title,dialog.FileName); MessageBox.Show(this,"Документ экспортирован:\n"+dialog.FileName,"Экспорт"); } catch(Exception ex){ MessageBox.Show(this,"Ошибка экспорта: "+ex.Message,"Ошибка",MessageBoxButton.OK,MessageBoxImage.Error); } }
    private async void Print_Click(object sender, RoutedEventArgs e) { if(_vm.SelectedTab is null)return;if(ModernEditor.Visibility==Visibility.Visible){try{await ModernEditor.PrintAsync();}catch(Exception ex){MessageBox.Show(this,"Не удалось открыть печать: "+ex.Message,"Печать",MessageBoxButton.OK,MessageBoxImage.Error);}return;} var dialog=new PrintDialog(); if(dialog.ShowDialog()!=true)return; var paginator=((IDocumentPaginatorSource)Editor.Document).DocumentPaginator; dialog.PrintDocument(paginator,"MasterDocumentation — "+_vm.SelectedTab.Title); }
    private void Duplicate_Click(object sender, RoutedEventArgs e) { if(_vm.SelectedTab is null)return; _vm.Save(_vm.SelectedTab); var id=_vm.Database.Duplicate(_vm.SelectedTab.DocumentId); _vm.ReloadTree(); UpdateNavigationCounts(); var node=FindNode(_vm.Nodes,id); if(node is not null){_vm.Open(node);ShowSelectedTab();} }
    private void OpenBackups_Click(object sender, RoutedEventArgs e) { Directory.CreateDirectory(AppPaths.Backups); Process.Start(new ProcessStartInfo("explorer.exe",$"\"{AppPaths.Backups}\""){UseShellExecute=true}); }
    private void History_Click(object sender,RoutedEventArgs e){if(_vm.SelectedTab is null)return;_vm.Save(_vm.SelectedTab);var dialog=new HistoryWindow(_vm.Database,_vm.SelectedTab.DocumentId){Owner=this};if(dialog.ShowDialog()!=true)return;var restored=_vm.Database.LoadDocument(_vm.SelectedTab.DocumentId);var structured=_vm.Database.LoadStructuredContent(_vm.SelectedTab.DocumentId);_vm.SelectedTab.Document=restored.Document;_vm.SelectedTab.EditorJson=structured.Json;_vm.SelectedTab.Html=structured.Html;_vm.SelectedTab.PlainText=structured.PlainText;_vm.SelectedTab.ModifiedAt=restored.Modified;_vm.SelectedTab.IsDirty=false;ShowSelectedTab();}
    private void DocumentProperties_Click(object sender,RoutedEventArgs e){if(_vm.SelectedTab is null)return;_vm.Save(_vm.SelectedTab);if(new DocumentPropertiesWindow(_vm.Database,_vm.SelectedTab.DocumentId){Owner=this}.ShowDialog()==true)LoadDocumentProperties();}
    private void AutoBackupIfNeeded() { try { if (!DateTime.TryParse(_vm.Database.GetSetting("LastAutoBackup"), out var last) || DateTime.UtcNow - last > TimeSpan.FromDays(1)) { _vm.Backups.CreateBackup(true); _vm.Database.SetSetting("LastAutoBackup", DateTime.UtcNow.ToString("O")); } } catch (Exception ex) { LogService.Error("Ошибка автоматической резервной копии", ex); } }
    private void RecoverEmergencyDrafts()
    {
        var recoverable=new List<EmergencyDraft>();
        foreach(var draft in DraftRecoveryService.ReadAll())
        {
            try{var saved=_vm.Database.LoadStructuredContent(draft.DocumentId);if(saved.Json==draft.Json&&saved.Html==draft.Html){DraftRecoveryService.Delete(draft.DocumentId);continue;}recoverable.Add(draft);}
            catch{DraftRecoveryService.Delete(draft.DocumentId);}
        }
        if(recoverable.Count==0)return;
        var names=string.Join("\n",recoverable.Take(5).Select(x=>"• "+x.Title));if(recoverable.Count>5)names+=$"\n…и ещё {recoverable.Count-5}";
        var result=MessageBox.Show(this,$"Найдены несохранённые аварийные черновики ({recoverable.Count}):\n\n{names}\n\nВосстановить их?\n«Нет» — удалить черновики, «Отмена» — оставить до следующего запуска.","Восстановление после сбоя",MessageBoxButton.YesNoCancel,MessageBoxImage.Warning);
        if(result==MessageBoxResult.Cancel)return;
        foreach(var draft in recoverable){try{if(result==MessageBoxResult.Yes)_vm.Database.SaveStructuredContent(draft.DocumentId,draft.Json,draft.Html,draft.PlainText);DraftRecoveryService.Delete(draft.DocumentId);}catch(Exception ex){LogService.Error("Не удалось восстановить аварийный черновик "+draft.DocumentId,ex);}}
        if(result==MessageBoxResult.Yes)_vm.ReloadTree();
    }
    private void RestoreSession() { var settings=_settingsService.Load(); if(settings.StartupBehavior=="Открывать последнюю сессию"){foreach(var id in settings.OpenDocumentIds){var node=_vm.Database.FindNode(id);if(node is not null)_vm.Open(node);}if(settings.SelectedDocumentId is long selected){var tab=_vm.Tabs.FirstOrDefault(x=>x.DocumentId==selected);if(tab is not null)_vm.SelectedTab=tab;}}ShowSelectedTab(); }
    private void RestoreWindow() { if (double.TryParse(_vm.Database.GetSetting("Width"), out var w)) Width = Math.Max(MinWidth, w); if (double.TryParse(_vm.Database.GetSetting("Height"), out var h)) Height = Math.Max(MinHeight, h); if (double.TryParse(_vm.Database.GetSetting("Left"), out var l) && double.TryParse(_vm.Database.GetSetting("Top"), out var t)) { WindowStartupLocation = WindowStartupLocation.Manual; Left = l; Top = t; } }
    private void RestorePanelLayout(){if(double.TryParse(_vm.Database.GetSetting("LibraryWidth"),out var library))LibraryColumn.Width=new GridLength(Math.Max(190,library));if(double.TryParse(_vm.Database.GetSetting("StructureWidth"),out var structure))StructureColumn.Width=new GridLength(Math.Max(160,structure));if(double.TryParse(_vm.Database.GetSetting("PropertiesWidth"),out var properties))PropertiesColumn.Width=new GridLength(Math.Max(210,properties));}
    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if(_closeCommitted)return;

        // Closing is synchronous. Cancel this attempt first and finish saving outside
        // the event; otherwise a synchronously completed editor snapshot can call
        // Close() while WPF is still inside Window.InternalClose.
        e.Cancel=true;
        if(_closeInProgress)return;
        if(_settingsView is not null&&SettingsHost.Visibility==Visibility.Visible&&!_settingsView.RequestClose())return;

        _closeInProgress=true;
        _saveTimer.Stop();
        _=CompleteCloseAsync();
    }

    private async Task CompleteCloseAsync()
    {
        try
        {
            try
            {
                if(_vm.SelectedTab is not null&&ModernEditor.Visibility==Visibility.Visible)
                {
                    using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(2));
                    var snapshot=await ModernEditor.CaptureContentAsync(timeout.Token);
                    var tab=_vm.Tabs.FirstOrDefault(x=>x.DocumentId==snapshot.DocumentId);
                    if(tab is not null)ApplyEditorContent(tab,snapshot,false);
                }
            }
            catch(Exception ex){LogService.Error("Не удалось получить финальный снимок редактора",ex);}

            _vm.SaveAll();
            SaveSessionAndWindowState();
        }
        catch(Exception ex)
        {
            LogService.Error("Не удалось завершить сохранение при закрытии",ex);
            MessageBox.Show(this,"Не удалось полностью сохранить состояние приложения:\n"+ex.Message,"Закрытие приложения",MessageBoxButton.OK,MessageBoxImage.Warning);
        }
        finally
        {
            _closeCommitted=true;
            _=Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background,new Action(Close));
        }
    }
    private void SaveSessionAndWindowState(){var settings=_settingsService.Load();settings.OpenDocumentIds=_vm.Tabs.Select(x=>x.DocumentId).ToList();settings.SelectedDocumentId=_vm.SelectedTab?.DocumentId;_settingsService.Save(settings);_vm.Database.SetSetting("LibraryWidth",LibraryColumn.ActualWidth.ToString());_vm.Database.SetSetting("StructureWidth",StructureColumn.ActualWidth.ToString());_vm.Database.SetSetting("PropertiesWidth",PropertiesColumn.ActualWidth.ToString());if(WindowState==WindowState.Normal){_vm.Database.SetSetting("Width",Width.ToString());_vm.Database.SetSetting("Height",Height.ToString());_vm.Database.SetSetting("Left",Left.ToString());_vm.Database.SetSetting("Top",Top.ToString());}}
    private void Exit_Click(object sender, RoutedEventArgs e) => Close();
    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void Maximize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        if(SettingsHost.Visibility==Visibility.Visible){if(_settingsView?.RequestClose()!=false)HideSettings();return;}
        _vm.SaveAll();_settingsView=new SettingsView(_vm.Database,_vm.Backups,_settingsService);_settingsView.SettingsSaved+=ApplySavedSettings;_settingsView.CloseRequested+=HideSettings;SettingsHost.Content=_settingsView;SettingsHost.Visibility=Visibility.Visible;Tabs.Visibility=Visibility.Collapsed;SettingsTabHeader.Visibility=Visibility.Visible;
    }
    private void ApplySavedSettings(ApplicationSettings settings){_settings=settings;_saveTimer.Interval=TimeSpan.FromSeconds(_settings.AutoSaveDelaySeconds);_vm.SetTheme(_settings.Theme,false);_=ModernEditor.ExecuteAsync("setTheme",new{theme=_vm.DarkTheme?"dark":"light"});Editor.FontFamily=new FontFamily(_settings.DefaultFont);Editor.FontSize=_settings.DefaultFontSize;Editor.SpellCheck.IsEnabled=_settings.SpellCheck;}
    private void HideSettings(){SettingsHost.Visibility=Visibility.Collapsed;SettingsHost.Content=null;_settingsView=null;SettingsTabHeader.Visibility=Visibility.Collapsed;Tabs.Visibility=Visibility.Visible;}
    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if(Keyboard.Modifiers==ModifierKeys.Control&&e.Key==Key.K){GlobalSearchBox.Focus();GlobalSearchBox.SelectAll();}else if(Keyboard.Modifiers==ModifierKeys.Control&&e.Key==Key.O)ImportDocument_Click(this,new RoutedEventArgs());else if(Keyboard.Modifiers==ModifierKeys.Control&&e.Key==Key.P)Print_Click(this,new RoutedEventArgs());else if(e.Key==Key.F2&&_vm.SelectedNode is not null)Rename_Click(this,new RoutedEventArgs());else if(e.Key==Key.Delete&&_vm.SelectedNode is not null)Delete_Click(this,new RoutedEventArgs());else if(e.Key==Key.F11)WindowState=WindowState==WindowState.Maximized?WindowState.Normal:WindowState.Maximized;else if(Matches("NewDocument",e))NewDocument_Click(this,new RoutedEventArgs());else if(Matches("NewFolder",e))NewFolder_Click(this,new RoutedEventArgs());else if(Matches("Save",e))SaveAllWithStatus();else if(Matches("Export",e))ExportDocument_Click(this,new RoutedEventArgs());else if(Matches("Settings",e))Settings_Click(this,new RoutedEventArgs());else if(Matches("CloseTab",e))CloseCurrentTab();else if(Matches("NextTab",e))SelectRelativeTab(1);else if(Matches("PreviousTab",e))SelectRelativeTab(-1);else return;e.Handled=true;
    }
    private bool Matches(string id,KeyEventArgs e){if(!_settings.Hotkeys.TryGetValue(id,out var value))return false;try{return new KeyGestureConverter().ConvertFromInvariantString(value) is KeyGesture gesture&&gesture.Matches(this,e);}catch{return false;}}
    private void CloseCurrentTab(){var tab=_vm.SelectedTab;if(tab is null)return;_vm.Save(tab);var index=_vm.Tabs.IndexOf(tab);_vm.Tabs.Remove(tab);_vm.SelectedTab=_vm.Tabs.Count==0?null:_vm.Tabs[Math.Clamp(index-1,0,_vm.Tabs.Count-1)];ShowSelectedTab();}
    private void SelectRelativeTab(int direction){if(_vm.Tabs.Count<2)return;var index=_vm.SelectedTab is null?0:_vm.Tabs.IndexOf(_vm.SelectedTab);_vm.SelectedTab=_vm.Tabs[(index+direction+_vm.Tabs.Count)%_vm.Tabs.Count];ShowSelectedTab();}
    private void About_Click(object sender, RoutedEventArgs e) => MessageBox.Show("MasterDocumentation\nПереносимая локальная система документации\n.NET 8 / WPF / SQLite / TipTap", "О программе");
}
