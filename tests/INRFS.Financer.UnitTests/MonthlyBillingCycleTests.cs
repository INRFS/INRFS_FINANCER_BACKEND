using INRFS.Financer.Application;
using Xunit;

namespace INRFS.Financer.UnitTests;

public sealed class MonthlyBillingCycleTests
{
    [Fact]
    public void BeforeThe25th_ReturnsPreviousClosedCycle()
    {
        var cycle = MonthlyBillingCycle.LatestClosed(new DateOnly(2026, 8, 19));
        Assert.Equal(new DateOnly(2026, 6, 26), cycle.PeriodStart);
        Assert.Equal(new DateOnly(2026, 7, 25), cycle.PeriodEnd);
        Assert.Equal(new DateOnly(2026, 8, 10), cycle.DueDate);
    }

    [Fact]
    public void OnThe25th_ReturnsCurrentClosedCycle()
    {
        var cycle = MonthlyBillingCycle.LatestClosed(new DateOnly(2026, 8, 25));
        Assert.Equal(new DateOnly(2026, 7, 26), cycle.PeriodStart);
        Assert.Equal(new DateOnly(2026, 8, 25), cycle.PeriodEnd);
        Assert.Equal(new DateOnly(2026, 9, 10), cycle.DueDate);
    }
}
