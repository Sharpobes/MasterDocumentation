using System.Windows;

namespace MasterDocumentation.UI;

public enum InteractionStatus
{
    Idle,
    Loading,
    Error,
    Success
}

public static class InteractionState
{
    public static readonly DependencyProperty StatusProperty = DependencyProperty.RegisterAttached(
        "Status",
        typeof(InteractionStatus),
        typeof(InteractionState),
        new FrameworkPropertyMetadata(InteractionStatus.Idle, FrameworkPropertyMetadataOptions.Inherits, OnStatusChanged));

    public static void SetStatus(DependencyObject element, InteractionStatus value) =>
        element.SetValue(StatusProperty, value);

    public static InteractionStatus GetStatus(DependencyObject element) =>
        (InteractionStatus)element.GetValue(StatusProperty);

    private static void OnStatusChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs args)
    {
        if (dependencyObject is FrameworkElement element)
            VisualStateManager.GoToElementState(element, args.NewValue?.ToString() ?? nameof(InteractionStatus.Idle), true);
    }
}
