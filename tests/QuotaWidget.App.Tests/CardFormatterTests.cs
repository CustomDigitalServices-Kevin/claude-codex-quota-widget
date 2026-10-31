using System.Globalization;
using QuotaWidget.Core;

namespace QuotaWidget.App.Tests;

public sealed class CardFormatterTests
{
    private static readonly string[] RemovedNotes = ["Plafond absolu non exposé par les sources",
        "Mesure Claude rafraîchie par une session Claude Code interactive", "Messages ChatGPT (chat) : non exposés"];

    private static CardPresentation Format(QuotaState state, Diagnostic[]? diagnostics = null, DateTimeOffset? now = null) =>
        CardFormatter.Format(state, diagnostics ?? [], now ?? TestData.Now, TestData.Now,
            TimeZoneInfo.FindSystemTimeZoneById("Europe/Paris"), CultureInfo.GetCultureInfo("fr-FR"));

    [Fact]
    public void Matches1031PlanWithFrenchDatesPercentagesAndCoreColors()
    {
        var state = TestData.State with { Codex = TestData.State.Codex with { Plan = "pro" } };
        var view = Format(state);
        Assert.Equal("Quotas IA", view.Header.Title);
        Assert.Equal("10:31", view.Header.CollectionText);
        Assert.Equal(TestData.Now, view.Header.CollectedAt);
        Assert.Equal("mesure il y a 2 min", view.Header.AgeText);
        Assert.Equal("Claude", view.Sections[0].Name);
        Assert.Empty(view.Sections[0].Plan); // No Claude plan is exposed by this fixture.
        var five = view.Sections[0].Windows[0];
        Assert.Equal("5 h glissantes", five.Label);
        Assert.Equal(31, five.Percent);
        Assert.Equal("31 %", five.PercentText);
        Assert.Equal("remise 10:50", five.ResetText);
        Assert.Equal("mesure il y a 2 min", five.AgeText);
        Assert.Equal("vert", five.Color);
        var week = view.Sections[0].Windows[1];
        Assert.Equal("Semaine", week.Label);
        Assert.Equal(68, week.Percent);
        Assert.Equal("68 %", week.PercentText);
        Assert.Equal("remise dim. 21:00", week.ResetText);
        Assert.Equal(3, Math.Round(week.PaceDifference!.Value));
        Assert.Contains("écart : +", week.PaceToolTip, StringComparison.Ordinal);
        Assert.Equal(QuotaCalculations.Calculate(state.Claude.Windows[1], TestData.Now).ElapsedFraction, week.ElapsedFraction);
        Assert.Equal("Codex", view.Sections[1].Name);
        Assert.Equal("Pro", view.Sections[1].Plan);
        var codex = Assert.Single(view.Sections[1].Windows);
        Assert.Equal(4, codex.Percent);
        Assert.Equal("4 %", codex.PercentText);
        Assert.Equal("remise mer. 07:35", codex.ResetText);
        Assert.Equal("mesure il y a 1 min", codex.AgeText);
        Assert.Equal("Crédits et remises gratuites : inconnu (lecture directe requise)", view.Sections[1].CreditsText);
        Assert.All(view.Sections.SelectMany(s => s.Windows), window => Assert.NotNull(window.Color));
        // Replaces the former three footer assertions: those facts now live in README only.
        Assert.All(RemovedNotes, note => Assert.DoesNotContain(note, view.AccessibleText, StringComparison.Ordinal));
        Assert.Contains("CLAUDE 5 h 31 %", view.ToolTip, StringComparison.Ordinal);
        Assert.Contains("CODEX 7 j 4 %", view.ToolTip, StringComparison.Ordinal);
        Assert.Contains("31 %", view.AccessibleText, StringComparison.Ordinal);
    }

    [Fact]
    public void UnknownAgeAndPassedResetNeverBecomeFreshOrZero()
    {
        var window = TestData.State.Claude.Windows[0] with { MeasuredAt = null, ResetsAt = TestData.Now.AddMinutes(-1) };
        var view = Format(TestData.State with { Claude = TestData.Provider("claude", window) });
        var row = Assert.Single(view.Sections[0].Windows);
        Assert.Equal("31 %", row.PercentText);
        Assert.Equal("remise passée, en attente de mesure", row.ResetText);
        Assert.Equal("âge inconnu", row.AgeText);
        Assert.Null(row.AgeSeconds);
        Assert.Equal("âge inconnu", view.Header.AgeText);
    }

