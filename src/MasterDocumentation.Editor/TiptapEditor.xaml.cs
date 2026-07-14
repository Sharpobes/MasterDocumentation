using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Web.WebView2.Core;

namespace MasterDocumentation.Editor;

public sealed record EditorHeading(int Level,string Text,int Position){public string Display=>new string(' ',Math.Max(0,Level-1)*3)+Text;}
public sealed record EditorContent(long DocumentId,string Json,string Html,string PlainText,IReadOnlyList<EditorHeading> Headings,bool IsInitialLoad=false);
public sealed record EditorFileData(long DocumentId,string Name,string MimeType,string DataUrl);
public partial class TiptapEditor : UserControl
{
    private readonly TaskCompletionSource _ready=new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Dictionary<string,TaskCompletionSource<EditorContent>> _snapshots=[];
    private Task? _initializationTask;
    private long _contentRequestVersion;
    public string DataFolder {get;set;}=Path.Combine(AppContext.BaseDirectory,"Data");
    public event EventHandler<EditorContent>? ContentChanged;
    public event EventHandler<JsonElement>? SelectionChanged;
    public event EventHandler<EditorFileData>? FileDropped;
    public event EventHandler<string>? AssetOpenRequested;
    public event EventHandler<string>? LinkOpenRequested;
    public event EventHandler? EditorReady;
    public TiptapEditor(){InitializeComponent();Loaded+=async(_,_)=>await InitializeAsync();}
    public Task InitializeAsync()
    {
        if(Browser.CoreWebView2 is not null)return Task.CompletedTask;
        return _initializationTask??=InitializeCoreAsync();
    }
    private async Task InitializeCoreAsync()
    {
        var runtimeRoot=Path.Combine(AppContext.BaseDirectory,"FixedRuntime");string? browserFolder=null;if(Directory.Exists(runtimeRoot)){var exe=Directory.EnumerateFiles(runtimeRoot,"msedgewebview2.exe",SearchOption.AllDirectories).FirstOrDefault();browserFolder=exe is null?null:Path.GetDirectoryName(exe);}var userData=Path.Combine(DataFolder,"Temp","WebView2");Directory.CreateDirectory(userData);var environment=await CoreWebView2Environment.CreateAsync(browserFolder,userData);await Browser.EnsureCoreWebView2Async(environment);var core=Browser.CoreWebView2!;core.Settings.AreDevToolsEnabled=false;core.Settings.AreDefaultContextMenusEnabled=false;core.Settings.IsStatusBarEnabled=false;core.Settings.IsPasswordAutosaveEnabled=false;core.Settings.IsGeneralAutofillEnabled=false;core.SetVirtualHostNameToFolderMapping("editor.local",Path.Combine(AppContext.BaseDirectory,"Editor"),CoreWebView2HostResourceAccessKind.DenyCors);var assets=Path.Combine(DataFolder,"Assets");Directory.CreateDirectory(assets);core.SetVirtualHostNameToFolderMapping("assets.local",assets,CoreWebView2HostResourceAccessKind.DenyCors);core.NavigationStarting+=(_,e)=>{if(!e.Uri.StartsWith("https://editor.local/",StringComparison.OrdinalIgnoreCase))e.Cancel=true;};core.NewWindowRequested+=(_,e)=>e.Handled=true;core.WebMessageReceived+=OnMessage;core.Navigate("https://editor.local/index.html");
    }
    private void OnMessage(object? sender,CoreWebView2WebMessageReceivedEventArgs e)
    {
        using var doc=JsonDocument.Parse(e.WebMessageAsJson);var root=doc.RootElement;var type=root.GetProperty("type").GetString();
        if(type=="ready"){_ready.TrySetResult();EditorReady?.Invoke(this,EventArgs.Empty);return;}
        if(type is "change" or "loaded" or "snapshot")
        {
            var content=ParseContent(root,type=="loaded");
            if(type=="snapshot"&&root.TryGetProperty("requestId",out var request)&&_snapshots.Remove(request.GetString()??"",out var completion)){completion.TrySetResult(content);return;}
            ContentChanged?.Invoke(this,content);return;
        }
        if(type=="selection")SelectionChanged?.Invoke(this,root.Clone());
        if(type=="openAsset"){AssetOpenRequested?.Invoke(this,root.TryGetProperty("src",out var source)?source.GetString()??"":"");return;}
        if(type=="openLink"){LinkOpenRequested?.Invoke(this,root.TryGetProperty("href",out var href)?href.GetString()??"":"");return;}
        if(type=="copyText"){var value=root.TryGetProperty("value",out var text)?text.GetString()??"":"";if(value.Length>0)Clipboard.SetText(value);return;}
        if(type=="fileData"){var documentId=root.TryGetProperty("documentId",out var id)&&id.TryGetInt64(out var parsed)?parsed:0;FileDropped?.Invoke(this,new EditorFileData(documentId,root.GetProperty("name").GetString()??"attachment",root.GetProperty("mime").GetString()??"application/octet-stream",root.GetProperty("data").GetString()??""));}
    }
    private static EditorContent ParseContent(JsonElement root,bool initial)
    {
        var headings=new List<EditorHeading>();if(root.TryGetProperty("headings",out var values))foreach(var h in values.EnumerateArray())headings.Add(new(h.GetProperty("level").GetInt32(),h.GetProperty("text").GetString()??"",h.GetProperty("pos").GetInt32()));
        var documentId=root.TryGetProperty("documentId",out var id)&&id.TryGetInt64(out var parsed)?parsed:0;
        return new(documentId,root.GetProperty("json").GetRawText(),root.GetProperty("html").GetString()??"",root.GetProperty("text").GetString()??"",headings,initial);
    }
    public async Task SetContentAsync(long documentId,string? json,string? html=null){var version=Interlocked.Increment(ref _contentRequestVersion);await InitializeAsync();await _ready.Task;if(version!=Volatile.Read(ref _contentRequestVersion))return;object content;if(!string.IsNullOrWhiteSpace(json)){try{content=JsonSerializer.Deserialize<JsonElement>(json);}catch{content=html??"<p></p>";}}else content=html??"<p></p>";Browser.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(new{type="setContent",documentId,json=content}));}
    public async Task<EditorContent> CaptureContentAsync(CancellationToken cancellationToken=default)
    {
        await InitializeAsync();await _ready.Task.WaitAsync(cancellationToken);var requestId=Guid.NewGuid().ToString("N");var completion=new TaskCompletionSource<EditorContent>(TaskCreationOptions.RunContinuationsAsynchronously);_snapshots[requestId]=completion;Browser.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(new{type="getContent",requestId}));
        try{return await completion.Task.WaitAsync(cancellationToken);}finally{_snapshots.Remove(requestId);}
    }
    public async Task ExecuteAsync(string name,object? args=null){await InitializeAsync();await _ready.Task;Browser.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(new{type="command",name,args=args??new{}}));}
    public async Task PrintAsync(){await InitializeAsync();await _ready.Task;Browser.CoreWebView2.ShowPrintUI(CoreWebView2PrintDialogKind.System);}
}
