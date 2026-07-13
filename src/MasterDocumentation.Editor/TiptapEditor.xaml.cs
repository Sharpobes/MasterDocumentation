using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Web.WebView2.Core;

namespace MasterDocumentation.Editor;

public sealed record EditorHeading(int Level,string Text,int Position){public string Display=>new string(' ',Math.Max(0,Level-1)*3)+Text;}
public sealed record EditorContent(string Json,string Html,string PlainText,IReadOnlyList<EditorHeading> Headings);
public partial class TiptapEditor : UserControl
{
    private readonly TaskCompletionSource _ready=new(TaskCreationOptions.RunContinuationsAsynchronously);
    public event EventHandler<EditorContent>? ContentChanged;
    public event EventHandler<JsonElement>? SelectionChanged;
    public TiptapEditor(){InitializeComponent();Loaded+=async(_,_)=>await InitializeAsync();}
    public async Task InitializeAsync()
    {
        if(Browser.CoreWebView2 is not null)return;var runtimeRoot=Path.Combine(AppContext.BaseDirectory,"FixedRuntime");string? browserFolder=null;if(Directory.Exists(runtimeRoot)){var exe=Directory.EnumerateFiles(runtimeRoot,"msedgewebview2.exe",SearchOption.AllDirectories).FirstOrDefault();browserFolder=exe is null?null:Path.GetDirectoryName(exe);}var userData=Path.Combine(AppContext.BaseDirectory,"Data","Temp","WebView2");Directory.CreateDirectory(userData);var environment=await CoreWebView2Environment.CreateAsync(browserFolder,userData);await Browser.EnsureCoreWebView2Async(environment);var core=Browser.CoreWebView2!;core.Settings.AreDevToolsEnabled=false;core.Settings.AreDefaultContextMenusEnabled=false;core.Settings.IsStatusBarEnabled=false;core.Settings.IsPasswordAutosaveEnabled=false;core.Settings.IsGeneralAutofillEnabled=false;core.SetVirtualHostNameToFolderMapping("editor.local",Path.Combine(AppContext.BaseDirectory,"Editor"),CoreWebView2HostResourceAccessKind.DenyCors);var assets=Path.Combine(AppContext.BaseDirectory,"Data","Assets");Directory.CreateDirectory(assets);core.SetVirtualHostNameToFolderMapping("assets.local",assets,CoreWebView2HostResourceAccessKind.DenyCors);core.NavigationStarting+=(_,e)=>{if(!e.Uri.StartsWith("https://editor.local/",StringComparison.OrdinalIgnoreCase))e.Cancel=true;};core.NewWindowRequested+=(_,e)=>e.Handled=true;core.WebMessageReceived+=OnMessage;core.Navigate("https://editor.local/index.html");
    }
    private void OnMessage(object? sender,CoreWebView2WebMessageReceivedEventArgs e){using var doc=JsonDocument.Parse(e.WebMessageAsJson);var root=doc.RootElement;var type=root.GetProperty("type").GetString();if(type=="ready"){_ready.TrySetResult();return;}if(type=="change"){var headings=new List<EditorHeading>();if(root.TryGetProperty("headings",out var values))foreach(var h in values.EnumerateArray())headings.Add(new(h.GetProperty("level").GetInt32(),h.GetProperty("text").GetString()??"",h.GetProperty("pos").GetInt32()));ContentChanged?.Invoke(this,new(root.GetProperty("json").GetRawText(),root.GetProperty("html").GetString()??"",root.GetProperty("text").GetString()??"",headings));}else if(type=="selection")SelectionChanged?.Invoke(this,root.Clone());}
    public async Task SetContentAsync(string? json,string? html=null){await InitializeAsync();await _ready.Task;object content;if(!string.IsNullOrWhiteSpace(json)){try{content=JsonSerializer.Deserialize<JsonElement>(json);}catch{content=html??"<p></p>";}}else content=html??"<p></p>";Browser.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(new{type="setContent",json=content}));}
    public async Task ExecuteAsync(string name,object? args=null){await InitializeAsync();await _ready.Task;Browser.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(new{type="command",name,args=args??new{}}));}
}
