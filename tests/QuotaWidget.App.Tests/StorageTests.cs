using System.IO;
using QuotaWidget.Core;

namespace QuotaWidget.App.Tests;

public sealed class StorageTests
{
    private static readonly string[] ExpectedLogFiles = ["widget.1.log", "widget.2.log", "widget.log"];

    [Fact]
    public void MissingAndCorruptSettingsUseLoggedDefaults()
    {
        using var directory = new TestDirectory();
        var log = new WidgetLog(directory.PathFor("logs"), new TestClock(TestData.Now));
        var path = directory.PathFor("settings.json");
        var store = new SettingsStore(path, log);
        Assert.Equal(new WidgetSettings(), store.Load());
        File.WriteAllText(path, "{truncated");
        Assert.Equal(new WidgetSettings(), store.Load());
        Assert.Contains("SettingsMissing", File.ReadAllText(log.FilePath), StringComparison.Ordinal);
        Assert.Contains("SettingsInvalid", File.ReadAllText(log.FilePath), StringComparison.Ordinal);
    }

    [Fact]
    public void SettingsRoundTripAndRejectPartialPosition()
    {
        using var directory = new TestDirectory();
        var path = directory.PathFor("settings.json");
        var store = new SettingsStore(path, new(directory.PathFor("logs"), new TestClock(TestData.Now)));
        var expected = new WidgetSettings(-1300.5, 42, false, false);
        store.Save(expected);
        Assert.Equal(expected, store.Load());
        AtomicJsonFile.Write(path, expected with { Y = null });
        Assert.Equal(new WidgetSettings(), store.Load());
        Assert.Empty(Directory.GetFiles(directory.Root, "*.tmp"));
    }

    [Fact]
    public void LogRotatesAtOneMiBAndKeepsAtMostThreeFiles()
    {
        using var directory = new TestDirectory();
        var log = new WidgetLog(directory.Root, new TestClock(TestData.Now));
        for (var index = 0; index < 4; index++)
        {
            // Construct a full log to exercise the real 1 MiB boundary without 50,000 writes.
            File.WriteAllBytes(log.FilePath, new byte[WidgetLog.MaximumBytes]);
            log.Record(WidgetEvent.CollectionCompleted, 12.3);
        }
        Assert.Equal(ExpectedLogFiles, Directory.GetFiles(directory.Root).Select(Path.GetFileName).Order(StringComparer.Ordinal));
        Assert.All(Directory.GetFiles(directory.Root), file => Assert.InRange(new FileInfo(file).Length, 1, WidgetLog.MaximumBytes));
    }

    [Fact]
    public void LogNeverWritesAccountOrExceptionMessageInClearText()
    {
        using var directory = new TestDirectory();
        var log = new WidgetLog(directory.Root, new TestClock(TestData.Now));
        log.RecordAccount("private-account@example.test");
        log.Record(WidgetEvent.CollectionFailed, 12.5, new IOException("secret token private-account@example.test"));
        var text = File.ReadAllText(log.FilePath);
        Assert.DoesNotContain("private-account", text, StringComparison.Ordinal);
        Assert.DoesNotContain("secret token", text, StringComparison.Ordinal);
        var fingerprint = JsonSupport.Fingerprint("private-account@example.test");
        Assert.NotNull(fingerprint);
        Assert.Contains(fingerprint, text, StringComparison.Ordinal);
        Assert.Contains("2026-10-30T09:31:00", text, StringComparison.Ordinal);
        Assert.Contains("ERROR CollectionFailed 12.5 IOException", text, StringComparison.Ordinal);
        Assert.False(File.ReadAllBytes(log.FilePath).AsSpan().StartsWith(new byte[] { 0xef, 0xbb, 0xbf }));
    }

    [Fact]
    public void StartupValueQuotesWholeExecutablePath() =>
        Assert.Equal("\"C:\\Program Files\\QuotaWidget\\QuotaWidget.exe\"", StartupRegistration.Command(@"C:\Program Files\QuotaWidget\QuotaWidget.exe"));
}
