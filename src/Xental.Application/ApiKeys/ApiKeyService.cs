using Microsoft.EntityFrameworkCore;
using Xental.Application.Common.Exceptions;
using Xental.Application.Common.Interfaces;
using Xental.Domain.Tenancy;

namespace Xental.Application.ApiKeys;

public sealed record CreatedApiKey(Guid Id, string ClientId, string ClientSecret, string Mode, string Label, DateTimeOffset CreatedAtUtc);

/// <summary>
/// Manages a developer's API keys. All operations are scoped to the current tenant
/// by the DbContext query filter, so keys of other tenants are invisible. Secrets
/// are shown once and stored only as a hash.
/// </summary>
public sealed class ApiKeyService(
    IApplicationDbContext db,
    ITenantContext tenantContext,
    ISecretHasher secrets,
    ITokenGenerator tokens,
    IClock clock)
{
    public async Task<CreatedApiKey> CreateAsync(string label, ApiKeyMode mode, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(label))
            throw new ValidationException("Key label is required.");

        var tenantId = tenantContext.RequireTenantId();
        var clientId = tokens.Generate(mode == ApiKeyMode.Live ? "xnt_live" : "xnt_test", 12);
        var secret = tokens.Generate(mode == ApiKeyMode.Live ? "sk_live" : "sk_test", 32);

        var key = new ApiKey(tenantId, clientId, secrets.Hash(secret), label.Trim(), mode);
        db.ApiKeys.Add(key);
        await db.SaveChangesAsync(ct);

        return new CreatedApiKey(key.Id, clientId, secret, mode.ToString(), key.Label, key.CreatedAtUtc);
    }

    public async Task<IReadOnlyList<ApiKey>> ListAsync(CancellationToken ct = default) =>
        await db.ApiKeys.AsNoTracking().OrderByDescending(k => k.CreatedAtUtc).ToListAsync(ct);

    public async Task RevokeAsync(Guid id, CancellationToken ct = default)
    {
        var key = await db.ApiKeys.FirstOrDefaultAsync(k => k.Id == id, ct)
            ?? throw new NotFoundException("API key not found.");
        key.Revoke(clock.UtcNow);
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Revoke the existing key and issue a fresh one with the same label + mode.</summary>
    public async Task<CreatedApiKey> RotateAsync(Guid id, CancellationToken ct = default)
    {
        var key = await db.ApiKeys.FirstOrDefaultAsync(k => k.Id == id, ct)
            ?? throw new NotFoundException("API key not found.");
        key.Revoke(clock.UtcNow);
        await db.SaveChangesAsync(ct);
        return await CreateAsync(key.Label, key.Mode, ct);
    }
}
