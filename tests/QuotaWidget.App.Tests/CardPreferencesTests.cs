using System.IO;
using QuotaWidget.Core;

namespace QuotaWidget.App.Tests;

public sealed class CardPreferencesTests
{
    [Fact]
    public void SettingsRoundTripPreservesOrderVisibilityAndPerCardChoices()
    {
        using var directory = new TestDirectory();
        var path = directory.PathFor("settings.json");
        var store = new SettingsStore(path, new(directory.Root, new TestClock(TestData.Now)));
        var cards = new[]
        {
            new CardPreference(CardPreferences.Pc, false, nameof(DisplayMode.Compact), nameof(ThemeChoice.Dark)),
            new CardPreference(CardPreferences.Claude, true, nameof(DisplayMode.Gauges), nameof(ThemeChoice.Light)),
            new CardPreference(CardPreferences.Codex, true),
        };

        store.Save(new WidgetSettings(Cards: cards));
        var actual = store.Load();

        Assert.Equal(cards, actual.Cards);
        Assert.Equal(DisplayMode.Compact, actual.Cards![0].DesignChoice);
        Assert.Equal(ThemeChoice.Dark, actual.Cards[0].ThemeChoice);
        var json = File.ReadAllText(path);
        Assert.Contains("\"cards\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("design_choice", json, StringComparison.Ordinal);
        Assert.DoesNotContain("theme_choice", json, StringComparison.Ordinal);
    }

    [Fact]
    public void OldJsonGetsThreeVisibleCardsThatInheritGlobalChoices()
    {
        using var directory = new TestDirectory();
        var path = directory.PathFor("settings.json");
        File.WriteAllText(path, """{"x":25,"y":40,"visible":false}""");

        var settings = new SettingsStore(path,
            new(directory.Root, new TestClock(TestData.Now))).Load();

        Assert.False(settings.Visible);
        var cards = Assert.IsType<CardPreference[]>(settings.Cards);
        Assert.Equal(CardPreferences.Normalize(null).Select(card => card.Id),
            cards.Select(card => card.Id));
        Assert.All(cards, card =>
        {
            Assert.True(card.Visible);
            Assert.Null(card.Design);
            Assert.Null(card.Theme);
        });
    }

    [Fact]
    public void NormalizeIgnoresUnknownAndDuplicateCardsAndAddsMissingDefaults()
    {
        var actual = CardPreferences.Normalize(
        [
            new("PC", false, "compact", "dark"),
            new("future-card", false, "Detailed", "Light"),
            new(CardPreferences.Pc, true, "Detailed", "Light"),
            new(CardPreferences.Codex, false, "future-design", "future-theme"),
        ]);

        Assert.Collection(actual,
            card => Assert.Equal(CardPreferences.Pc, card.Id),
            card => Assert.Equal(CardPreferences.Codex, card.Id),
            card => Assert.Equal(CardPreferences.Claude, card.Id));
        Assert.False(actual[0].Visible);
        Assert.Equal(nameof(DisplayMode.Compact), actual[0].Design);
        Assert.Equal(nameof(ThemeChoice.Dark), actual[0].Theme);
        Assert.False(actual[1].Visible);
        Assert.Null(actual[1].Design);
        Assert.Null(actual[1].Theme);
        Assert.True(actual[2].Visible);
    }

    [Fact]
    public void AllCardsCanBeHiddenWithoutResettingTheSelection()
    {
        var cards = CardPreferences.Normalize(null);
        foreach (var id in new[] { CardPreferences.Claude, CardPreferences.Codex, CardPreferences.Pc })
        {
            cards = CardPreferences.SetVisible(cards, id, false);
        }

        Assert.All(cards, card => Assert.False(card.Visible));
        Assert.Collection(cards,
            card => Assert.Equal(CardPreferences.Claude, card.Id),
            card => Assert.Equal(CardPreferences.Codex, card.Id),
            card => Assert.Equal(CardPreferences.Pc, card.Id));
    }

    [Fact]
    public void MoveClampsAtBoundsAndInvalidIdOnlyNormalizes()
    {
        var cards = CardPreferences.Normalize(null);

        cards = CardPreferences.Move(cards, CardPreferences.Pc, int.MinValue);
        Assert.Collection(cards,
            card => Assert.Equal(CardPreferences.Pc, card.Id),
            card => Assert.Equal(CardPreferences.Claude, card.Id),
            card => Assert.Equal(CardPreferences.Codex, card.Id));

        cards = CardPreferences.Move(cards, CardPreferences.Pc, int.MaxValue);
        Assert.Collection(cards,
            card => Assert.Equal(CardPreferences.Claude, card.Id),
            card => Assert.Equal(CardPreferences.Codex, card.Id),
            card => Assert.Equal(CardPreferences.Pc, card.Id));

        var unchanged = CardPreferences.Move(cards, "unknown", 1);
        Assert.Equal(cards, unchanged);
    }

    [Fact]
    public void HelpersSetOrClearPerCardInheritanceAndIgnoreInvalidId()
    {
        var cards = CardPreferences.SetDesign(null, CardPreferences.Claude, DisplayMode.Gauges);
        cards = CardPreferences.SetTheme(cards, CardPreferences.Claude, ThemeChoice.Dark);
        Assert.Equal(nameof(DisplayMode.Gauges), cards[0].Design);
        Assert.Equal(nameof(ThemeChoice.Dark), cards[0].Theme);

        cards = CardPreferences.SetDesign(cards, CardPreferences.Claude, null);
        cards = CardPreferences.SetTheme(cards, CardPreferences.Claude, null);
        Assert.Null(cards[0].Design);
        Assert.Null(cards[0].Theme);

        var unchanged = CardPreferences.SetVisible(cards, "unknown", false);
        Assert.Equal(cards, unchanged);
    }
}
