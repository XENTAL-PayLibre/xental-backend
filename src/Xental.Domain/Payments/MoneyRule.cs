using Xental.Domain.Common;

namespace Xental.Domain.Payments;

/// <summary>What outcome of a reconciled deposit a rule reacts to.</summary>
public enum RuleTrigger { AnyDeposit = 1, Overpaid = 2, Underpaid = 3, HighRisk = 4, FullyPaid = 5 }

/// <summary>What the rule does when it fires. All reuse existing primitives; none change the verdict.</summary>
public enum RuleAction { Hold = 1, Notify = 2, ReviewFlag = 3 }

/// <summary>
/// A declarative if-this-then-that rule on reconciled inflows (Feature 3). Evaluated
/// <b>after</b> the reconciliation transaction commits, so a rule reacts to the outcome and can
/// never change the classification. Typed thresholds keep evaluation deterministic + safe:
/// <see cref="ThresholdKobo"/> gates the amount triggers, <see cref="MinRiskScore"/> the risk
/// trigger. Additive + opt-in — no rules means the engine is a no-op.
/// </summary>
public sealed class MoneyRule : BaseEntity, ITenantOwned
{
    public Guid TenantId { get; private set; }
    public RuleTrigger Trigger { get; private set; }
    public RuleAction Action { get; private set; }
    public long? ThresholdKobo { get; private set; }   // Overpaid/Underpaid gate
    public int? MinRiskScore { get; private set; }      // HighRisk gate
    public bool Enabled { get; private set; }
    public int Priority { get; private set; }

    private MoneyRule() { } // EF

    public MoneyRule(Guid tenantId, RuleTrigger trigger, RuleAction action, long? thresholdKobo, int? minRiskScore, int priority)
    {
        if (tenantId == Guid.Empty) throw new DomainException("TenantId is required.");
        if (thresholdKobo is < 0) throw new DomainException("Threshold cannot be negative.");
        if (minRiskScore is < 0 or > 100) throw new DomainException("MinRiskScore must be 0..100.");
        TenantId = tenantId;
        Trigger = trigger;
        Action = action;
        ThresholdKobo = thresholdKobo;
        MinRiskScore = minRiskScore;
        Priority = priority;
        Enabled = true;
    }

    /// <summary>Does this rule fire for the given reconciled deposit outcome?</summary>
    public bool Matches(ReconciliationStatus reconciliation, PaymentState paymentState, long overpaymentKobo, long deficitKobo, int riskScore) => Trigger switch
    {
        RuleTrigger.AnyDeposit => true,
        RuleTrigger.Overpaid => reconciliation == ReconciliationStatus.Overpaid && overpaymentKobo >= (ThresholdKobo ?? 0),
        RuleTrigger.Underpaid => reconciliation == ReconciliationStatus.Underpaid && deficitKobo >= (ThresholdKobo ?? 0),
        RuleTrigger.HighRisk => riskScore >= (MinRiskScore ?? 0),
        RuleTrigger.FullyPaid => paymentState == PaymentState.FullyPaid,
        _ => false,
    };
}
