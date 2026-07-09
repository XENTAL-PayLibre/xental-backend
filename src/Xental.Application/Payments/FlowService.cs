using Microsoft.EntityFrameworkCore;
using Xental.Application.Common.Exceptions;
using Xental.Application.Common.Interfaces;
using Xental.Domain.Payments;

namespace Xental.Application.Payments;

public sealed record FlowSpec(
    string Name, string Trigger, IReadOnlyList<string> Actions,
    long? MinAmountKobo, int? MinRiskScore, int Priority);

/// <summary>Manages a tenant's programmable payment flows (dashboard plane). Configuration only —
/// the <see cref="FlowEngine"/> runs them. Nothing here moves money.</summary>
public sealed class FlowService(IApplicationDbContext db, ITenantContext tenantContext, IClock clock)
{
    public async Task<IReadOnlyList<Flow>> ListAsync(CancellationToken ct = default)
    {
        var tenantId = tenantContext.RequireTenantId();
        return await db.Flows.AsNoTracking().Include(f => f.Actions)
            .Where(f => f.TenantId == tenantId).OrderBy(f => f.Priority).ToListAsync(ct);
    }

    public async Task<Flow> GetAsync(Guid id, CancellationToken ct = default)
    {
        var tenantId = tenantContext.RequireTenantId();
        return await db.Flows.AsNoTracking().Include(f => f.Actions)
            .FirstOrDefaultAsync(f => f.Id == id && f.TenantId == tenantId, ct)
            ?? throw new NotFoundException($"Flow '{id}' not found.");
    }

    public async Task<Flow> CreateAsync(FlowSpec spec, CancellationToken ct = default)
    {
        var tenantId = tenantContext.RequireTenantId();
        var (trigger, actions) = Parse(spec);
        if (string.IsNullOrWhiteSpace(spec.Name))
            throw new ValidationException("A flow name is required.");

        var flow = new Flow(tenantId, spec.Name, trigger, spec.MinAmountKobo, spec.MinRiskScore, spec.Priority, clock.UtcNow);
        flow.SetActions(actions);
        db.Flows.Add(flow);
        await db.SaveChangesAsync(ct);
        return flow;
    }

    public async Task<Flow> UpdateAsync(Guid id, FlowSpec spec, CancellationToken ct = default)
    {
        var tenantId = tenantContext.RequireTenantId();
        var (trigger, actions) = Parse(spec);
        var flow = await db.Flows.Include(f => f.Actions)
            .FirstOrDefaultAsync(f => f.Id == id && f.TenantId == tenantId, ct)
            ?? throw new NotFoundException($"Flow '{id}' not found.");

        // Recreate cleanly: a flow is small + config-only, so replace rather than diff.
        db.Flows.Remove(flow);
        await db.SaveChangesAsync(ct);
        var replacement = new Flow(tenantId, spec.Name, trigger, spec.MinAmountKobo, spec.MinRiskScore, spec.Priority, clock.UtcNow);
        replacement.SetActions(actions);
        db.Flows.Add(replacement);
        await db.SaveChangesAsync(ct);
        return replacement;
    }

    public async Task SetEnabledAsync(Guid id, bool enabled, CancellationToken ct = default)
    {
        var tenantId = tenantContext.RequireTenantId();
        var flow = await db.Flows.FirstOrDefaultAsync(f => f.Id == id && f.TenantId == tenantId, ct)
            ?? throw new NotFoundException($"Flow '{id}' not found.");
        flow.SetEnabled(enabled);
        await db.SaveChangesAsync(ct);
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct = default)
    {
        var tenantId = tenantContext.RequireTenantId();
        var flow = await db.Flows.FirstOrDefaultAsync(f => f.Id == id && f.TenantId == tenantId, ct)
            ?? throw new NotFoundException($"Flow '{id}' not found.");
        db.Flows.Remove(flow);
        await db.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<FlowRun>> RunsAsync(int take = 50, CancellationToken ct = default)
    {
        var tenantId = tenantContext.RequireTenantId();
        return await db.FlowRuns.AsNoTracking()
            .Where(r => r.TenantId == tenantId)
            .OrderByDescending(r => r.CreatedAtUtc)
            .Take(Math.Clamp(take, 1, 200))
            .ToListAsync(ct);
    }

    private static (FlowTrigger, List<FlowActionType>) Parse(FlowSpec spec)
    {
        if (!Enum.TryParse<FlowTrigger>(spec.Trigger, ignoreCase: true, out var trigger))
            throw new ValidationException($"Unknown trigger '{spec.Trigger}'.");
        if (spec.Actions is null || spec.Actions.Count == 0)
            throw new ValidationException("A flow needs at least one action.");
        var actions = new List<FlowActionType>();
        foreach (var a in spec.Actions)
        {
            if (!Enum.TryParse<FlowActionType>(a, ignoreCase: true, out var parsed))
                throw new ValidationException($"Unknown action '{a}'.");
            actions.Add(parsed);
        }
        return (trigger, actions);
    }
}
