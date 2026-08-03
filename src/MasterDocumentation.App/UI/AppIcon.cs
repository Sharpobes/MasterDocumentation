using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Media;

namespace MasterDocumentation.UI;

public enum AppIconKind
{
    App, Menu, Document, Folder, Add, Close, Search, Help, Minimize, Maximize, Restore,
    Settings, Documents, Star, Clock, Trash, Template, Apps, Storage, ChevronRight,
    ChevronDown, Save, Export, Print, Copy, History, Properties, Attachment, OpenFolder,
    Backup, Import, Undo, Redo, Link, Image, Table, Code, Quote, More, Tag, Info, Warning,
    Error, Success, Delete, ZoomIn, ZoomOut, AlignLeft, AlignCenter, AlignRight, AlignJustify,
    ListBullet, ListNumbered, Checklist, RotateLeft, RotateRight, Crop, Compress, Replace,
    OpenExternal, WrapLeft, WrapRight, NoWrap, Caption, AltText, Indent, Outdent,
    Subscript, Superscript, ClearFormatting, HorizontalRule, Spoiler, Minus, Calculator,
    Notification
}

public sealed class AppIcon : FrameworkElement
{
    public static readonly DependencyProperty KindProperty = DependencyProperty.Register(
        nameof(Kind), typeof(AppIconKind), typeof(AppIcon),
        new FrameworkPropertyMetadata(AppIconKind.Document, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ForegroundProperty = DependencyProperty.Register(
        nameof(Foreground), typeof(Brush), typeof(AppIcon),
        new FrameworkPropertyMetadata(Brushes.White, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty StrokeThicknessProperty = DependencyProperty.Register(
        nameof(StrokeThickness), typeof(double), typeof(AppIcon),
        new FrameworkPropertyMetadata(1.7d, FrameworkPropertyMetadataOptions.AffectsRender));

    public AppIconKind Kind
    {
        get => (AppIconKind)GetValue(KindProperty);
        set => SetValue(KindProperty, value);
    }

    public Brush Foreground
    {
        get => (Brush)GetValue(ForegroundProperty);
        set => SetValue(ForegroundProperty, value);
    }

    public double StrokeThickness
    {
        get => (double)GetValue(StrokeThicknessProperty);
        set => SetValue(StrokeThicknessProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var width = double.IsNaN(Width) ? 18d : Width;
        var height = double.IsNaN(Height) ? 18d : Height;
        return new Size(Math.Min(width, availableSize.Width), Math.Min(height, availableSize.Height));
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);
        var geometry = TryFindResource($"Icon.{Kind}") as Geometry
                       ?? Application.Current?.TryFindResource("Icon.Document") as Geometry;
        if (geometry is null || ActualWidth <= 0 || ActualHeight <= 0)
            return;

        var bounds = geometry.Bounds;
        if (bounds.IsEmpty)
            return;

        const double padding = 1.5;
        var scale = Math.Min(
            Math.Max(0, ActualWidth - padding * 2) / Math.Max(1, bounds.Width),
            Math.Max(0, ActualHeight - padding * 2) / Math.Max(1, bounds.Height));
        var offsetX = (ActualWidth - bounds.Width * scale) / 2 - bounds.X * scale;
        var offsetY = (ActualHeight - bounds.Height * scale) / 2 - bounds.Y * scale;

        drawingContext.PushTransform(new TranslateTransform(offsetX, offsetY));
        drawingContext.PushTransform(new ScaleTransform(scale, scale));
        var pen = new Pen(Foreground, StrokeThickness / Math.Max(scale, 0.01))
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round,
            LineJoin = PenLineJoin.Round
        };
        drawingContext.DrawGeometry(null, pen, geometry);
        drawingContext.Pop();
        drawingContext.Pop();
    }
}

public sealed class IconPresenter : FrameworkElement
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(object), typeof(IconPresenter),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ForegroundProperty = DependencyProperty.Register(
        nameof(Foreground), typeof(Brush), typeof(IconPresenter),
        new FrameworkPropertyMetadata(Brushes.White, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty FontSizeProperty = DependencyProperty.Register(
        nameof(FontSize), typeof(double), typeof(IconPresenter),
        new FrameworkPropertyMetadata(15d, FrameworkPropertyMetadataOptions.AffectsRender));

    public object? Value
    {
        get => GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    public Brush Foreground
    {
        get => (Brush)GetValue(ForegroundProperty);
        set => SetValue(ForegroundProperty, value);
    }

    public double FontSize
    {
        get => (double)GetValue(FontSizeProperty);
        set => SetValue(FontSizeProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize) => new(
        double.IsInfinity(availableSize.Width) ? 18 : availableSize.Width,
        double.IsInfinity(availableSize.Height) ? 18 : availableSize.Height);

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);
        var value = Value?.ToString() ?? string.Empty;
        var hint = (TemplatedParent as FrameworkElement)?.ToolTip?.ToString() ?? string.Empty;
        var tag = (TemplatedParent as FrameworkElement)?.Tag?.ToString() ?? string.Empty;
        var kind = ResolveKind(value, hint, tag);
        if (kind is null)
        {
            if (string.IsNullOrWhiteSpace(value) || Value is FrameworkElement)
                return;
            var text = new FormattedText(
                value,
                System.Globalization.CultureInfo.CurrentUICulture,
                FlowDirection.LeftToRight,
                new Typeface(new FontFamily("Segoe UI Variable Text, Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal),
                FontSize,
                Foreground,
                VisualTreeHelper.GetDpi(this).PixelsPerDip);
            drawingContext.DrawText(text, new Point((ActualWidth - text.Width) / 2, (ActualHeight - text.Height) / 2));
            return;
        }

        var geometry = TryFindResource($"Icon.{kind}") as Geometry;
        if (geometry is null)
            return;
        var bounds = geometry.Bounds;
        var scale = Math.Min(Math.Max(0, ActualWidth - 4) / Math.Max(1, bounds.Width), Math.Max(0, ActualHeight - 4) / Math.Max(1, bounds.Height));
        drawingContext.PushTransform(new TranslateTransform((ActualWidth - bounds.Width * scale) / 2 - bounds.X * scale, (ActualHeight - bounds.Height * scale) / 2 - bounds.Y * scale));
        drawingContext.PushTransform(new ScaleTransform(scale, scale));
        drawingContext.DrawGeometry(null, new Pen(Foreground, 1.7 / Math.Max(scale, .01)) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round }, geometry);
        drawingContext.Pop();
        drawingContext.Pop();
    }

    internal static AppIconKind? ResolveKind(string value, string hint, string tag)
    {
        var context = $"{hint} {tag}".ToLowerInvariant();
        if (tag.Contains("undo", StringComparison.OrdinalIgnoreCase)) return AppIconKind.Undo;
        if (tag.Contains("redo", StringComparison.OrdinalIgnoreCase)) return AppIconKind.Redo;
        if (tag.Contains("link", StringComparison.OrdinalIgnoreCase)) return AppIconKind.Link;
        if (tag.Contains("table", StringComparison.OrdinalIgnoreCase)) return AppIconKind.Table;
        if (tag.Contains("code", StringComparison.OrdinalIgnoreCase)) return AppIconKind.Code;
        if (context.Contains("настрой")) return AppIconKind.Settings;
        if (context.Contains("поиск")) return AppIconKind.Search;
        if (context.Contains("истори")) return AppIconKind.History;
        if (context.Contains("экспорт")) return AppIconKind.Export;
        if (context.Contains("печать")) return AppIconKind.Print;
        if (context.Contains("влож")) return AppIconKind.Attachment;
        if (context.Contains("повернуть влево")) return AppIconKind.RotateLeft;
        if (context.Contains("повернуть вправо")) return AppIconKind.RotateRight;
        if (context.Contains("обрез")) return AppIconKind.Crop;
        if (context.Contains("сжать")) return AppIconKind.Compress;
        if (context.Contains("заменить")) return AppIconKind.Replace;
        if (context.Contains("открыть оригинал")) return AppIconKind.OpenExternal;
        if (context.Contains("альтернативн")) return AppIconKind.AltText;
        if (context.Contains("подпись")) return AppIconKind.Caption;
        if (context.Contains("обтекание")&&context.Contains("слева")) return AppIconKind.WrapLeft;
        if (context.Contains("обтекание")&&context.Contains("справа")) return AppIconKind.WrapRight;
        if (context.Contains("без обтекания")) return AppIconKind.NoWrap;
        if (context.Contains("левому краю")) return AppIconKind.AlignLeft;
        if (context.Contains("центру")) return AppIconKind.AlignCenter;
        if (context.Contains("правому краю")) return AppIconKind.AlignRight;
        if (context.Contains("по ширине")) return AppIconKind.AlignJustify;
        if (context.Contains("уменьшить отступ")) return AppIconKind.Outdent;
        if (context.Contains("увеличить отступ")) return AppIconKind.Indent;
        if (context.Contains("подстроч")) return AppIconKind.Subscript;
        if (context.Contains("надстроч")) return AppIconKind.Superscript;
        if (context.Contains("очистить формат")) return AppIconKind.ClearFormatting;
        if (context.Contains("горизонтальн")) return AppIconKind.HorizontalRule;
        if (context.Contains("спойлер")||context.Contains("скрытый текст")) return AppIconKind.Spoiler;
        if (context.Contains("изображ")) return AppIconKind.Image;
        if (context.Contains("таблиц")) return AppIconKind.Table;
        if (context.Contains("код")) return AppIconKind.Code;
        if (context.Contains("цитат")) return AppIconKind.Quote;
        if (context.Contains("ссыл")) return AppIconKind.Link;
        if (context.Contains("удал")) return AppIconKind.Delete;
        if (context.Contains("уменьш")) return AppIconKind.ZoomOut;
        if (context.Contains("увелич")) return AppIconKind.ZoomIn;
        return value switch
        {
            "☰" => AppIconKind.Menu,
            "×" or "✕" => AppIconKind.Close,
            "—" => AppIconKind.Minimize,
            "□" => AppIconKind.Maximize,
            "＋" or "+" => AppIconKind.Add,
            "?" => AppIconKind.Help,
            "⚙" => AppIconKind.Settings,
            "◇" => AppIconKind.Tag,
            "↥" or "⇧" => AppIconKind.Export,
            "↧" or "⇩" => AppIconKind.Import,
            "▤" or "⎘" => AppIconKind.Copy,
            "◴" or "◷" => AppIconKind.History,
            "▱" => AppIconKind.OpenFolder,
            "▧" or "▣" => AppIconKind.Document,
            "⌫" => AppIconKind.Delete,
            "…" or "..." => AppIconKind.More,
            "</>" => AppIconKind.Code,
            "”" => AppIconKind.Quote,
            _ => null
        };
    }
}

public sealed class ButtonContentPresenter : ContentPresenter
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(object), typeof(ButtonContentPresenter),
        new FrameworkPropertyMetadata(null, OnValueChanged));

    public object? Value
    {
        get => GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    public static readonly DependencyProperty ForegroundProperty = TextElement.ForegroundProperty.AddOwner(
        typeof(ButtonContentPresenter), new FrameworkPropertyMetadata(Brushes.White));
    public static readonly DependencyProperty FontFamilyProperty = TextElement.FontFamilyProperty.AddOwner(
        typeof(ButtonContentPresenter), new FrameworkPropertyMetadata(new FontFamily("Segoe UI")));
    public static readonly DependencyProperty FontSizeProperty = TextElement.FontSizeProperty.AddOwner(
        typeof(ButtonContentPresenter), new FrameworkPropertyMetadata(13d));
    public static readonly DependencyProperty FontWeightProperty = TextElement.FontWeightProperty.AddOwner(
        typeof(ButtonContentPresenter), new FrameworkPropertyMetadata(FontWeights.Normal));
    public static readonly DependencyProperty FontStyleProperty = TextElement.FontStyleProperty.AddOwner(
        typeof(ButtonContentPresenter), new FrameworkPropertyMetadata(FontStyles.Normal));

    public Brush Foreground { get => (Brush)GetValue(ForegroundProperty); set => SetValue(ForegroundProperty, value); }
    public FontFamily FontFamily { get => (FontFamily)GetValue(FontFamilyProperty); set => SetValue(FontFamilyProperty, value); }
    public double FontSize { get => (double)GetValue(FontSizeProperty); set => SetValue(FontSizeProperty, value); }
    public FontWeight FontWeight { get => (FontWeight)GetValue(FontWeightProperty); set => SetValue(FontWeightProperty, value); }
    public FontStyle FontStyle { get => (FontStyle)GetValue(FontStyleProperty); set => SetValue(FontStyleProperty, value); }

    private static void OnValueChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs args) =>
        ((ButtonContentPresenter)dependencyObject).Rebuild();

    private void Rebuild()
    {
        if (Value is UIElement element)
        {
            Content = element;
            return;
        }

        var value = Value?.ToString() ?? string.Empty;
        var (kind, text) = Parse(value);
        if (kind is null)
        {
            Content = CreateText(value);
            return;
        }

        var panel = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        var icon = new AppIcon { Kind = kind.Value, Width = 16, Height = 16, VerticalAlignment = VerticalAlignment.Center };
        icon.SetBinding(AppIcon.ForegroundProperty, new Binding(nameof(Foreground)) { Source = this });
        panel.Children.Add(icon);
        if (!string.IsNullOrWhiteSpace(text))
        {
            var label = CreateText(text);
            label.Margin = new Thickness(8, 0, 0, 0);
            panel.Children.Add(label);
        }
        Content = panel;
    }

    private TextBlock CreateText(string text)
    {
        var label = new TextBlock
        {
            Text = text,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center
        };
        label.SetBinding(TextBlock.ForegroundProperty, new Binding(nameof(Foreground)) { Source = this });
        label.SetBinding(TextBlock.FontFamilyProperty, new Binding(nameof(FontFamily)) { Source = this });
        label.SetBinding(TextBlock.FontSizeProperty, new Binding(nameof(FontSize)) { Source = this });
        label.SetBinding(TextBlock.FontWeightProperty, new Binding(nameof(FontWeight)) { Source = this });
        label.SetBinding(TextBlock.FontStyleProperty, new Binding(nameof(FontStyle)) { Source = this });
        return label;
    }

    private static (AppIconKind? Kind, string Text) Parse(string value)
    {
        var trimmed = value.Trim();
        var separator = trimmed.IndexOf(' ');
        var prefix = separator < 0 ? trimmed : trimmed[..separator];
        var text = separator < 0 ? string.Empty : trimmed[(separator + 1)..].TrimStart();
        var context = trimmed.ToLowerInvariant();
        AppIconKind? kind = prefix switch
        {
            "+" or "＋" => AppIconKind.Add,
            "▧" or "▣" => context.Contains("папк") ? AppIconKind.Folder : context.Contains("шаблон") ? AppIconKind.Template : AppIconKind.Document,
            "▱" or "▰" => AppIconKind.Folder,
            "▦" => context.Contains("прилож") ? AppIconKind.Apps : AppIconKind.Table,
            "☆" => AppIconKind.Star,
            "◷" or "◴" => context.Contains("истори") ? AppIconKind.History : AppIconKind.Clock,
            "♲" or "⌫" => AppIconKind.Delete,
            "⇩" or "↧" => AppIconKind.Import,
            "⇧" or "↥" => AppIconKind.Export,
            "▤" or "⎘" or "⧉" => AppIconKind.Copy,
            "⚙" => AppIconKind.Settings,
            "◇" => context.Contains("mermaid") ? AppIconKind.App : AppIconKind.Tag,
            "?" => AppIconKind.Help,
            "ⓘ" => AppIconKind.Info,
            "✓" or "☑" => AppIconKind.Success,
            "⚠" => AppIconKind.Warning,
            "⛔" => AppIconKind.Error,
            "▾" => AppIconKind.ChevronDown,
            "⚓" => AppIconKind.Tag,
            "🔗" or "⌁" => AppIconKind.Link,
            "</>" => AppIconKind.Code,
            "−" => AppIconKind.Minus,
            _ => null
        };
        return kind is null ? (null, value) : (kind, text);
    }
}
