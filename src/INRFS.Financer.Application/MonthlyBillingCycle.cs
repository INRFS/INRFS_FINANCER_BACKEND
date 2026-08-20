namespace INRFS.Financer.Application;

public sealed record MonthlyBillingCycle(DateOnly PeriodStart, DateOnly PeriodEnd, DateOnly DueDate)
{
    public static MonthlyBillingCycle LatestClosed(DateOnly today)
    {
        var closeMonth = today.Day >= 25 ? today : today.AddMonths(-1);
        var periodEnd = new DateOnly(closeMonth.Year, closeMonth.Month, 25);
        var previousClose = periodEnd.AddMonths(-1);
        var periodStart = previousClose.AddDays(1);
        var nextMonth = periodEnd.AddMonths(1);
        var dueDate = new DateOnly(nextMonth.Year, nextMonth.Month, 10);
        return new MonthlyBillingCycle(periodStart, periodEnd, dueDate);
    }
}
