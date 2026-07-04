using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Xental.Application.Common.Interfaces;
using Xental.Domain.Onboarding;
using Xental.Infrastructure.Persistence;
using Xental.Infrastructure.Security;

namespace Xental.UnitTests.TestSupport;

/// <summary>Seeds onboarding state so tests can exercise the Live-tier gate.</summary>
public static class OnboardingSeed
{
    /// <summary>Create an approved application (both tracks) so the tenant is Live-tier.</summary>
    public static async Task ApprovedLiveAsync(TestDatabase db, Guid tenantId)
    {
        await using var ctx = db.CreateContext();
        var app = new OnboardingApplication(tenantId);
        var admin = Guid.NewGuid();
        app.SubmitTrack(OnboardingTrack.DeveloperKyc, db.Clock.UtcNow);
        app.ApproveTrack(OnboardingTrack.DeveloperKyc, admin, db.Clock.UtcNow);
        app.SubmitTrack(OnboardingTrack.BusinessKyb, db.Clock.UtcNow);
        app.ApproveTrack(OnboardingTrack.BusinessKyb, admin, db.Clock.UtcNow);
        ctx.OnboardingApplications.Add(app);
        await ctx.SaveChangesAsync();
    }
}

public sealed class FakeClock(DateTimeOffset now) : IClock
{
    public DateTimeOffset UtcNow { get; set; } = now;
}

/// <summary>Real security primitives wired with test-friendly options.</summary>
public static class TestSecurity
{
    /// <summary>A password that satisfies the strong-password policy.</summary>
    public const string StrongPassword = "Str0ng-Passw0rd!";

    public static BcryptPasswordHasher PasswordHasher() =>
        new(Options.Create(new AuthOptions()));

    public static JwtTokenService Jwt(IClock? clock = null) =>
        new(Options.Create(new JwtOptions
        {
            Issuer = "xental",
            Audience = "xental-api",
            SigningKey = "0123456789012345678901234567890123456789",
            AccessTokenLifetimeSeconds = 3600,
            DashboardTokenLifetimeSeconds = 900,
        }), clock ?? new FakeClock(DateTimeOffset.Parse("2026-01-01T00:00:00Z")));

    /// <summary>Builds a SessionIssuer over the given context (for login/refresh/oauth tests).</summary>
    public static Xental.Application.Authentication.SessionIssuer Sessions(
        Xental.Infrastructure.Persistence.XentalDbContext ctx, IClock clock) =>
        new(ctx, Jwt(clock), new FakeTokenGenerator(), new Sha256TokenHasher(), new FakeLinkBuilder(), clock);
}

public sealed class FakeTenantContext : ITenantContext
{
    public Guid? TenantId { get; set; }
    public Guid RequireTenantId() => TenantId ?? throw new InvalidOperationException("No tenant.");
}

/// <summary>Records operational alerts so tests can assert they fired.</summary>
public sealed class FakeAlerter : IErrorAlerter
{
    public int OperationalAlerts { get; private set; }
    public string? LastSubject { get; private set; }

    public Task NotifyServerErrorAsync(Exception exception, string path, string method, string? traceId, CancellationToken ct = default) =>
        Task.CompletedTask;

    public Task NotifyOperationalAsync(string subject, string message, string dedupeKey, CancellationToken ct = default)
    {
        OperationalAlerts++;
        LastSubject = subject;
        return Task.CompletedTask;
    }
}

public sealed class FakeAdminContext : IAdminContext
{
    public Guid? AdminId { get; set; } = Guid.NewGuid();
    public Xental.Domain.Admin.AdminRole? Role { get; set; } = Xental.Domain.Admin.AdminRole.SuperAdmin;
    public Guid RequireAdminId() => AdminId ?? throw new InvalidOperationException("No admin.");
}

public sealed class FakeTokenGenerator : ITokenGenerator
{
    // Static so tokens are unique across generator instances (e.g. rotation across
    // multiple DbContexts), preventing duplicate token-hash collisions in tests.
    private static int _counter;
    public string Generate(string prefix, int bytes = 24) => $"{prefix}_test_{Interlocked.Increment(ref _counter)}";
}

/// <summary>Records the last links "sent" so tests can drive the magic-link flows.</summary>
public sealed class FakeEmailSender : IEmailSender
{
    public string? LastVerificationLink { get; private set; }
    public string? LastResetLink { get; private set; }
    public string? LastInviteLink { get; private set; }
    public string? LastOtpCode { get; private set; }

    public Task SendEmailVerificationAsync(string toEmail, string verifyLink, CancellationToken ct = default)
    {
        LastVerificationLink = verifyLink;
        return Task.CompletedTask;
    }

    public Task SendLoginOtpAsync(string toEmail, string code, CancellationToken ct = default)
    {
        LastOtpCode = code;
        return Task.CompletedTask;
    }

    public Task SendPasswordResetAsync(string toEmail, string resetLink, CancellationToken ct = default)
    {
        LastResetLink = resetLink;
        return Task.CompletedTask;
    }

    public Task SendTeamInviteAsync(string toEmail, string inviteLink, string accountName, CancellationToken ct = default)
    {
        LastInviteLink = inviteLink;
        return Task.CompletedTask;
    }

    public Task SendOperationalAlertAsync(string toEmail, string subject, string html, CancellationToken ct = default) =>
        Task.CompletedTask;

    public int BillingReminders { get; private set; }
    public bool LastReminderOverdue { get; private set; }

    public Task SendBillingReminderAsync(
        string toEmail, string brand, long amountKobo, DateTimeOffset dueDateUtc,
        string accountNumber, string bankName, bool overdue, CancellationToken ct = default)
    {
        BillingReminders++;
        LastReminderOverdue = overdue;
        return Task.CompletedTask;
    }
}

