using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Threading;

namespace QuotaWidget.App.Tests;

[Collection("WPF rendering")]
public sealed partial class SessionAccordionTests
{
    private static readonly CardPresentation Presentation = CardFormatter.Format(
        TestData.State, [], TestData.Now, TestData.Now);

    [Fact]
    public void CollapsedAccordionDoesNotReadAndExposesAccessiblePlusButton() => RunStaAsync(async () =>
    {
        var reference = Reference("root-0001");
        var calls = 0;
        var window = Window([reference], (_, _) => { calls++; return []; });
        try
        {
            await window.ReloadSessionDetailsAsync("codex");
            var button = Find<Button>(window, "codex-session-details");
            Assert.Equal("+", button.Content);
            Assert.Equal("Déplier les sessions Codex", AutomationProperties.GetName(button));
            Assert.Equal("Replié", AutomationProperties.GetItemStatus(button));
            Assert.Null(FindOrDefault<Border>(window, "codex-sessions-expanded"));
            Assert.Equal(0, calls);
        }
        finally { window.Close(); }
    });

    [Fact]
    public void ExpandRendersSafeFieldsThenCollapseShrinksAndRestoresAccessiblePlus() => RunStaAsync(async () =>
    {
        var reference = Reference("root-0001");
        var now = DateTimeOffset.Now;
        var details = new[]
        {
            new SessionDetails("codex", reference.Id, reference.ObservedAt, "Safe title",
                "gpt-5-2-codex", "high", "SECRET_PROJECT", "SECRET_ORIGIN", "explorer",
                "Atlas", Notice: "SECRET_NOTICE", CreatedAt: now.AddHours(-1)),
            new SessionDetails("codex", "root-0002", reference.ObservedAt, "Second safe title"),
            new SessionDetails("codex", "root-0003", reference.ObservedAt, "Third safe title"),
        };
        var window = Window([reference, Reference("root-0002"), Reference("root-0003")], (_, _) => details);
        window.Opacity = 0;
        window.Show();
        try
        {
            await Dispatcher.Yield(DispatcherPriority.Background);
            window.UpdateLayout();
            var collapsedHeight = window.ActualHeight;

            await window.ToggleSessionDetailsAsync("codex");
            window.UpdateLayout();
            var expandedHeight = window.ActualHeight;
            var button = Find<Button>(window, "codex-session-details");
            Assert.Equal("−", button.Content);
            Assert.Equal("Replier les sessions Codex", AutomationProperties.GetName(button));
            Assert.Equal("Déplié", AutomationProperties.GetItemStatus(button));
            Assert.NotNull(FindOrDefault<Border>(window, "codex-sessions-expanded"));
            var strings = UiStrings(window);
            Assert.Contains("Safe title", strings);
            Assert.Contains("Agent : Atlas · Exploration", strings);
            Assert.Contains("GPT 5 2 Codex", strings);
            Assert.Contains("Effort élevé", strings);
            Assert.Contains(strings, value => value.StartsWith("Créée", StringComparison.Ordinal));
            Assert.DoesNotContain(strings, value => value.Contains("root-0001", StringComparison.Ordinal)
                || value.Contains("SECRET", StringComparison.Ordinal));
            Assert.True(expandedHeight > collapsedHeight);

            await window.ToggleSessionDetailsAsync("codex");
            window.UpdateLayout();
            Assert.Equal("+", Find<Button>(window, "codex-session-details").Content);
            Assert.Null(FindOrDefault<Border>(window, "codex-sessions-expanded"));
            Assert.True(window.ActualHeight < expandedHeight);
        }
        finally { window.Close(); }
    });

    [Fact]
    public void CollapseCancelsPendingReadAndDiscardsLateResults() => RunStaAsync(async () =>
    {
        using var entered = new ManualResetEventSlim();
        using var finished = new ManualResetEventSlim();
        var window = Window([Reference("root-0001")], BlockingRead(entered, finished));
        try
        {
            var expansion = window.ToggleSessionDetailsAsync("codex");
            Assert.True(await Task.Run(() => entered.Wait(TimeSpan.FromSeconds(5))));
            await window.ToggleSessionDetailsAsync("codex");
            Assert.True(await Task.Run(() => finished.Wait(TimeSpan.FromSeconds(5))));
            await expansion;
            Assert.Null(FindOrDefault<Border>(window, "codex-sessions-expanded"));
            Assert.DoesNotContain("Late result", UiStrings(window));
        }
        finally { window.Close(); }
    });

