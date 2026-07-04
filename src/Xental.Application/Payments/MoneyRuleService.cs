using Microsoft.EntityFrameworkCore;
using Xental.Application.Common.Exceptions;
using Xental.Application.Common.Interfaces;
using Xental.Domain.Payments;

namespace Xental.Application.Payments;

public sealed record RuleSpec(string Trigger, string Action, long? ThresholdKobo, int? MinRiskScore, int Priority);

/// <summary>Manages a tenant's money rules (dashboard plane). Configuration only — nothing here moves money.</summary>
public sealed class MoneyRuleService(IApplicationDbContext db, ITenantContext tenantContext)
{
    public async Task<IReadOnlyList<MoneyRule>> ListAsync(CancellationToken ct = default)
    {
        var tenantId = tenantContext.RequireTenantId();
        return await db.MoneyRules.AsNoTracking()
            .Where(r => r.TenantId == tenantId).OrderBy(r => r.Priority).ToListAsync(ct);
    }

    public async Task<MoneyRule> CreateAsync(RuleSpec spec, CancellationToken ct = default)
    {
        var tenantId = tenantContext.RequireTenantId();
        if (!Enum.TryParse<RuleTrigger>(spec.Trigger, ignoreCase: true, out var trigger))
            throw new ValidationException($"Unknown trigger '{spec.Trigger}'.");
        if (!Enum.TryParse<RuleAction>(spec.Action, ignoreCase: true, out var action))
            throw new ValidationException($"Unknown action '{spec.Action}'.");

        var rule = new MoneyRule(tenantId, trigger, action, spec.ThresholdKobo, spec.MinRiskScore, spec.Priority);
        db.MoneyRules.Add(rule);
        await db.SaveChangesAsync(ct);
        return rule;
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct = default)
    {
        var tenantId = tenantContext.RequireTenantId();
        var rule = await db.MoneyRules.FirstOrDefaultAsync(r => r.Id == id && r.TenantId == tenantId, ct)
            ?? throw new NotFoundException($"Rule '{id}' not found.");
        db.MoneyRules.Remove(rule);
        await db.SaveChangesAsync(ct);
    }
}