    [Fact]
    public void NoMeasurementExplainsDiagnostics()
    {
        var view = Format(StateBuilder.Build([]), [new("claude_dossier_illisible", "private-path"), new("codex_fichier_ignore", "private-path")]);
        Assert.Contains("Aucune mesure — dossier absent ou illisible", view.Sections[0].Messages);
        Assert.Contains("Aucune mesure — fichier illisible ou invalide", view.Sections[1].Messages);
        Assert.All(view.Sections, section => { Assert.Empty(section.Windows); Assert.Equal("Aucune mesure", section.Badge); });
        Assert.DoesNotContain("private-path", view.AccessibleText, StringComparison.Ordinal);
    }

    [Fact]
    public void BlockagePersistsAfterResetAndUsesExclamationIcon()
    {
        var blocked = TestData.State.Codex with { Blockage = new("bloque", TestData.Now.AddHours(-2), "fixture") };
        var view = Format(TestData.State with { Codex = blocked });
        Assert.Contains(view.Sections[1].Messages, text => text.Contains("Blocage constaté le", StringComparison.Ordinal)
            && text.Contains("rétablissement non confirmé", StringComparison.Ordinal));
        Assert.Equal("Bloqué", view.Sections[1].Badge);
        Assert.Equal(new IconPresentation("!", "rouge"), view.Icon);
        blocked = blocked with { Windows = [blocked.Windows[0] with { UsedPercent = 100 }] };
        Assert.Contains(Format(TestData.State with { Codex = blocked }).Sections[1].Messages,
            text => text.StartsWith("Bloqué depuis", StringComparison.Ordinal));
    }

    [Fact]
    public void AgesAdvanceWithoutACollection() =>
        Assert.Equal("mesure il y a 7 min", Format(TestData.State, now: TestData.Now.AddMinutes(5)).Header.AgeText);

    [Fact]
    public void OldestDisplayedWindowWinsEvenWhenNewestProviderWasJustCollected()
    {
        var old = TestData.State.Claude.Windows[0] with { MeasuredAt = TestData.Now.AddMinutes(-91) };
        var state = TestData.State with { Claude = TestData.Provider("claude", old, TestData.State.Claude.Windows[1]) };
        var view = Format(state);
        Assert.Equal("mesure il y a 91 min", view.Header.AgeText);
        Assert.Equal("mesure il y a 91 min", view.Sections[0].AgeText);
        Assert.Equal("mesure il y a 1 min", view.Sections[1].AgeText);
        Assert.True(view.Sections[0].Windows[0].IsStale);
        Assert.False(view.Sections[0].Windows[1].IsStale);
    }

    [Theory]
    [InlineData(1800, false)]
    [InlineData(1801, true)]
    public void StaleMarkerStartsStrictlyAfterThirtyMinutes(int seconds, bool expected)
    {
        var row = TestData.State.Claude.Windows[0] with { MeasuredAt = TestData.Now.AddSeconds(-seconds) };
        var view = Format(TestData.State with { Claude = TestData.Provider("claude", row) });
        Assert.Equal(expected, view.Sections[0].Windows[0].IsStale);
        Assert.Equal(seconds, view.Sections[0].Windows[0].AgeSeconds);
    }

    [Theory]
    [InlineData(30, "OK", "vert")]
    [InlineData(60, "À surveiller", "orange")]
    [InlineData(85, "Critique", "rouge")]
    public void BadgeUsesWorstWindowColor(double used, string badge, string color)
    {
        var window = TestData.State.Codex.Windows[0] with { UsedPercent = used, ResetsAt = null };
        var view = Format(TestData.State with { Codex = TestData.Provider("codex", window) });
        Assert.Equal(badge, view.Sections[1].Badge);
        Assert.Equal(color, view.Sections[1].Color);
    }

