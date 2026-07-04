using System.Net.Http.Json;

namespace Xental.IntegrationTests;

/// <summary>Helper for the two-step dashboard login (password → emailed OTP → session cookies).</summary>
internal static class DashboardLogin
{
    /// <summary>Complete both login steps on <paramref name="client"/>, leaving it with session cookies.</summary>
    public static async Task CompleteAsync(HttpClient client, string email, string password)
    {
        var begin = await client.PostAsJsonAsync("/api/v1/developers/login", new { email, password });
        begin.EnsureSuccessStatusCode(); // 202 Accepted — password OK, OTP emailed
        var code = FakeEmailSender.OtpFor(email)
            ?? throw new InvalidOperationException($"No login OTP captured for {email}.");
        var verify = await client.PostAsJsonAsync("/api/v1/developers/login/verify", new { email, code });
        verify.EnsureSuccessStatusCode(); // 200 OK — session cookies set
    }
}
