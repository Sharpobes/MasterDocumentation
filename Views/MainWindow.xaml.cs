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
using MasterDocumentation.Models;
using MasterDocumentation.Services;
using MasterDocumentation.Utilities;
using MasterDocumentation.ViewModels;
using MasterDocumentation.Editor;

namespace MasterDocumentation.Views;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm; private readonly SettingsService _settingsService; private ApplicationSettings _settings; private readonly System.Windows.Threading.DispatcherTimer _saveTimer; private readonly System.Windows.Threading.DispatcherTimer _highlightTimer; private bool _loading; private bool _highlighting; private bool _settingModernContent; private Point _dragStart;
    public MainWindow(MainViewModel viewModel, SettingsService settingsService)
    {
        InitializeComponent(); _vm = viewModel; _settingsService = settingsService; DataContext = _vm;
        FontFamilyBox.ItemsSource = Fonts.SystemFontFamilies.OrderBy(f => f.Source); FontFamilyBox.SelectedItem = new FontFamily("Segoe UI");
        FontSizeBox.ItemsSource = new[] { 8d, 9d, 10d, 11d, 12d, 13d, 14d, 16d, 18d, 20d, 24d, 28d, 32d, 40d, 48d, 64d }; FontSizeBox.SelectedItem = 13d;
        _settings = _settingsService.Load();
        _saveTimer = new() { Interval = TimeSpan.FromSeconds(_settings.AutoSaveDelaySeconds) }; _saveTimer.Tick += (_, _) => { _saveTimer.Stop(); _vm.Save(_vm.SelectedTab); };
        _highlightTimer = new() { Interval = TimeSpan.FromMilliseconds(450) }; _highlightTimer.Tick += (_, _) => { _highlightTimer.Stop(); HighlightCodeBlocks(); };
        UpdateNavigationCounts();
        RestoreWindow(); Loaded += (_, _) => { RestoreSession(); AutoBackupIfNeeded(); };
        AddHandler(Hyperlink.RequestNavigateEvent, new System.Windows.Navigation.RequestNavigateEventHandler((_, e) => { try { Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true }); } catch (Exception ex) { LogService.Error("Не удалось открыть ссылку", ex); } e.Handled = true; }));
    }

    private void Tree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e) => _vm.SelectedNode = e.NewValue as NodeItem;
    private void Tree_DoubleClick(object sender, MouseButtonEventArgs e) { if (_vm.SelectedNode is not null) _vm.Open(_vm.SelectedNode); }
    private void OpenNode_Click(object sender, RoutedEventArgs e) { if (_vm.SelectedNode is not null) _vm.Open(_vm.SelectedNode); }
    private long? TargetFolder() => _vm.SelectedNode is null ? null : _vm.SelectedNode.IsFolder ? _vm.SelectedNode.Id : _vm.SelectedNode.ParentId;
    private void NewDocument_Click(object sender, RoutedEventArgs e) => CreateNode(false);
    private void NewFolder_Click(object sender, RoutedEventArgs e) => CreateNode(true);
    private void CreateNode(bool folder)
    {
        var roots=_vm.Database.LoadTree();var dialog=new NewItemDialog(roots,_vm.Database.LoadTemplates(),folder,TargetFolder()){Owner=this};if(dialog.ShowDialog()!=true)return;try{var id=dialog.TemplateId is long template?_vm.Database.CreateFromTemplate(template,dialog.ParentId,dialog.ItemTitle):_vm.Database.Create(dialog.ParentId,dialog.IsFolder,dialog.ItemTitle);_vm.ReloadTree();UpdateNavigationCounts();if(!dialog.IsFolder){var node=FindNode(_vm.Nodes,id);if(node is not null){_vm.Open(node);ShowSelectedTab();}}}catch(Exception ex){MessageBox.Show(this,ex.Message,"Не удалось создать",MessageBoxButton.OK,MessageBoxImage.Warning);}
    }
    private void Rename_Click(object sender, RoutedEventArgs e)
    {
        var node = _vm.SelectedNode; if (node is null) return; var prompt = new TextPrompt("Переименовать", node.Title) { Owner = this };
        if (prompt.ShowDialog() != true) return;try{_vm.Database.Rename(node.Id, prompt.Value); node.Title = prompt.Value; var tab = _vm.Tabs.FirstOrDefault(t => t.DocumentId == node.Id); if (tab is not null) tab.Title = prompt.Value;}catch(Exception ex){MessageBox.Show(this,ex.Message,"Переименование",MessageBoxButton.OK,MessageBoxImage.Warning);}
    }
    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        var node = _vm.SelectedNode; if (node is null) return;
        if (MessageBox.Show($"Удалить «{node.Title}»{(node.IsFolder ? " и всё содержимое" : "")}?", "Подтверждение", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        foreach (var tab in _vm.Tabs.Where(t => t.DocumentId == node.Id).ToList()) _vm.Tabs.Remove(tab); _vm.Database.Delete(node.Id); _vm.ReloadTree(); AllDocumentsCountText.Text = _vm.Database.CountDocuments().ToString(); ShowSelectedTab();
    }
    private static NodeItem? FindNode(IEnumerable<NodeItem> nodes, long id) { foreach (var n in nodes) { if (n.Id == id) return n; var child = FindNode(n.Children, id); if (child is not null) return child; } return null; }

    private void Tabs_SelectionChanged(object sender, SelectionChangedEventArgs e) { if (e.Source == Tabs) ShowSelectedTab(); }
    private void ShowSelectedTab()
    {
        _loading = true; if (_vm.SelectedTab is null) { Editor.Visibility = Visibility.Collapsed; ModernEditor.Visibility=Visibility.Collapsed; EmptyHint.Visibility = Visibility.Visible; }
        else { Editor.Document = _vm.SelectedTab.Document;Editor.Visibility=Visibility.Collapsed;ModernEditor.Visibility=Visibility.Visible; EmptyHint.Visibility = Visibility.Collapsed;var html=_vm.SelectedTab.Html;if(string.IsNullOrWhiteSpace(html)){var text=new TextRange(Editor.Document.ContentStart,Editor.Document.ContentEnd).Text;html="<p>"+System.Net.WebUtility.HtmlEncode(text).Replace("\r\n","</p><p>").Replace("\n","</p><p>")+"</p>";}_settingModernContent=true;_=ModernEditor.SetContentAsync(_vm.SelectedTab.EditorJson,html);LoadDocumentProperties(); } _loading = false; UpdateNavigationCounts(); UpdateDocumentStats();
    }
    private void CloseTab_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not DocumentTab tab) return; _vm.Save(tab); _vm.Tabs.Remove(tab); if (_vm.SelectedTab == tab) _vm.SelectedTab = _vm.Tabs.LastOrDefault(); ShowSelectedTab(); e.Handled = true;
    }
    private void Editor_TextChanged(object sender, TextChangedEventArgs e) { if (_loading || _highlighting || _vm.SelectedTab is null) return; _vm.SelectedTab.IsDirty = true; _saveTimer.Stop(); _saveTimer.Start(); UpdateDocumentStats(); if (Editor.CaretPosition.Paragraph?.Tag as string == "CodeBlock") { _highlightTimer.Stop(); _highlightTimer.Start(); } }
    private void UpdateDocumentStats() { if (_vm.SelectedTab is null) { DocumentStatsText.Text = "Слов: 0   Символов: 0"; return; } var text = !string.IsNullOrEmpty(_vm.SelectedTab.PlainText)?_vm.SelectedTab.PlainText:new TextRange(Editor.Document.ContentStart, Editor.Document.ContentEnd).Text.TrimEnd(); var words = Regex.Matches(text, @"[\p{L}\p{N}_]+", RegexOptions.CultureInvariant).Count; DocumentStatsText.Text = $"Слов: {words}   Символов: {text.Length}"; }
    private void ModernEditor_ContentChanged(object? sender,EditorContent content){if(_loading||_vm.SelectedTab is null)return;StructureList.ItemsSource=content.Headings;TocList.ItemsSource=content.Headings;if(_settingModernContent){_settingModernContent=false;return;}_vm.SelectedTab.EditorJson=content.Json;_vm.SelectedTab.Html=content.Html;_vm.SelectedTab.PlainText=content.PlainText;_vm.SelectedTab.IsDirty=true;_loading=true;Editor.Document=new FlowDocument(new Paragraph(new Run(content.PlainText)));_loading=false;UpdateDocumentStats();_saveTimer.Stop();_saveTimer.Start();}
    private void Heading_DoubleClick(object sender,MouseButtonEventArgs e){if((sender as ListBox)?.SelectedItem is EditorHeading heading)_=ModernEditor.ExecuteAsync("gotoHeading",new{pos=heading.Position});}
    private void UpdateNavigationCounts() { AllDocumentsCountText.Text = _vm.Database.CountDocuments().ToString(); FavoritesCountText.Text = _vm.Database.CountFavorites().ToString(); RecentDocumentsCountText.Text = _vm.Database.CountRecent().ToString(); TrashCountText.Text = _vm.Database.CountTrash().ToString(); }
    private void AllDocuments_Click(object sender, RoutedEventArgs e) => _vm.ShowAll();
    private void Favorites_Click(object sender, RoutedEventArgs e) => _vm.ShowFavorites();
    private void Recent_Click(object sender, RoutedEventArgs e) => _vm.ShowRecent(_settingsService.Load().RecentFilesCount);
    private void Trash_Click(object sender, RoutedEventArgs e) => _vm.ShowTrash();
    private void FavoriteNode_Click(object sender, RoutedEventArgs e) { if (_vm.SelectedNode is null || _vm.SelectedNode.IsFolder) return; _vm.Database.ToggleFavorite(_vm.SelectedNode.Id); UpdateNavigationCounts(); }
    private void RestoreNode_Click(object sender, RoutedEventArgs e) { if (_vm.SelectedNode is null) return; _vm.Database.Restore(_vm.SelectedNode.Id); _vm.ShowTrash(); UpdateNavigationCounts(); }
    private void MakeTemplate_Click(object sender,RoutedEventArgs e){if(_vm.SelectedNode is null||_vm.SelectedNode.IsFolder){MessageBox.Show(this,"Шаблоном может быть только документ.");return;}_vm.Save(_vm.Tabs.FirstOrDefault(x=>x.DocumentId==_vm.SelectedNode.Id));_vm.Database.SetTemplate(_vm.SelectedNode.Id,true);MessageBox.Show(this,"Документ добавлен в шаблоны. При создании нового документа его можно выбрать в поле «Создать из шаблона».","Шаблон");}
    private void Templates_Click(object sender,RoutedEventArgs e)=>_vm.ShowTemplates();
    private void Applications_Click(object sender,RoutedEventArgs e)=>new ApplicationsWindow{Owner=this}.ShowDialog();
    private void LoadDocumentProperties(){if(_vm.SelectedTab is null){AttachmentsHeaderText.Text="Вложения (0)";TagsButton.Content="＋ Добавить";return;}var status=_vm.Database.GetStatus(_vm.SelectedTab.DocumentId);StatusBox.SelectedItem=StatusBox.Items.OfType<ComboBoxItem>().FirstOrDefault(x=>x.Content?.ToString()==status);var tags=_vm.Database.GetTags(_vm.SelectedTab.DocumentId);TagsButton.Content=tags.Count==0?"＋ Добавить":string.Join(", ",tags);AttachmentsHeaderText.Text=$"Вложения ({_vm.Database.GetAttachments(_vm.SelectedTab.DocumentId).Count})";}
    private void Status_Changed(object sender,SelectionChangedEventArgs e){if(_loading||_vm?.SelectedTab is null||StatusBox.SelectedItem is not ComboBoxItem item)return;_vm.Database.SetStatus(_vm.SelectedTab.DocumentId,item.Content?.ToString()??"Черновик");}
    private void Tags_Click(object sender,RoutedEventArgs e){if(_vm.SelectedTab is null)return;var current=string.Join(", ",_vm.Database.GetTags(_vm.SelectedTab.DocumentId));var prompt=new TextPrompt("Теги через запятую",current){Owner=this};if(prompt.ShowDialog()!=true)return;_vm.Database.SetTags(_vm.SelectedTab.DocumentId,prompt.Value.Split(',',StringSplitOptions.RemoveEmptyEntries|StringSplitOptions.TrimEntries));LoadDocumentProperties();}
    private void Editor_SelectionChanged(object sender, RoutedEventArgs e)
    {
        var family = Editor.Selection.GetPropertyValue(TextElement.FontFamilyProperty); if (family != DependencyProperty.UnsetValue) FontFamilyBox.SelectedItem = family;
        var size = Editor.Selection.GetPropertyValue(TextElement.FontSizeProperty); if (size is double d) FontSizeBox.Text = Math.Round(d).ToString();
    }
    private void Save_Executed(object sender, ExecutedRoutedEventArgs e) => _vm.Save(_vm.SelectedTab);
    private void Find_Executed(object sender, ExecutedRoutedEventArgs e) => Tree.Focus();
    private void FontFamily_Changed(object sender, SelectionChangedEventArgs e) { if (Editor?.Selection is not null && FontFamilyBox.SelectedItem is FontFamily f) Editor.Selection.ApplyPropertyValue(TextElement.FontFamilyProperty, f); }
    private void FontSize_Changed(object sender, SelectionChangedEventArgs e) { if (FontSizeBox.SelectedItem is double d) ApplyFontSize(d); }
    private void FontSize_LostFocus(object sender, KeyboardFocusChangedEventArgs e) { if (double.TryParse(FontSizeBox.Text, out var d) && d is >= 6 and <= 200) ApplyFontSize(d); }
    private void ApplyFontSize(double size) => Editor.Selection.ApplyPropertyValue(TextElement.FontSizeProperty, size);
    private async void Heading_Changed(object sender, SelectionChangedEventArgs e) { if(ModernEditor is not null&&ModernEditor.Visibility==Visibility.Visible&&HeadingBox.SelectedItem is ComboBoxItem modern&&int.TryParse(modern.Tag?.ToString(),out var level)){await ModernEditor.ExecuteAsync(level==0?"paragraph":"heading",level==0?null:new{level});return;}if (Editor is null || HeadingBox.SelectedItem is not ComboBoxItem item || !double.TryParse(item.Tag?.ToString(), out var size)) return; ApplyFontSize(size); Editor.Selection.ApplyPropertyValue(TextElement.FontWeightProperty, size > 13 ? FontWeights.Bold : FontWeights.Normal); }
    private async void ModernCommand_Click(object sender,RoutedEventArgs e){if(sender is FrameworkElement{Tag:string command})await ModernEditor.ExecuteAsync(command);}
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
    private void Spoiler_Click(object sender, RoutedEventArgs e) { var existing = FindSpoiler(Editor.CaretPosition); if (existing is not null) { existing.Tag = null; existing.ClearValue(TextElement.ForegroundProperty); existing.ClearValue(TextElement.BackgroundProperty); return; } if (Editor.Selection.IsEmpty) return; var spoiler = new Span(Editor.Selection.Start, Editor.Selection.End) { Tag = "SpoilerHidden", Background = new SolidColorBrush(Color.FromRgb(55, 57, 62)), Foreground = Brushes.Transparent }; Editor.CaretPosition = spoiler.ElementEnd; }
    private static Span? FindSpoiler(TextPointer position) { DependencyObject? item = position.Parent as DependencyObject; while (item is not null) { if (item is Span { Tag: string tag } span && tag.StartsWith("Spoiler")) return span; item = item is FrameworkContentElement f ? f.Parent : null; } return null; }
    private void Editor_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e) { var point = Editor.GetPositionFromPoint(e.GetPosition(Editor), true); if (point is null || FindSpoiler(point) is not Span spoiler) return; var hidden = spoiler.Tag as string == "SpoilerHidden"; spoiler.Tag = hidden ? "SpoilerShown" : "SpoilerHidden"; spoiler.Foreground = hidden ? new SolidColorBrush(Color.FromRgb(245, 245, 245)) : Brushes.Transparent; e.Handled = true; }
    private void Editor_PreviewKeyDown(object sender, KeyEventArgs e) { var p = Editor.CaretPosition.Paragraph; if (p?.Tag is not string tag || tag is not ("CodeBlock" or "Quote")) return; var empty = string.IsNullOrWhiteSpace(new TextRange(p.ContentStart, p.ContentEnd).Text); if ((e.Key == Key.Back && empty) || (e.Key == Key.Enter && empty)) { ResetBlock(p); e.Handled = true; Editor.Focus(); } }
    private static void ResetBlock(Paragraph p) { p.Tag = null; p.ClearValue(TextElement.FontFamilyProperty); p.ClearValue(TextElement.ForegroundProperty); p.ClearValue(Block.BackgroundProperty); p.ClearValue(Block.BorderBrushProperty); p.ClearValue(Block.BorderThicknessProperty); p.ClearValue(Block.PaddingProperty); p.ClearValue(Block.MarginProperty); p.ToolTip = null; }
    private void Subscript_Click(object sender, RoutedEventArgs e) {if(ModernEditor.Visibility==Visibility.Visible){_=ModernEditor.ExecuteAsync("subscript");return;}ToggleBaseline(BaselineAlignment.Subscript);}
    private void Superscript_Click(object sender, RoutedEventArgs e) {if(ModernEditor.Visibility==Visibility.Visible){_=ModernEditor.ExecuteAsync("superscript");return;}ToggleBaseline(BaselineAlignment.Superscript);}
    private void ToggleBaseline(BaselineAlignment alignment) { var current = Editor.Selection.GetPropertyValue(Inline.BaselineAlignmentProperty); Editor.Selection.ApplyPropertyValue(Inline.BaselineAlignmentProperty, current is BaselineAlignment a && a == alignment ? BaselineAlignment.Baseline : alignment); }
    private void ChangeCase_Click(object sender, RoutedEventArgs e) { if (Editor.Selection.IsEmpty) return; var text = Editor.Selection.Text; Editor.Selection.Text = text.Any(char.IsLower) ? text.ToUpperInvariant() : text.ToLowerInvariant(); }
    private void ClearFormatting_Click(object sender, RoutedEventArgs e) {if(ModernEditor.Visibility==Visibility.Visible){_=ModernEditor.ExecuteAsync("clear");return;}Editor.Selection.ClearAllProperties(); foreach (var p in SelectedParagraphs()) ResetBlock(p); }
    private void LineSpacing_Changed(object sender, SelectionChangedEventArgs e) { if (Editor is null || sender is not ComboBox { SelectedItem: ComboBoxItem item } || !double.TryParse(item.Tag?.ToString(), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var factor)) return; var size = Editor.Selection.GetPropertyValue(TextElement.FontSizeProperty) is double d ? d : 13; foreach (var p in SelectedParagraphs()) { p.LineHeight = size * factor; p.LineStackingStrategy = LineStackingStrategy.BlockLineHeight; } }
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
            var name = Guid.NewGuid().ToString("N") + Path.GetExtension(dialog.FileName).ToLowerInvariant(); var stored = Path.Combine(AppPaths.Assets, name); File.Copy(dialog.FileName, stored); using(var input=File.OpenRead(stored)){var hash=Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(input));_vm.Database.RegisterAttachment(_vm.SelectedTab.DocumentId,Path.GetFileName(dialog.FileName),name,"image/"+Path.GetExtension(name).TrimStart('.'),new FileInfo(stored).Length,hash);}if(ModernEditor.Visibility==Visibility.Visible){_=ModernEditor.ExecuteAsync("image",new{src="https://assets.local/"+name,alt=Path.GetFileName(dialog.FileName)});LoadDocumentProperties();return;}
            var bitmap = new BitmapImage(); bitmap.BeginInit(); bitmap.UriSource = new Uri(stored); bitmap.CacheOption = BitmapCacheOption.OnLoad; bitmap.EndInit(); bitmap.Freeze();
            var image = new Image { Source = bitmap, Stretch = Stretch.Uniform, MaxWidth = 900 }; if (bitmap.PixelWidth > 700) image.Width = 700;
            new InlineUIContainer(image, Editor.CaretPosition); Editor.CaretPosition = Editor.CaretPosition.GetNextInsertionPosition(LogicalDirection.Forward) ?? Editor.CaretPosition; LoadDocumentProperties(); Editor.Focus();
        }
        catch (Exception ex) { LogService.Error("Ошибка вставки изображения", ex); MessageBox.Show("Не удалось вставить изображение: " + ex.Message); }
    }
    private Image? SelectedImage() => Editor.CaretPosition.GetAdjacentElement(LogicalDirection.Forward) as Image ?? Editor.CaretPosition.GetAdjacentElement(LogicalDirection.Backward) as Image;
    private void ImageSmaller_Click(object sender, RoutedEventArgs e) { var i = SelectedImage(); if (i is not null) i.Width = Math.Max(40, (double.IsNaN(i.Width) ? i.ActualWidth : i.Width) * .8); }
    private void ImageLarger_Click(object sender, RoutedEventArgs e) { var i = SelectedImage(); if (i is not null) i.Width = Math.Min(1600, (double.IsNaN(i.Width) ? i.ActualWidth : i.Width) * 1.2); }
    private void Link_Click(object sender, RoutedEventArgs e)
    {
        if (ModernEditor.Visibility!=Visibility.Visible&&Editor.Selection.IsEmpty) { MessageBox.Show("Сначала выделите текст ссылки."); return; } var p = new TextPrompt("Адрес гиперссылки", "https://") { Owner = this }; if (p.ShowDialog() != true) return;
        if (!Uri.TryCreate(p.Value, UriKind.Absolute, out var uri)) { MessageBox.Show("Некорректный адрес."); return; }
        try { if(ModernEditor.Visibility==Visibility.Visible){_=ModernEditor.ExecuteAsync("link",new{href=uri.AbsoluteUri});return;}var link = new Hyperlink(Editor.Selection.Start, Editor.Selection.End) { NavigateUri = uri, Foreground = Brushes.RoyalBlue, TextDecorations = TextDecorations.Underline }; }
        catch (Exception ex) { MessageBox.Show("Не удалось создать ссылку: " + ex.Message); }
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
    private void ExportDocument_Click(object sender, RoutedEventArgs e) { if (_vm.SelectedTab is null) return; var dialog=new SaveFileDialog { Title="Экспорт документа", FileName=_vm.SelectedTab.Title, Filter="PDF (*.pdf)|*.pdf|Word (*.docx)|*.docx|HTML (*.html)|*.html|Markdown (*.md)|*.md|Текст (*.txt)|*.txt", InitialDirectory=AppPaths.Exports }; if(dialog.ShowDialog(this)!=true)return; try { _vm.Save(_vm.SelectedTab); new DocumentExportService().Export(Editor.Document,_vm.SelectedTab.Title,dialog.FileName); MessageBox.Show(this,"Документ экспортирован:\n"+dialog.FileName,"Экспорт"); } catch(Exception ex){ MessageBox.Show(this,"Ошибка экспорта: "+ex.Message,"Ошибка",MessageBoxButton.OK,MessageBoxImage.Error); } }
    private void Print_Click(object sender, RoutedEventArgs e) { if(_vm.SelectedTab is null)return; var dialog=new PrintDialog(); if(dialog.ShowDialog()!=true)return; var paginator=((IDocumentPaginatorSource)Editor.Document).DocumentPaginator; dialog.PrintDocument(paginator,"MasterDocumentation — "+_vm.SelectedTab.Title); }
    private void Duplicate_Click(object sender, RoutedEventArgs e) { if(_vm.SelectedTab is null)return; _vm.Save(_vm.SelectedTab); var id=_vm.Database.Duplicate(_vm.SelectedTab.DocumentId); _vm.ReloadTree(); UpdateNavigationCounts(); var node=FindNode(_vm.Nodes,id); if(node is not null){_vm.Open(node);ShowSelectedTab();} }
    private void OpenBackups_Click(object sender, RoutedEventArgs e) { Directory.CreateDirectory(AppPaths.Backups); Process.Start(new ProcessStartInfo("explorer.exe",$"\"{AppPaths.Backups}\""){UseShellExecute=true}); }
    private void History_Click(object sender,RoutedEventArgs e){if(_vm.SelectedTab is null)return;_vm.Save(_vm.SelectedTab);var dialog=new HistoryWindow(_vm.Database,_vm.SelectedTab.DocumentId){Owner=this};if(dialog.ShowDialog()!=true)return;_vm.SelectedTab.Document=_vm.Database.LoadDocument(_vm.SelectedTab.DocumentId).Document;ShowSelectedTab();}
    private void AutoBackupIfNeeded() { try { if (!DateTime.TryParse(_vm.Database.GetSetting("LastAutoBackup"), out var last) || DateTime.UtcNow - last > TimeSpan.FromDays(1)) { _vm.Backups.CreateBackup(true); _vm.Database.SetSetting("LastAutoBackup", DateTime.UtcNow.ToString("O")); } } catch (Exception ex) { LogService.Error("Ошибка автоматической резервной копии", ex); } }
    private void RestoreSession() { var settings=_settingsService.Load(); if(settings.StartupBehavior!="Открывать последнюю сессию")return; foreach(var id in settings.OpenDocumentIds){var node=_vm.Database.FindNode(id);if(node is not null)_vm.Open(node);} if(settings.SelectedDocumentId is long selected){var tab=_vm.Tabs.FirstOrDefault(x=>x.DocumentId==selected);if(tab is not null)_vm.SelectedTab=tab;} ShowSelectedTab(); }
    private void RestoreWindow() { if (double.TryParse(_vm.Database.GetSetting("Width"), out var w)) Width = Math.Max(MinWidth, w); if (double.TryParse(_vm.Database.GetSetting("Height"), out var h)) Height = Math.Max(MinHeight, h); if (double.TryParse(_vm.Database.GetSetting("Left"), out var l) && double.TryParse(_vm.Database.GetSetting("Top"), out var t)) { WindowStartupLocation = WindowStartupLocation.Manual; Left = l; Top = t; } }
    private void Window_Closing(object? sender, CancelEventArgs e) { _saveTimer.Stop(); _vm.SaveAll(); var settings=_settingsService.Load(); settings.OpenDocumentIds=_vm.Tabs.Select(x=>x.DocumentId).ToList(); settings.SelectedDocumentId=_vm.SelectedTab?.DocumentId; _settingsService.Save(settings); if (WindowState == WindowState.Normal) { _vm.Database.SetSetting("Width", Width.ToString()); _vm.Database.SetSetting("Height", Height.ToString()); _vm.Database.SetSetting("Left", Left.ToString()); _vm.Database.SetSetting("Top", Top.ToString()); } }
    private void Exit_Click(object sender, RoutedEventArgs e) => Close();
    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void Maximize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        _vm.SaveAll(); var window = new SettingsWindow(_vm.Database, _vm.Backups) { Owner = this };
        if (window.ShowDialog() != true) return; _settings = window.SavedSettings;
        _saveTimer.Interval = TimeSpan.FromSeconds(_settings.AutoSaveDelaySeconds); _vm.DarkTheme = _settings.Theme != "Светлая";
        Editor.FontFamily = new FontFamily(_settings.DefaultFont); Editor.FontSize = _settings.DefaultFontSize; Editor.SpellCheck.IsEnabled = _settings.SpellCheck;
    }
    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if(Matches("NewDocument",e))NewDocument_Click(this,new RoutedEventArgs());else if(Matches("NewFolder",e))NewFolder_Click(this,new RoutedEventArgs());else if(Matches("Save",e))_vm.Save(_vm.SelectedTab);else if(Matches("Export",e))ExportDocument_Click(this,new RoutedEventArgs());else if(Matches("Settings",e))Settings_Click(this,new RoutedEventArgs());else if(Matches("CloseTab",e))CloseCurrentTab();else if(Matches("NextTab",e))SelectRelativeTab(1);else if(Matches("PreviousTab",e))SelectRelativeTab(-1);else return;e.Handled=true;
    }
    private bool Matches(string id,KeyEventArgs e){if(!_settings.Hotkeys.TryGetValue(id,out var value))return false;try{return new KeyGestureConverter().ConvertFromInvariantString(value) is KeyGesture gesture&&gesture.Matches(this,e);}catch{return false;}}
    private void CloseCurrentTab(){var tab=_vm.SelectedTab;if(tab is null)return;_vm.Save(tab);var index=_vm.Tabs.IndexOf(tab);_vm.Tabs.Remove(tab);_vm.SelectedTab=_vm.Tabs.Count==0?null:_vm.Tabs[Math.Clamp(index-1,0,_vm.Tabs.Count-1)];ShowSelectedTab();}
    private void SelectRelativeTab(int direction){if(_vm.Tabs.Count<2)return;var index=_vm.SelectedTab is null?0:_vm.Tabs.IndexOf(_vm.SelectedTab);_vm.SelectedTab=_vm.Tabs[(index+direction+_vm.Tabs.Count)%_vm.Tabs.Count];ShowSelectedTab();}
    private void About_Click(object sender, RoutedEventArgs e) => MessageBox.Show("MasterDocumentation\nПереносимая локальная система документации\n.NET 10 / WPF / SQLite", "О программе");
}
