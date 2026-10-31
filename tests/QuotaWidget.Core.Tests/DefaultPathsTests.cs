using QuotaWidget.Core;

namespace QuotaWidget.Core.Tests;

public sealed class DefaultPathsTests
{
    [Fact]
    public void CodexProfilesIncludeApplicationCliAndDedicatedProfile()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var expected = new[]
        {
            Path.Combine(home, ".codex", "sessions"),
            Path.Combine(home, ".codex-cli", "sessions"),
            Path.Combine(DefaultPaths.DataDirectory, "codex-home", "sessions"),
        };

        Assert.Equal(expected, DefaultPaths.CodexProfiles);
    }

    [Fact]
    public void DataDirectoryIsUnderLocalApplicationData()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        Assert.Equal(Path.Combine(local, "QuotaWidget"), DefaultPaths.DataDirectory);
    }
}
