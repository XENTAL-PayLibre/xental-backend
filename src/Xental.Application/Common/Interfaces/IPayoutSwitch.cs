namespace Xental.Application.Common.Interfaces;

/// <summary>The global payout kill-switch. When off, no money leaves — this gates human-approved
/// refunds as well as the automated settlement sweep, so a single flag pauses all outflows.</summary>
public interface IPayoutSwitch
{
    bool PayoutsEnabled { get; }
}
