using System.Globalization;
using System.ComponentModel;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Documents;

namespace MasterDocumentation.Utilities;

/// <summary>
/// Applies the selected language to the existing WPF views while retaining the original
/// Russian values as stable setting identifiers. This keeps old settings.json files valid.
/// </summary>
public static class LocalizationService
{
    public const string Russian = "Русский";
    public const string English = "English";

    private sealed class PropertyState
    {
        public required string Original { get; set; }
        public required string LastApplied { get; set; }
        public bool Applying { get; set; }
        public EventHandler? ChangeHandler { get; set; }
    }

    private sealed class ElementState
    {
        public Dictionary<DependencyProperty,PropertyState> Properties { get; } = [];
    }

    private static readonly ConditionalWeakTable<DependencyObject,ElementState> States = new();
    private static readonly Lazy<IReadOnlyDictionary<string,string>> EnglishTexts = new(LoadEnglishTexts);
    private static readonly Regex Cyrillic = new("[А-Яа-яЁё]",RegexOptions.Compiled|RegexOptions.CultureInvariant);
    private static bool _enabled;

    public static string CurrentLanguage { get; private set; } = Russian;
    public static bool IsEnglish => CurrentLanguage == English;
    public static event EventHandler? LanguageChanged;

    public static void Enable()
    {
        if(_enabled)return;
        _enabled=true;
        EventManager.RegisterClassHandler(typeof(Window),FrameworkElement.LoadedEvent,new RoutedEventHandler(OnRootLoaded),true);
        EventManager.RegisterClassHandler(typeof(UserControl),FrameworkElement.LoadedEvent,new RoutedEventHandler(OnRootLoaded),true);
        EventManager.RegisterClassHandler(typeof(ContextMenu),FrameworkElement.LoadedEvent,new RoutedEventHandler(OnRootLoaded),true);
        EventManager.RegisterClassHandler(typeof(ToolTip),FrameworkElement.LoadedEvent,new RoutedEventHandler(OnRootLoaded),true);
    }

    public static void SetLanguage(string? language)
    {
        var normalized=string.Equals(language,English,StringComparison.OrdinalIgnoreCase)||
                       string.Equals(language,"en",StringComparison.OrdinalIgnoreCase)||
                       string.Equals(language,"en-US",StringComparison.OrdinalIgnoreCase)
            ?English
            :Russian;
        var changed=CurrentLanguage!=normalized;
        CurrentLanguage=normalized;
        var culture=CultureInfo.GetCultureInfo(IsEnglish?"en-US":"ru-RU");
        CultureInfo.DefaultThreadCurrentCulture=culture;
        CultureInfo.DefaultThreadCurrentUICulture=culture;
        if(Application.Current is not null)
            foreach(var window in Application.Current.Windows.OfType<Window>().ToList())Apply(window);
        if(changed)LanguageChanged?.Invoke(null,EventArgs.Empty);
    }

    public static string T(string? value)
    {
        if(string.IsNullOrEmpty(value)||!IsEnglish)return value??string.Empty;
        if(EnglishTexts.Value.TryGetValue(value,out var translated))return translated;

        // Runtime messages often append a path, count, URL, or exception to a fixed caption.
        // Translate the stable leading phrase without touching that user or system data.
        foreach(var pair in DynamicPrefixes)
            if(value.StartsWith(pair.Key,StringComparison.Ordinal))
                return pair.Value+value[pair.Key.Length..];
        return value;
    }

    public static string SourceContent(ContentControl item)
    {
        if(States.TryGetValue(item,out var state)&&state.Properties.TryGetValue(ContentControl.ContentProperty,out var property))
            return property.Original;
        return item.Content?.ToString()??string.Empty;
    }

    public static void Apply(DependencyObject root)
    {
        var visited=new HashSet<DependencyObject>(ReferenceEqualityComparer.Instance);
        ApplyCore(root,visited);
    }

    private static void OnRootLoaded(object sender,RoutedEventArgs e)
    {
        if(sender is DependencyObject root)Apply(root);
    }

    private static void ApplyCore(DependencyObject element,HashSet<DependencyObject> visited)
    {
        if(!visited.Add(element))return;

        switch(element)
        {
            case Window window:
                ApplyProperty(window,Window.TitleProperty);
                break;
            case TextBlock text:
                ApplyProperty(text,TextBlock.TextProperty);
                break;
            case AccessText accessText:
                ApplyProperty(accessText,AccessText.TextProperty);
                break;
        }

        if(element is ContentControl content)ApplyProperty(content,ContentControl.ContentProperty);
        if(element is HeaderedContentControl headered)ApplyProperty(headered,HeaderedContentControl.HeaderProperty);
        if(element is HeaderedItemsControl headeredItems)ApplyProperty(headeredItems,HeaderedItemsControl.HeaderProperty);
        if(element is FrameworkElement frameworkElement)
        {
            ApplyProperty(frameworkElement,ToolTipService.ToolTipProperty);
            ApplyProperty(frameworkElement,AutomationProperties.NameProperty);
            ApplyProperty(frameworkElement,AutomationProperties.HelpTextProperty);
            if(frameworkElement.ContextMenu is not null)ApplyCore(frameworkElement.ContextMenu,visited);
            if(frameworkElement.ToolTip is DependencyObject toolTip)ApplyCore(toolTip,visited);
        }
        if(element is Popup { Child: DependencyObject popupChild })ApplyCore(popupChild,visited);
        if(element is DataGrid grid)
            foreach(var column in grid.Columns)ApplyProperty(column,DataGridColumn.HeaderProperty);
        if(element is ItemsControl items)
            foreach(var item in items.Items.OfType<DependencyObject>())ApplyCore(item,visited);

        foreach(var child in LogicalTreeHelper.GetChildren(element).OfType<DependencyObject>())ApplyCore(child,visited);
        if(element is System.Windows.Media.Visual or System.Windows.Media.Media3D.Visual3D)
        {
            var visualChildren=System.Windows.Media.VisualTreeHelper.GetChildrenCount(element);
            for(var index=0;index<visualChildren;index++)ApplyCore(System.Windows.Media.VisualTreeHelper.GetChild(element,index),visited);
        }
    }

