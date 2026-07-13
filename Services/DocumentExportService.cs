using System.Net;
using System.IO;
using System.Text;
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
    public static string PlainText(FlowDocument document) => new TextRange(document.ContentStart, document.ContentEnd).Text.TrimEnd();
    public static string Markdown(FlowDocument document, string title)
    {
        var b = new StringBuilder(); if (!string.IsNullOrWhiteSpace(title)) b.Append("# ").AppendLine(title).AppendLine();
        foreach (var block in document.Blocks) { if (block is WpfParagraph p) { var text = new TextRange(p.ContentStart, p.ContentEnd).Text.Trim(); var level = p.FontSize switch { >= 24 => 1, >= 20 => 2, >= 16 => 3, _ => 0 }; if (level > 0) b.Append(new string('#', level)).Append(' '); b.AppendLine(text).AppendLine(); } else if (block is System.Windows.Documents.List list) { var i=1; foreach (var item in list.ListItems) { var text=new TextRange(item.ContentStart,item.ContentEnd).Text.Trim(); b.Append(list.MarkerStyle==System.Windows.TextMarkerStyle.Decimal?$"{i++}. ":"- ").AppendLine(text); } b.AppendLine(); } else if (block is System.Windows.Documents.Table table) AppendMarkdownTable(b, table); }
        return b.ToString();
    }
    public static string Html(FlowDocument document, string title) { var body = WebUtility.HtmlEncode(PlainText(document)).Replace("\r\n", "<br>\n").Replace("\n", "<br>\n"); return $"<!doctype html><html lang=\"ru\"><head><meta charset=\"utf-8\"><title>{WebUtility.HtmlEncode(title)}</title><style>body{{max-width:900px;margin:40px auto;font:16px/1.6 Segoe UI,Arial;color:#222}}h1{{color:#206ea5}}</style></head><body><h1>{WebUtility.HtmlEncode(title)}</h1><p>{body}</p></body></html>"; }
    private static void Docx(FlowDocument document, string title, string path)
    {
        using var word = WordprocessingDocument.Create(path, DocumentFormat.OpenXml.WordprocessingDocumentType.Document); var main = word.AddMainDocumentPart(); var body = new Body(); main.Document = new Document(body);
        if (!string.IsNullOrWhiteSpace(title)) body.Append(new WordParagraph(new ParagraphProperties(new ParagraphStyleId { Val="Title" }), new WordRun(new RunProperties(new DocumentFormat.OpenXml.Wordprocessing.Bold(), new FontSize { Val="32" }), new Text(title))));
        foreach (var block in document.Blocks) { if (block is WpfParagraph p) { var text=new TextRange(p.ContentStart,p.ContentEnd).Text.TrimEnd(); var props=new ParagraphProperties(); if (p.FontSize>=24) props.Append(new ParagraphStyleId{Val="Heading1"}); else if(p.FontSize>=20) props.Append(new ParagraphStyleId{Val="Heading2"}); var runProps=new RunProperties(); if(p.FontWeight==System.Windows.FontWeights.Bold) runProps.Append(new DocumentFormat.OpenXml.Wordprocessing.Bold()); if(p.FontStyle==System.Windows.FontStyles.Italic) runProps.Append(new DocumentFormat.OpenXml.Wordprocessing.Italic()); body.Append(new WordParagraph(props,new WordRun(runProps,new Text(text){Space=DocumentFormat.OpenXml.SpaceProcessingModeValues.Preserve}))); } else if (block is System.Windows.Documents.Table table) body.Append(ToWordTable(table)); }
        main.Document.Save();
    }
    private static DocumentFormat.OpenXml.Wordprocessing.Table ToWordTable(System.Windows.Documents.Table source) { var table=new DocumentFormat.OpenXml.Wordprocessing.Table(new TableProperties(new TableBorders(new TopBorder{Val=BorderValues.Single},new BottomBorder{Val=BorderValues.Single},new LeftBorder{Val=BorderValues.Single},new RightBorder{Val=BorderValues.Single},new InsideHorizontalBorder{Val=BorderValues.Single},new InsideVerticalBorder{Val=BorderValues.Single}))); foreach(var group in source.RowGroups) foreach(var row in group.Rows){var tr=new DocumentFormat.OpenXml.Wordprocessing.TableRow(); foreach(var cell in row.Cells) tr.Append(new DocumentFormat.OpenXml.Wordprocessing.TableCell(new WordParagraph(new WordRun(new Text(new TextRange(cell.ContentStart,cell.ContentEnd).Text.Trim()))))); table.Append(tr);} return table; }
    private static void AppendMarkdownTable(StringBuilder b, System.Windows.Documents.Table table) { var rows=table.RowGroups.SelectMany(g=>g.Rows).ToList(); if(rows.Count==0)return; for(var r=0;r<rows.Count;r++){b.Append('|'); foreach(var cell in rows[r].Cells)b.Append(' ').Append(new TextRange(cell.ContentStart,cell.ContentEnd).Text.Trim().Replace("|","\\|")).Append(" |"); b.AppendLine(); if(r==0){b.Append('|'); foreach(var _ in rows[r].Cells)b.Append(" --- |"); b.AppendLine();}} b.AppendLine(); }
    private static void Pdf(FlowDocument source,string title,string path){var pdf=new PdfDocument();pdf.Info.Title=title;var paragraphs=PlainText(source).Replace("\r","").Split('\n');var titleFont=new XFont("Segoe UI",20,XFontStyleEx.Bold);var font=new XFont("Segoe UI",11);PdfPage page=pdf.AddPage();page.Size=PdfSharp.PageSize.A4;var gfx=XGraphics.FromPdfPage(page);var formatter=new XTextFormatter(gfx);double y=42;formatter.DrawString(title,titleFont,XBrushes.Black,new XRect(48,y,page.Width.Point-96,34));y+=45;foreach(var paragraph in paragraphs){if(string.IsNullOrWhiteSpace(paragraph)){y+=10;continue;}var estimated=Math.Max(20,Math.Ceiling(paragraph.Length/85d)*17);if(y+estimated>page.Height.Point-48){gfx.Dispose();page=pdf.AddPage();page.Size=PdfSharp.PageSize.A4;gfx=XGraphics.FromPdfPage(page);formatter=new XTextFormatter(gfx);y=48;}formatter.DrawString(paragraph,font,XBrushes.Black,new XRect(48,y,page.Width.Point-96,estimated));y+=estimated+5;}gfx.Dispose();pdf.Save(path);}
}