    [Fact]
    public void BadgeDoesNotHideOneCriticalCounterBehindHealthyWindows()
    {
        var healthy = TestData.State.Codex.Windows[0];
        var critical = healthy with { Counter = "codex_spark", UsedPercent = 86, ResetsAt = null };
        var view = Format(TestData.State with { Codex = TestData.Provider("codex", healthy, critical) });
        Assert.Equal("Critique", view.Sections[1].Badge);
        Assert.Equal("rouge", view.Sections[1].Color);
        Assert.Contains(view.Sections[1].Windows, window => window.Color == "vert");
        Assert.Contains(view.Sections[1].Windows, window => window.Color == "rouge");
    }

    [Fact]
    public void AboveOneHundredIsKeptAsTextAndData()
    {
        var window = TestData.State.Codex.Windows[0] with { UsedPercent = 140 };
        var row = Assert.Single(Format(TestData.State with { Codex = TestData.Provider("codex", window) }).Sections[1].Windows);
        Assert.Equal(140, row.Percent);
        Assert.Equal("140 %", row.PercentText);
        Assert.Equal("rouge", row.Color);
    }

    [Fact]
    public void EveryCounterAndWindowRemainsDistinctAndNamed()
    {
        var first = TestData.State.Codex.Windows[0] with { Counter = "gpt-5.3-codex", Type = "primary" };
        var second = first with { Counter = "codex_spark", UsedPercent = 12 };
        var third = first with { Type = "secondary", DurationMinutes = 300 };
        var view = Format(TestData.State with { Codex = TestData.Provider("codex", first, second, third) });
        Assert.Equal(3, view.Sections[1].Windows.Length);
        Assert.Equal(2, view.Sections[1].Windows.Count(w => w.Counter == "gpt-5.3-codex"));
        Assert.Contains(view.Sections[1].Windows, w => w.Label == "Semaine · codex spark" && w.Percent == 12);
        var single = Assert.Single(Format(TestData.State with { Codex = TestData.Provider("codex", first) }).Sections[1].Windows);
        Assert.Equal("Semaine · gpt-5.3-codex", single.Label);
        Assert.Equal("Codex 7 j · gpt-5.3-codex", single.ShortLabel);
    }

    [Fact]
    public void UnknownPercentageStaysUnknownAndOtherAccountsStayOutsideGraphs()
    {
        var window = TestData.State.Codex.Windows[0] with { UsedPercent = null, DurationMinutes = null, ResetsAt = null };
        var view = Format(TestData.State with { Codex = TestData.Provider("codex", window),
            OtherAccounts = [TestData.State.Codex with { Account = "private-account" }] });
        var row = Assert.Single(view.Sections[1].Windows);
        Assert.Null(row.Percent);
        Assert.Equal("? %", row.PercentText);
        Assert.Null(row.ElapsedFraction);
        Assert.Null(row.PaceDifference);
        Assert.Equal("Aucune mesure", view.Sections[1].Badge);
        Assert.Contains("Autres comptes", view.Sections[1].HeaderToolTip, StringComparison.Ordinal);
        Assert.DoesNotContain("private-account", view.AccessibleText, StringComparison.Ordinal);
    }

    [Fact]
    public void HighestPercentageUsesCoreColorBeforeRounding()
    {
        var window = TestData.State.Codex.Windows[0] with { UsedPercent = 84.6, ResetsAt = null };
        var view = Format(TestData.State with { Codex = TestData.Provider("codex", window) });
        Assert.Equal(new IconPresentation("85", "orange"), view.Icon);
        Assert.Equal("orange", view.Sections[1].Windows[0].Color);
        Assert.Equal(new IconPresentation("?", null), Format(StateBuilder.Build([])).Icon);
    }

    [Theory]
    [InlineData(127)]
    [InlineData(128)]
    [InlineData(500)]
    public void ToolTipFitsNet10(int length)
    {
        var text = new string('a', length);
        var result = CardFormatter.TruncateToolTip(text);
        Assert.True(result.Length <= 127);
        Assert.Equal(length <= 127 ? text : new string('a', 126) + "…", result);
    }

    [Fact]
    public void ToolTipDoesNotSplitUnicodeTextElements()
    {
        Assert.Equal(new string('a', 125) + "…", CardFormatter.TruncateToolTip(new string('a', 125) + "😀more"));
        Assert.Equal(new string('a', 125) + "…", CardFormatter.TruncateToolTip(new string('a', 125) + "e\u0301more"));
    }
}
