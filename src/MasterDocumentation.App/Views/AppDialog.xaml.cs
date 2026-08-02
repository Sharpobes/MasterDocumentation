using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using MasterDocumentation.UI;
using MasterDocumentation.Utilities;

namespace MasterDocumentation.Views;

public partial class AppDialog : Window
{
    private readonly MessageBoxButton _buttons;
    private MessageBoxResult _result = MessageBoxResult.None;

    public AppDialog(string message, string caption, MessageBoxButton buttons, MessageBoxImage image)
    {
        InitializeComponent();
        Title = string.IsNullOrWhiteSpace(caption) ? "MasterDocumentation" : caption;
        MessageText.Text = message;
        _buttons = buttons;
        ConfigureIcon(image);
        ConfigureButtons(buttons);
        PreviewKeyDown += OnPreviewKeyDown;
        Closing += OnClosing;
    }

    public MessageBoxResult Result => _result;

    private void ConfigureIcon(MessageBoxImage image)
    {
        var (kind, colorKey, surfaceKey) = image switch
        {
            MessageBoxImage.Error => (AppIconKind.Error, "State/Error", "State/ErrorSoft"),
            MessageBoxImage.Warning => (AppIconKind.Warning, "State/Warning", "State/WarningSoft"),
            MessageBoxImage.Question => (AppIconKind.Help, "Accent/Hover", "Accent/Soft"),
            _ => (AppIconKind.Info, "State/Info", "State/InfoSoft")
        };
        DialogIcon.Kind = kind;
        DialogIcon.SetResourceReference(AppIcon.ForegroundProperty, colorKey);
        IconSurface.SetResourceReference(BackgroundProperty, surfaceKey);
    }

    private void ConfigureButtons(MessageBoxButton buttons)
    {
        switch (buttons)
        {
            case MessageBoxButton.OK:
                AddButton("ОК", MessageBoxResult.OK, isDefault: true, primary: true);
                break;
            case MessageBoxButton.OKCancel:
                AddButton("Отмена", MessageBoxResult.Cancel, isCancel: true);
                AddButton("ОК", MessageBoxResult.OK, isDefault: true, primary: true);
                break;
            case MessageBoxButton.YesNo:
                AddButton("Нет", MessageBoxResult.No);
                AddButton("Да", MessageBoxResult.Yes, isDefault: true, primary: true);
                break;
            case MessageBoxButton.YesNoCancel:
                AddButton("Отмена", MessageBoxResult.Cancel, isCancel: true);
                AddButton("Нет", MessageBoxResult.No);
                AddButton("Да", MessageBoxResult.Yes, isDefault: true, primary: true);
                break;
        }
    }

    private void AddButton(string caption, MessageBoxResult result, bool isDefault = false, bool isCancel = false, bool primary = false)
    {
        var button = new Button
        {
            Content = caption,
            MinWidth = 96,
            IsDefault = isDefault,
            IsCancel = isCancel,
            Margin = new Thickness(8, 0, 0, 0)
        };
        if (primary)
            button.SetResourceReference(StyleProperty, "PrimaryButtonStyle");
        button.Click += (_, _) =>
        {
            _result = result;
            DialogResult = result is MessageBoxResult.OK or MessageBoxResult.Yes;
        };
        ButtonsPanel.Children.Add(button);
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs args)
    {
        if (args.Key != Key.Escape)
            return;
        _result = _buttons is MessageBoxButton.OK ? MessageBoxResult.OK
            : _buttons is MessageBoxButton.YesNo ? MessageBoxResult.No
            : MessageBoxResult.Cancel;
        Close();
        args.Handled = true;
    }

    private void OnClosing(object? sender, CancelEventArgs args)
    {
        if (_result != MessageBoxResult.None)
            return;
        _result = _buttons switch
        {
            MessageBoxButton.OK => MessageBoxResult.OK,
            MessageBoxButton.YesNo => MessageBoxResult.No,
            _ => MessageBoxResult.Cancel
        };
    }
}

public static class MessageBox
{
    public static MessageBoxResult Show(string message) =>
        Show(null, message, "MasterDocumentation", MessageBoxButton.OK, MessageBoxImage.None);

    public static MessageBoxResult Show(string message, string caption) =>
        Show(null, message, caption, MessageBoxButton.OK, MessageBoxImage.None);

    public static MessageBoxResult Show(string message, string caption, MessageBoxButton buttons) =>
        Show(null, message, caption, buttons, MessageBoxImage.None);

    public static MessageBoxResult Show(string message, string caption, MessageBoxButton buttons, MessageBoxImage image) =>
        Show(null, message, caption, buttons, image);

    public static MessageBoxResult Show(Window? owner, string message) =>
        Show(owner, message, "MasterDocumentation", MessageBoxButton.OK, MessageBoxImage.None);

    public static MessageBoxResult Show(Window? owner, string message, string caption) =>
        Show(owner, message, caption, MessageBoxButton.OK, MessageBoxImage.None);

    public static MessageBoxResult Show(Window? owner, string message, string caption, MessageBoxButton buttons) =>
        Show(owner, message, caption, buttons, MessageBoxImage.None);

    public static MessageBoxResult Show(Window? owner, string message, string caption, MessageBoxButton buttons, MessageBoxImage image)
    {
        owner ??= Application.Current?.Windows.OfType<Window>().FirstOrDefault(window => window.IsActive);
        var dialog = new AppDialog(message, caption, buttons, image);
        if (owner is { IsLoaded: true } && !ReferenceEquals(owner, dialog))
            dialog.Owner = owner;
        else
            // Диалог до появления главного окна остаётся единственным окном приложения:
            // без этого он не попадает на панель задач и может открыться на другом мониторе.
            WindowPlacementService.PrepareOwnerlessWindow(dialog);
        dialog.ShowDialog();
        return dialog.Result;
    }
}
