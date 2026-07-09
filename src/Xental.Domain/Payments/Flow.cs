using Xental.Domain.Common;

namespace Xental.Domain.Payments;

/// <summary>What kicks off a flow. Mirrors the reconciliation outcomes of a deposit.</summary>
public enum FlowTrigger { Deposit = 1, Overpaid = 2, Underpaid = 3, FullyPaid = 4, HighRisk = 5 }

/// <summary>An action a flow can take. Each reuses an existing money primitive and is idempotent.</summary>
public enum FlowActionType { Hold = 1, Release = 2, NotifyWebhook = 3, ReviewFlag = 4 }

/// <summary>
/// A programmable payment flow: a trigger + optional conditions + an ordered list of actions run
/// when a deposit's reconciliation matches. This generalises the single-action Money Rules engine
/// into multi-step automation with an audit trail (<see cref="FlowRun"/>). Actions run after the
/// reconciliation transaction commits, so a flow can never change the payment verdict.
/// </summary>
public sealed class Flow : BaseEntity, ITenantOwned
{
    private readonly List<FlowAction> _actions = new();

    public Guid TenantId { get; private set; }
    public string Name { get; private set; } = null!;
    public FlowTrigger Trigger { get; private set; }

    /// <summary>Only run when the deposit amount is at least this (optional gate).</summary>
    public long? MinAmountKobo { get; private set; }
    /// <summary>Only run when the risk score is at least this (optional gate).</summary>
    public int? MinRiskScore { get; private set; }

    public bool Enabled { get; private set; } = true;
    public int Priority { get; private set; }

    public IReadOnlyList<FlowAction> Actions => _actions;

    private Flow() { }

    public Flow(Guid tenantId, string name, FlowTrigger trigger, long? minAmountKobo, int? minRiskScore, int priority, DateTimeOffset now)
    {
        TenantId = tenantId;
        Name = DomainException.Require(name, nameof(name)).Trim();
        Trigger = trigger;
        MinAmountKobo = minAmountKobo is >= 0 ? minAmountKobo : null;
        MinRiskScore = minRiskScore;
        Priority = priority;
        Enabled = true;
        CreatedAtUtc = now;
    }

    public void SetActions(IEnumerable<FlowActionType> actions)
    {
        _actions.Clear();
        var order = 0;
        foreach (var a in actions)
            _actions.Add(new FlowAction(Id, order++, a));
    }

    public void SetEnabled(bool enabled) => Enabled = enabled;

    /// <summary>Does this flow fire for the given reconciliation outcome + deposit facts?</summary>
    public bool Matches(ReconciliationStatus reconciliation, PaymentState paymentState, long overpaymentKobo, long deficitKobo, int riskScore, long amountKobo)
    {
        if (MinAmountKobo is long min && amountKobo < min) return false;
        if (MinRiskScore is int mr && riskScore < mr) return false;
        return Trigger switch
        {
            FlowTrigger.Deposit => true,
            FlowTrigger.Overpaid => reconciliation == ReconciliationStatus.Overpaid,
            FlowTrigger.Underpaid => reconciliation == ReconciliationStatus.Underpaid,
            FlowTrigger.FullyPaid => paymentState == PaymentState.FullyPaid,
            FlowTrigger.HighRisk => reconciliation == ReconciliationStatus.PendingReview || riskScore >= (MinRiskScore ?? 70),
            _ => false,
        };
    }
}

/// <summary>One step of a <see cref="Flow"/>, executed in <see cref="Order"/>.</summary>
public sealed class FlowAction : BaseEntity
{
    public Guid FlowId { get; private set; }
    public int Order { get; private set; }
    public FlowActionType Type { get; private set; }

    private FlowAction() { }

    public FlowAction(Guid flowId, int order, FlowActionType type)
    {
        FlowId = flowId;
        Order = order;
        Type = type;
    }
}

/// <summary>Audit record: one row per flow that fired for a deposit, with a human-readable outcome.</summary>
public sealed class FlowRun : BaseEntity, ITenantOwned
{
    public Guid TenantId { get; private set; }
    public Guid FlowId { get; private set; }
    public string FlowName { get; private set; } = null!;
    public string Trigger { get; private set; } = null!;
    public string? AccountRef { get; private set; }
    public string? TransactionRef { get; private set; }
    public string Outcome { get; private set; } = null!;

    private FlowRun() { }

    public FlowRun(Guid tenantId, Guid flowId, string flowName, string trigger, string? accountRef, string? transactionRef, string outcome, DateTimeOffset now)
    {
        TenantId = tenantId;
        FlowId = flowId;
        FlowName = flowName;
        Trigger = trigger;
        AccountRef = accountRef;
        TransactionRef = transactionRef;
        Outcome = outcome;
        CreatedAtUtc = now;
    }
}
