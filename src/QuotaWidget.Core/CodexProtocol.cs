using System.Text.Json;

namespace QuotaWidget.Core;

public static class CodexProtocol
{
    // All writes to app-server stdin go through this gate, including notifications.
    public static async Task SendAsync(TextWriter writer, string method, Action<string>? sent = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(writer);
        object message = method switch
        {
            "initialize" => new { id = 1, method, @params = new { clientInfo = new { name = "quota-widget",
                version = typeof(CodexProtocol).Assembly.GetName().Version?.ToString() ?? "1.0.0" } } },
            "initialized" => new { method },
            "account/rateLimits/read" => new { id = 2, method },
            _ => throw new InvalidOperationException("Method is not allowed by the read-only protocol."),
        };
        await writer.WriteLineAsync(JsonSerializer.Serialize(message).AsMemory(), cancellationToken).ConfigureAwait(false);
        await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
        sent?.Invoke(method);
    }
}
