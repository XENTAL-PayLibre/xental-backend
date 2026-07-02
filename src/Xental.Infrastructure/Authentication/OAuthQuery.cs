namespace Xental.Infrastructure.Authentication;

internal static class OAuthQuery
{
    /// <summary>Append a URL-encoded query string to a base URL.</summary>
    public static string Build(string baseUrl, IReadOnlyDictionary<string, string> parameters)
    {
        var query = string.Join("&", parameters
            .Where(kv => !string.IsNullOrEmpty(kv.Value))
            .Select(kv => $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value)}"));
        return $"{baseUrl}?{query}";
    }
}
