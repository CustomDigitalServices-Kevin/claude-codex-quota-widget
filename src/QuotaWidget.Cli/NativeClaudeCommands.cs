using System.Text;
using System.Text.Json;
using QuotaWidget.Core;

namespace QuotaWidget.Cli;

public static class NativeClaudeCommands
{
    private const int MaximumInput = 1024 * 1024;

    public static int? Run(string[] args, TextReader input, TextWriter output, TextWriter error,
        string? executablePath = null)
    {
        ArgumentNullException.ThrowIfNull(args);
        if (args.Length == 0 || args[0] is not ("--claude-statusline" or "--configure-claude" or "--unconfigure-claude"))
        { return null; }
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);
        try
        {
            var options = Parse(args);
            if (args[0] == "--claude-statusline")
            {
                var producer = new ClaudeStatusLineProducer(Value(options, "--state-dir", DefaultPaths.ClaudeDirectory));
                output.WriteLine(producer.Process(ReadInput(input)));
                return 0;
            }
            var executable = executablePath ?? Environment.ProcessPath
                ?? throw new InvalidDataException("Executable unavailable.");
            if (args[0] == "--configure-claude" && !options.ContainsKey("--apply"))
            { output.WriteLine(ClaudeConfiguration.Preview(executable)); return 0; }
            var integration = Value(options, "--integration-path", Path.Combine(DefaultPaths.DataDirectory, "claude-integration.json"));
            // An installer with Claude disabled must not inspect Claude's settings at all.
            if (args[0] == "--unconfigure-claude" && !File.Exists(integration))
            { output.WriteLine("No owned Claude integration; nothing changed."); return 0; }
            var settings = Value(options, "--settings-path", Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "settings.json"));
            var result = args[0] == "--unconfigure-claude"
                ? ClaudeConfiguration.Remove(settings, executable, integration)
                : ClaudeConfiguration.Apply(settings, executable, integration, options.ContainsKey("--replace-existing"));
            output.WriteLine(JsonSerializer.Serialize(result));
            return 0;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidDataException or IOException or UnauthorizedAccessException
            or JsonException or NotSupportedException or System.Security.SecurityException)
        {
            error.WriteLine("Claude integration refused: invalid input, unsafe path, existing status line or inaccessible settings. No automatic replacement was authorized.");
            return 1;
        }
    }

    private static Dictionary<string, string?> Parse(string[] args)
    {
        var result = new Dictionary<string, string?>(StringComparer.Ordinal);
        var allowed = args[0] switch
        {
            "--claude-statusline" => new[] { "--state-dir" },
            "--unconfigure-claude" => ["--settings-path", "--integration-path"],
            _ => ["--apply", "--replace-existing", "--settings-path", "--integration-path"],
        };
        for (var index = 1; index < args.Length; index++)
        {
            var key = args[index];
            if (!allowed.Contains(key, StringComparer.Ordinal) || result.ContainsKey(key))
            { throw new ArgumentException("Unknown or repeated option."); }
            string? value = null;
            if (key is not ("--apply" or "--replace-existing"))
            {
                if (++index >= args.Length || args[index].StartsWith("--", StringComparison.Ordinal))
                { throw new ArgumentException("Missing option value."); }
                value = Path.GetFullPath(args[index]);
            }
            result.Add(key, value);
        }
        if (result.ContainsKey("--replace-existing") && !result.ContainsKey("--apply"))
        { throw new ArgumentException("Replacement requires explicit apply."); }
        return result;
    }

    private static string Value(Dictionary<string, string?> options, string key, string fallback) =>
        options.TryGetValue(key, out var value) ? value! : fallback;

    private static string ReadInput(TextReader input)
    {
        var text = new StringBuilder();
        var buffer = new char[4096];
        int count;
        while ((count = input.Read(buffer, 0, buffer.Length)) > 0)
        {
            if (text.Length + count > MaximumInput) { throw new InvalidDataException("Input too large."); }
            text.Append(buffer, 0, count);
        }
        if (Encoding.UTF8.GetByteCount(text.ToString()) > MaximumInput)
        { throw new InvalidDataException("Input too large."); }
        return text.ToString();
    }
}
