using FluentAssertions;
using Xental.Domain.Common;

namespace Xental.UnitTests;

public class MoneyTests
{
    [Theory]
    [InlineData(1, 100)]
    [InlineData(1.5, 150)]
    [InlineData(0.01, 1)]
    [InlineData(1234.56, 123456)]
    public void FromNaira_converts_to_kobo(decimal naira, long expectedKobo)
    {
        Money.FromNaira(naira).Kobo.Should().Be(expectedKobo);
    }

    [Fact]
    public void FromNaira_uses_bankers_rounding_at_the_half_kobo()
    {
        // 0.005 naira = 0.5 kobo -> rounds to even (0)
        Money.FromNaira(0.005m).Kobo.Should().Be(0);
        // 0.015 naira = 1.5 kobo -> rounds to even (2)
        Money.FromNaira(0.015m).Kobo.Should().Be(2);
    }

    [Fact]
    public void ToNaira_round_trips()
    {
        Money.FromKobo(123456).ToNaira().Should().Be(1234.56m);
    }

    [Fact]
    public void Arithmetic_and_comparisons_work()
    {
        var a = Money.FromKobo(500);
        var b = Money.FromKobo(200);
        (a + b).Kobo.Should().Be(700);
        (a - b).Kobo.Should().Be(300);
        (a > b).Should().BeTrue();
        (b < a).Should().BeTrue();
        Money.Zero.IsZero.Should().BeTrue();
        (b - a).IsNegative.Should().BeTrue();
    }

    [Fact]
    public void Addition_overflow_throws_rather_than_wrapping()
    {
        var max = Money.FromKobo(long.MaxValue);
        var act = () => _ = max + Money.FromKobo(1);
        act.Should().Throw<OverflowException>();
    }

    [Fact]
    public void Equality_is_by_value()
    {
        Money.FromKobo(100).Should().Be(Money.FromKobo(100));
        Money.FromKobo(100).Should().NotBe(Money.FromKobo(101));
    }
}
