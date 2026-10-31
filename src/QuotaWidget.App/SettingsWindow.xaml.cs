using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace QuotaWidget.App;

public partial class SettingsWindow : Window
{
    private WidgetSettings current;
    private readonly Action<WidgetSettings> apply;

    internal SettingsWindow(MainWindow owner, WidgetSettings settings, Action<WidgetSettings> apply)
    {
        this.apply = apply;
        current = settings;
        InitializeComponent();
        Owner = owner;
        MaxWidth = NativeDesktop.AvailableWidth(owner);
        MaxHeight = NativeDesktop.AvailableHeight(owner);
        Loaded += (_, _) =>
        {
            MaxWidth = NativeDesktop.AvailableWidth(this);
            MaxHeight = NativeDesktop.AvailableHeight(this);
            Update(current);
            NativeDesktop.KeepFullyVisible(this);
        };
        Resources.MergedDictionaries.Add(owner.Resources);
        Update(settings);
    }

    internal void Update(WidgetSettings settings)
    {
        current = settings with { Cards = CardPreferences.Normalize(settings.Cards) };
        CardsPanel.Children.Clear();
        var cards = current.Cards!;
        for (var index = 0; index < cards.Length; index++) { CardsPanel.Children.Add(CardRow(cards[index], index, cards.Length)); }
        BackgroundButton.Content = "Fond global : " + WidgetMenu.ThemeLabel(settings.Theme) + "  ▾";
    }

    private Border CardRow(CardPreference card, int index, int count)
    {
        var content = new StackPanel();
        var header = new DockPanel { Margin = new Thickness(0, 0, 0, 10) };
        var movement = new StackPanel { Orientation = Orientation.Horizontal };
        movement.Children.Add(MoveButton(card, -1, "↑", index > 0));
        movement.Children.Add(MoveButton(card, 1, "↓", index < count - 1));
        DockPanel.SetDock(movement, Dock.Right);
        header.Children.Add(movement);
        var visible = new CheckBox { Content = CardName(card.Id), IsChecked = card.Visible,
            FontSize = 15, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
        visible.SetResourceReference(Control.ForegroundProperty, "PrimaryText");
        AutomationProperties.SetName(visible, "Afficher " + CardName(card.Id));
        visible.Checked += (_, _) => ApplyCards(CardPreferences.SetVisible(current.Cards, card.Id, true));
        visible.Unchecked += (_, _) => ApplyCards(CardPreferences.SetVisible(current.Cards, card.Id, false));
        header.Children.Add(visible);
        content.Children.Add(header);
        var choices = new Grid();
        choices.ColumnDefinitions.Add(new ColumnDefinition());
        choices.ColumnDefinitions.Add(new ColumnDefinition());
        choices.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        choices.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var design = new Button { Content = "Design : " + DesignLabel(card.Id, card.DesignChoice) + "  ▾", Margin = new Thickness(0, 0, 4, 0) };
        design.Click += (_, _) => ShowDesignMenu(design, card);
        choices.Children.Add(design);
        var theme = new Button { Content = "Fond : " + (card.ThemeChoice is { } value ? WidgetMenu.ThemeLabel(value) : "Hérité") + "  ▾",
            Margin = new Thickness(4, 0, 0, 0) };
        theme.Click += (_, _) => ShowThemeMenu(theme, card.Id);
        Grid.SetColumn(theme, 1);
        if (MaxWidth < 440)
        {
            Grid.SetColumn(theme, 0);
            Grid.SetColumnSpan(theme, 2);
            Grid.SetRow(theme, 1);
            Grid.SetColumnSpan(design, 2);
            theme.Margin = new Thickness(0, 4, 0, 0);
            design.Margin = new Thickness(0);
        }
        choices.Children.Add(theme);
        content.Children.Add(choices);
        var border = new Border { Padding = new Thickness(13), Margin = new Thickness(0, 0, 10, 10),
            CornerRadius = new CornerRadius(11), BorderThickness = new Thickness(1), Child = content };
        border.SetResourceReference(Border.BackgroundProperty, "TileSurface");
        border.SetResourceReference(Border.BorderBrushProperty, "SurfaceBorder");
        return border;
    }

    private Button MoveButton(CardPreference card, int delta, string icon, bool enabled)
    {
        var button = new Button { Content = icon, Width = 32, Padding = new Thickness(4),
            IsEnabled = enabled, Margin = new Thickness(4, 0, 0, 0) };
        AutomationProperties.SetName(button, (delta < 0 ? "Monter " : "Descendre ") + CardName(card.Id));
        button.ToolTip = AutomationProperties.GetName(button);
        button.Click += (_, _) => ApplyCards(CardPreferences.Move(current.Cards, card.Id, delta));
        return button;
    }

    private void ShowDesignMenu(Button target, CardPreference card)
    {
        var menu = Menu(target);
        Choice(menu, "Par défaut", card.DesignChoice is null, () => ApplyCards(CardPreferences.SetDesign(current.Cards, card.Id, null)));
        foreach (var design in Enum.GetValues<DisplayMode>())
        { Choice(menu, DesignLabel(card.Id, design), card.DesignChoice == design, () => ApplyCards(CardPreferences.SetDesign(current.Cards, card.Id, design))); }
        menu.IsOpen = true;
    }

    private void ShowThemeMenu(Button target, string? cardId)
    {
        var menu = Menu(target);
        var selected = cardId is null ? current.Theme : CardPreferences.Normalize(current.Cards).First(card => card.Id == cardId).ThemeChoice;
        if (cardId is not null)
        { Choice(menu, "Hérité", selected is null, () => ApplyCards(CardPreferences.SetTheme(current.Cards, cardId, null))); }
        foreach (var theme in Enum.GetValues<ThemeChoice>())
        {
            Choice(menu, WidgetMenu.ThemeLabel(theme), selected == theme,
                () => { if (cardId is null) { apply(current with { Theme = theme }); } else { ApplyCards(CardPreferences.SetTheme(current.Cards, cardId, theme)); } });
        }
        menu.IsOpen = true;
    }

    private ContextMenu Menu(Button target)
    {
        var menu = new ContextMenu { PlacementTarget = target, Placement = PlacementMode.Bottom };
        menu.Resources.MergedDictionaries.Add(Resources);
        menu.SetResourceReference(Control.BackgroundProperty, "SectionSurface");
        menu.SetResourceReference(Control.ForegroundProperty, "PrimaryText");
        return menu;
    }

    private static void Choice(ContextMenu menu, string label, bool selected, Action action)
    {
        var item = new MenuItem { Header = label, IsCheckable = true, IsChecked = selected };
        item.Click += (_, _) => action();
        menu.Items.Add(item);
    }

    private void ApplyCards(CardPreference[] cards) => apply(current with { Cards = cards });

    private void ChooseBackground(object sender, RoutedEventArgs e) => ShowThemeMenu(BackgroundButton, null);

    internal static string CardName(string id) => id switch { "claude" => "Claude", "codex" => "Codex", _ => "Ce PC" };

    internal static string DesignLabel(string id, DisplayMode? design) => design switch
    {
        null => "Par défaut",
        DisplayMode.Detailed => id == "pc" ? "Tuiles" : "Vue d’ensemble",
        DisplayMode.Compact => id == "pc" ? "Lignes" : "Compact",
        _ => id == "pc" ? "Anneaux" : "Jauges",
    };
}
