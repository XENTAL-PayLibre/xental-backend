using FluentAssertions;
using Xental.Application.Common.Interfaces;
using Xental.Infrastructure.Payments;

namespace Xental.UnitTests;

public class CheckoutNotifierTests
{
    private static CheckoutStatusEvent Evt(Guid accountId, string state) =>
        new(accountId, "acct-1", state, 5000, 5000, "Reconciled");

    [Fact]
    public async Task Subscriber_receives_published_status_for_its_account()
    {
        var notifier = new InMemoryReconciliationNotifier();
        var accountId = Guid.NewGuid();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        var received = new TaskCompletionSource<CheckoutStatusEvent>();
        var pump = Task.Run(async () =>
        {
            await foreach (var e in notifier.SubscribeAsync(accountId, cts.Token))
            {
                received.TrySetResult(e);
                break;
            }
        });

        // Give the subscriber a moment to register, then publish.
        await Task.Delay(50, cts.Token);
        notifier.Publish(Evt(accountId, "FullyPaid"));

        var got = await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
        got.PaymentState.Should().Be("FullyPaid");
        cts.Cancel();
        await pump.ContinueWith(_ => { }); // drain
    }

    [Fact]
    public void Publishing_to_an_account_with_no_subscribers_is_a_noop()
    {
        var notifier = new InMemoryReconciliationNotifier();
        var act = () => notifier.Publish(Evt(Guid.NewGuid(), "PartiallyPaid"));
        act.Should().NotThrow();
    }

    [Fact]
    public async Task A_subscriber_does_not_receive_events_for_a_different_account()
    {
        var notifier = new InMemoryReconciliationNotifier();
        var mine = Guid.NewGuid();
        var other = Guid.NewGuid();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));

        var gotSomething = false;
        var pump = Task.Run(async () =>
        {
            await foreach (var _ in notifier.SubscribeAsync(mine, cts.Token))
                gotSomething = true;
        });

        await Task.Delay(50);
        notifier.Publish(Evt(other, "FullyPaid")); // different account
        await Task.Delay(100);
        cts.Cancel();
        await pump.ContinueWith(_ => { });

        gotSomething.Should().BeFalse();
    }
}