    private static void ApplyProperty(DependencyObject element,DependencyProperty property)
    {
        if(BindingOperations.IsDataBound(element,property))return;
        if(element.GetValue(property) is not string current||current.Length==0)return;

        var elementState=States.GetOrCreateValue(element);
        if(!elementState.Properties.TryGetValue(property,out var state))
        {
            state=new PropertyState{Original=current,LastApplied=current};
            elementState.Properties[property]=state;
            var descriptor=DependencyPropertyDescriptor.FromProperty(property,element.GetType());
            if(descriptor is not null)
            {
                state.ChangeHandler=(_,_)=>OnLocalizedPropertyChanged(element,property,state);
                descriptor.AddValueChanged(element,state.ChangeHandler);
            }
        }
        else if(current!=state.LastApplied&&Cyrillic.IsMatch(current))
        {
            state.Original=current;
        }

        var target=IsEnglish?T(state.Original):state.Original;
        if(current!=target)
        {
            state.Applying=true;
            try{element.SetValue(property,target);}finally{state.Applying=false;}
        }
        state.LastApplied=target;
    }

    private static void OnLocalizedPropertyChanged(DependencyObject element,DependencyProperty property,PropertyState state)
    {
        if(state.Applying||BindingOperations.IsDataBound(element,property)||element.GetValue(property) is not string current)return;
        state.Original=current;
        var target=IsEnglish?T(current):current;
        state.LastApplied=target;
        if(target==current)return;
        state.Applying=true;
        try{element.SetValue(property,target);}finally{state.Applying=false;}
    }

    private static IReadOnlyDictionary<string,string> LoadEnglishTexts()
    {
        try
        {
            var assembly=Assembly.GetExecutingAssembly();
            var resource=assembly.GetManifestResourceNames().FirstOrDefault(name=>name.EndsWith("Localization.English.json",StringComparison.Ordinal));
            if(resource is null)return EssentialEnglish;
            using var stream=assembly.GetManifestResourceStream(resource);
            if(stream is null)return EssentialEnglish;
            var values=JsonSerializer.Deserialize<Dictionary<string,string>>(stream)??[];
            foreach(var pair in EssentialEnglish)values[pair.Key]=pair.Value;
            return values;
        }
        catch
        {
            return EssentialEnglish;
        }
    }

    private static readonly Dictionary<string,string> EssentialEnglish = new(StringComparer.Ordinal)
    {
        ["Язык интерфейса"]="Interface language",
        ["Язык меню и сообщений приложения"]="Language of application menus and messages",
        ["Язык меню и системных сообщений"]="Language of menus and system messages",
        ["Русский"]="Russian",
        ["Тема"]="Theme",
        ["Тема приложения"]="Application theme",
        ["Системная"]="System",
        ["Светлая"]="Light",
        ["Тёмная"]="Dark",
        ["Обычный"]="Normal",
        ["Обычный текст"]="Normal text",
        ["О программе"]="About",
        ["Горячие клавиши"]="Keyboard shortcuts",
        ["Недавние"]="Recent",
        ["Избранное"]="Favorites",
        ["Корзина"]="Trash",
        ["Открывать ссылки во"]="Open links in",
        ["Поведение при запуске"]="Startup behavior",
        ["Первый запуск — MasterDocumentation"]="First run — MasterDocumentation",
        ["Начать работу"]="Start",
        ["Да"]="Yes",
        ["Нет"]="No",
        ["Отмена"]="Cancel",
        ["ОК"]="OK",
        ["Хотите перейти по ссылке?"]="Do you want to open this link?",
        ["Переход по ссылке"]="Open link",
        ["Ссылка"]="Link",
        ["Безопасность"]="Security",
        ["Этот тип внешней ссылки заблокирован."]="This type of external link is blocked.",
        ["Не удалось открыть ссылку"]="Could not open the link",
        ["Не удалось открыть ссылку: "]="Could not open the link: "
    };

    private static readonly KeyValuePair<string,string>[] DynamicPrefixes =
    [
        new("Хотите перейти по ссылке?\n\n","Do you want to open this link?\n\n"),
        new("Не удалось открыть ссылку: ","Could not open the link: "),
        new("Произошла ошибка: ","An error occurred: "),
        new("Подробности записаны в ","Details were written to "),
        new("Результат проверки: ","Check result: "),
        new("Версия .NET: ",".NET version: "),
        new("Путь приложения: ","Application path: ")
    ];
}