    [Fact]
    public void ReopenedAccordionRejectsOldResultEvenWhenReaderIgnoresCancellation() => RunStaAsync(async () =>
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var reference = Reference("root-0001");
        var calls = 0;
        var window = Window([reference], (_, _) =>
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                entered.Set();
                release.Wait(TimeSpan.FromSeconds(5), CancellationToken.None);
                return [new("codex", reference.Id, reference.ObservedAt, "Obsolete result")];
            }
            return [new("codex", reference.Id, reference.ObservedAt, "Fresh result")];
        });
        try
        {
            var old = window.ToggleSessionDetailsAsync("codex");
            Assert.True(await Task.Run(() => entered.Wait(TimeSpan.FromSeconds(5))));
            await window.ToggleSessionDetailsAsync("codex");
            await window.ToggleSessionDetailsAsync("codex");
            release.Set();
            await old;
            Assert.Contains("Fresh result", UiStrings(window));
            Assert.DoesNotContain("Obsolete result", UiStrings(window));
        }
        finally { release.Set(); window.Close(); }
    });

    [Fact]
    public void SessionsWithoutHumanMetadataHaveOnlyOneShortAvailabilityMessage() => RunStaAsync(async () =>
    {
        var reference = Reference("root-0001");
        var window = Window([reference], (_, _) =>
            [new("codex", reference.Id, reference.ObservedAt, Notice: "SECRET_NOTICE")]);
        try
        {
            await window.ToggleSessionDetailsAsync("codex");
            var strings = UiStrings(window);
            Assert.Contains("1 session sans détails disponibles.", strings);
            Assert.DoesNotContain("Session Codex", strings);
            Assert.DoesNotContain(strings, value => value.Contains("root-0001", StringComparison.Ordinal)
                || value.Contains("SECRET", StringComparison.Ordinal));
        }
        finally { window.Close(); }
    });

    [Fact]
    public void SameCountNewReferencesReloadAndExpansionSurvivesSettingsThemeModeAndRenderCards() => RunStaAsync(async () =>
    {
        var first = Reference("root-0001");
        var second = Reference("root-0002");
        var calls = 0;
        var window = Window([first], (items, _) =>
        {
            Interlocked.Increment(ref calls);
            var item = Assert.Single(items);
            return [new("codex", item.Id, item.ObservedAt, item.Id == first.Id ? "Premier sujet" : "Nouveau sujet")];
        });
        try
        {
            await window.ToggleSessionDetailsAsync("codex");
            Assert.Contains("Premier sujet", UiStrings(window));

            window.RenderTelemetry(null, Activity([second]));
            await WaitUntilAsync(() => UiStrings(window).Contains("Nouveau sujet", StringComparer.Ordinal));
            Assert.Equal(2, calls);
            Assert.Equal(new[] { second }, window.GetRecentSessions("codex"));

            window.ApplySettings(new WidgetSettings(Mode: DisplayMode.Gauges, Cards:
                [new("codex", true, "Compact", "Black"), new("claude", false), new("pc", false)]));
            window.Render(Presentation);
            Assert.NotNull(FindOrDefault<Border>(window, "codex-sessions-expanded"));
            Assert.Equal("−", Find<Button>(window, "codex-session-details").Content);
            Assert.Contains("Nouveau sujet", UiStrings(window));
        }
        finally { window.Close(); }
    });

    [Fact]
    public void HidingCardAndClosingWindowCancelReadsAndClosedWindowNeverReadsAgain() => RunStaAsync(async () =>
    {
        using var hiddenEntered = new ManualResetEventSlim();
        using var hiddenFinished = new ManualResetEventSlim();
        var hidden = Window([Reference("root-0001")], BlockingRead(hiddenEntered, hiddenFinished));
        try
        {
            var expansion = hidden.ToggleSessionDetailsAsync("codex");
            Assert.True(await Task.Run(() => hiddenEntered.Wait(TimeSpan.FromSeconds(5))));
            hidden.ApplySettings(new WidgetSettings(Cards:
                [new("codex", false), new("claude", false), new("pc", false)]));
            hidden.Render(Presentation);
            Assert.True(await Task.Run(() => hiddenFinished.Wait(TimeSpan.FromSeconds(5))));
            await expansion;
            Assert.DoesNotContain("Late result", UiStrings(hidden));
        }
        finally { hidden.Close(); }

        using var closedEntered = new ManualResetEventSlim();
        using var closedFinished = new ManualResetEventSlim();
        var calls = 0;
        var closed = Window([Reference("root-0002")], (items, token) =>
        {
            Interlocked.Increment(ref calls);
            return BlockingRead(closedEntered, closedFinished)(items, token);
        });
        closed.Opacity = 0;
        closed.Show();
        var pending = closed.ToggleSessionDetailsAsync("codex");
        Assert.True(await Task.Run(() => closedEntered.Wait(TimeSpan.FromSeconds(5))));
        closed.Close();
        Assert.True(await Task.Run(() => closedFinished.Wait(TimeSpan.FromSeconds(5))));
        await pending;
        await closed.ToggleSessionDetailsAsync("codex");
        await closed.ReloadSessionDetailsAsync("codex");
        Assert.Equal(1, calls);
    });

}
