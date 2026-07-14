using System.Net;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Documents;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using WpfParagraph = System.Windows.Documents.Paragraph;
using WordParagraph = DocumentFormat.OpenXml.Wordprocessing.Paragraph;
using WordRun = DocumentFormat.OpenXml.Wordprocessing.Run;
using PdfSharp.Pdf;
using PdfSharp.Drawing;
using PdfSharp.Drawing.Layout;

namespace MasterDocumentation.Services;

public sealed class DocumentExportService
{
    public void Export(FlowDocument document, string title, string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!); var ext = Path.GetExtension(path).ToLowerInvariant();
        switch (ext) { case ".txt": File.WriteAllText(path, PlainText(document), Encoding.UTF8); break; case ".md": File.WriteAllText(path, Markdown(document, title), Encoding.UTF8); break; case ".html": File.WriteAllText(path, Html(document, title), Encoding.UTF8); break; case ".docx": Docx(document, title, path); break; case ".pdf": Pdf(document,title,path);break; default: throw new NotSupportedException("Неподдерживаемый формат: " + ext); }
    }
    public void Export(string html,string plainText,string title,string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);var ext=Path.GetExtension(path).ToLowerInvariant();
        switch(ext)
        {
            case ".txt":File.WriteAllText(path,plainText,Encoding.UTF8);break;
            case ".html":File.WriteAllText(path,HtmlDocument(html,title),Encoding.UTF8);break;
            case ".md":File.WriteAllText(path,MarkdownFromHtml(html,title),Encoding.UTF8);break;
            case ".docx":DocxFromHtml(html,title,path);break;
            case ".pdf":PdfFromText(plainText,title,path);break;
            default:throw new NotSupportedException("Неподдерживаемый формат: "+ext);
        }
    }
    public static string PlainText(FlowDocument document) => new TextRange(document.ContentStart, document.ContentEnd).Text.TrimEnd();
    public static string Markdown(FlowDocument document, string title)
    {
        var b = new StringBuilder(); if (!string.IsNullOrWhiteSpace(title)) b.Append("# ").AppendLine(title).AppendLine();
        foreach (var block in document.Blocks) { if (block is WpfParagraph p) { var text = new TextRange(p.ContentStart, p.ContentEnd).Text.Trim(); var level = p.FontSize switch { >= 24 => 1, >= 20 => 2, >= 16 => 3, _ => 0 }; if (level > 0) b.Append(new string('#', level)).Append(' '); b.AppendLine(text).AppendLine(); } else if (block is System.Windows.Documents.List list) { var i=1; foreach (var item in list.ListItems) { var text=new TextRange(item.ContentStart,item.ContentEnd).Text.Trim(); b.Append(list.MarkerStyle==System.Windows.TextMarkerStyle.Decimal?$"{i++}. ":"- ").AppendLine(text); } b.AppendLine(); } else if (block is System.Windows.Documents.Table table) AppendMarkdownTable(b, table); }
        return b.ToString();
    }
    public static string Html(FlowDocument document, string title) { var body = WebUtility.HtmlEncode(PlainText(document)).Replace("\r\n", "<br>\n").Replace("\n", "<br>\n"); return $"<!doctype html><html lang=\"ru\"><head><meta charset=\"utf-8\"><title>{WebUtility.HtmlEncode(title)}</title><style>body{{max-width:900px;margin:40px auto;font:16px/1.6 Segoe UI,Arial;color:#222}}h1{{color:#206ea5}}</style></head><body><h1>{WebUtility.HtmlEncode(title)}</h1><p>{body}</p></body></html>"; }
    public static string HtmlDocument(string html,string title)=>"""<!doctype html><html lang="ru"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width"><title>"""+WebUtility.HtmlEncode(title)+"""</title><style>body{max-width:920px;margin:40px auto;padding:0 24px;font:16px/1.6 "Segoe UI",Arial;color:#20242a}table{border-collapse:collapse;width:100%}td,th{border:1px solid #9aa3ad;padding:7px}pre{background:#171f29;color:#e2e7ee;padding:14px;border-radius:6px;overflow:auto}blockquote{border-left:4px solid #6e52b5;margin-left:0;padding-left:14px;color:#596273}img{max-width:100%;height:auto}mark{padding:0 2px}</style></head><body><article>"""+html+"""</article></body></html>""";
    public static string MarkdownFromHtml(string html,string title)
    {
        var value=Regex.Replace(html,@"(?is)<div[^>]*data-safe-html[^>]*>(.*?)</div>",m=>"\n```html\n"+WebUtility.HtmlEncode(WebUtility.HtmlDecode(StripTags(m.Groups[1].Value)).Trim())+"\n```\n");
        value=Regex.Replace(value,@"(?is)<span(?<attributes>[^>]*data-document-anchor[^>]*)>.*?</span>",m=>"\n@@MASTERDOCUMENTATION_ANCHOR:"+Convert.ToBase64String(Encoding.UTF8.GetBytes(AttributeValue(m.Groups["attributes"].Value,"data-document-anchor")))+"@@\n");
        value=Regex.Replace(value,@"(?is)<div[^>]*data-mermaid[^>]*>(.*?)</div>",m=>"\n```mermaid\n"+WebUtility.HtmlDecode(StripTags(m.Groups[1].Value)).Trim()+"\n```\n");
        value=Regex.Replace(value,@"(?is)<span[^>]*data-formula[^>]*>(.*?)</span>",m=>"$"+WebUtility.HtmlDecode(StripTags(m.Groups[1].Value)).Trim()+"$");
        value=Regex.Replace(value,@"(?is)<div[^>]*data-page-break[^>]*>\s*</div>","\n@@MASTERDOCUMENTATION_PAGE_BREAK@@\n");
        value=Regex.Replace(value,@"(?is)<details[^>]*data-collapsible[^>]*>\s*<summary[^>]*>(.*?)</summary>\s*<div[^>]*data-details-content[^>]*>(.*?)</div>\s*</details>",m=>"\n**"+InlineMarkdown(m.Groups[1].Value)+"**\n\n"+InlineMarkdown(m.Groups[2].Value)+"\n");
        value=Regex.Replace(value,@"(?is)<div(?<attributes>[^>]*data-callout[^>]*)>(?<body>.*?)</div>",m=>
        {
            var label=AttributeValue(m.Groups["attributes"].Value,"data-label");
            if(string.IsNullOrWhiteSpace(label))label="Примечание";
            return "\n> **"+WebUtility.HtmlDecode(label)+"**\n> "+InlineMarkdown(m.Groups["body"].Value).Replace("\n","\n> ")+"\n";
        });
        value=Regex.Replace(value,@"(?is)<pre[^>]*>\s*<code[^>]*>(.*?)</code>\s*</pre>",m=>"\n```\n"+WebUtility.HtmlDecode(StripTags(m.Groups[1].Value)).Trim()+"\n```\n");
        value=Regex.Replace(value,@"(?is)<h([1-6])[^>]*>(.*?)</h\1>",m=>"\n"+new string('#',int.Parse(m.Groups[1].Value))+" "+InlineMarkdown(m.Groups[2].Value)+"\n");
        value=Regex.Replace(value,@"(?is)<blockquote[^>]*>(.*?)</blockquote>",m=>"\n> "+InlineMarkdown(m.Groups[1].Value).Replace("\n","\n> ")+"\n");
        value=Regex.Replace(value,@"(?is)<li[^>]*>(.*?)</li>",m=>"\n- "+InlineMarkdown(m.Groups[1].Value));
        value=Regex.Replace(value,@"(?is)<p[^>]*>(.*?)</p>",m=>"\n"+InlineMarkdown(m.Groups[1].Value)+"\n");
        value=Regex.Replace(value,@"(?is)<br\s*/?>","\n");value=StripTags(value);value=WebUtility.HtmlDecode(value);value=value.Replace("@@MASTERDOCUMENTATION_PAGE_BREAK@@","<div style=\"page-break-after: always;\"></div>",StringComparison.Ordinal);value=Regex.Replace(value,@"@@MASTERDOCUMENTATION_ANCHOR:(?<name>[A-Za-z0-9+/=]+)@@",m=>"<a id=\""+WebUtility.HtmlEncode(Encoding.UTF8.GetString(Convert.FromBase64String(m.Groups["name"].Value)))+"\"></a>");value=Regex.Replace(value,@"\n{3,}","\n\n").Trim();return(string.IsNullOrWhiteSpace(title)?"":"# "+title+"\n\n")+value+"\n";
    }
    private static string InlineMarkdown(string value){value=Regex.Replace(value,@"(?is)<(strong|b)[^>]*>(.*?)</\1>","**$2**");value=Regex.Replace(value,@"(?is)<(em|i)[^>]*>(.*?)</\1>","*$2*");value=Regex.Replace(value,@"(?is)<(s|del|strike)[^>]*>(.*?)</\1>","~~$2~~");value=Regex.Replace(value,@"(?is)<code[^>]*>(.*?)</code>","`$1`");value=Regex.Replace(value,"(?is)<a[^>]*href=[\"']([^\"']+)[\"'][^>]*>(.*?)</a>","[$2]($1)");return WebUtility.HtmlDecode(StripTags(value));}
    private static string AttributeValue(string attributes,string name)
    {
        var match=Regex.Match(attributes,Regex.Escape(name)+"\\s*=\\s*[\"'](?<value>[^\"']*)[\"']",RegexOptions.IgnoreCase);
        return match.Success?match.Groups["value"].Value:"";
    }
    private static string StripTags(string value)=>Regex.Replace(value,"<[^>]+>","");
    private static void DocxFromHtml(string html,string title,string path)
    {
        html=Regex.Replace(html,@"(?is)<div[^>]*data-safe-html[^>]*>(.*?)</div>",m=>"<pre>HTML:\n"+WebUtility.HtmlEncode(WebUtility.HtmlDecode(StripTags(m.Groups[1].Value)))+"</pre>");html=Regex.Replace(html,@"(?is)<span[^>]*data-document-anchor[^>]*>.*?</span>","");html=Regex.Replace(html,@"(?is)<div[^>]*data-mermaid[^>]*>(.*?)</div>",m=>"<pre>Mermaid:\n"+m.Groups[1].Value+"</pre>");html=Regex.Replace(html,@"(?is)<div[^>]*data-page-break[^>]*>\s*</div>","<p>— Разрыв страницы —</p>");html=Regex.Replace(html,@"(?is)<details[^>]*data-collapsible[^>]*>\s*<summary[^>]*>(.*?)</summary>\s*<div[^>]*data-details-content[^>]*>(.*?)</div>\s*</details>","<p><strong>$1</strong><br/>$2</p>");html=Regex.Replace(html,@"(?is)<div(?<attributes>[^>]*data-callout[^>]*)>(?<body>.*?)</div>",m=>"<blockquote><strong>"+WebUtility.HtmlEncode(string.IsNullOrWhiteSpace(AttributeValue(m.Groups["attributes"].Value,"data-label"))?"Примечание":WebUtility.HtmlDecode(AttributeValue(m.Groups["attributes"].Value,"data-label")))+"</strong><br/>"+m.Groups["body"].Value+"</blockquote>");
        using var word=WordprocessingDocument.Create(path,DocumentFormat.OpenXml.WordprocessingDocumentType.Document);var main=word.AddMainDocumentPart();var body=new Body();main.Document=new Document(body);
        if(!string.IsNullOrWhiteSpace(title))body.Append(new WordParagraph(new ParagraphProperties(new ParagraphStyleId{Val="Title"}),new WordRun(new RunProperties(new DocumentFormat.OpenXml.Wordprocessing.Bold(),new FontSize{Val="32"}),new Text(title))));
        foreach(Match block in Regex.Matches(html,@"(?is)<(h[1-6]|p|blockquote|pre|ul|ol|table)\b[^>]*>(.*?)</\1>"))
        {
            var tag=block.Groups[1].Value.ToLowerInvariant();var content=block.Groups[2].Value;
            if(tag=="table"){var table=new DocumentFormat.OpenXml.Wordprocessing.Table(new TableProperties(new TableBorders(new TopBorder{Val=BorderValues.Single},new BottomBorder{Val=BorderValues.Single},new LeftBorder{Val=BorderValues.Single},new RightBorder{Val=BorderValues.Single},new InsideHorizontalBorder{Val=BorderValues.Single},new InsideVerticalBorder{Val=BorderValues.Single})));foreach(Match row in Regex.Matches(content,@"(?is)<tr\b[^>]*>(.*?)</tr>")){var tr=new DocumentFormat.OpenXml.Wordprocessing.TableRow();foreach(Match cell in Regex.Matches(row.Groups[1].Value,@"(?is)<t[dh]\b[^>]*>(.*?)</t[dh]>"))tr.Append(new DocumentFormat.OpenXml.Wordprocessing.TableCell(CreateWordParagraph(cell.Groups[1].Value,null)));table.Append(tr);}body.Append(table);continue;}
            if(tag is "ul" or "ol"){var number=1;foreach(Match item in Regex.Matches(content,@"(?is)<li\b[^>]*>(.*?)</li>")){body.Append(CreateWordParagraph((tag=="ol"?$"{number++}. ":"• ")+item.Groups[1].Value,null));}continue;}
            var style=tag.StartsWith('h')?"Heading"+tag[1]:null;var paragraph=CreateWordParagraph(content,style);if(tag=="blockquote")paragraph.ParagraphProperties!.Append(new Indentation{Left="480"});if(tag=="pre")paragraph.ParagraphProperties!.Append(new Shading{Fill="E8ECF1"});body.Append(paragraph);
        }
        main.Document.Save();
    }
    private static WordParagraph CreateWordParagraph(string fragment,string? style)
    {
        var properties=new ParagraphProperties();if(style is not null)properties.Append(new ParagraphStyleId{Val=style});var paragraph=new WordParagraph(properties);var bold=false;var italic=false;var underline=false;var code=false;
        foreach(Match token in Regex.Matches(fragment,@"(?is)<(/?)(strong|b|em|i|u|code|br)\b[^>]*>|([^<]+)")){if(token.Groups[3].Success){var text=WebUtility.HtmlDecode(token.Groups[3].Value);if(text.Length==0)continue;var rp=new RunProperties();if(bold)rp.Append(new DocumentFormat.OpenXml.Wordprocessing.Bold());if(italic)rp.Append(new DocumentFormat.OpenXml.Wordprocessing.Italic());if(underline)rp.Append(new DocumentFormat.OpenXml.Wordprocessing.Underline{Val=UnderlineValues.Single});if(code)rp.Append(new RunFonts{Ascii="Consolas",HighAnsi="Consolas"});paragraph.Append(new WordRun(rp,new Text(text){Space=DocumentFormat.OpenXml.SpaceProcessingModeValues.Preserve}));continue;}var closing=token.Groups[1].Value=="/";switch(token.Groups[2].Value.ToLowerInvariant()){case "strong":case "b":bold=!closing;break;case "em":case "i":italic=!closing;break;case "u":underline=!closing;break;case "code":code=!closing;break;case "br":paragraph.Append(new WordRun(new Break()));break;}}
        return paragraph;
    }
    private static void Docx(FlowDocument document, string title, string path)
    {
        using var word = WordprocessingDocument.Create(path, DocumentFormat.OpenXml.WordprocessingDocumentType.Document); var main = word.AddMainDocumentPart(); var body = new Body(); main.Document = new Document(body);
        if (!string.IsNullOrWhiteSpace(title)) body.Append(new WordParagraph(new ParagraphProperties(new ParagraphStyleId { Val="Title" }), new WordRun(new RunProperties(new DocumentFormat.OpenXml.Wordprocessing.Bold(), new FontSize { Val="32" }), new Text(title))));
        foreach (var block in document.Blocks) { if (block is WpfParagraph p) { var text=new TextRange(p.ContentStart,p.ContentEnd).Text.TrimEnd(); var props=new ParagraphProperties(); if (p.FontSize>=24) props.Append(new ParagraphStyleId{Val="Heading1"}); else if(p.FontSize>=20) props.Append(new ParagraphStyleId{Val="Heading2"}); var runProps=new RunProperties(); if(p.FontWeight==System.Windows.FontWeights.Bold) runProps.Append(new DocumentFormat.OpenXml.Wordprocessing.Bold()); if(p.FontStyle==System.Windows.FontStyles.Italic) runProps.Append(new DocumentFormat.OpenXml.Wordprocessing.Italic()); body.Append(new WordParagraph(props,new WordRun(runProps,new Text(text){Space=DocumentFormat.OpenXml.SpaceProcessingModeValues.Preserve}))); } else if (block is System.Windows.Documents.Table table) body.Append(ToWordTable(table)); }
        main.Document.Save();
    }
    private static DocumentFormat.OpenXml.Wordprocessing.Table ToWordTable(System.Windows.Documents.Table source) { var table=new DocumentFormat.OpenXml.Wordprocessing.Table(new TableProperties(new TableBorders(new TopBorder{Val=BorderValues.Single},new BottomBorder{Val=BorderValues.Single},new LeftBorder{Val=BorderValues.Single},new RightBorder{Val=BorderValues.Single},new InsideHorizontalBorder{Val=BorderValues.Single},new InsideVerticalBorder{Val=BorderValues.Single}))); foreach(var group in source.RowGroups) foreach(var row in group.Rows){var tr=new DocumentFormat.OpenXml.Wordprocessing.TableRow(); foreach(var cell in row.Cells) tr.Append(new DocumentFormat.OpenXml.Wordprocessing.TableCell(new WordParagraph(new WordRun(new Text(new TextRange(cell.ContentStart,cell.ContentEnd).Text.Trim()))))); table.Append(tr);} return table; }
    private static void AppendMarkdownTable(StringBuilder b, System.Windows.Documents.Table table) { var rows=table.RowGroups.SelectMany(g=>g.Rows).ToList(); if(rows.Count==0)return; for(var r=0;r<rows.Count;r++){b.Append('|'); foreach(var cell in rows[r].Cells)b.Append(' ').Append(new TextRange(cell.ContentStart,cell.ContentEnd).Text.Trim().Replace("|","\\|")).Append(" |"); b.AppendLine(); if(r==0){b.Append('|'); foreach(var _ in rows[r].Cells)b.Append(" --- |"); b.AppendLine();}} b.AppendLine(); }
    private static void Pdf(FlowDocument source,string title,string path)=>PdfFromText(PlainText(source),title,path);
    private static void PdfFromText(string text,string title,string path){var pdf=new PdfDocument();pdf.Info.Title=title;var paragraphs=text.Replace("\r","").Split('\n');var titleFont=new XFont("Segoe UI",20,XFontStyleEx.Bold);var font=new XFont("Segoe UI",11);PdfPage page=pdf.AddPage();page.Size=PdfSharp.PageSize.A4;var gfx=XGraphics.FromPdfPage(page);var formatter=new XTextFormatter(gfx);double y=42;formatter.DrawString(title,titleFont,XBrushes.Black,new XRect(48,y,page.Width.Point-96,34));y+=45;foreach(var paragraph in paragraphs){if(string.IsNullOrWhiteSpace(paragraph)){y+=10;continue;}var estimated=Math.Max(20,Math.Ceiling(paragraph.Length/85d)*17);if(y+estimated>page.Height.Point-48){gfx.Dispose();page=pdf.AddPage();page.Size=PdfSharp.PageSize.A4;gfx=XGraphics.FromPdfPage(page);formatter=new XTextFormatter(gfx);y=48;}formatter.DrawString(paragraph,font,XBrushes.Black,new XRect(48,y,page.Width.Point-96,estimated));y+=estimated+5;}gfx.Dispose();pdf.Save(path);}
}
