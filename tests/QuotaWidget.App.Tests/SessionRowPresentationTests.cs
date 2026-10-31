using System.Globalization;

namespace QuotaWidget.App.Tests;

public sealed class SessionRowPresentationTests
{
    private static readonly CultureInfo French = CultureInfo.GetCultureInfo("fr-FR");

    [Fact]
    public void AvailableMetadataBecomesHumanReadableTitleAgentModelEffortAndCreationTime()
    {
        var now = DateTimeOffset.Now;
        var created = now.AddHours(-1);
        var detail = new SessionDetails("claude", Guid.NewGuid().ToString("D"), now,
            "Analyse quota", "claude-opus-4-1", "xhigh", AgentRole: "explorer",
            AgentNickname: "Atlas", CreatedAt: created);

        var row = SessionRowPresentation.From(detail, now);

        Assert.Equal("Analyse quota", row.Title);
        Assert.Equal("Atlas · Exploration", row.Agent);
        Assert.Equal("Claude Opus 4.1", row.Model);
        Assert.Equal("Effort très élevé", row.Effort);
        Assert.Equal("Créée à " + created.ToLocalTime().ToString("HH:mm", French), row.Time);
    }

    [Fact]
    public void MissingCreationUsesObservationAndUnknownMetadataStaysAbsent()
    {
        var now = DateTimeOffset.Now;
        var observed = now.AddMinutes(-5);
        var detail = new SessionDetails("codex", "root-0001", observed,
            Model: "gpt-5-2-codex", Effort: "unexpected", AgentRole: "default");

        var row = SessionRowPresentation.From(detail, now);

        Assert.Equal("Session Codex", row.Title);
        Assert.Null(row.Agent);
        Assert.Equal("GPT 5 2 Codex", row.Model);
        Assert.Null(row.Effort);
        Assert.Equal("Observée à " + observed.ToLocalTime().ToString("HH:mm", French), row.Time);
    }

    [Fact]
    public void IdentifiersAndUuidNamesFallBackToProviderTitle()
    {
        var now = DateTimeOffset.Now;
        var uuid = Guid.NewGuid().ToString("D");
        var claude = SessionRowPresentation.From(new("claude", uuid, now,
            Name: uuid, Model: uuid, AgentNickname: uuid), now);
        var codex = SessionRowPresentation.From(new("codex", "abcdef0123456789", now,
            Name: "abcdef0123456789", AgentNickname: "abcdef0123456789"), now);

        Assert.Equal("Session Claude", claude.Title);
        Assert.Null(claude.Agent);
        Assert.Null(claude.Model);
        Assert.False(claude.HasDetails);
        Assert.Equal("Session Codex", codex.Title);
        Assert.Null(codex.Agent);
        Assert.False(codex.HasDetails);
    }

    [Fact]
    public void EmbeddedIdentifiersAreRemovedFromHumanFieldsBeforeDisplay()
    {
        const string id = "root-0001";
        var now = DateTimeOffset.Now;
        var uuid = Guid.NewGuid().ToString("D");
        var row = SessionRowPresentation.From(new("codex", id, now,
            Name: "Diagnostic " + id, Model: id, AgentNickname: "Atlas " + uuid), now);
        Assert.Equal("Diagnostic", row.Title);
        Assert.Equal("Atlas", row.Agent);
        Assert.Null(row.Model);
    }
}
