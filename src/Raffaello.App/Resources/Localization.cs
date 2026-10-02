using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Markup;
using System.Windows.Media;
using Raffaello.Core.Localization;

namespace Raffaello.App.Resources;

/// <summary>
/// The interface language at runtime: strings by key (<c>{res:L Nav_Dashboard}</c>), right-to-left layout for Arabic, the Arabic-capable
/// font fallback, and the display culture (numbers / dates; storage stays invariant). Literal English texts still written in XAML are
/// translated by <see cref="AutoTranslator"/> until each screen is converted to keys.
/// </summary>
public sealed class LocService : INotifyPropertyChanged
{
    public static LocService Instance { get; } = new();
    public event PropertyChangedEventHandler? PropertyChanged;

    public string this[string key] => Loc.T(key);
    public string Language => Loc.Language;
    public bool IsArabic => Loc.IsArabic;
    public FlowDirection FlowDirection => Loc.IsArabic ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
    /// <summary>Changes on every switch (multi-bindings re-translate bound English texts).</summary>
    public int Version { get; private set; }

    /// <summary>Arabic glyphs: IBM Plex Sans Arabic when installed, else Segoe UI / Tahoma (Windows ships both).</summary>
    public const string ArabicFontFallback = "IBM Plex Sans Arabic, Segoe UI, Tahoma";

    private static bool _languageMetadataSet;

    /// <summary>
    /// Once, before the first window: WPF formats bound numbers and dates with the element language. en-GB, or ar-AE for Arabic
    /// (Gregorian calendar, "." and "," separators). A later switch takes effect for these formats after a restart.
    /// </summary>
    public static void InitializeFormatting(string lang)
    {
        var culture = Loc.Culture(lang);
        CultureInfo.DefaultThreadCurrentCulture = culture;
        CultureInfo.DefaultThreadCurrentUICulture = culture;
        Thread.CurrentThread.CurrentCulture = culture;
        Thread.CurrentThread.CurrentUICulture = culture;
        if (_languageMetadataSet) return;
        _languageMetadataSet = true;
        FrameworkElement.LanguageProperty.OverrideMetadata(typeof(FrameworkElement),
            new FrameworkPropertyMetadata(XmlLanguage.GetLanguage(Loc.WpfLanguageTag(lang))));
    }

    /// <summary>Switches the language: strings, RTL and fonts update at once on every open window.</summary>
    public void Apply(string? lang)
    {
        Loc.SetLanguage(lang);
        Version++;
        var culture = Loc.Culture();
        Thread.CurrentThread.CurrentCulture = culture;
        Thread.CurrentThread.CurrentUICulture = culture;
        CultureInfo.DefaultThreadCurrentCulture = culture;
        CultureInfo.DefaultThreadCurrentUICulture = culture;
        ApplyFonts();
        if (Application.Current != null)
            foreach (Window w in Application.Current.Windows) ApplyTo(w);
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Language)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsArabic)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(FlowDirection)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Version)));
    }

    public void ApplyTo(Window w)
    {
        w.FlowDirection = FlowDirection;
        AutoTranslator.TranslateTree(w);
    }

    private static readonly Dictionary<string, object> OriginalFonts = new();

    /// <summary>The font resources (FontSans ...) get an Arabic fallback in Arabic; English keeps the bundled fonts.</summary>
    private static void ApplyFonts()
    {
        var res = Application.Current?.Resources;
        if (res is null) return;
        foreach (var key in new[] { "FontSans", "FontSansSemi", "FontDisplay", "FontDisplaySemi" })
        {
            if (res[key] is not FontFamily current) continue;
            if (!OriginalFonts.ContainsKey(key)) OriginalFonts[key] = current;
            var original = (FontFamily)OriginalFonts[key];
            res[key] = Loc.IsArabic ? new FontFamily(original.BaseUri, original.Source + ", " + ArabicFontFallback) : original;
        }
    }
}

/// <summary>Markup extension: <c>Text="{res:L Btn_Save}"</c> - follows the language switch.</summary>
[MarkupExtensionReturnType(typeof(BindingExpression))]
public sealed class LExtension : MarkupExtension
{
    public LExtension() { }
    public LExtension(string key) => Key = key;
    [ConstructorArgument("key")] public string Key { get; set; } = "";

    public override object ProvideValue(IServiceProvider serviceProvider) =>
        new Binding($"[{Key}]") { Source = LocService.Instance, Mode = BindingMode.OneWay }.ProvideValue(serviceProvider);
}

/// <summary>
/// Translates bound English text (navigation labels, page titles, queue categories). Use as a multi-binding with
/// LocService.Version as the second value so the text updates on a language switch, or as a plain converter.
/// </summary>
public sealed class TrConverter : IValueConverter, IMultiValueConverter
{
    public static TrConverter Instance { get; } = new();

