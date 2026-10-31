using System.Globalization;
using System.IO;
using System.Text.Json;

namespace QuotaWidget.App.Tests;

public sealed partial class SessionDetailsTests
{
    [Fact]
    public void ClaudeFindsUniqueRuntimeBeyondFirstTwoHundredFiftySixProjectDirectories()
    {
        using var fixture = new SessionFixture();
        var state = fixture.Directory("state");
        var projects = fixture.Directory("projects");
        var directories = Enumerable.Range(0, 320)
            .Select(index => fixture.Directory("projects", $"project-{index:0000}"))
            .ToArray();
        var discoveryOrder = Directory.EnumerateDirectories(projects, "*", SearchOption.TopDirectoryOnly)
            .ToArray();
        Assert.Equal(directories.Length, discoveryOrder.Length);
        var runtimeDirectory = discoveryOrder[300];
        var id = Guid.NewGuid().ToString("D", CultureInfo.InvariantCulture);
        var observed = DateTimeOffset.UtcNow.AddMinutes(-1);
        var first = observed.AddMinutes(-4);
        var latest = observed.AddMinutes(-2);
        var statePath = fixture.WriteRaw(Path.Combine(state, id + ".json"), "{}");
        fixture.Write(Path.Combine(runtimeDirectory, id + ".jsonl"),
            JsonSerializer.Serialize(new
            {
                type = "assistant",
                timestamp = first.ToString("O", CultureInfo.InvariantCulture),
                effort = "low",
                cwd = "C:/old",
                message = new { model = "claude-old" },
            }),
            JsonSerializer.Serialize(new
            {
                type = "assistant",
                timestamp = latest.ToString("O", CultureInfo.InvariantCulture),
                effort = "high",
                cwd = "C:/current",
                message = new { model = "claude-current" },
            }),
            JsonSerializer.Serialize(new { type = "ai-title", sessionId = id, aiTitle = "Runtime title" }));

        var detail = Assert.Single(new SessionDetailsSource([], state, projects)
            .Read([new("claude", id, observed, statePath)]));

        Assert.Equal("Runtime title", detail.Name);
        Assert.Equal("claude-current", detail.Model);
        Assert.Equal("high", detail.Effort);
        Assert.Equal("C:/current", detail.ProjectPath);
        Assert.Equal(latest, detail.ContextAt);
        Assert.Equal(ClaudeSessionDetails.LocalNotice, detail.Notice);
    }

    [Fact]
    public void ClaudeFailsClosedWhenProjectDirectoryLimitIsExceeded()
    {
        using var fixture = new SessionFixture();
        var state = fixture.Directory("state");
        var projects = fixture.Directory("projects");
        var id = Guid.NewGuid().ToString("D", CultureInfo.InvariantCulture);
        var observed = DateTimeOffset.UtcNow.AddMinutes(-1);
        var statePath = fixture.WriteRaw(Path.Combine(state, id + ".json"), "{}");
        for (var index = 0; index < 4097; index++)
        {
            fixture.Directory("projects", $"project-{index:0000}");
        }

        var detail = Assert.Single(new SessionDetailsSource([], state, projects)
            .Read([new("claude", id, observed, statePath)]));

        Assert.Equal("Détails Claude indisponibles.", detail.Notice);
        Assert.Null(detail.Name);
        Assert.Null(detail.Model);
        Assert.Null(detail.Effort);
        Assert.Null(detail.ProjectPath);
    }

    [Fact]
    public void ClaudeFailsClosedWhenExactRuntimeExistsInMultipleProjectDirectories()
    {
        using var fixture = new SessionFixture();
        var state = fixture.Directory("state");
        var projects = fixture.Directory("projects");
        var firstProject = fixture.Directory("projects", "first");
        var secondProject = fixture.Directory("projects", "second");
        var id = Guid.NewGuid().ToString("D", CultureInfo.InvariantCulture);
        var observed = DateTimeOffset.UtcNow.AddMinutes(-1);
        var statePath = fixture.WriteRaw(Path.Combine(state, id + ".json"), "{}");
        var runtime = JsonSerializer.Serialize(new
        {
            type = "assistant",
            timestamp = observed.ToString("O", CultureInfo.InvariantCulture),
            effort = "high",
            cwd = "C:/project",
            message = new { model = "claude-model" },
        });
        fixture.Write(Path.Combine(firstProject, id + ".jsonl"), runtime);
        fixture.Write(Path.Combine(secondProject, id + ".jsonl"), runtime);

        var detail = Assert.Single(new SessionDetailsSource([], state, projects)
            .Read([new("claude", id, observed, statePath)]));

        Assert.Equal("Détails Claude indisponibles.", detail.Notice);
        Assert.Null(detail.Name);
        Assert.Null(detail.Model);
        Assert.Null(detail.Effort);
        Assert.Null(detail.ProjectPath);
    }
}
