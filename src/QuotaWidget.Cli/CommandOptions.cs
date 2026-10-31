using System.Globalization;
using QuotaWidget.Core;

namespace QuotaWidget.Cli;

internal sealed record CommandOptions(bool Json, bool Arbitration, string ClaudeDirectory,
    string[] CodexProfiles, string DataDirectory, DateTimeOffset? Now, bool Live, string? CodexExecutablePath)
{
    internal static CommandOptions Parse(string[] args)
    {
        var claude = DefaultPaths.ClaudeDirectory;
        var data = DefaultPaths.DataDirectory;
        List<string> profiles = [];
        var json = false;
        var text = false;
        var arbitration = false;
        var live = false;
        string? executable = null;
        DateTimeOffset? now = null;
        for (var index = 0; index < args.Length; index++)
        {
            var option = args[index];
            switch (option)
            {
                case "--json": json = true; break;
                case "--text": text = true; break;
                case "--arbitrage": arbitration = true; break;
                case "--live": live = true; break;
                case "--codex-executable": executable = Value(args, ref index); break;
                case "--claude-dir": claude = Value(args, ref index); break;
                case "--codex-sessions": profiles.Add(Value(args, ref index)); break;
                case "--data-dir": data = Value(args, ref index); break;
                case "--now":
                    var input = Value(args, ref index);
                    string[] formats =
                    [
                        "yyyy-MM-dd'T'HH:mm:ss'Z'", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'",
                        "yyyy-MM-dd'T'HH:mm:sszzz", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFFzzz",
                    ];
                    if (!DateTimeOffset.TryParseExact(input, formats, CultureInfo.InvariantCulture,
                            DateTimeStyles.AssumeUniversal, out var parsed)
                        || !(input.EndsWith('Z') || input.LastIndexOf('+') > 10 || input.LastIndexOf('-') > 10))
                    {
                        throw new ArgumentException("Date --now invalide : ISO-8601 avec fuseau attendu.");
                    }
                    now = parsed.ToUniversalTime();
                    break;
                default: throw new ArgumentException($"Option inconnue : {option}.");
            }
        }
        if (json && text)
        {
            throw new ArgumentException("Choisir --json ou --text.");
        }
        if (profiles.Count == 0)
        {
            profiles.AddRange(DefaultPaths.CodexProfiles);
        }
        return new(json, arbitration, claude, [.. profiles], data, now, live, executable);
    }

    private static string Value(string[] args, ref int index)
    {
        var option = args[index];
        index++;
        if (index >= args.Length || string.IsNullOrWhiteSpace(args[index]) || args[index].StartsWith("--", StringComparison.Ordinal))
        {
            throw new ArgumentException($"Valeur manquante pour {option}.");
        }
        return args[index];
    }
}

internal sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => now;
}
