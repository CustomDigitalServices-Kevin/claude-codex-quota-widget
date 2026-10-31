namespace QuotaWidget.Core;

public static class CodexExecutable
{
    public static string NativePath(string applicationData) => Path.Combine(applicationData, "npm", "node_modules",
        "@openai", "codex", "node_modules", "@openai", "codex-win32-x64", "vendor",
        "x86_64-pc-windows-msvc", "bin", "codex.exe");

    public static string? Resolve(string? explicitPath = null, string? applicationData = null, string? searchPath = null)
    {
        // An explicit invalid path must not accidentally launch a different installation.
        if (explicitPath is not null) { return Executable(explicitPath); }
        var native = Executable(NativePath(applicationData ?? Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData)));
        if (native is not null) { return native; }
        foreach (var directory in (searchPath ?? Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            if (string.IsNullOrWhiteSpace(directory)) { continue; }
            var candidate = Executable(Path.Combine(directory.Trim('"'), "codex.exe"));
            if (candidate is not null) { return candidate; }
        }
        return null;
    }

    private static string? Executable(string path)
    {
        try
        {
            return Path.GetExtension(path).Equals(".exe", StringComparison.OrdinalIgnoreCase) && File.Exists(path)
                ? Path.GetFullPath(path) : null;
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or IOException)
        { return null; }
    }
}
