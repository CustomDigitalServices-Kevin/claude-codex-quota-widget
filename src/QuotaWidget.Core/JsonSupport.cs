using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace QuotaWidget.Core;

public static class JsonSupport
{
    public static JsonSerializerOptions Options { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = true,
    };

    public static string? Fingerprint(string? account) => string.IsNullOrWhiteSpace(account)
        ? null : Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(account)))[..8];

    internal static JsonElement Property(this JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value)
            ? value : default;

    internal static string? Text(this JsonElement element) =>
        element.ValueKind == JsonValueKind.String ? element.GetString() : null;

    internal static double? Number(this JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Number && element.TryGetDouble(out var number)
            && double.IsFinite(number))
        {
            return number;
        }
        return null;
    }

    internal static long? Integer(this JsonElement element) =>
        element.ValueKind == JsonValueKind.Number && element.TryGetInt64(out var number) ? number : null;

    internal static bool? Boolean(this JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        _ => null,
    };

    internal static DateTimeOffset? Timestamp(this JsonElement element)
    {
        if (element.Text() is { } text && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal, out var date))
        {
            return date.ToUniversalTime();
        }
        if (element.Integer() is { } seconds && seconds >= -62135596800L && seconds <= 253402300799L)
        {
            return DateTimeOffset.FromUnixTimeSeconds(seconds);
        }
        return null;
    }

    internal static Credits? ParseCredits(JsonElement value, bool camelCase)
    {
        if (value.ValueKind != JsonValueKind.Object)
        {
            return null;
        }
        double? balance = double.TryParse(value.Property("balance").Text(), NumberStyles.Float,
            CultureInfo.InvariantCulture, out var parsed) && double.IsFinite(parsed) ? parsed : null;
        return new(balance, value.Property("unlimited").Boolean(),
            value.Property(camelCase ? "hasCredits" : "has_credits").Boolean());
    }
}
