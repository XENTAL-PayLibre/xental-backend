using Microsoft.Extensions.Options;
using Xental.Application.Common.Interfaces;

namespace Xental.Infrastructure.Payments;

/// <summary>Backs <see cref="IPayoutSwitch"/> with the settlement kill-switch, so pausing payouts
/// (<c>Settlement:PayoutsEnabled=false</c>) also blocks manual refunds.</summary>
public sealed class SettlementPayoutSwitch(IOptions<SettlementOptions> options) : IPayoutSwitch
{
    public bool PayoutsEnabled => options.Value.PayoutsEnabled;
}