    public static string Tr(string? english) => string.IsNullOrEmpty(english) ? "" : Loc.FromEnglish(english) ?? english;

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => Tr(value as string ?? value?.ToString());
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture) => Tr(values.Length > 0 ? values[0] as string ?? values[0]?.ToString() : "");
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) => Array.Empty<object>();
}

/// <summary>
/// Translates literal (not data-bound) English texts of TextBlocks, buttons, check boxes, headers and tool tips when they are loaded,
/// and again on every language switch; the English original is kept so switching back restores it. Data-bound values, inputs,
/// combo / list items and status tags (whose English value drives their colour) are never touched.
/// </summary>
public static class AutoTranslator
{
    private static readonly DependencyProperty OriginalProperty =
        DependencyProperty.RegisterAttached("Original", typeof(string), typeof(AutoTranslator), new PropertyMetadata(null));
    private static readonly DependencyProperty OriginalTipProperty =
        DependencyProperty.RegisterAttached("OriginalTip", typeof(string), typeof(AutoTranslator), new PropertyMetadata(null));

    private static bool _registered;

    public static void Register()
    {
        if (_registered) return;
        _registered = true;
        EventManager.RegisterClassHandler(typeof(FrameworkElement), FrameworkElement.LoadedEvent, new RoutedEventHandler((s, _) =>
        {
            if (s is DependencyObject d && (Loc.IsArabic || d.ReadLocalValue(OriginalProperty) != DependencyProperty.UnsetValue)) Translate(d);
        }), true);
        EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent, new RoutedEventHandler((s, _) =>
        {
            if (s is Window w) w.FlowDirection = LocService.Instance.FlowDirection;
        }), true);
    }

    private static bool IsLiteral(DependencyObject d, DependencyProperty p)
    {
        if (BindingOperations.GetBindingExpressionBase(d, p) != null) return false;
        var source = DependencyPropertyHelper.GetValueSource(d, p);
        if (source.IsExpression || source.IsAnimated) return false;
        return source.BaseValueSource is BaseValueSource.Local or BaseValueSource.ParentTemplate;
    }

    private static string? Next(DependencyObject d, string current, DependencyProperty store)
    {
        var original = d.GetValue(store) as string;
        if (original is null)
        {
            if (!Loc.IsArabic) return null;
            var t = Loc.FromEnglish(current);
            if (t is null) return null;
            d.SetValue(store, current);
            return t;
        }
        // already translated once: switch between the English original and the current language
        return Loc.IsArabic ? Loc.FromEnglish(original) ?? original : original;
    }

    public static void Translate(DependencyObject d)
    {
        switch (d)
        {
            case TextBlock tb when tb.Inlines.Count <= 1 && IsLiteral(tb, TextBlock.TextProperty) && !(tb.TemplatedParent is ContentPresenter):
            {
                var next = Next(tb, tb.Text, OriginalProperty);
                if (next != null && next != tb.Text) tb.Text = next;
                break;
            }
            case ComboBoxItem or ListBoxItem or TextBoxBase or PasswordBox:
                return;
            case HeaderedContentControl hc when hc.Header is string h && IsLiteral(hc, HeaderedContentControl.HeaderProperty):
            {
                var next = Next(hc, h, OriginalProperty);
                if (next != null) hc.Header = next;
                break;
            }
            case HeaderedItemsControl hi when hi.Header is string h2 && IsLiteral(hi, HeaderedItemsControl.HeaderProperty):
            {
                var next = Next(hi, h2, OriginalProperty);
                if (next != null) hi.Header = next;
                break;
            }
            case ContentControl cc when cc is not Window && cc.Content is string c && IsLiteral(cc, ContentControl.ContentProperty) && !IsStatusTag(cc):
            {
                var next = Next(cc, c, OriginalProperty);
                if (next != null) cc.Content = next;
                break;
            }
        }
        if (d is FrameworkElement fe && fe.ToolTip is string tip && IsLiteral(fe, FrameworkElement.ToolTipProperty))
        {
            var next = Next(fe, tip, OriginalTipProperty);
            if (next != null) fe.ToolTip = next;
        }
    }

    private static bool IsStatusTag(FrameworkElement e) =>
        e.Style != null && Application.Current?.TryFindResource("Tag") is Style tag && (ReferenceEquals(e.Style, tag) || ReferenceEquals(e.Style.BasedOn, tag));

    /// <summary>Walks a window (logical + visual tree) and applies the current language to every literal text.</summary>
    public static void TranslateTree(DependencyObject root)
    {
        var seen = new HashSet<DependencyObject>();
        var stack = new Stack<DependencyObject>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var d = stack.Pop();
            if (!seen.Add(d)) continue;
            Translate(d);
            if (d is Visual or System.Windows.Media.Media3D.Visual3D)
            {
                var n = VisualTreeHelper.GetChildrenCount(d);
                for (var i = 0; i < n; i++) stack.Push(VisualTreeHelper.GetChild(d, i));
            }
            foreach (var child in LogicalTreeHelper.GetChildren(d).OfType<DependencyObject>()) stack.Push(child);
        }
    }
}
