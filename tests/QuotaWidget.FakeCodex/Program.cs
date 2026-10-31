using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;

// Explicit test executable. Each scenario is JSONL: one login configuration followed by
// app-server steps { expect, messages, delayMs, hang, spawnChild, stderr }.
// The three sidecars record launches (including CODEX_HOME), PIDs and received methods.
Console.InputEncoding = new UTF8Encoding(false);
Console.OutputEncoding = new UTF8Encoding(false);
var scenario = Environment.GetEnvironmentVariable("QUOTAWIDGET_FAKE_SCENARIO")
    ?? throw new InvalidOperationException("A fake scenario is required.");
File.AppendAllText(scenario + ".pids", Environment.ProcessId.ToString(CultureInfo.InvariantCulture) + "\n");
if (args is ["child"])
{
    await Task.Delay(Timeout.InfiniteTimeSpan);
    return 0;
}
File.AppendAllText(scenario + ".launches", JsonSerializer.Serialize(new
{
    arguments = args, home = Environment.GetEnvironmentVariable("CODEX_HOME"),
}) + "\n");
var steps = File.ReadAllLines(scenario).Where(line => !string.IsNullOrWhiteSpace(line))
    .Select(line => JsonSerializer.Deserialize<JsonElement>(line)).ToArray();
if (args is ["login", "status"])
{
    var login = steps[0];
    if (login.TryGetProperty("hang", out var hang) && hang.GetBoolean()) { await Task.Delay(Timeout.InfiniteTimeSpan); }
    if (login.TryGetProperty("stdout", out var stdout)) { await Console.Out.WriteLineAsync(stdout.GetString()); }
    if (login.TryGetProperty("stderr", out var stderr)) { await Console.Error.WriteLineAsync(stderr.GetString()); }
    return login.GetProperty("exitCode").GetInt32();
}
if (args is not ["app-server"]) { return 64; }
foreach (var step in steps.Skip(1))
{
    var line = await Console.In.ReadLineAsync();
    if (line is null) { return 65; }
    using var document = JsonDocument.Parse(line);
    var request = document.RootElement;
    var method = request.GetProperty("method").GetString();
    File.AppendAllText(scenario + ".methods", method + "\n");
    if (request.TryGetProperty("jsonrpc", out _) || method != step.GetProperty("expect").GetString()) { return 66; }
    if (method == "initialize" && (request.GetProperty("id").GetInt32() != 1
        || request.GetProperty("params").GetProperty("clientInfo").GetProperty("name").GetString() != "quota-widget"
        || string.IsNullOrEmpty(request.GetProperty("params").GetProperty("clientInfo").GetProperty("version").GetString())))
    { return 67; }
    if (method == "account/rateLimits/read" && request.GetProperty("id").GetInt32() != 2) { return 68; }
    if (step.TryGetProperty("spawnChild", out var child) && child.GetBoolean())
    {
        using var process = Process.Start(new ProcessStartInfo(Environment.ProcessPath!)
        { UseShellExecute = false, CreateNoWindow = true, ArgumentList = { "child" } });
    }
    if (step.TryGetProperty("stderr", out var noise)) { await Console.Error.WriteLineAsync(noise.GetString()); }
    if (step.TryGetProperty("delayMs", out var delay)) { await Task.Delay(delay.GetInt32()); }
    if (step.TryGetProperty("messages", out var messages))
    {
        foreach (var message in messages.EnumerateArray()) { await Console.Out.WriteLineAsync(message.GetRawText()); }
        await Console.Out.FlushAsync();
    }
    if (step.TryGetProperty("hang", out var hang) && hang.GetBoolean()) { await Task.Delay(Timeout.InfiniteTimeSpan); }
}
return 0;
