using System.IO;
using QuotaWidget.Core;

namespace QuotaWidget.App.Tests;

public sealed class ActivePresentationTests
{
    private static readonly bool[] ExpectedRequests = [false, true];
    [Fact]
    public void CardShowsDirectCreditsValidityAgeAndAuthenticationSuspension()
    {
        var now = TestData.Now;
        var codex = TestData.State.Codex with
        {
            Credits = new(12.5, false, true), ActiveMeasuredAt = now.AddMinutes(-7),
            ResetCredits = new(2, [new("private-credit-id", null, "available", now, now.AddDays(1), null, null)]),
        };
        var presentation = CardFormatter.Format(TestData.State with { Codex = codex },
            [new("authentification_refusee", "app-server")], now);
        var text = presentation.Sections[1].HeaderToolTip;
        Assert.Contains("Crédits : 12,5; remises gratuites : 2; validité :", text, StringComparison.Ordinal);
        Assert.Contains("lecture directe il y a 7 min", text, StringComparison.Ordinal);
        Assert.Contains(presentation.Sections[1].Messages, message => message.Contains("suspendue : authentification refusée", StringComparison.Ordinal));
        Assert.DoesNotContain("private-credit-id", text, StringComparison.Ordinal);
        Assert.DoesNotContain(presentation.Sections[1].Messages, message => message.Contains("Crédits", StringComparison.Ordinal));
        Assert.Equal("Lecture directe refusée — authentification", presentation.Sections[1].StatusText);
    }

    [Theory]
    [InlineData("profil_dedie_non_connecte", "Profil Codex non connecté")]
    [InlineData("profil_partage_refuse", "Lecture directe refusée : profil partagé")]
    [InlineData("erreur_lecture_directe", "Échec de lecture directe")]
    public void AbnormalDirectReadStateIsShortAndBelongsOnlyToCodex(string code, string expected)
    {
        var presentation = CardFormatter.Format(TestData.State, [new(code, "app-server")], TestData.Now);
        Assert.Empty(presentation.Sections[0].Messages);
        Assert.Empty(presentation.Sections[0].StatusText);
        Assert.Equal(expected, presentation.Sections[1].StatusText);
        Assert.Single(presentation.Sections[1].Messages);
    }

    [Fact]
    public void NormalReadIntervalDoesNotAddAnInformationalMessage()
    {
        var presentation = CardFormatter.Format(TestData.State,
            [new("lecture_directe_trop_recente", "app-server", NextAttemptAt: TestData.Now.AddMinutes(5))], TestData.Now);
        Assert.All(presentation.Sections, section => { Assert.Empty(section.Messages); Assert.Empty(section.StatusText); });
    }

    [Fact]
    public void AlertServicePersistsBeforePublishingAndLogsNoAccountOrPayload()
    {
        using var directory = new TestDirectory();
        var clock = new TestClock(TestData.Now);
        var log = new WidgetLog(directory.Root, clock);
        var window = TestData.State.Claude.Windows[0] with { UsedPercent = 95, MeasuredAt = TestData.Now };
        var snapshot = TestData.Snapshot with { Observations = [new("claude", "private-source", TestData.Now, TestData.Now, [window])] };
        var first = new AlertService(directory.Root, log, clock).Process(snapshot);
        Assert.Single(first);
        Assert.True(File.Exists(Path.Combine(directory.Root, "alerts.json")));
        Assert.Empty(new AlertService(directory.Root, log, clock).Process(snapshot));
        var text = File.ReadAllText(log.FilePath);
        Assert.Contains("QuotaAlert 95", text, StringComparison.Ordinal);
        Assert.DoesNotContain("private-source", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DirectMenuRequestDuringAutomaticCollectionIsSerializedOnWorker()
    {
        using var directory = new TestDirectory();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var concurrent = 0;
        var maximum = 0;
        List<bool> requests = [];
        var clock = new TestClock(TestData.Now);
        await using var service = new CollectionService(_ => throw new InvalidOperationException("Async source required"),
            _ => { }, () => { }, new(directory.Root, clock), clock, collectAsync: async (_, direct, token) =>
            {
                concurrent++;
                maximum = Math.Max(maximum, concurrent);
                requests.Add(direct);
                if (requests.Count == 1)
                {
                    entered.SetResult();
                    await release.Task.WaitAsync(token);
                }
                concurrent--;
                return TestData.Snapshot;
            });
        var first = service.RefreshAsync();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var manual = service.RefreshDirectAsync();
        Assert.Same(first, manual);
        release.SetResult();
        await manual;
        Assert.Equal(1, maximum);
        Assert.Equal(ExpectedRequests, requests);
    }
}
