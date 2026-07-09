using System.Globalization;
using Xental.Application.Payments;

namespace Xental.Application.Assistant;

public sealed record CopilotAction(string Label, string Href);
public sealed record CopilotAnswer(
    string Intent, string Reply, IReadOnlyList<string> Suggestions, IReadOnlyList<CopilotAction> Actions, object? Data);

/// <summary>
/// A grounded, in-dashboard assistant (the "agent plane"): it answers natural-language questions
/// about the merchant's own account by routing to a real data source (insights, aging, forecast,
/// customer scores, flows) and never invents figures. Deterministic and tenant-scoped — the same
/// question always yields the same numbers. The intent router is the seam an LLM can later sit behind.
/// </summary>
public sealed class CopilotService(InsightsService insights, FlowService flows)
{
    private static readonly string[] DefaultSuggestions =
    {
        "What's my collection rate?",
        "How much is outstanding?",
        "What can I expect over the next 30 days?",
        "Which customers are at risk?",
        "What automation do I have?",
    };

    public async Task<CopilotAnswer> AskAsync(string? prompt, CancellationToken ct = default)
    {
        var q = (prompt ?? string.Empty).Trim().ToLowerInvariant();

        if (q.Length == 0 || Has(q, "help", "what can you", "capabilities", "hello", "hi "))
            return Help();

        if (Has(q, "outstanding", "owe", "owed", "receivable", "aging", "overdue", "unpaid"))
            return await AgingAnswer(ct);

        if (Has(q, "forecast", "cash flow", "cashflow", "expect", "projection", "next 30", "next month", "upcoming"))
            return await ForecastAnswer(ct);

        if (Has(q, "risk", "at-risk", "at risk", "reliable", "reliability", "worst", "score", "defaulter", "late"))
            return await CustomersAnswer(ct);

        if (Has(q, "flow", "automation", "automate", "rule", "hold "))
            return await FlowsAnswer(ct);

        if (Has(q, "collection rate", "rate", "collected", "performance", "how am i doing", "summary", "overview"))
            return await SummaryAnswer(ct);

        // Unrecognised — fall back to help, but acknowledge.
        return Help($"I'm not sure how to answer that yet. Here's what I can help with:");
    }

    private static bool Has(string q, params string[] terms) => terms.Any(q.Contains);

    private static string Naira(long kobo) => "₦" + (kobo / 100m).ToString("N2", CultureInfo.InvariantCulture);

    private CopilotAnswer Help(string? lead = null)
    {
        var reply = (lead ?? "I'm your collections assistant. I can answer questions about your account using live data.")
            + "\n\nTry asking about your collection rate, outstanding receivables, cash-flow forecast, at-risk customers, or your automation flows.";
        return new CopilotAnswer("help", reply, DefaultSuggestions, Array.Empty<CopilotAction>(), null);
    }

    private async Task<CopilotAnswer> SummaryAnswer(CancellationToken ct)
    {
        var s = await insights.GetAsync(ct);
        var reply = $"Your collection rate is {s.CollectionRatePct}%. You've collected {Naira(s.TotalCollectedKobo)} "
            + $"of {Naira(s.ExpectedKobo)} expected across {s.VirtualAccounts} account(s), from {s.Deposits} deposit(s). "
            + $"{s.FullyPaidAccounts} fully paid, {s.PartiallyPaidAccounts} partially paid.";
        if (s.PendingReview > 0 || s.HighRisk > 0)
            reply += $" ⚠️ {s.PendingReview} awaiting review and {s.HighRisk} high-risk deposit(s).";
        return new CopilotAnswer("summary", reply,
            new[] { "How much is outstanding?", "Which customers are at risk?" },
            new[] { new CopilotAction("Open balances", "/dashboard/balances") }, s);
    }

    private async Task<CopilotAnswer> AgingAnswer(CancellationToken ct)
    {
        var r = await insights.GetAgingAsync(ct);
        if (r.TotalOutstandingKobo == 0)
            return new CopilotAnswer("aging", "You have no outstanding receivables — everything expected has been collected. 🎉",
                new[] { "What's my collection rate?", "What can I expect over the next 30 days?" }, Array.Empty<CopilotAction>(), r);

        var over60 = r.Buckets.FirstOrDefault(b => b.Label.StartsWith("60+"));
        var reply = $"You have {Naira(r.TotalOutstandingKobo)} outstanding across your accounts.";
        if (over60 is { OutstandingKobo: > 0 })
            reply += $" Of that, {Naira(over60.OutstandingKobo)} is 60+ days overdue ({over60.Accounts} account(s)) — worth chasing first.";
        return new CopilotAnswer("aging", reply,
            new[] { "Which customers are at risk?", "What can I expect over the next 30 days?" },
            new[] { new CopilotAction("View collections", "/dashboard/collections") }, r);
    }

    private async Task<CopilotAnswer> ForecastAnswer(CancellationToken ct)
    {
        var f = await insights.GetForecastAsync(30, ct);
        var reply = $"Over the next {f.Days} days you can expect roughly {Naira(f.ProjectedTotalKobo)} in — "
            + $"{Naira(f.ScheduledDueKobo)} from scheduled billing due and about {Naira(f.RunRateProjectedKobo)} "
            + $"from your recent collection run-rate ({Naira((long)f.DailyRunRateKobo)}/day).";
        return new CopilotAnswer("forecast", reply,
            new[] { "How much is outstanding?", "Which customers are at risk?" },
            new[] { new CopilotAction("View collections", "/dashboard/collections") }, f);
    }

    private async Task<CopilotAnswer> CustomersAnswer(CancellationToken ct)
    {
        var scores = await insights.GetCustomerScoresAsync(5, ct);
        var atRisk = scores.Where(s => s.Rating is "Poor" or "Fair").ToList();
        if (scores.Count == 0)
            return new CopilotAnswer("customers", "You don't have any scored customers yet.",
                DefaultSuggestions, Array.Empty<CopilotAction>(), scores);

        string reply;
        if (atRisk.Count == 0)
            reply = "None of your customers are currently at risk — collection reliability looks healthy across the board. 👍";
        else
        {
            var worst = atRisk[0];
            reply = $"{atRisk.Count} customer(s) look at risk. The most concerning is {worst.CustomerName} "
                + $"(score {worst.Score}/100, {worst.Rating}) with {Naira(worst.OutstandingKobo)} outstanding "
                + $"at a {worst.CollectionRatePct}% collection rate.";
        }
        return new CopilotAnswer("customers", reply,
            new[] { "How much is outstanding?", "What automation do I have?" },
            new[] { new CopilotAction("View collections", "/dashboard/collections") }, scores);
    }

    private async Task<CopilotAnswer> FlowsAnswer(CancellationToken ct)
    {
        var list = await flows.ListAsync(ct);
        var active = list.Count(f => f.Enabled);
        var reply = list.Count == 0
            ? "You have no payment flows set up yet. Flows let you automatically hold, release, or flag payments the moment they reconcile — e.g. auto-hold overpayments for review."
            : $"You have {list.Count} flow(s), {active} active. They run automatically on every reconciled deposit.";
        return new CopilotAnswer("flows", reply,
            new[] { "How much is outstanding?", "What's my collection rate?" },
            new[] { new CopilotAction("Manage flows", "/dashboard/flows") }, list);
    }
}
