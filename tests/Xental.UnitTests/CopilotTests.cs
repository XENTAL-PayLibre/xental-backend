using FluentAssertions;
using Xental.Application.Assistant;
using Xental.Application.Payments;
using Xental.Domain.Common;
using Xental.Domain.Payments;
using Xental.Infrastructure.Persistence;
using Xental.UnitTests.TestSupport;

namespace Xental.UnitTests;

public class CopilotTests
{
    private static CopilotService Copilot(TestDatabase db, XentalDbContext ctx) =>
        new(new InsightsService(ctx, db.Tenant, db.Clock), new FlowService(ctx, db.Tenant, db.Clock));

    private static async Task<Guid> SeedAsync(TestDatabase db)
    {
        await using var ctx = db.CreateContext();
        var t = new Xental.Domain.Tenancy.Tenant("Acme", $"a-{Guid.NewGuid():N}@x.com", "h");
        ctx.Tenants.Add(t);
        var c = new Customer(t.Id, "c-1", "Slow Payer");
        ctx.Customers.Add(c);
        var va = new VirtualAccount(t.Id, c.Id, "acc-1", "9000000000", "Bank", "Name", expectedAmountKobo: 100_00);
        va.ApplyInflow(Money.FromKobo(60_00)); // 60% paid → 40_00 outstanding
        ctx.VirtualAccounts.Add(va);
        ctx.Transactions.Add(new Transaction(t.Id, va.Id, "dep-1", "Slow Payer",
            Money.FromKobo(60_00), Money.Zero, TransactionStatus.Success, ReconciliationStatus.Underpaid,
            null, db.Clock.UtcNow, db.Clock.UtcNow));
        await ctx.SaveChangesAsync();
        return t.Id;
    }

    [Theory]
    [InlineData("", "help")]
    [InlineData("what can you do?", "help")]
    [InlineData("what's my collection rate?", "summary")]
    [InlineData("how much is outstanding?", "aging")]
    [InlineData("what can I expect over the next 30 days?", "forecast")]
    [InlineData("which customers are at risk?", "customers")]
    [InlineData("what automation do I have?", "flows")]
    public async Task Routes_prompts_to_the_right_intent(string prompt, string expectedIntent)
    {
        using var db = new TestDatabase();
        db.Tenant.TenantId = await SeedAsync(db);
        await using var ctx = db.CreateContext();

        var answer = await Copilot(db, ctx).AskAsync(prompt);

        answer.Intent.Should().Be(expectedIntent);
        answer.Reply.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task Summary_answer_is_grounded_in_the_real_collection_rate()
    {
        using var db = new TestDatabase();
        db.Tenant.TenantId = await SeedAsync(db);
        await using var ctx = db.CreateContext();

        var answer = await Copilot(db, ctx).AskAsync("how am I doing?");

        answer.Intent.Should().Be("summary");
        answer.Reply.Should().Contain("60%");     // 60_00 paid of 100_00 expected
        answer.Data.Should().NotBeNull();
    }

    [Fact]
    public async Task Aging_answer_reports_the_outstanding_balance()
    {
        using var db = new TestDatabase();
        db.Tenant.TenantId = await SeedAsync(db);
        await using var ctx = db.CreateContext();

        var answer = await Copilot(db, ctx).AskAsync("what do my customers owe me?");

        answer.Intent.Should().Be("aging");
        answer.Reply.Should().Contain("₦40.00").And.Contain("outstanding");
    }

    [Fact]
    public async Task Unrecognised_prompt_falls_back_to_help_with_suggestions()
    {
        using var db = new TestDatabase();
        db.Tenant.TenantId = await SeedAsync(db);
        await using var ctx = db.CreateContext();

        var answer = await Copilot(db, ctx).AskAsync("tell me a joke about bananas");

        answer.Intent.Should().Be("help");
        answer.Suggestions.Should().NotBeEmpty();
    }
}