/// <summary>Returns the raw token verbatim as the "link" so tests can consume it directly.</summary>
public sealed class FakeLinkBuilder : ILinkBuilder
{
    public TimeSpan EmailVerificationTtl { get; set; } = TimeSpan.FromMinutes(30);
    public TimeSpan PasswordResetTtl { get; set; } = TimeSpan.FromMinutes(30);
    public TimeSpan TeamInviteTtl { get; set; } = TimeSpan.FromDays(7);
    public TimeSpan RefreshTokenLifetime { get; set; } = TimeSpan.FromDays(14);

    public string EmailVerificationLink(string rawToken) => rawToken;
    public string PasswordResetLink(string rawToken) => rawToken;
    public string TeamInviteLink(string rawToken) => rawToken;
}

public sealed class FakeOAuthProvider(string name, ExternalUserProfile profile) : IExternalIdentityProvider
{
    public string Name => name;
    public string BuildAuthorizationUrl(string redirectUri, string state) => $"https://provider.test/auth?state={state}";
    public Task<ExternalUserProfile> ExchangeCodeAsync(string code, string redirectUri, CancellationToken ct = default) =>
        Task.FromResult(profile);
}

/// <summary>Returns a deterministic NUBAN so webhook-matching tests can target it.</summary>
public sealed class FakeNombaClient(string accountNumber = "1234567890") : INombaClient
{
    public string AccountNumber { get; } = accountNumber;
    public bool TransferSucceeds { get; set; } = true;

    public Task<ProvisionedVirtualAccount> CreateVirtualAccountAsync(
        string accountRef, string accountName, string? email, string? phone, CancellationToken ct = default) =>
        Task.FromResult(new ProvisionedVirtualAccount(AccountNumber, "Test Bank", accountName, "prov-" + accountRef));

    /// <summary>Account-holder name returned by the NUBAN lookup (set to match the applicant in KYC tests).</summary>
    public string LookupAccountName { get; set; } = "Resolved Name";
    public bool LookupThrows { get; set; }

    public Task<BankAccountName> LookupBankAccountAsync(string accountNumber, string bankCode, CancellationToken ct = default) =>
        LookupThrows
            ? throw new InvalidOperationException("nuban lookup failed")
            : Task.FromResult(new BankAccountName(LookupAccountName, accountNumber, bankCode));

    public Task<TransferResult> InitiateTransferAsync(
        string merchantTxRef, long amountKobo, string accountNumber, string bankCode, string? accountName, string? narration, CancellationToken ct = default) =>
        Task.FromResult(TransferSucceeds
            ? new TransferResult(true, "prov-" + merchantTxRef, null)
            : new TransferResult(false, null, "declined"));
}

public sealed class FakeSignatureVerifier(bool result = true) : INombaSignatureVerifier
{
    public bool Result { get; set; } = result;
    public bool Verify(byte[] rawBody, string? signatureHeader, string? timestampHeader) => Result;
}

/// <summary>Configurable identity verifier (Dojah stand-in) for KYC/KYB tests.</summary>
public sealed class FakeIdentityVerifier : IIdentityVerifier
{
    public IdentityResult IdentityResult { get; set; } = new(true, "Ada", "Obi", null);
    public CompanyResult CompanyResult { get; set; } = new(true, "Acme Ltd", "RC123456");
    public bool Throws { get; set; }

    public Task<IdentityResult> VerifyBvnAsync(string bvn, CancellationToken ct = default) =>
        Throws ? throw new InvalidOperationException("dojah down") : Task.FromResult(IdentityResult);
    public Task<IdentityResult> VerifyNinAsync(string nin, CancellationToken ct = default) =>
        Throws ? throw new InvalidOperationException("dojah down") : Task.FromResult(IdentityResult);
    public Task<CompanyResult> VerifyCacAsync(string rcNumber, CancellationToken ct = default) =>
        Throws ? throw new InvalidOperationException("dojah down") : Task.FromResult(CompanyResult);
}

/// <summary>A real AES protector wired with a test key (for encrypting KYC id numbers).</summary>
public static class TestProtector
{
    public static AesSecretProtector Create() =>
        new(Options.Create(new JwtOptions { SigningKey = new string('k', 40) }),
            Options.Create(new Xental.Infrastructure.Configuration.EncryptionOptions()));
}

/// <summary>In-memory document storage for KYB tests (records what was stored).</summary>
public sealed class FakeDocumentStorage : IDocumentStorage
{
    public readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte[]> Objects = new();

    public async Task PutAsync(string objectKey, Stream content, string contentType, CancellationToken ct = default)
    {
        using var ms = new MemoryStream();
        await content.CopyToAsync(ms, ct);
        Objects[objectKey] = ms.ToArray();
    }

    public Task<Uri> CreateDownloadUrlAsync(string objectKey, TimeSpan ttl, CancellationToken ct = default) =>
        Task.FromResult(new Uri($"https://storage.test/{objectKey}"));
}

/// <summary>An isolated SQLite in-memory database bound to a controllable tenant/clock.</summary>
public sealed class TestDatabase : IDisposable
{
    private readonly SqliteConnection _connection;

    public FakeTenantContext Tenant { get; }
    public FakeClock Clock { get; }

    public TestDatabase(FakeTenantContext? tenant = null, FakeClock? clock = null)
    {
        Tenant = tenant ?? new FakeTenantContext();
        Clock = clock ?? new FakeClock(DateTimeOffset.Parse("2026-01-01T00:00:00Z"));
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        using var ctx = CreateContext();
        ctx.Database.EnsureCreated();
    }

    public XentalDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<XentalDbContext>()
            .UseSqlite(_connection)
            .Options;
        return new XentalDbContext(options, Tenant, Clock);
    }

    public void Dispose() => _connection.Dispose();
}
