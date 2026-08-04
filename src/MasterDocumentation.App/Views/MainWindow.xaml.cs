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
using System.Windows.Automation;
using System.Windows.Controls.Primitives;
using DocumentFormat.OpenXml.Packaging;
using OpenXmlParagraph = DocumentFormat.OpenXml.Wordprocessing.Paragraph;
using MasterDocumentation.Models;
using MasterDocumentation.Services;
using MasterDocumentation.Storage;
using MasterDocumentation.Utilities;
using MasterDocumentation.ViewModels;
using MasterDocumentation.Editor;
using MasterDocumentation.UI;

namespace MasterDocumentation.Views;

public sealed class OutlineItem : INotifyPropertyChanged
{
    private bool _isCurrent;
    public EditorHeading Heading { get; }
    public string Text=>Heading.Text;
    public ObservableCollection<OutlineItem> Children { get; }=[];
    public bool IsCurrent { get=>_isCurrent; set{if(_isCurrent==value)return;_isCurrent=value;PropertyChanged?.Invoke(this,new(nameof(IsCurrent)));} }
    public OutlineItem(EditorHeading heading)=>Heading=heading;
    public event PropertyChangedEventHandler? PropertyChanged;
}

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm; private readonly SettingsService _settingsService; private ApplicationSettings _settings; private readonly System.Windows.Threading.DispatcherTimer _saveTimer; private readonly System.Windows.Threading.DispatcherTimer _draftTimer; private readonly System.Windows.Threading.DispatcherTimer _highlightTimer; private readonly ObservableCollection<AttachmentInfo> _attachments=[]; private readonly ObservableCollection<string> _tags=[]; private readonly ObservableCollection<string> _visibleTags=[]; private readonly ObservableCollection<DocumentSearchResult> _searchResults=[]; private DocumentTab? _pendingDraft; private SettingsView? _settingsView; private bool _loading; private bool _highlighting; private bool _closeInProgress; private bool _closeCommitted; private bool _resettingHeadingSelector; private bool _updatingToolbarState; private bool _modernImageSelected; private string _selectedImageSource=""; private string _selectedImageAlt=""; private string _selectedImageCaption=""; private string _selectedImageWrap="none"; private double? _spaceBefore; private double? _spaceAfter; private double? _firstIndent; private double? _leftIndent; private double? _rightIndent; private string? _textDirection; private string _outlineSignature=""; private long _outlineDocumentId; private double _zoom=1; private Point _dragStart;
    private int _responsiveLayoutMode = -1;
    private GridLength _wideStructureWidth = new(225);
    private GridLength _widePropertiesWidth = new(265);
    private TextBlock? _brandTitle;
    private Button? _templatesButton;
    private Button? _applicationsButton;
    private bool _focusMode;
    private GridLength _focusLibraryWidth;
    private GridLength _focusStructureWidth;
    private GridLength _focusPropertiesWidth;
    private string _navigationMode = "all";
    private bool _saveFailed;
    private bool _libraryPanelCollapsed;
    private bool _structurePanelCollapsed;
    private bool _propertiesPanelCollapsed;
    private ObservableCollection<OutlineItem> _outlineRoots=[];
    /// <summary>Минимальная ширина панели свойств: ниже неё подписи и поле состояния обрезаются.</summary>
    private const double PropertiesMinWidth=280;
    /// <summary>Минимальная ширина библиотеки: ниже неё обрезается подпись кнопки создания.</summary>
    private const double LibraryMinWidth=215;
    private Point _tabDragStart;
    private DocumentTab? _draggedTab;
    public MainWindow(MainViewModel viewModel, SettingsService settingsService)
    {
        InitializeComponent(); ToastService.Initialize(this); ToastService.UnreadChanged+=(_,_)=>UpdateNotificationBadge(); UpdateNotificationBadge(); ShowPreReleaseBadge(); Tabs.HorizontalAlignment=HorizontalAlignment.Left;Tabs.MaxWidth=720;Tabs.PreviewMouseLeftButtonDown+=Tabs_PreviewMouseLeftButtonDown;Tabs.PreviewMouseMove+=Tabs_PreviewMouseMove;Tabs.PreviewMouseUp+=(_,_)=>_draggedTab=null;Tabs.PreviewMouseDown+=Tabs_PreviewMouseDown;SettingsHost.SizeChanged+=SettingsHost_SizeChanged; _vm = viewModel; _settingsService = settingsService; DataContext = _vm; AttachmentList.ItemsSource=_attachments;TagChips.ItemsSource=_visibleTags;TagEditorList.ItemsSource=_tags;SearchResultsList.ItemsSource=_searchResults;ModernEditor.DataFolder=AppPaths.Data;StorageLocationText.Text=AppPaths.Data;StorageLocationText.ToolTip=AppPaths.Data;
        FontFamilyBox.ItemsSource = Fonts.SystemFontFamilies.OrderBy(f => f.Source); FontFamilyBox.SelectedItem = new FontFamily("Segoe UI");
        FontSizeBox.ItemsSource = new[] { 8d, 9d, 10d, 11d, 12d, 13d, 14d, 16d, 18d, 20d, 24d, 28d, 32d, 40d, 48d, 64d }; FontSizeBox.SelectedItem = 13d;
        _settings = _settingsService.Load();ApplyInterfacePreferences(_settings);
        _saveTimer = new() { Interval = TimeSpan.FromSeconds(_settings.AutoSaveDelaySeconds) }; _saveTimer.Tick += (_, _) => { _saveTimer.Stop(); SaveAllWithStatus(); };
        _draftTimer = new() { Interval = TimeSpan.FromMilliseconds(650) }; _draftTimer.Tick += (_, _) => { _draftTimer.Stop();var tab=_pendingDraft;_pendingDraft=null;if(tab?.IsDirty!=true)return;try{DraftRecoveryService.Write(tab);}catch(Exception ex){LogService.Error("Не удалось записать аварийный черновик",ex);} };
        _highlightTimer = new() { Interval = TimeSpan.FromMilliseconds(450) }; _highlightTimer.Tick += (_, _) => { _highlightTimer.Stop(); HighlightCodeBlocks(); };
        UpdateNavigationCounts();
        RestoreWindow();RestorePanelLayout(); Loaded += (_, _) => { ConfigureAccessibility();RecoverEmergencyDrafts(); RestoreSession(); AutoBackupIfNeeded(); PurgeExpiredTrash(); UpdateNavigationCounts(); if(_settings.CheckUpdates)_=CheckForUpdatesAsync(true); }; Deactivated += (_, _) => _vm.SaveAll();
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
        var roots=_vm.Database.LoadTree();var storage=_vm.Database.ActiveProvider==MasterDocumentation.Storage.StorageProviderKind.Postgres?$"Хранилище: общая база данных PostgreSQL, вы подключены как «{UserIdentity.Current}».":"Хранилище: локальная база. Признак приватности сохранится при выгрузке документа в общую базу данных.";var dialog=new NewItemDialog(roots,_vm.Database.LoadTemplates(),folder,TargetFolder(),storage){Owner=this};if(dialog.ShowDialog()!=true)return null;
        try
        {
            IReadOnlyDictionary<string,string>? values=null;
            if(dialog.TemplateId is long template)
            {
                var builtIn=new HashSet<string>(["Title","Date","Time","Author","Section"],StringComparer.OrdinalIgnoreCase);var custom=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
                foreach(var name in _vm.Database.GetTemplateVariables(template).Where(x=>!builtIn.Contains(x))){var prompt=new TextPrompt("Переменная шаблона: "+name,""){Owner=this};if(prompt.ShowDialog()!=true)return null;custom[name]=prompt.Value;}values=custom;
            }
            var id=dialog.TemplateId is long source?_vm.Database.CreateFromTemplate(source,dialog.ParentId,dialog.ItemTitle,values):_vm.Database.Create(dialog.ParentId,dialog.IsFolder,dialog.ItemTitle,dialog.IsPrivate);
            if(dialog.TemplateId is not null&&dialog.IsPrivate)_vm.Database.SetDocumentAccess(id,true);
            _vm.ReloadTree();UpdateNavigationCounts();if(!dialog.IsFolder){var node=FindNode(_vm.Nodes,id);if(node is not null){_vm.Open(node);ShowSelectedTab(true,"start");}}return id;
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
            else if(extension is ".md" or ".markdown") {plain=File.ReadAllText(dialog.FileName);html=ImportInlineImages(MarkdownService.ToHtml(plain),id,Path.GetDirectoryName(Path.GetFullPath(dialog.FileName))!);}
            else if(extension is ".html" or ".htm") {html=ImportInlineImages(SanitizeImportedHtml(File.ReadAllText(dialog.FileName)),id,Path.GetDirectoryName(Path.GetFullPath(dialog.FileName))!);plain=HtmlToPlainText(html);}
            else if(extension==".rtf") {var document=new FlowDocument();using var stream=File.OpenRead(dialog.FileName);new TextRange(document.ContentStart,document.ContentEnd).Load(stream,DataFormats.Rtf);plain=new TextRange(document.ContentStart,document.ContentEnd).Text.TrimEnd();html="<p>"+System.Net.WebUtility.HtmlEncode(plain).Replace("\r\n","</p><p>").Replace("\n","</p><p>")+"</p>";}
            else {plain=File.ReadAllText(dialog.FileName);html="<p>"+System.Net.WebUtility.HtmlEncode(plain).Replace("\r\n","</p><p>").Replace("\n","</p><p>")+"</p>";}
            _vm.Database.SaveStructuredContent(id,"",html,plain);_vm.ReloadTree();UpdateNavigationCounts();var node=FindNode(_vm.Nodes,id);if(node is not null){_vm.Open(node);ShowSelectedTab();}ToastService.Show("Импорт завершён","Документ добавлен в локальное хранилище.",ToastKind.Success);
        }
        catch(Exception ex){LogService.Error("Ошибка импорта документа",ex);MessageBox.Show(this,"Не удалось импортировать документ: "+ex.Message,"Импорт",MessageBoxButton.OK,MessageBoxImage.Error);}
    }
    private void DatabaseTransfer_Click(object sender,RoutedEventArgs e)
    {
        CreateMenuPopup.IsOpen=false;
        _vm.SaveAll();
        var dialog=new DatabaseTransferWindow(_vm.Database){Owner=this};
        dialog.ShowDialog();
        if(!dialog.LibraryChanged)return;
        _navigationMode="all";
        _vm.ReloadTree();
        UpdateNavigationCounts();
        if(_vm.SelectedTab is null)LoadStartPage();
        ToastService.Show("Импорт из базы данных завершён","Выбранные страницы добавлены в текущее хранилище.",ToastKind.Success);
    }
    private string UniqueTitle(long? parent,string value){var title=string.IsNullOrWhiteSpace(value)?"Импортированный документ":value.Trim();if(!_vm.Database.TitleExists(parent,title))return title;for(var i=2;;i++){var candidate=$"{title} ({i})";if(!_vm.Database.TitleExists(parent,candidate))return candidate;}}
    private static (string Html,string Plain) ImportDocx(string path){using var document=WordprocessingDocument.Open(path,false);var paragraphs=document.MainDocumentPart?.Document?.Body?.Elements<OpenXmlParagraph>()??[];var html=new System.Text.StringBuilder();var plain=new System.Text.StringBuilder();foreach(var paragraph in paragraphs){var text=paragraph.InnerText;plain.AppendLine(text);var style=paragraph.ParagraphProperties?.ParagraphStyleId?.Val?.Value??"";var match=Regex.Match(style,@"Heading\s*([1-6])",RegexOptions.IgnoreCase);if(match.Success)html.Append($"<h{match.Groups[1].Value}>{System.Net.WebUtility.HtmlEncode(text)}</h{match.Groups[1].Value}>");else html.Append($"<p>{System.Net.WebUtility.HtmlEncode(text)}</p>");}return(html.ToString(),plain.ToString().TrimEnd());}
    /// <summary>
    /// Переносит изображения импортируемого файла в хранилище: файл копируется в папку вложений
    /// и в базу данных, а ссылка в содержимом заменяется на адрес вложения — иначе на странице
    /// осталась бы лишь ссылка на локальный путь, и картинка не отображалась бы.
    /// </summary>
    private string ImportInlineImages(string html,long documentId,string baseFolder)
    {
        return MarkdownService.RewriteImageSources(html,source=>
        {
            try
            {
                if(string.IsNullOrWhiteSpace(source)||MarkdownService.AssetName(source) is not null)return null;
                byte[] bytes;var fileName=Path.GetFileName(source);var mime="";
                if(source.StartsWith("data:",StringComparison.OrdinalIgnoreCase))
                {
                    var comma=source.IndexOf(',');if(comma<0)return null;
                    mime=Regex.Match(source[..comma],@"data:(?<mime>[^;,]+)").Groups["mime"].Value;
                    bytes=Convert.FromBase64String(source[(comma+1)..]);
                    fileName="image"+(mime.Contains("png")?".png":mime.Contains("gif")?".gif":mime.Contains("webp")?".webp":".jpg");
                }
                else
                {
                    var path=Uri.TryCreate(source,UriKind.Absolute,out var uri)&&uri.IsFile?uri.LocalPath:Path.IsPathRooted(source)?source:Path.GetFullPath(Path.Combine(baseFolder,Uri.UnescapeDataString(source)));
                    if(!File.Exists(path))return null;
                    bytes=File.ReadAllBytes(path);fileName=Path.GetFileName(path);
                }
                if(mime.Length==0)mime=GuessMimeType(fileName);
                var asset=StoreAssetBytes(bytes,fileName,mime);
                if(!_vm.Database.GetAttachments(documentId).Any(x=>x.Sha256.Equals(asset.Hash,StringComparison.OrdinalIgnoreCase)))
                    _vm.Database.RegisterAttachment(documentId,fileName,asset.StoredName,mime,asset.Size,asset.Hash);
                return "https://assets.local/"+asset.StoredName;
            }
            catch(Exception ex){LogService.Error("Не удалось перенести изображение импортируемого документа",ex);return null;}
        });
    }
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
        var deletedId=node.Id;
        var deletedTitle=node.Title;
        foreach (var tab in _vm.Tabs.Where(t => t.DocumentId == node.Id).ToList()) _vm.Tabs.Remove(tab);
        _vm.Database.Delete(node.Id);
        _vm.ReloadTree();
        UpdateNavigationCounts();
        ShowSelectedTab();
        ToastService.Show(
            "Перемещено в корзину",
            deletedTitle,
            ToastKind.Information,
            TimeSpan.FromSeconds(12),
            "Отменить",
            ()=>Dispatcher.BeginInvoke(new Action(()=>{
                try
                {
                    _vm.Database.Restore(deletedId);
                    _navigationMode="all";
                    _vm.ReloadTree();
                    UpdateNavigationCounts();
                    ToastService.Show("Документ восстановлен",deletedTitle,ToastKind.Success);
                }
                catch(Exception ex)
                {
                    LogService.Error("Не удалось отменить удаление",ex);
                    MessageBox.Show(this,ex.Message,"Восстановление",MessageBoxButton.OK,MessageBoxImage.Error);
                }
            })));
    }
    private static NodeItem? FindNode(IEnumerable<NodeItem> nodes, long id) { foreach (var n in nodes) { if (n.Id == id) return n; var child = FindNode(n.Children, id); if (child is not null) return child; } return null; }

    private void Tabs_SelectionChanged(object sender,SelectionChangedEventArgs e)
    {
        if(e.Source!=Tabs)return;
        if(Tabs.SelectedItem is DocumentTab selected&&!ReferenceEquals(_vm.SelectedTab,selected))_vm.SelectedTab=selected;
        ShowSelectedTab(true);
    }
    private void Tabs_PreviewMouseLeftButtonDown(object sender,MouseButtonEventArgs e)
    {
        _tabDragStart=e.GetPosition(Tabs);
        _draggedTab=TabFromSource(e.OriginalSource as DependencyObject);
    }
    private void Tabs_PreviewMouseMove(object sender,MouseEventArgs e)
    {
        if(e.LeftButton!=MouseButtonState.Pressed||_draggedTab is null)return;
        var point=e.GetPosition(Tabs);
        if(Math.Abs(point.X-_tabDragStart.X)<SystemParameters.MinimumHorizontalDragDistance&&Math.Abs(point.Y-_tabDragStart.Y)<SystemParameters.MinimumVerticalDragDistance)return;
        var target=TabFromSource(e.OriginalSource as DependencyObject);
        if(target is null||ReferenceEquals(target,_draggedTab))return;
        var oldIndex=_vm.Tabs.IndexOf(_draggedTab);
        var newIndex=_vm.Tabs.IndexOf(target);
        if(oldIndex<0||newIndex<0)return;
        _vm.Tabs.Move(oldIndex,newIndex);
        Tabs.SelectedItem=_draggedTab;
        _tabDragStart=point;
    }
    private void Tabs_PreviewMouseDown(object sender,MouseButtonEventArgs e)
    {
        if(e.ChangedButton!=MouseButton.Middle)return;
        var tab=TabFromSource(e.OriginalSource as DependencyObject);
        if(tab is null)return;
        CloseTab(tab);
        e.Handled=true;
    }
    private DocumentTab? TabFromSource(DependencyObject? source)
    {
        if(source is null)return null;
        return ItemsControl.ContainerFromElement(Tabs,source) is ListBoxItem item?item.DataContext as DocumentTab:null;
    }
    private void ShowSelectedTab(bool focusEditor=false,string focusPosition="end")
    {
        _loading = true;
        if (_vm.SelectedTab is null)
        {
            Editor.Visibility = Visibility.Collapsed;
            ModernEditor.Visibility = Visibility.Collapsed;
            EmptyHint.Visibility = Visibility.Visible;
            DocumentHeader.Visibility = Visibility.Collapsed;
            StructureTree.ItemsSource = null;
            HeadersList.ItemsSource = null;
            SetOutlineEmptyState(true);
            LoadStartPage();
        }
        else
        {
            Editor.Document = _vm.SelectedTab.Document;
            Editor.Visibility = Visibility.Collapsed;
            ModernEditor.Visibility = Visibility.Visible;
            EmptyHint.Visibility = Visibility.Collapsed;
            DocumentHeader.Visibility = Visibility.Visible;
            _outlineSignature = "";
            _outlineDocumentId = _vm.SelectedTab.DocumentId;
            StructureTree.ItemsSource = null;
            HeadersList.ItemsSource = null;
            SetOutlineEmptyState(true);
            UpdateDocumentHeader();
            var html = _vm.SelectedTab.Html;
            if (string.IsNullOrWhiteSpace(html))
            {
                var text = new TextRange(Editor.Document.ContentStart, Editor.Document.ContentEnd).Text;
                html = "<p>" + System.Net.WebUtility.HtmlEncode(text).Replace("\r\n", "</p><p>").Replace("\n", "</p><p>") + "</p>";
            }
            _ = ModernEditor.SetContentAsync(_vm.SelectedTab.DocumentId, _vm.SelectedTab.EditorJson, html, focusEditor, focusPosition);
            _zoom = Math.Clamp(_vm.Database.GetZoom(_vm.SelectedTab.DocumentId), .5, 2);
            ZoomText.Text = $"{_zoom * 100:0}%";
            _ = ModernEditor.ExecuteAsync("setZoom", new { value = _zoom });
            _ = ModernEditor.ExecuteAsync("setTheme", new { theme = _vm.DarkTheme ? "dark" : "light" });
            LoadDocumentProperties();
        }
        _loading = false;
        UpdateNavigationCounts();
        UpdateDocumentStats();
    }
    private void LoadStartPage(){StartRecentList.ItemsSource=_vm.Database.LoadRecent(_settings.RecentFilesCount);StartStatisticsText.Text=$"Документов: {_vm.Database.CountDocuments()}   •   Избранных: {_vm.Database.CountFavorites()}   •   В корзине: {_vm.Database.CountTrash()}   •   Хранилище: {AppPaths.Data}";}
    private void UpdateDocumentHeader()
    {
        if (_vm.SelectedTab is null)
            return;
        DocumentTitleBox.Text = _vm.SelectedTab.Title;
        var parts = new List<string>();
        var node = _vm.Database.FindNode(_vm.SelectedTab.DocumentId);
        while (node is not null)
        {
            parts.Add(node.Title);
            node = node.ParentId is long parentId ? _vm.Database.FindNode(parentId) : null;
        }
        parts.Reverse();
        DocumentPathText.Text = parts.Count > 1 ? string.Join("  /  ", parts.Take(parts.Count - 1)) : "Все документы";
        DocumentPathText.ToolTip = string.Join(" / ", parts);
        var favorite = _vm.Database.LoadFavorites().Any(item => item.Id == _vm.SelectedTab.DocumentId);
        DocumentFavoriteButton.ToolTip = favorite ? "Убрать документ из избранного" : "Добавить документ в избранное";
        DocumentFavoriteIcon.SetResourceReference(AppIcon.ForegroundProperty, favorite ? "Accent/Primary" : "Text/Secondary");
    }
    private void DocumentTitle_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            CommitDocumentTitle();
            ModernEditor.Focus();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            if (_vm.SelectedTab is not null)
                DocumentTitleBox.Text = _vm.SelectedTab.Title;
            ModernEditor.Focus();
            e.Handled = true;
        }
    }
    private void DocumentTitle_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (!_loading)
            CommitDocumentTitle();
    }
    private void CommitDocumentTitle()
    {
        if (_vm.SelectedTab is null)
            return;
        var title = DocumentTitleBox.Text.Trim();
        if (title == _vm.SelectedTab.Title)
            return;
        if (string.IsNullOrWhiteSpace(title))
        {
            DocumentTitleBox.Text = _vm.SelectedTab.Title;
            MessageBox.Show(this, "Название документа не может быть пустым.", "Переименование", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        try
        {
            _vm.Database.Rename(_vm.SelectedTab.DocumentId, title);
            _vm.SelectedTab.Title = title;
            var treeNode = FindNode(_vm.Nodes, _vm.SelectedTab.DocumentId);
            if (treeNode is not null)
                treeNode.Title = title;
            UpdateDocumentHeader();
            ToastService.Show("Название изменено", title, ToastKind.Success);
        }
        catch (Exception ex)
        {
            DocumentTitleBox.Text = _vm.SelectedTab.Title;
            MessageBox.Show(this, ex.Message, "Переименование", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
    private void DocumentFavorite_Click(object sender, RoutedEventArgs e)
    {
        if (_vm.SelectedTab is null)
            return;
        _vm.Database.ToggleFavorite(_vm.SelectedTab.DocumentId);
        UpdateNavigationCounts();
        UpdateDocumentHeader();
    }
    private void StartRecent_DoubleClick(object sender,MouseButtonEventArgs e){if(StartRecentList.SelectedItem is not NodeItem node)return;_vm.Open(node);ShowSelectedTab();}
    /// <summary>
    /// Поиск подсказывает найденное списком под строкой и не трогает дерево слева: библиотека
    /// должна оставаться на месте, пока пользователь ищет.
    /// </summary>
    private void GlobalSearch_TextChanged(object sender,TextChangedEventArgs e)
    {
        var query=GlobalSearchBox.Text.Trim();
        _searchResults.Clear();
        if(query.Length<2){SearchResultsPopup.IsOpen=false;return;}
        try{foreach(var result in _vm.Database.SearchDocuments(query,limit:8))_searchResults.Add(result);}
        catch(Exception ex){LogService.Error("Не удалось выполнить поиск",ex);}
        SearchResultsEmptyText.Visibility=_searchResults.Count==0?Visibility.Visible:Visibility.Collapsed;
        SearchResultsHeaderText.Visibility=_searchResults.Count==0?Visibility.Collapsed:Visibility.Visible;
        SearchResultsPopup.IsOpen=true;
    }
    private void GlobalSearch_PreviewKeyDown(object sender,KeyEventArgs e)
    {
        if(e.Key==Key.Escape){SearchResultsPopup.IsOpen=false;e.Handled=true;return;}
        if(e.Key!=Key.Enter)return;
        e.Handled=true;SearchResultsPopup.IsOpen=false;OpenGlobalSearch(GlobalSearchBox.Text);
    }
    private void AdvancedSearch_Click(object sender,RoutedEventArgs e){SearchResultsPopup.IsOpen=false;OpenGlobalSearch(GlobalSearchBox.Text);}
    private void SearchResult_Click(object sender,RoutedEventArgs e)
    {
        if((sender as FrameworkElement)?.Tag is not DocumentSearchResult result)return;
        SearchResultsPopup.IsOpen=false;
        OpenDocumentById(result.Id);
    }
    private void OpenDocumentById(long id)
    {
        var node=FindNode(_vm.Nodes,id);
        if(node is null){_navigationMode="all";_vm.ShowAll();node=FindNode(_vm.Nodes,id);}
        if(node is null)return;
        _vm.Open(node);ShowSelectedTab();
    }
    private void OpenGlobalSearch(string initialQuery)
    {
        var dialog=new SearchWindow(_vm.Database,initialQuery){Owner=this};
        if(dialog.ShowDialog()!=true)return;
        if(dialog.SelectedActionId is string action)
        {
            switch(action)
            {
                case "NewDocument": NewDocument_Click(this,new RoutedEventArgs()); break;
                case "NewFolder": NewFolder_Click(this,new RoutedEventArgs()); break;
                case "Export": ExportDocument_Click(this,new RoutedEventArgs()); break;
                case "Print": Print_Click(this,new RoutedEventArgs()); break;
                case "Settings": Settings_Click(this,new RoutedEventArgs()); break;
                case "ToggleTheme": ThemeQuickToggle_Click(this,new RoutedEventArgs()); break;
                case "FocusMode": ToggleFocusMode(); break;
                case "Backup": Backup_Click(this,new RoutedEventArgs()); break;
                case "Attachments":
                    if(_vm.SelectedTab is null)ToastService.Show("Вложения","Сначала откройте документ.",ToastKind.Information);
                    else AttachmentsTab_Click(this,new RoutedEventArgs());
                    break;
            }
            return;
        }
        if(dialog.SelectedDocumentId is not long id)return;
        var node=_vm.Database.FindNode(id);
        if(node is null)
        {
            MessageBox.Show(this,"Документ находится в корзине. Восстановите его перед открытием.","Поиск",MessageBoxButton.OK,MessageBoxImage.Information);
            _vm.ShowTrash();
            return;
        }
        _vm.Open(node);
        ShowSelectedTab();
    }
    private void CloseTab_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not DocumentTab tab) return;CloseTab(tab);e.Handled = true;
    }
    private void CloseTab(DocumentTab tab){_vm.Save(tab);_vm.Tabs.Remove(tab);if(_vm.SelectedTab==tab)_vm.SelectedTab=_vm.Tabs.LastOrDefault();ShowSelectedTab();}
    private void TabsOverflow_Click(object sender,RoutedEventArgs e){TabsOverflowPopup.IsOpen=!TabsOverflowPopup.IsOpen;}
    private void TabsOverflowItem_Click(object sender,RoutedEventArgs e)
    {
        if((sender as FrameworkElement)?.Tag is not DocumentTab tab)return;
        TabsOverflowPopup.IsOpen=false;
        _vm.SelectedTab=tab;
        Tabs.SelectedItem=tab;
        Tabs.ScrollIntoView(tab);
        ShowSelectedTab(true);
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
        tab.EditorJson=content.Json;tab.Html=content.Html;tab.PlainText=content.PlainText;tab.IsDirty=true;SetSaveStatus("Есть несохранённые изменения","State/Warning",false);
        if(_vm.SelectedTab?.DocumentId==tab.DocumentId)UpdateDocumentStats();
        _pendingDraft=tab;_draftTimer.Stop();_draftTimer.Start();
        if(restartAutoSave){_saveTimer.Stop();_saveTimer.Start();}
    }
    private void UpdateOutline(long documentId,IReadOnlyList<EditorHeading> headings)
    {
        var signature=string.Join('\u001f',headings.Select(x=>$"{x.Level}:{x.Position}:{x.Text}"));if(_outlineDocumentId==documentId&&_outlineSignature==signature)return;_outlineDocumentId=documentId;_outlineSignature=signature;
        var roots=new ObservableCollection<OutlineItem>();var stack=new Stack<(int Level,OutlineItem Item)>();
        foreach(var heading in headings){while(stack.Count>0&&stack.Peek().Level>=heading.Level)stack.Pop();var item=new OutlineItem(heading);if(stack.Count==0)roots.Add(item);else stack.Peek().Item.Children.Add(item);stack.Push((heading.Level,item));}
        _outlineRoots=roots;StructureTree.ItemsSource=roots;HeadersList.ItemsSource=headings;
        SetOutlineEmptyState(headings.Count==0);
    }
    private void UpdateCurrentOutlineItem(int position)
    {
        var flat=new List<OutlineItem>();
        void Visit(IEnumerable<OutlineItem> items){foreach(var item in items){flat.Add(item);Visit(item.Children);}}
        Visit(_outlineRoots);
        var current=flat.Where(item=>item.Heading.Position<=position).OrderBy(item=>item.Heading.Position).LastOrDefault();
        foreach(var item in flat)item.IsCurrent=ReferenceEquals(item,current);
    }
    private void SetOutlineEmptyState(bool isEmpty)
    {
        OutlineEmptyState.Visibility=isEmpty?Visibility.Visible:Visibility.Collapsed;
    }
    private void Heading_DoubleClick(object sender,MouseButtonEventArgs e){var heading=sender switch{TreeView tree when tree.SelectedItem is OutlineItem item=>item.Heading,ListBox list when list.SelectedItem is EditorHeading item=>item,_=>null};if(heading is not null)_=ModernEditor.ExecuteAsync("gotoHeading",new{pos=heading.Position});}
    private void StructureTab_Click(object sender,RoutedEventArgs e){StructureTab.IsChecked=true;HeadersTab.IsChecked=false;StructureTree.Visibility=Visibility.Visible;HeadersList.Visibility=Visibility.Collapsed;}
    private void HeadersTab_Click(object sender,RoutedEventArgs e){StructureTab.IsChecked=false;HeadersTab.IsChecked=true;StructureTree.Visibility=Visibility.Collapsed;HeadersList.Visibility=Visibility.Visible;}
    private void UpdateNavigationCounts() { AllDocumentsCountText.Text = _vm.Database.CountDocuments().ToString(); FavoritesCountText.Text = _vm.Database.CountFavorites().ToString(); RecentDocumentsCountText.Text = _vm.Database.CountRecent().ToString(); TrashCountText.Text = _vm.Database.CountTrash().ToString();UpdateNavigationEmptyState(); }
    private void UpdateNavigationEmptyState()
    {
        var isEmpty=_vm.Nodes.Count==0;
        NavigationEmptyState.Visibility=isEmpty?Visibility.Visible:Visibility.Collapsed;
        TrashHintText.Visibility=_navigationMode=="trash"&&!isEmpty?Visibility.Visible:Visibility.Collapsed;
        if(!isEmpty)return;
        (NavigationEmptyTitle.Text,NavigationEmptyDescription.Text,NavigationEmptyAction.Visibility,NavigationEmptyIcon.Kind)=_navigationMode switch
        {
            "favorites"=>("Нет избранных документов","Добавляйте часто используемые документы в избранное через контекстное меню.",Visibility.Collapsed,AppIconKind.Star),
            "recent"=>("Нет недавних документов","Откройте документ — он появится здесь для быстрого доступа.",Visibility.Collapsed,AppIconKind.Clock),
            "trash"=>("Корзина пуста",$"Удалённые документы можно восстановить отсюда в течение {DatabaseService.TrashRetentionDays} дней.",Visibility.Collapsed,AppIconKind.Trash),
            "templates"=>("Нет шаблонов","Создайте документ и выберите «Использовать как шаблон» в контекстном меню.",Visibility.Visible,AppIconKind.Template),
            _=>("Нет документов","Создайте первый документ, чтобы начать работу.",Visibility.Visible,AppIconKind.Documents)
        };
    }
    private void AllDocuments_Click(object sender, RoutedEventArgs e) { _navigationMode="all";_vm.ShowAll();UpdateNavigationEmptyState(); }
    private void Favorites_Click(object sender, RoutedEventArgs e) { _navigationMode="favorites";_vm.ShowFavorites();UpdateNavigationEmptyState(); }
    private void Recent_Click(object sender, RoutedEventArgs e) { _navigationMode="recent";_vm.ShowRecent(_settingsService.Load().RecentFilesCount);UpdateNavigationEmptyState(); }
    private void Trash_Click(object sender, RoutedEventArgs e) { _navigationMode="trash";PurgeExpiredTrash();_vm.ShowTrash();UpdateNavigationCounts(); }
    /// <summary>Корзина сама освобождается: элементы старше срока хранения удаляются окончательно.</summary>
    private void PurgeExpiredTrash()
    {
        try{if(_vm.Database.PurgeExpiredTrash(DatabaseService.TrashRetentionDays)>0)LogService.Info("Из корзины удалены элементы с истёкшим сроком хранения");}
        catch(Exception ex){LogService.Error("Не удалось очистить просроченные элементы корзины",ex);}
    }
    /// <summary>Пункты корзины уместны только там, где применимы: восстановление и окончательное удаление — в корзине.</summary>
    private void Tree_ContextMenuOpening(object sender,ContextMenuEventArgs e)
    {
        var inTrash=_navigationMode=="trash";var hasNode=_vm.SelectedNode is not null;
        RestoreMenuItem.Visibility=inTrash&&hasNode?Visibility.Visible:Visibility.Collapsed;
        DeletePermanentlyMenuItem.Visibility=inTrash&&hasNode?Visibility.Visible:Visibility.Collapsed;
        MoveToTrashMenuItem.Visibility=!inTrash&&hasNode?Visibility.Visible:Visibility.Collapsed;
        TrashMenuSeparator.Visibility=hasNode?Visibility.Visible:Visibility.Collapsed;
    }
    private static void RunTool(string file,string? arguments=null)
    {
        try{Process.Start(new ProcessStartInfo(file,arguments??""){UseShellExecute=true});}
        catch(Exception ex){LogService.Error("Не удалось запустить инструмент "+file,ex);ToastService.Show("Не удалось открыть инструмент",ex.Message,ToastKind.Warning);}
    }
    private void QuickSnipping_Click(object sender,RoutedEventArgs e)=>RunTool("ms-screenclip:");
    private void QuickCalculator_Click(object sender,RoutedEventArgs e)=>RunTool("calc.exe");
    private void QuickAssets_Click(object sender,RoutedEventArgs e)=>RunTool("explorer.exe",$"\"{AppPaths.Assets}\"");
    private void QuickDataFolder_Click(object sender,RoutedEventArgs e)=>RunTool("explorer.exe",$"\"{AppPaths.Data}\"");
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
    private void MakeTemplate_Click(object sender,RoutedEventArgs e){if(_vm.SelectedNode is null||_vm.SelectedNode.IsFolder){MessageBox.Show(this,"Шаблоном может быть только документ.");return;}_vm.Save(_vm.Tabs.FirstOrDefault(x=>x.DocumentId==_vm.SelectedNode.Id));_vm.Database.SetTemplate(_vm.SelectedNode.Id,true);ToastService.Show("Шаблон создан","Документ доступен в списке шаблонов.",ToastKind.Success);}
    private void Templates_Click(object sender,RoutedEventArgs e){_navigationMode="templates";_vm.ShowTemplates();UpdateNavigationEmptyState();}
    private void Applications_Click(object sender,RoutedEventArgs e)=>new ApplicationsWindow{Owner=this}.ShowDialog();
    private void StorageHealth_Click(object sender,RoutedEventArgs e){try{var result=_vm.Database.CheckIntegrity();if(result.Equals("ok",StringComparison.OrdinalIgnoreCase))ToastService.Show("Хранилище исправно","Проверка целостности завершена.",ToastKind.Success);else MessageBox.Show(this,"Результат проверки: "+result,"Проверка хранилища",MessageBoxButton.OK,MessageBoxImage.Warning);}catch(Exception ex){MessageBox.Show(this,"Не удалось проверить хранилище: "+ex.Message,"Проверка",MessageBoxButton.OK,MessageBoxImage.Error);}}
    private void ThemeQuickToggle_Click(object sender,RoutedEventArgs e)
    {
        _vm.DarkTheme=!_vm.DarkTheme;
        _=ModernEditor.ExecuteAsync("setTheme",new{theme=_vm.DarkTheme?"dark":"light"});
        // Открытая страница настроек и снимок настроек окна должны знать о смене темы,
        // иначе следующее сохранение вернёт прежнюю тему.
        _settings.Theme=_vm.Theme;
        _settingsView?.SyncTheme(_vm.Theme);
    }
    private void OpenDataFolder_Click(object sender,RoutedEventArgs e){Directory.CreateDirectory(AppPaths.Data);Process.Start(new ProcessStartInfo("explorer.exe",$"\"{AppPaths.Data}\""){UseShellExecute=true});}
    private void LoadDocumentProperties()
    {
        _attachments.Clear();
        _tags.Clear();
        if(_vm.SelectedTab is null)
        {
            AttachmentsHeaderText.Text="Вложения (0)";
            TagsPopup.IsOpen=false;
            UpdateTagChips();
            AttachmentsEmptyState.Visibility=Visibility.Visible;
            return;
        }
        var status=_vm.Database.GetStatus(_vm.SelectedTab.DocumentId);
        StatusBox.ItemsSource=null;
        StatusBox.Items.Clear();
        foreach(var value in _vm.Database.GetStatuses())StatusBox.Items.Add(value);
        StatusBox.SelectedItem=status;
        UpdateStatusIndicator(status);
        foreach(var tag in _vm.Database.GetTags(_vm.SelectedTab.DocumentId))_tags.Add(tag);
        UpdateTagChips();
        foreach(var attachment in _vm.Database.GetAttachments(_vm.SelectedTab.DocumentId))_attachments.Add(attachment);
        AttachmentsHeaderText.Text=$"Вложения ({_attachments.Count})";
        AttachmentsEmptyState.Visibility=_attachments.Count==0?Visibility.Visible:Visibility.Collapsed;
    }
    private void PropertiesTab_Click(object sender,RoutedEventArgs e){AttachmentsPanel.Visibility=Visibility.Collapsed;PropertiesTabIndicator.Visibility=Visibility.Visible;AttachmentsTabIndicator.Visibility=Visibility.Collapsed;}
    private void AttachmentsTab_Click(object sender,RoutedEventArgs e){AttachmentsPanel.Visibility=Visibility.Visible;PropertiesTabIndicator.Visibility=Visibility.Collapsed;AttachmentsTabIndicator.Visibility=Visibility.Visible;LoadDocumentProperties();}
    private void AddAttachment_Click(object sender,RoutedEventArgs e)
    {
        if(_vm.SelectedTab is null){MessageBox.Show(this,"Сначала откройте документ.","Вложения");return;}var dialog=new OpenFileDialog{Title="Добавить вложение",Filter="Все файлы|*.*",Multiselect=true};if(dialog.ShowDialog(this)!=true)return;
        try{foreach(var path in dialog.FileNames){var asset=StoreAsset(path);if(_vm.Database.GetAttachments(_vm.SelectedTab.DocumentId).Any(x=>x.Sha256.Equals(asset.Hash,StringComparison.OrdinalIgnoreCase)))continue;_vm.Database.RegisterAttachment(_vm.SelectedTab.DocumentId,Path.GetFileName(path),asset.StoredName,GuessMimeType(path),asset.Size,asset.Hash);}LoadDocumentProperties();}catch(Exception ex){LogService.Error("Не удалось добавить вложение",ex);MessageBox.Show(this,"Не удалось добавить вложение: "+ex.Message,"Вложения",MessageBoxButton.OK,MessageBoxImage.Error);}
    }
    /// <summary>
    /// Выбор вложения переносится в текст: изображение выделяется в документе, и панель свойств
    /// начинает править именно его, а не то, что было выбрано мышью до этого.
    /// </summary>
    private void AttachmentList_SelectionChanged(object sender,SelectionChangedEventArgs e)
    {
        var isImage=AttachmentList.SelectedItem is AttachmentInfo item&&item.MimeType.StartsWith("image/",StringComparison.OrdinalIgnoreCase);
        ShowAttachmentInDocumentButton.IsEnabled=isImage;
        if(isImage&&AttachmentList.SelectedItem is AttachmentInfo selected)SelectAttachmentInDocument(selected);
    }
    private void ShowAttachmentInDocument_Click(object sender,RoutedEventArgs e)
    {
        if(AttachmentList.SelectedItem is not AttachmentInfo item){ToastService.Show("Вложения","Выберите вложение в списке.",ToastKind.Information);return;}
        SelectAttachmentInDocument(item);
    }
    private void SelectAttachmentInDocument(AttachmentInfo item)
    {
        if(!item.MimeType.StartsWith("image/",StringComparison.OrdinalIgnoreCase))
        {
            ToastService.Show("Вложение",item.FileName+" — не изображение, в тексте его нет.",ToastKind.Information);
            return;
        }
        _=ModernEditor.ExecuteAsync("selectImage",new{src=item.StoredName});
    }
    /// <summary>
    /// Пока идёт долгая операция, редактор скрывается: WebView2 рисуется собственным HWND поверх
    /// элементов WPF, и карточка с ходом выполнения оказывалась под ним — пользователь видел
    /// только затемнённое окно без единой строки о происходящем.
    /// </summary>
    private void ShowOperationOverlay(string title,string description)
    {
        OperationTitle.Text=title;OperationDescription.Text=description;
        OperationOverlay.Visibility=Visibility.Visible;
        AutomationProperties.SetName(OperationOverlay,title);
        ModernEditor.SetBrowserVisible(false);
    }
    private void HideOperationOverlay()
    {
        OperationOverlay.Visibility=Visibility.Collapsed;
        if(SettingsHost.Visibility!=Visibility.Visible)ModernEditor.SetBrowserVisible(true);
    }
    private void ModernEditor_InitializationFailed(object? sender,string error)
    {
        LogService.Error("Не удалось запустить редактор WebView2: "+error);
        ToastService.Show("Редактор не запустился","Подробности записаны в Data\\Logs. Нажмите «Повторить запуск редактора» на месте редактора или перезапустите приложение.",ToastKind.Error,TimeSpan.FromSeconds(12));
    }
    private void ModernEditor_ImageMissing(object? sender,string source)=>
        ToastService.Show("Вложения не видно в тексте","Файл прикреплён к документу, но не вставлен в содержимое.",ToastKind.Information);
    private void OpenAttachment_Click(object sender,RoutedEventArgs e){if(AttachmentList.SelectedItem is not AttachmentInfo item)return;_vm.Database.MaterializeAsset(item.StoredName);var path=Path.Combine(AppPaths.Assets,item.StoredName);if(!File.Exists(path)){MessageBox.Show(this,"Файл вложения не найден в локальном хранилище.","Вложения",MessageBoxButton.OK,MessageBoxImage.Warning);return;}Process.Start(new ProcessStartInfo(path){UseShellExecute=true});}
    private void ShowAttachmentInFolder_Click(object sender,RoutedEventArgs e){if(AttachmentList.SelectedItem is not AttachmentInfo item)return;_vm.Database.MaterializeAsset(item.StoredName);var path=Path.Combine(AppPaths.Assets,item.StoredName);if(!File.Exists(path)){MessageBox.Show(this,"Файл вложения не найден. Проверьте целостность хранилища или восстановите резервную копию.","Вложения",MessageBoxButton.OK,MessageBoxImage.Warning);return;}Process.Start(new ProcessStartInfo("explorer.exe",$"/select,\"{path}\""){UseShellExecute=true});}
    private void SaveAttachment_Click(object sender,RoutedEventArgs e){if(AttachmentList.SelectedItem is not AttachmentInfo item)return;_vm.Database.MaterializeAsset(item.StoredName);var source=Path.Combine(AppPaths.Assets,item.StoredName);if(!File.Exists(source)){MessageBox.Show(this,"Файл вложения не найден.","Вложения");return;}var dialog=new SaveFileDialog{Title="Сохранить вложение",FileName=item.FileName,Filter="Все файлы|*.*"};if(dialog.ShowDialog(this)==true)File.Copy(source,dialog.FileName,true);}
    private void RemoveAttachment_Click(object sender,RoutedEventArgs e){if(AttachmentList.SelectedItem is not AttachmentInfo item)return;if(MessageBox.Show(this,$"Удалить связь с файлом «{item.FileName}»?\nСам файл будет удалён только если больше нигде не используется.","Удаление вложения",MessageBoxButton.YesNo,MessageBoxImage.Warning)!=MessageBoxResult.Yes)return;try{var removed=_vm.Database.RemoveAttachment(item.Id);if(removed.IsUnused){var path=Path.Combine(AppPaths.Assets,removed.StoredName);if(File.Exists(path))File.Delete(path);}LoadDocumentProperties();}catch(Exception ex){MessageBox.Show(this,"Не удалось удалить вложение: "+ex.Message,"Вложения",MessageBoxButton.OK,MessageBoxImage.Error);}}
    private static string GuessMimeType(string path)=>Path.GetExtension(path).ToLowerInvariant() switch{".png"=>"image/png",".jpg" or ".jpeg"=>"image/jpeg",".gif"=>"image/gif",".svg"=>"image/svg+xml",".pdf"=>"application/pdf",".docx"=>"application/vnd.openxmlformats-officedocument.wordprocessingml.document",".xlsx"=>"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",".zip"=>"application/zip",".json"=>"application/json",".html" or ".htm"=>"text/html",".md"=>"text/markdown",".txt"=>"text/plain",_=>"application/octet-stream"};
    private void Status_Changed(object sender,SelectionChangedEventArgs e){if(_loading||_vm?.SelectedTab is null||StatusBox.SelectedItem is null)return;var value=StatusBox.SelectedItem is ComboBoxItem item?item.Content?.ToString():StatusBox.SelectedItem.ToString();if(!string.IsNullOrWhiteSpace(value)){_vm.Database.SetStatus(_vm.SelectedTab.DocumentId,value);UpdateStatusIndicator(value);}}
    private void UpdateStatusIndicator(string? status)
    {
        var key=status switch
        {
            "Завершён"=>"State/Success",
            "На проверке"=>"State/Warning",
            "В работе"=>"Accent/Primary",
            "Архив"=>"Text/Muted",
            _=>"State/Info"
        };
        StatusIndicator.SetResourceReference(System.Windows.Shapes.Shape.FillProperty,key);
        AutomationProperties.SetName(StatusIndicator,$"Статус: {status??"не задан"}");
    }
    /// <summary>
    /// Теги правятся списком: чип убирается крестиком, а окно добавления остаётся открытым,
    /// чтобы можно было навесить несколько тегов подряд.
    /// </summary>
    private void Tags_Click(object sender,RoutedEventArgs e)
    {
        if(_vm.SelectedTab is null){ToastService.Show("Теги","Сначала откройте документ.",ToastKind.Information);return;}
        TagsPopup.IsOpen=!TagsPopup.IsOpen;
        if(TagsPopup.IsOpen){TagInputBox.Text="";Dispatcher.BeginInvoke(new Action(()=>TagInputBox.Focus()));}
    }
    private void AddTag_Click(object sender,RoutedEventArgs e)=>AddTagFromInput();
    private void TagInput_KeyDown(object sender,KeyEventArgs e){if(e.Key!=Key.Enter)return;AddTagFromInput();e.Handled=true;}
    private void AddTagFromInput()
    {
        if(_vm.SelectedTab is null)return;
        var value=TagInputBox.Text.Trim();
        if(value.Length==0)return;
        // Запятая остаётся разделителем: так можно вставить сразу несколько тегов из буфера.
        var added=false;
        foreach(var tag in value.Split(',',StringSplitOptions.RemoveEmptyEntries|StringSplitOptions.TrimEntries))
        {
            if(_tags.Any(x=>string.Equals(x,tag,StringComparison.CurrentCultureIgnoreCase)))continue;
            _tags.Add(tag);added=true;
        }
        TagInputBox.Text="";TagInputBox.Focus();
        if(added)SaveTags();
    }
    private void RemoveTag_Click(object sender,RoutedEventArgs e)
    {
        if(_vm.SelectedTab is null||(sender as FrameworkElement)?.DataContext is not string tag)return;
        if(!_tags.Remove(tag))return;
        SaveTags();
    }
    private void SaveTags()
    {
        if(_vm.SelectedTab is null)return;
        _vm.Database.SetTags(_vm.SelectedTab.DocumentId,_tags.ToList());
        UpdateTagChips();
    }
    /// <summary>В строке свойств помещается пара чипов, остальные показываются счётчиком и в окне тегов.</summary>
    private void UpdateTagChips()
    {
        _visibleTags.Clear();
        foreach(var tag in _tags.Take(1))_visibleTags.Add(tag);
        var hidden=_tags.Count-_visibleTags.Count;
        TagsOverflowText.Text=hidden>0?$"+{hidden}":"";
        TagsOverflowText.Visibility=hidden>0?Visibility.Visible:Visibility.Collapsed;
        TagEditorEmptyText.Visibility=_tags.Count==0?Visibility.Visible:Visibility.Collapsed;
        TagsButton.ToolTip=_tags.Count==0?"Добавить теги":"Теги: "+string.Join(", ",_tags);
    }
    private void Editor_SelectionChanged(object sender, RoutedEventArgs e)
    {
        var family = Editor.Selection.GetPropertyValue(TextElement.FontFamilyProperty); if (family != DependencyProperty.UnsetValue) FontFamilyBox.SelectedItem = family;
        var size = Editor.Selection.GetPropertyValue(TextElement.FontSizeProperty); if (size is double d) FontSizeBox.Text = Math.Round(d).ToString();
    }
    private void Save_Executed(object sender, ExecutedRoutedEventArgs e)=>SaveAllWithStatus();
    private void SetSaveStatus(string text,string brushKey,bool failed)
    {
        SaveStatusText.Text=text;
        SaveStatusText.SetResourceReference(TextBlock.ForegroundProperty,brushKey);
        SaveStatusText.ToolTip=failed
            ?"Не удалось сохранить изменения. Нажмите, чтобы повторить. Подробности записаны в журнал."
            :"Состояние локального сохранения";
        _saveFailed=failed;
        AutomationProperties.SetHelpText(SaveStatusText,SaveStatusText.ToolTip?.ToString()??text);
    }
    private void SaveAllWithStatus()
    {
        SetSaveStatus("Сохранение…","Text/Secondary",false);
        var success=_vm.SaveAll();
        SetSaveStatus(success?"Сохранено":"Ошибка сохранения — повторить",success?"Text/Muted":"State/Error",!success);
        if(success&&_vm.SelectedTab is not null)UpdateDocumentHeader();
    }
    private void SaveStatus_Click(object sender,MouseButtonEventArgs e)
    {
        if(_saveFailed||_vm.Tabs.Any(tab=>tab.IsDirty))
            SaveAllWithStatus();
    }
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
    private void ModernEditor_SelectionChanged(object? sender,JsonElement state)
    {
        _updatingToolbarState=true;
        try
        {
            BoldToggle.IsChecked=state.TryGetProperty("bold",out var bold)&&bold.GetBoolean();
            ItalicToggle.IsChecked=state.TryGetProperty("italic",out var italic)&&italic.GetBoolean();
            UnderlineToggle.IsChecked=state.TryGetProperty("underline",out var underline)&&underline.GetBoolean();
            StrikeToggle.IsChecked=state.TryGetProperty("strike",out var strike)&&strike.GetBoolean();
            if(state.TryGetProperty("fontFamily",out var family)&&!string.IsNullOrWhiteSpace(family.GetString()))
                FontFamilyBox.SelectedItem=Fonts.SystemFontFamilies.FirstOrDefault(x=>x.Source.Equals(family.GetString(),StringComparison.OrdinalIgnoreCase));
            if(state.TryGetProperty("fontSize",out var size))
            {
                var value=size.GetString()?.Replace("px","");
                if(double.TryParse(value,System.Globalization.NumberStyles.Any,System.Globalization.CultureInfo.InvariantCulture,out var parsed))
                    FontSizeBox.Text=parsed.ToString();
            }
            _spaceBefore=JsonNumber(state,"spaceBefore");
            _spaceAfter=JsonNumber(state,"spaceAfter");
            _firstIndent=JsonNumber(state,"firstIndent");
            _leftIndent=JsonNumber(state,"leftIndent");
            _rightIndent=JsonNumber(state,"rightIndent");
            _textDirection=state.TryGetProperty("textDirection",out var direction)?direction.GetString():null;
            _modernImageSelected=state.TryGetProperty("imageSelected",out var selected)&&selected.GetBoolean();
            _selectedImageSource=state.TryGetProperty("imageSrc",out var source)?source.GetString()??"":"";
            _selectedImageAlt=state.TryGetProperty("imageAlt",out var alt)?alt.GetString()??"":"";
            _selectedImageCaption=state.TryGetProperty("imageCaption",out var caption)?caption.GetString()??"":"";
            _selectedImageWrap=state.TryGetProperty("imageWrap",out var wrap)?wrap.GetString()??"none":"none";
            if(state.TryGetProperty("position",out var position)&&position.TryGetInt32(out var editorPosition))
                UpdateCurrentOutlineItem(editorPosition);
        }
        finally{_updatingToolbarState=false;}
    }
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
    // Файл вложения хранится под именем «<sha256>.<расширение>»: одинаковые файлы не дублируются,
    // а содержимое кладётся и в локальную папку Assets, и в базу данных — тогда изображение
    // отображается у всех, кто подключён к этой базе, а не только на компьютере автора.
    private (string StoredName,string Hash,long Size) StoreAsset(string source)=>StoreAssetBytes(File.ReadAllBytes(source),Path.GetFileName(source),GuessMimeType(source));
    private (string StoredName,string Hash,long Size) StoreAssetBytes(byte[] bytes,string fileName,string mime){var hash=Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes));var existing=_vm.Database.FindStoredAttachmentByHash(hash);if(existing is not null&&_vm.Database.MaterializeAsset(existing))return(existing,hash,bytes.LongLength);var extension=Path.GetExtension(fileName).ToLowerInvariant();if(!Regex.IsMatch(extension,@"^\.[a-z0-9]{1,10}$"))extension=mime.ToLowerInvariant() switch{"image/png"=>".png","image/jpeg"=>".jpg","image/gif"=>".gif","image/webp"=>".webp","application/pdf"=>".pdf",_=>".bin"};var name=hash.ToLowerInvariant()+extension;_vm.Database.StoreAssetContent(name,hash,mime,bytes);return(name,hash,bytes.LongLength);}
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
    private string? ResolveAssetPath(string source){if(!Uri.TryCreate(source,UriKind.Absolute,out var uri)||!uri.Host.Equals("assets.local",StringComparison.OrdinalIgnoreCase))return null;var file=Path.GetFileName(Uri.UnescapeDataString(uri.AbsolutePath));_vm.Database.MaterializeAsset(file);var root=Path.GetFullPath(AppPaths.Assets).TrimEnd(Path.DirectorySeparatorChar)+Path.DirectorySeparatorChar;var path=Path.GetFullPath(Path.Combine(AppPaths.Assets,file));return path.StartsWith(root,StringComparison.OrdinalIgnoreCase)&&File.Exists(path)?path:null;}
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
        if(!Uri.TryCreate(source,UriKind.Absolute,out var uri)||!uri.Host.Equals("assets.local",StringComparison.OrdinalIgnoreCase))return;var relative=Uri.UnescapeDataString(uri.AbsolutePath.TrimStart('/')).Replace('/',Path.DirectorySeparatorChar);_vm.Database.MaterializeAsset(relative);var root=Path.GetFullPath(AppPaths.Assets).TrimEnd(Path.DirectorySeparatorChar)+Path.DirectorySeparatorChar;var path=Path.GetFullPath(Path.Combine(AppPaths.Assets,relative));if(!path.StartsWith(root,StringComparison.OrdinalIgnoreCase)||!File.Exists(path))return;Process.Start(new ProcessStartInfo(path){UseShellExecute=true});
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
    /// <summary>Предрелизная копия помечается в заголовке: по сборке иначе не видно, что она бета.</summary>
    private void ShowPreReleaseBadge()
    {
        if(!AppVersion.IsPreRelease)return;
        PreReleaseBadgeText.Text=AppVersion.Display.Split('-')[1].ToUpperInvariant();
        PreReleaseBadge.ToolTip=$"Предварительная версия {AppVersion.Display}";
        PreReleaseBadge.Visibility=Visibility.Visible;
    }
    private bool _updateOffered;
    /// <summary>
    /// Обновление ставится тем же установщиком, что и само приложение: приложение скачивает его,
    /// запускает в режиме обновления и закрывается — установщик распакует новую версию в ту же
    /// папку и запустит приложение обратно. Скачивать релиз вручную не нужно.
    /// </summary>
    private async Task CheckForUpdatesAsync(bool silent)
    {
        if(_updateOffered)return;
        // Бета-копия видит и предварительные выпуски, стабильная — только стабильные.
        var release=await UpdateService.CheckAsync(AppVersion.IsPreRelease);
        if(release is null)
        {
            if(!silent)ToastService.Show("Обновлений нет",$"Установлена последняя версия {AppVersion.Display}.",ToastKind.Information,null,"Страница загрузок",()=>OpenWebPage(UpdateService.ReleasesPageUrl));
            return;
        }
        _updateOffered=true;
        ToastService.Show(
            $"Доступна версия {release.Display}{(release.IsPreRelease?" (бета)":"")}",
            $"Текущая версия {AppVersion.Display}. Обновление скачается и установится само, документы останутся на месте.",
            ToastKind.Information,
            TimeSpan.FromSeconds(12),
            "Обновить",
            ()=>Dispatcher.BeginInvoke(new Action(async()=>await InstallUpdateAsync(release))),
            "Страница выпуска",
            ()=>OpenWebPage(release.PageUrl));
    }
    private void Downloads_Click(object sender,RoutedEventArgs e)=>OpenWebPage(UpdateService.ReleasesPageUrl);
    private async Task InstallUpdateAsync(UpdateRelease release)
    {
        if(OperationOverlay.Visibility==Visibility.Visible)return;
        if(MessageBox.Show(this,$"Обновить MasterDocumentation до версии {release.Display}?\n\nПриложение закроется, установщик обновит папку {UpdateService.InstallDirectory} и запустит его снова.\n\nДокументация и настройки сохранятся.","Обновление",MessageBoxButton.YesNo,MessageBoxImage.Question)!=MessageBoxResult.Yes)return;
        var size=release.InstallerSize>0?$" ({release.InstallerSize/1024d/1024d:0} МБ)":"";
        ShowOperationOverlay("Загрузка обновления",$"Скачивается установщик версии {release.Display}{size}. Не закрывайте приложение.");
        try
        {
            var progress=new Progress<double>(value=>OperationDescription.Text=$"Скачивается установщик версии {release.Display}{size}: {value:0}%.");
            var installer=await UpdateService.DownloadAsync(release,progress);
            _vm.SaveAll();
            UpdateService.LaunchInstaller(installer);
            Application.Current.Shutdown();
        }
        catch(Exception ex)
        {
            LogService.Error("Не удалось обновить приложение",ex);
            HideOperationOverlay();
            MessageBox.Show(this,ex.Message,"Обновление",MessageBoxButton.OK,MessageBoxImage.Error);
        }
    }
    private async void CheckUpdates_Click(object sender,RoutedEventArgs e)=>await CheckForUpdatesAsync(false);
    private NotificationCenterWindow? _notificationCenter;
    private DateTime _notificationCenterClosedAt;
    /// <summary>
    /// Центр уведомлений открывается и закрывается одной кнопкой. Панель закрывается сама при
    /// потере фокуса, поэтому нажатие по кнопке при открытой панели успевает закрыть её раньше
    /// обработчика — короткая пауза не даёт ей тут же открыться снова.
    /// </summary>
    private void NotificationCenter_Click(object sender,RoutedEventArgs e)
    {
        if(_notificationCenter is not null){_notificationCenter.Close();_notificationCenter=null;return;}
        if((DateTime.UtcNow-_notificationCenterClosedAt).TotalMilliseconds<250)return;
        var window=new NotificationCenterWindow(this);window.Closed+=(_,_)=>{_notificationCenterClosedAt=DateTime.UtcNow;if(ReferenceEquals(_notificationCenter,window))_notificationCenter=null;};_notificationCenter=window;window.ShowAbove(NotificationCenterButton);
    }
    private void UpdateNotificationBadge()
    {
        var unread=ToastService.Unread;NotificationBadgeText.Text=unread>99?"99+":unread.ToString();NotificationBadge.Visibility=unread>0?Visibility.Visible:Visibility.Collapsed;
    }
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

    private async void Backup_Click(object sender, RoutedEventArgs e)
    {
        _vm.SaveAll();
        string? password=null;
        if(_settingsService.Load().EncryptManualBackups){var prompt=new PasswordDialog("Пароль для новой резервной копии"){Owner=this};if(prompt.ShowDialog()!=true)return;password=prompt.Value;}
        await RunOperationAsync("Создание резервной копии","Документы и вложения сохраняются в локальный архив.",()=>_vm.Backups.CreateBackup(false,password),"Резервная копия создана");
    }
    private async void Restore_Click(object sender, RoutedEventArgs e)
    {
        var d = new OpenFileDialog { Filter = "Резервные копии (*.mdbackup)|*.mdbackup|Старые ZIP-копии (*.zip)|*.zip" };
        if (d.ShowDialog(this) != true || MessageBox.Show("Текущие данные будут заменены. Продолжить?", "Восстановление", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        if(OperationOverlay.Visibility==Visibility.Visible)return;
        try
        {
            string? password=null;
            if(BackupService.IsEncrypted(d.FileName)){var prompt=new PasswordDialog("Пароль зашифрованной резервной копии"){Owner=this};if(prompt.ShowDialog()!=true)return;password=prompt.Value;}
            _vm.SaveAll();
            ShowOperationOverlay("Восстановление из резервной копии","Данные заменяются содержимым выбранного архива. Не закрывайте приложение.");
            await Task.Run(()=>_vm.Backups.Restore(d.FileName,password));
            MessageBox.Show("Данные восстановлены. Приложение будет закрыто; запустите его снова.");
            Close();
        }
        catch (Exception ex) { LogService.Error("Ошибка восстановления", ex); MessageBox.Show("Не удалось восстановить: " + ex.Message); }
        finally { HideOperationOverlay(); }
    }
    private async void Export_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Выберите папку экспорта" };
        if (dialog.ShowDialog(this) != true) return;
        _vm.SaveAll();
        var target = Path.Combine(dialog.FolderName, "MasterDocumentation-Export-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"));
        await RunOperationAsync("Экспорт библиотеки","Создаётся переносимая копия локальной документации.",()=>{_vm.Backups.Export(target);return target;},"Экспорт создан");
    }
    private async Task RunOperationAsync(string title,string description,Func<string> operation,string successTitle)
    {
        if(OperationOverlay.Visibility==Visibility.Visible)return;
        ShowOperationOverlay(title,description);
        try
        {
            var result=await Task.Run(operation);
            ToastService.Show(successTitle,result,ToastKind.Success,TimeSpan.FromSeconds(8));
        }
        catch(Exception ex)
        {
            LogService.Error(title,ex);
            MessageBox.Show(this,ex.Message,title,MessageBoxButton.OK,MessageBoxImage.Error);
        }
        finally{HideOperationOverlay();}
    }
    private async void ExportDocument_Click(object sender, RoutedEventArgs e)
    {
        if (_vm.SelectedTab is null || InteractionState.GetStatus(ExportDocumentButton)==InteractionStatus.Loading)
            return;
        var dialog=new SaveFileDialog
        {
            Title="Экспорт документа",
            FileName=_vm.SelectedTab.Title,
            Filter="PDF (*.pdf)|*.pdf|Word (*.docx)|*.docx|HTML (*.html)|*.html|Markdown (*.md)|*.md|Текст (*.txt)|*.txt",
            InitialDirectory=AppPaths.Exports
        };
        if(dialog.ShowDialog(this)!=true)
            return;

        var tab=_vm.SelectedTab;
        _vm.Save(tab);
        var path=dialog.FileName;
        var html=tab.Html;
        var plainText=tab.PlainText;
        var title=tab.Title;
        var actionButton=sender as Button??ExportDocumentButton;
        var previousContent=actionButton.Content;
        actionButton.Content="Экспорт…";
        actionButton.IsEnabled=false;
        InteractionState.SetStatus(actionButton,InteractionStatus.Loading);
        if(!ReferenceEquals(actionButton,ExportDocumentButton))
        {
            ExportDocumentButton.IsEnabled=false;
            InteractionState.SetStatus(ExportDocumentButton,InteractionStatus.Loading);
        }
        try
        {
            if(!string.IsNullOrWhiteSpace(html))
                await Task.Run(()=>new DocumentExportService(AppPaths.Assets).Export(html,plainText,title,path));
            else
                new DocumentExportService(AppPaths.Assets).Export(Editor.Document,title,path);
            InteractionState.SetStatus(actionButton,InteractionStatus.Success);
            ToastService.Show(
                "Документ экспортирован",
                path,
                ToastKind.Success,
                TimeSpan.FromSeconds(3),
                "Открыть файл",
                ()=>OpenPath(path),
                "Показать в папке",
                ()=>ShowPathInFolder(path));
        }
        catch(Exception ex)
        {
            LogService.Error("Ошибка экспорта документа",ex);
            InteractionState.SetStatus(actionButton,InteractionStatus.Error);
            ToastService.Show(
                "Не удалось экспортировать документ",
                "Проверьте права записи, свободное место и не открыт ли файл в другой программе.",
                ToastKind.Error,
                TimeSpan.FromSeconds(30),
                "Повторить",
                ()=>Dispatcher.BeginInvoke(new Action(()=>ExportDocument_Click(ExportDocumentButton,new RoutedEventArgs()))),
                "Подробнее",
                ()=>Dispatcher.BeginInvoke(new Action(()=>MessageBox.Show(this,ex.Message,"Ошибка экспорта",MessageBoxButton.OK,MessageBoxImage.Error))));
        }
        finally
        {
            actionButton.Content=previousContent;
            actionButton.IsEnabled=true;
            if(InteractionState.GetStatus(actionButton)==InteractionStatus.Loading)
                InteractionState.SetStatus(actionButton,InteractionStatus.Idle);
            if(!ReferenceEquals(actionButton,ExportDocumentButton))
            {
                ExportDocumentButton.IsEnabled=true;
                InteractionState.SetStatus(ExportDocumentButton,InteractionStatus.Idle);
            }
            var completedButton=actionButton;
            _=Dispatcher.InvokeAsync(async()=>{
                await Task.Delay(1400);
                if(InteractionState.GetStatus(completedButton) is InteractionStatus.Success or InteractionStatus.Error)
                    InteractionState.SetStatus(completedButton,InteractionStatus.Idle);
            });
        }
    }
    private static void OpenPath(string path)
    {
        if(File.Exists(path))
            Process.Start(new ProcessStartInfo(path){UseShellExecute=true});
    }
    /// <summary>Открывает страницу выпусков во внешнем браузере.</summary>
    private static void OpenWebPage(string url)
    {
        try{if(Uri.TryCreate(url,UriKind.Absolute,out var uri)&&uri.Scheme is "http" or "https")Process.Start(new ProcessStartInfo(uri.AbsoluteUri){UseShellExecute=true});}
        catch(Exception ex){LogService.Error("Не удалось открыть страницу выпусков",ex);}
    }
    private static void ShowPathInFolder(string path)
    {
        if(File.Exists(path))
            Process.Start(new ProcessStartInfo("explorer.exe",$"/select,\"{path}\""){UseShellExecute=true});
        else if(Directory.Exists(path))
            Process.Start(new ProcessStartInfo("explorer.exe",$"\"{path}\""){UseShellExecute=true});
    }
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
    private void RestoreWindow()
    {
        var width = double.TryParse(_vm.Database.GetSetting("Width"), out var storedWidth) ? storedWidth : Width;
        var height = double.TryParse(_vm.Database.GetSetting("Height"), out var storedHeight) ? storedHeight : Height;
        var left = double.TryParse(_vm.Database.GetSetting("Left"), out var storedLeft) ? storedLeft : SystemParameters.WorkArea.Left + 40;
        var top = double.TryParse(_vm.Database.GetSetting("Top"), out var storedTop) ? storedTop : SystemParameters.WorkArea.Top + 40;
        WindowPlacementService.RestoreVisibleBounds(this, left, top, width, height);
    }
    private void RestorePanelLayout()
    {
        if(double.TryParse(_vm.Database.GetSetting("LibraryWidth"),out var library))LibraryColumn.Width=new GridLength(Math.Clamp(library,LibraryMinWidth,360));
        if(double.TryParse(_vm.Database.GetSetting("StructureWidth"),out var structure))StructureColumn.Width=new GridLength(Math.Clamp(structure,168,330));
        if(double.TryParse(_vm.Database.GetSetting("PropertiesWidth"),out var properties))PropertiesColumn.Width=new GridLength(Math.Clamp(properties,PropertiesMinWidth,380));
        _wideStructureWidth=StructureColumn.Width;
        _widePropertiesWidth=PropertiesColumn.Width;
        _libraryPanelCollapsed=_vm.Database.GetSetting("LibraryPanelCollapsed")=="1";
        _structurePanelCollapsed=_vm.Database.GetSetting("StructurePanelCollapsed")=="1";
        _propertiesPanelCollapsed=_vm.Database.GetSetting("PropertiesPanelCollapsed")=="1";
        ApplyResponsiveLayout(ActualWidth>0?ActualWidth:Width);
    }
    private void Window_SizeChanged(object sender,SizeChangedEventArgs e)
        => ApplyResponsiveLayout(e.NewSize.Width);
    private void ApplyResponsiveLayout(double width)
    {
        if(BrandColumn is null||TopSearchBorder is null||LibraryColumn is null)return;
        _brandTitle??=FindVisualDescendants<TextBlock>(this).FirstOrDefault(text=>text.Text=="MasterDocumentation");
        _templatesButton??=FindVisualDescendants<Button>(this).FirstOrDefault(button=>button.ToolTip?.ToString()=="Открыть шаблоны документов");
        _applicationsButton??=FindVisualDescendants<Button>(this).FirstOrDefault(button=>button.ToolTip?.ToString()=="Открыть локальные инструменты");
        var compactHeader=width<1050;
        if(_brandTitle is not null){_brandTitle.Visibility=compactHeader?Visibility.Collapsed:Visibility.Visible;_brandTitle.ToolTip="MasterDocumentation";}
        if(_templatesButton is not null)_templatesButton.Content=compactHeader?"Шаблоны":"▣  Шаблоны";
        if(_applicationsButton is not null)_applicationsButton.Content=compactHeader?"Приложения":"▦  Приложения";
        BrandColumn.Width=new GridLength(compactHeader?96:width<1250?220:width<1420?240:260);
        TopSearchBorder.Width=width<1250?150:width<1420?180:202;
        TitleTools.Visibility=width<1010?Visibility.Collapsed:Visibility.Visible;

        var nextMode=width<1040?2:width<1220?1:0;
        if(StructureColumn.Width.Value>0)_wideStructureWidth=StructureColumn.Width;
        if(PropertiesColumn.Width.Value>0)_widePropertiesWidth=PropertiesColumn.Width;
        LibraryColumn.MinWidth=_libraryPanelCollapsed?0:LibraryMinWidth;
        LibraryColumn.Width=_libraryPanelCollapsed?new GridLength(0):new GridLength(Math.Clamp(LibraryColumn.Width.Value,LibraryMinWidth,360));
        WorkspaceGrid.ColumnDefinitions[1].Width=_libraryPanelCollapsed?new GridLength(0):new GridLength(6);
        var hideStructure=_structurePanelCollapsed||nextMode==2;
        StructureColumn.MinWidth=hideStructure?0:168;
        StructureColumn.Width=hideStructure?new GridLength(0):_wideStructureWidth;
        WorkspaceGrid.ColumnDefinitions[3].Width=hideStructure?new GridLength(0):new GridLength(6);
        var hideProperties=_propertiesPanelCollapsed||nextMode>=1;
        PropertiesColumn.MinWidth=hideProperties?0:PropertiesMinWidth;
        PropertiesColumn.Width=hideProperties?new GridLength(0):_widePropertiesWidth;
        WorkspaceGrid.ColumnDefinitions[5].Width=hideProperties?new GridLength(0):new GridLength(6);
        LibraryPanelMenuItem.IsChecked=!_libraryPanelCollapsed;
        StructurePanelMenuItem.IsChecked=!_structurePanelCollapsed;
        PropertiesPanelMenuItem.IsChecked=!_propertiesPanelCollapsed;
        _responsiveLayoutMode=nextMode;

        // Панель свойств не сужается ниже PropertiesMinWidth ни при каком размере окна:
        // при меньшей ширине подписи и поле состояния обрезаются.
        if(width<1300){LibraryColumn.MaxWidth=LibraryMinWidth;StructureColumn.MaxWidth=185;PropertiesColumn.MaxWidth=PropertiesMinWidth;}
        else if(width<1450){LibraryColumn.MaxWidth=235;StructureColumn.MaxWidth=205;PropertiesColumn.MaxWidth=285;}
        else{LibraryColumn.MaxWidth=double.PositiveInfinity;StructureColumn.MaxWidth=double.PositiveInfinity;PropertiesColumn.MaxWidth=double.PositiveInfinity;}
    }
    private void EditorPanel_SizeChanged(object sender,SizeChangedEventArgs e)=>UpdateStartPageLayout(e.NewSize.Width);
    private static IEnumerable<T> FindVisualDescendants<T>(DependencyObject root) where T:DependencyObject
    {
        for(var index=0;index<VisualTreeHelper.GetChildrenCount(root);index++)
        {
            var child=VisualTreeHelper.GetChild(root,index);
            if(child is T match)yield return match;
            foreach(var descendant in FindVisualDescendants<T>(child))yield return descendant;
        }
    }
    private void ConfigureAccessibility()
    {
        KeyboardNavigation.SetTabNavigation(this,KeyboardNavigationMode.Continue);
        AutomationProperties.SetName(Tree,"Библиотека документов");
        AutomationProperties.SetHelpText(Tree,"Стрелки перемещают выбор, Enter открывает документ, F2 переименовывает.");
        AutomationProperties.SetName(StructureTree,"Структура текущего документа");
        AutomationProperties.SetName(ModernEditor,"Редактор документа");
        AutomationProperties.SetName(DocumentTitleBox,"Название текущего документа");
        AutomationProperties.SetHelpText(DocumentTitleBox,"Введите новое название и нажмите Enter.");
        AutomationProperties.SetName(AttachmentList,"Вложения текущего документа");
        AutomationProperties.SetLiveSetting(SaveStatusText,AutomationLiveSetting.Polite);
        foreach(var button in FindVisualDescendants<ButtonBase>(this))
        {
            var currentName=AutomationProperties.GetName(button);
            if(!string.IsNullOrWhiteSpace(currentName)&&!currentName.Contains("System.Windows.Controls.",StringComparison.Ordinal))continue;
            var name=button.ToolTip?.ToString();
            if(string.IsNullOrWhiteSpace(name))
                name=FindVisualDescendants<TextBlock>(button).Select(text=>text.Text).FirstOrDefault(text=>!string.IsNullOrWhiteSpace(text));
            if(string.IsNullOrWhiteSpace(name))name=button.Content?.ToString() switch
            {
                "☰"=>"Главное меню",
                "—"=>"Свернуть окно",
                "□"=>"Развернуть или восстановить окно",
                "×"=>"Закрыть",
                _=>button.Content?.ToString()
            };
            if(!string.IsNullOrWhiteSpace(name))AutomationProperties.SetName(button,name);
        }
    }
    private void UpdateStartPageLayout(double width)
    {
        if(StartPrimaryActions is null||StartCardsGrid is null)return;
        StartPrimaryActions.Columns=width<620?1:3;
        StartPageContent.Margin=width<620?new Thickness(16):new Thickness(24);
        var stackCards=width<700;
        if(stackCards)
        {
            StartCardsGapColumn.Width=new GridLength(0);StartQuickColumn.Width=new GridLength(0);
            Grid.SetRow(StartQuickCard,1);Grid.SetColumn(StartQuickCard,0);Grid.SetColumnSpan(StartQuickCard,3);StartQuickCard.Margin=new Thickness(0,12,0,0);StartRecentCard.MinHeight=160;
        }
        else
        {
            StartCardsGapColumn.Width=new GridLength(18);StartQuickColumn.Width=new GridLength(260);
            Grid.SetRow(StartQuickCard,0);Grid.SetColumn(StartQuickCard,2);Grid.SetColumnSpan(StartQuickCard,1);StartQuickCard.Margin=new Thickness(0);StartRecentCard.MinHeight=210;
        }
    }
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
        _saveTimer.Stop();_draftTimer.Stop();
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
    private void SaveSessionAndWindowState()
    {
        var settings=_settingsService.Load();
        settings.OpenDocumentIds=_vm.Tabs.Select(x=>x.DocumentId).ToList();
        settings.SelectedDocumentId=_vm.SelectedTab?.DocumentId;
        _settingsService.Save(settings);
        _vm.Database.SetSetting("LibraryWidth",LibraryColumn.ActualWidth.ToString());
        _vm.Database.SetSetting("StructureWidth",(_responsiveLayoutMode==2?_wideStructureWidth.Value:StructureColumn.ActualWidth).ToString());
        _vm.Database.SetSetting("PropertiesWidth",(_responsiveLayoutMode>=1?_widePropertiesWidth.Value:PropertiesColumn.ActualWidth).ToString());
        if(WindowState==WindowState.Normal)
        {
            _vm.Database.SetSetting("Width",Width.ToString());
            _vm.Database.SetSetting("Height",Height.ToString());
            _vm.Database.SetSetting("Left",Left.ToString());
            _vm.Database.SetSetting("Top",Top.ToString());
        }
    }
    private void Exit_Click(object sender, RoutedEventArgs e) => Close();
    private void AppMenu_Click(object sender,RoutedEventArgs e)
    {
        if(AppMenuButton.ContextMenu is null)return;
        AppMenuButton.ContextMenu.PlacementTarget=AppMenuButton;
        AppMenuButton.ContextMenu.Placement=PlacementMode.Bottom;
        AppMenuButton.ContextMenu.IsOpen=true;
    }
    private void ToggleLibraryPanel_Click(object sender,RoutedEventArgs e)
    {
        _libraryPanelCollapsed=LibraryPanelMenuItem.IsChecked!=true;
        _vm.Database.SetSetting("LibraryPanelCollapsed",_libraryPanelCollapsed?"1":"0");
        ApplyResponsiveLayout(ActualWidth);
    }
    private void ToggleStructurePanel_Click(object sender,RoutedEventArgs e)
    {
        _structurePanelCollapsed=StructurePanelMenuItem.IsChecked!=true;
        _vm.Database.SetSetting("StructurePanelCollapsed",_structurePanelCollapsed?"1":"0");
        ApplyResponsiveLayout(ActualWidth);
    }
    private void TogglePropertiesPanel_Click(object sender,RoutedEventArgs e)
    {
        _propertiesPanelCollapsed=PropertiesPanelMenuItem.IsChecked!=true;
        _vm.Database.SetSetting("PropertiesPanelCollapsed",_propertiesPanelCollapsed?"1":"0");
        ApplyResponsiveLayout(ActualWidth);
    }
    private void FocusMode_Click(object sender,RoutedEventArgs e)=>ToggleFocusMode();
    private void Minimize_Click(object sender, RoutedEventArgs e) => SystemCommands.MinimizeWindow(this);
    private void Maximize_Click(object sender, RoutedEventArgs e)
    {
        if (WindowState == WindowState.Maximized)
            SystemCommands.RestoreWindow(this);
        else
            SystemCommands.MaximizeWindow(this);
    }
    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        if(SettingsHost.Visibility==Visibility.Visible){if(_settingsView?.RequestClose()!=false)HideSettings();return;}
        _vm.SaveAll();
        ModernEditor.SetBrowserVisible(false);ModernEditor.Visibility=Visibility.Collapsed;Editor.Visibility=Visibility.Collapsed;EmptyHint.Visibility=Visibility.Collapsed;
        TabsOverflowPopup.IsOpen=false;
        _settingsView=new SettingsView(_vm.Database,_vm.Backups,_settingsService);_settingsView.SettingsSaved+=ApplySavedSettings;_settingsView.CloseRequested+=HideSettings;SettingsHost.Content=_settingsView;SettingsHost.Visibility=Visibility.Visible;Tabs.Visibility=Visibility.Collapsed;SettingsTabHeader.Visibility=Visibility.Visible;Dispatcher.BeginInvoke(new Action(UpdateSettingsHostSize));
    }
    private void ApplySavedSettings(ApplicationSettings settings){_settings=settings;ApplyInterfacePreferences(_settings);_saveTimer.Interval=TimeSpan.FromSeconds(_settings.AutoSaveDelaySeconds);_vm.SetTheme(_settings.Theme,false);_=ModernEditor.ExecuteAsync("setTheme",new{theme=_vm.DarkTheme?"dark":"light"});Editor.FontFamily=new FontFamily(_settings.DefaultFont);Editor.FontSize=_settings.DefaultFontSize;Editor.SpellCheck.IsEnabled=_settings.SpellCheck;}
    private void SettingsHost_SizeChanged(object sender,SizeChangedEventArgs e)=>UpdateSettingsHostSize();
    private void UpdateSettingsHostSize()
    {
        if(_settingsView is null)return;
        _settingsView.Width=Math.Max(0,SettingsHost.ActualWidth);
        _settingsView.Height=Math.Max(0,SettingsHost.ActualHeight);
    }
    private void ApplyInterfacePreferences(ApplicationSettings settings)
    {
        if(Application.Current is not null)
        {
            Application.Current.Resources["Control.Height.Default"]=settings.CompactMode?32d:36d;
            Application.Current.Resources["Tree.RowHeight"]=settings.CompactMode?30d:34d;
            Application.Current.Resources["Control.Padding.Default"]=settings.CompactMode?new Thickness(9,5,9,5):new Thickness(12,7,12,7);
        }
        ToolTipService.SetIsEnabled(this,settings.ShowTooltips);
    }
    private void HideSettings(){SettingsHost.Visibility=Visibility.Collapsed;SettingsHost.Content=null;_settingsView=null;SettingsTabHeader.Visibility=Visibility.Collapsed;Tabs.Visibility=Visibility.Visible;ShowSelectedTab(true);ModernEditor.SetBrowserVisible(true);}
    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if(e.Key==Key.Escape&&CloseTransientSurface()){}
        else if(e.Key==Key.F6)FocusNextRegion();
        else if(Keyboard.Modifiers==ModifierKeys.Control&&e.Key==Key.K){GlobalSearchBox.Focus();GlobalSearchBox.SelectAll();}
        else if(Keyboard.Modifiers==ModifierKeys.Control&&e.Key>=Key.D1&&e.Key<=Key.D9)SelectTabByNumber((int)e.Key-(int)Key.D1);
        else if(Keyboard.Modifiers==ModifierKeys.Control&&e.Key==Key.O)ImportDocument_Click(this,new RoutedEventArgs());
        else if(Keyboard.Modifiers==ModifierKeys.Control&&e.Key==Key.P)Print_Click(this,new RoutedEventArgs());
        else if(Keyboard.Modifiers==ModifierKeys.Shift&&e.Key==Key.F10&&Tree.IsKeyboardFocusWithin&&Tree.ContextMenu is not null){Tree.ContextMenu.PlacementTarget=Tree;Tree.ContextMenu.IsOpen=true;}
        else if(e.Key==Key.F2&&Tree.IsKeyboardFocusWithin&&_vm.SelectedNode is not null)Rename_Click(this,new RoutedEventArgs());
        else if(e.Key==Key.Delete&&Tree.IsKeyboardFocusWithin&&_vm.SelectedNode is not null)Delete_Click(this,new RoutedEventArgs());
        else if(e.Key==Key.F11)WindowState=WindowState==WindowState.Maximized?WindowState.Normal:WindowState.Maximized;
        else if(Keyboard.Modifiers==(ModifierKeys.Control|ModifierKeys.Shift)&&e.Key==Key.F)ToggleFocusMode();
        else if(Matches("NewDocument",e))NewDocument_Click(this,new RoutedEventArgs());
        else if(Matches("NewFolder",e))NewFolder_Click(this,new RoutedEventArgs());
        else if(Matches("Save",e))SaveAllWithStatus();
        else if(Matches("Export",e))ExportDocument_Click(this,new RoutedEventArgs());
        else if(Matches("Settings",e))Settings_Click(this,new RoutedEventArgs());
        else if(Matches("CloseTab",e))CloseCurrentTab();
        else if(Matches("NextTab",e))SelectRelativeTab(1);
        else if(Matches("PreviousTab",e))SelectRelativeTab(-1);
        else return;
        e.Handled=true;
    }
    private bool CloseTransientSurface()
    {
        if(CreateMenuPopup.IsOpen){CreateMenuPopup.IsOpen=false;return true;}
        if(SectionAddPopup.IsOpen){SectionAddPopup.IsOpen=false;return true;}
        if(SettingsHost.Visibility==Visibility.Visible){if(_settingsView?.RequestClose()!=false)HideSettings();return true;}
        if(AttachmentsPanel.Visibility==Visibility.Visible){PropertiesTab_Click(this,new RoutedEventArgs());return true;}
        if(_focusMode){ToggleFocusMode();return true;}
        return false;
    }
    private void ToggleFocusMode()
    {
        if(!_focusMode)
        {
            _focusLibraryWidth=LibraryColumn.Width;
            _focusStructureWidth=StructureColumn.Width;
            _focusPropertiesWidth=PropertiesColumn.Width;
            LibraryColumn.MinWidth=0;
            StructureColumn.MinWidth=0;
            PropertiesColumn.MinWidth=0;
            LibraryColumn.Width=new GridLength(0);
            StructureColumn.Width=new GridLength(0);
            PropertiesColumn.Width=new GridLength(0);
            WorkspaceGrid.ColumnDefinitions[1].Width=new GridLength(0);
            WorkspaceGrid.ColumnDefinitions[3].Width=new GridLength(0);
            WorkspaceGrid.ColumnDefinitions[5].Width=new GridLength(0);
            _focusMode=true;
            ToastService.Show("Режим фокусировки","Боковые панели скрыты. Нажмите Esc или Ctrl+Shift+F, чтобы вернуть их.",ToastKind.Information);
        }
        else
        {
            LibraryColumn.MinWidth=LibraryMinWidth;
            StructureColumn.MinWidth=160;
            PropertiesColumn.MinWidth=210;
            LibraryColumn.Width=_focusLibraryWidth.Value>0?_focusLibraryWidth:new GridLength(255);
            StructureColumn.Width=_focusStructureWidth.Value>0?_focusStructureWidth:_wideStructureWidth;
            PropertiesColumn.Width=_focusPropertiesWidth.Value>0?_focusPropertiesWidth:_widePropertiesWidth;
            WorkspaceGrid.ColumnDefinitions[1].Width=new GridLength(6);
            WorkspaceGrid.ColumnDefinitions[3].Width=new GridLength(6);
            WorkspaceGrid.ColumnDefinitions[5].Width=new GridLength(6);
            _focusMode=false;
            ApplyResponsiveLayout(ActualWidth);
        }
        ModernEditor.Focus();
    }
    private void FocusNextRegion()
    {
        var regions=new UIElement[]{Tree,StructureTree,ModernEditor,StatusBox}.Where(element=>element.IsVisible&&element.IsEnabled).ToList();
        if(regions.Count==0)return;
        var current=regions.FindIndex(element=>element.IsKeyboardFocusWithin);
        regions[(current+1+regions.Count)%regions.Count].Focus();
    }
    private bool Matches(string id,KeyEventArgs e){if(!_settings.Hotkeys.TryGetValue(id,out var value))return false;try{return new KeyGestureConverter().ConvertFromInvariantString(value) is KeyGesture gesture&&gesture.Matches(this,e);}catch{return false;}}
    private void CloseCurrentTab(){var tab=_vm.SelectedTab;if(tab is null)return;_vm.Save(tab);var index=_vm.Tabs.IndexOf(tab);_vm.Tabs.Remove(tab);_vm.SelectedTab=_vm.Tabs.Count==0?null:_vm.Tabs[Math.Clamp(index-1,0,_vm.Tabs.Count-1)];ShowSelectedTab();}
    private void SelectRelativeTab(int direction){if(_vm.Tabs.Count<2)return;var index=_vm.SelectedTab is null?0:_vm.Tabs.IndexOf(_vm.SelectedTab);_vm.SelectedTab=_vm.Tabs[(index+direction+_vm.Tabs.Count)%_vm.Tabs.Count];ShowSelectedTab();}
    private void SelectTabByNumber(int index){if(index<0||index>=_vm.Tabs.Count)return;_vm.SelectedTab=_vm.Tabs[index];Tabs.SelectedItem=_vm.SelectedTab;ShowSelectedTab(true);}
    private void About_Click(object sender, RoutedEventArgs e) => MessageBox.Show($"MasterDocumentation {AppVersion.Display}{(AppVersion.IsPreRelease?" — предварительная версия":"")}\nПереносимая локальная система документации\n.NET 8 / WPF / TipTap\nХранилище: SQLite или PostgreSQL, с переносом данных между ними", "О программе");
}
