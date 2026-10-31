using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using QuotaWidget.Core;

namespace QuotaWidget.App;

// Explicit synthetic diagnostic mode. Never starts account discovery or normal collection.
internal static class PackagingSmoke
{
    public static bool TryRun(Application application, string[] args)
    {
        if (args.Length == 0 || args[0] != "--packaging-smoke") { return false; }
        try
        {
            if (args.Length != 2) { throw new ArgumentException("Expected an empty output directory."); }
            var root = Path.GetFullPath(args[1]);
            if (Directory.Exists(root) && Directory.EnumerateFileSystemEntries(root).Any())
            { throw new IOException("Diagnostic output must be empty."); }
            AssertDirectoryChain(root);
            Directory.CreateDirectory(root);
            Start(application, root);
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or ArgumentException or UnauthorizedAccessException)
        { application.Shutdown(1); }
        return true;
    }

    private static void Start(Application application, string root)
    {
        var now = DateTimeOffset.UtcNow;
        var state = Path.Combine(root, "synthetic-state");
        var producer = new ClaudeStatusLineProducer(state);
        producer.Process(Payload(now, 1));
        producer.Process(Payload(now, 2));
        var claude = new ClaudeSource(TimeProvider.System).Read(state);
        var refs = RecentActivitySource.ClaudeReferences(claude.Observations, now, state);
        var source = new SessionDetailsSource([], state, Path.Combine(root, "absent-projects"));
        var details = source.Read(refs);
        if (claude.Observations.Length != 1 || refs.Length != 1 || details.Length != 1
            || details[0].Name != "Analyse d’un tableau de bord" || details[0].AgentNickname != "Analyste"
            || claude.Observations[0].Windows.Any(window => window.MeasuredAt is null))
        { throw new InvalidDataException("Synthetic integration contract failed."); }
        var window = new MainWindow { AllowClose = true, ShowActivated = false, Opacity = 0 };
        window.ApplySettings(new(Theme: ThemeChoice.Dark));
        window.ApplyTheme(ThemeChoice.Dark, new(Path.Combine(root, "logs"), TimeProvider.System));
        window.ReadSessionDetails = (items, token) => source.Read(items, token);
        window.Render(CardFormatter.Format(StateBuilder.Build(claude.Observations), [], now, now));
        ((TextBlock)window.FindName("CardTitle")).Text = "Quotas IA · Démo";
        window.RenderTelemetry(new(now, 18, 12UL << 30, 32UL << 30,
            [new("demo", "GPU de démonstration", 24, 4UL << 30, 16UL << 30, 1UL << 30)]),
            new(now, 0, false, "Synthetic diagnostic data.", 1, refs));
        window.Loaded += async (_, _) =>
        {
            try
            {
                await window.TogglePackagingDetailsAsync();
                await window.Dispatcher.InvokeAsync(() => Save(window, root), DispatcherPriority.ApplicationIdle);
                WriteResult(root, details.Length);
                window.Close();
                application.Shutdown(0);
            }
            catch (Exception) { window.Close(); application.Shutdown(1); }
        };
        application.MainWindow = window;
        window.Show();
    }

    private static string Payload(DateTimeOffset now, int duration) => JsonSerializer.Serialize(new
    {
        session_id = "00000000-0000-4000-8000-000000000021",
        session_name = "Analyse d’un tableau de bord",
        model = new { id = "claude-opus-5-5" }, effort = new { level = "high" }, agent = new { name = "Analyste" },
        cost = new { total_api_duration_ms = duration },
        rate_limits = new
        {
            five_hour = new { used_percentage = 18, resets_at = now.AddHours(3).ToUnixTimeSeconds() },
            seven_day = new { used_percentage = 36, resets_at = now.AddDays(4).ToUnixTimeSeconds() },
        },
    });

    private static void Save(MainWindow window, string root)
    {
        window.UpdateLayout();
        var content = (FrameworkElement)window.Content;
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(content.ActualWidth),
            (int)Math.Ceiling(content.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(content);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(Path.Combine(root, "widget.png"));
        encoder.Save(file);
    }

    private static void WriteResult(string root, int details)
    {
        using var process = Process.GetCurrentProcess();
        var runtime = process.Modules.Cast<ProcessModule>().Where(module =>
            module.ModuleName is "coreclr.dll" or "hostfxr.dll" or "hostpolicy.dll")
            .Select(module => module.FileName).ToArray();
        File.WriteAllText(Path.Combine(root, "smoke.json"), JsonSerializer.Serialize(new
        { schema = 1, synthetic = true, executablePath = Environment.ProcessPath,
            coreClrPath = runtime.Single(path => Path.GetFileName(path) == "coreclr.dll"),
            metadataDetails = details, screenshot = true, runtimeModules = runtime }));
    }

    private static void AssertDirectoryChain(string path)
    {
        for (var current = new DirectoryInfo(path); current is not null; current = current.Parent)
        {
            if (current.Exists && (current.Attributes & FileAttributes.ReparsePoint) != 0)
            { throw new IOException("Reparse paths are not allowed."); }
        }
    }
}
