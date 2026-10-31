using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shell;

namespace QuotaWidget.App;

public partial class MainWindow : Window
{
    public event EventHandler? HideRequested;
    public event EventHandler? PositionChanged;
    public event EventHandler? RefreshRequested;
    public event EventHandler? PinRequested;
    public event EventHandler? SettingsRequested;
    public bool AllowClose { get; set; }
    internal Func<WidgetMenuItem[]>? MenuItems { get; set; }
    private DisplayMode mode;
    private bool applyingSize;
    private ThemeChoice resolvedTheme = ThemeChoice.Light;
    private WidgetLog? log;

    public MainWindow()
    {
        InitializeComponent();
        Loaded += (_, _) => FitToContent();
        Closed += (_, _) => CloseSessionDetails();
        SizeChanged += (_, args) => { if (args.WidthChanged) { FitToContent(); } };
        Resources.MergedDictionaries.Add(ThemeResources.Light);
        SourceInitialized += (_, _) =>
        {
            HwndSource.FromHwnd(new WindowInteropHelper(this).Handle)?.AddHook(WindowMessage);
            ApplyAppearance();
        };
        StateChanged += (_, _) =>
        {
            if (CardWindowState.MustRestore(WindowState)) { WindowState = WindowState.Normal; }
        };
        ContextMenu = new ContextMenu();
        ContextMenuOpening += (_, _) => FillMenu(ContextMenu, MenuItems?.Invoke() ?? []);
        Closing += (_, args) =>
        {
            if (!AllowClose) { args.Cancel = true; HideRequested?.Invoke(this, EventArgs.Empty); }
        };
    }

    internal void ApplyTheme(ThemeChoice choice, WidgetLog widgetLog)
    {
        log = widgetLog;
        resolvedTheme = ThemeResources.Resolve(choice, widgetLog);
        Resources.MergedDictionaries.Clear();
        Resources.MergedDictionaries.Add(ThemeResources.For(resolvedTheme));
        ApplyAppearance();
    }

    private void ApplyAppearance()
    {
        if (log is not null && NativeAppearance.Apply(this, resolvedTheme, log)) { Background = Brushes.Transparent; }
        else
        {
            WindowChrome.GetWindowChrome(this).GlassFrameThickness = new Thickness(0);
            SetResourceReference(BackgroundProperty, "CardBackground");
        }
    }

    internal void ApplySettings(WidgetSettings settings)
    {
        applyingSize = true;
        try
        {
            mode = settings.Mode;
            var minimum = DisplaySizing.Minimum(mode);
            MinWidth = minimum.Width;
            MinHeight = minimum.Height;
            Width = settings.Size.Width;
            Height = settings.Size.Height;
            Topmost = settings.Topmost;
            ApplyCardPreferences(settings.Cards);
        }
        finally { applyingSize = false; }
    }

    public void Render(CardPresentation presentation)
    {
        ArgumentNullException.ThrowIfNull(presentation);
        CardTitle.Text = presentation.Header.Title;
        CollectionTime.Text = presentation.Header.CollectionText;
        MeasurementAge.Text = presentation.Header.AgeText;
        CollectionTime.ToolTip = "Heure de collecte";
        PinButton.SetResourceReference(Control.BackgroundProperty, Topmost ? "BarTrack" : "CardBackground");
        PinButton.SetResourceReference(Control.ForegroundProperty, Topmost ? "StateGreen" : "PrimaryText");
        PinButton.Content = Topmost ? "\uE840" : "\uE718";
        PinButton.ToolTip = Topmost ? "Désépingler : toujours au premier plan activé" : "Épingler : toujours au premier plan désactivé";
        AutomationProperties.SetName(PinButton, (string)PinButton.ToolTip);
        AutomationProperties.SetHelpText(this, presentation.AccessibleText);
        lastPresentation = presentation;
        RenderCards();
    }

    private void ChooseMode(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = ModeButton, Placement = PlacementMode.Bottom };
        FillMenu(menu, MenuItems?.Invoke().FirstOrDefault(item => item.Label == "Mode d’affichage")?.Children ?? []);
        menu.Resources.MergedDictionaries.Add(Resources);
        menu.IsOpen = true;
    }

    private static void FillMenu(ContextMenu menu, WidgetMenuItem[] items)
    {
        menu.Items.Clear();
        foreach (var item in items) { menu.Items.Add(MenuItem(item)); }
    }

    private static MenuItem MenuItem(WidgetMenuItem item)
    {
        var result = new MenuItem { Header = item.Label, IsCheckable = item.Checked.HasValue, IsChecked = item.Checked == true };
        if (item.Execute is { } execute) { result.Click += (_, e) => { execute(); e.Handled = true; }; }
        foreach (var child in item.Children ?? []) { result.Items.Add(MenuItem(child)); }
        return result;
    }

    private IntPtr WindowMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        // Persist once on exiting the native move/resize loop, never on each size notification.
        if (message == 0x0232 && !applyingSize) { PositionChanged?.Invoke(this, EventArgs.Empty); }
        return IntPtr.Zero;
    }

    private void HideCard(object sender, RoutedEventArgs e) => HideRequested?.Invoke(this, EventArgs.Empty);
    private void RefreshCard(object sender, RoutedEventArgs e) => RefreshRequested?.Invoke(this, EventArgs.Empty);
    private void PinCard(object sender, RoutedEventArgs e) => PinRequested?.Invoke(this, EventArgs.Empty);
    private void OpenSettings(object sender, RoutedEventArgs e) => SettingsRequested?.Invoke(this, EventArgs.Empty);

    private void DragCard(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) { return; }
        for (var element = e.OriginalSource as DependencyObject; element is not null;
            element = element is Visual ? VisualTreeHelper.GetParent(element) : LogicalTreeHelper.GetParent(element))
        { if (element is ButtonBase) { return; } }
        DragMove();
        PositionChanged?.Invoke(this, EventArgs.Empty);
    }
}
