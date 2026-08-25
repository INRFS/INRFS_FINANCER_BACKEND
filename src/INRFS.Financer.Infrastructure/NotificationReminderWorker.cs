using INRFS.Financer.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace INRFS.Financer.Infrastructure;

public sealed class NotificationReminderWorker(
    IServiceScopeFactory scopeFactory,
    ILogger<NotificationReminderWorker> logger
) : BackgroundService
{
    public static void ApplyOverdueTransitions(IEnumerable<PaymentSchedule> schedules, IEnumerable<ServiceChargeInvoice> invoices, DateOnly today)
    {
        foreach (var schedule in schedules.Where(x => x.DueDate < today && x.Status is ScheduleStatus.Upcoming or ScheduleStatus.Due))
        {
            schedule.Status = ScheduleStatus.Overdue;
            if (schedule.Loan.Status == LoanStatus.Active) schedule.Loan.Status = LoanStatus.Overdue;
        }
        foreach (var invoice in invoices.Where(x => x.DueDate < today && x.Status is ScheduleStatus.Upcoming or ScheduleStatus.Due or ScheduleStatus.PartiallyPaid))
            invoice.Status = ScheduleStatus.Overdue;
    }
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await GenerateAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to generate scheduled in-app notifications");
            }

            await Task.Delay(TimeSpan.FromHours(1), stoppingToken);
        }
    }

    private async Task GenerateAsync(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FinancerDbContext>();
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var collectionSettings = await db.Settings.AsNoTracking()
            .Where(x => x.Scope == "Platform" && (x.Key == "CollectionReminderDaysBefore" || x.Key == "CollectionEscalationDays"))
            .ToDictionaryAsync(x => x.Key, x => x.Value, ct);
        var reminderDays = collectionSettings.TryGetValue("CollectionReminderDaysBefore", out var reminderValue) && int.TryParse(reminderValue, out var parsedReminder) ? Math.Clamp(parsedReminder, 0, 30) : 1;
        var escalationDays = collectionSettings.TryGetValue("CollectionEscalationDays", out var escalationValue) && int.TryParse(escalationValue, out var parsedEscalation) ? Math.Clamp(parsedEscalation, 1, 365) : 3;
        var upcomingThrough = today.AddDays(reminderDays);

        var schedules = await db.PaymentSchedules
            .Include(x => x.Loan).ThenInclude(x => x.Customer)
            .Where(x => x.Status != ScheduleStatus.Paid && x.Status != ScheduleStatus.Waived && x.DueDate <= upcomingThrough)
            .ToListAsync(ct);

        foreach (var schedule in schedules)
        {
            var overdue = schedule.DueDate < today;
            var type = overdue ? "Overdue" : "Loans";
            var reminderKey = overdue ? "PaymentOverdue" : "PaymentDueUpcoming";
            if (await ExistsAsync(db, nameof(PaymentSchedule), schedule.Id, reminderKey, ct))
                continue;

            var notification = new Notification
            {
                FinancerId = schedule.Loan.FinancerId,
                Title = overdue ? "Loan payment overdue" : "Loan payment due soon",
                Message = overdue
                    ? $"Installment {schedule.InstallmentNumber} for loan {schedule.Loan.LoanNumber} was due on {schedule.DueDate:dd MMM yyyy}."
                    : $"Installment {schedule.InstallmentNumber} for loan {schedule.Loan.LoanNumber} is due on {schedule.DueDate:dd MMM yyyy}.",
                Type = type,
                Channel = NotificationChannel.InApp,
                EntityType = nameof(PaymentSchedule),
                EntityId = schedule.Id,
                DeliveryReference = reminderKey,
                SentAt = DateTimeOffset.UtcNow,
            };
            db.Notifications.Add(notification);
            var phone = schedule.Loan.Customer.Phone;
            db.SmsDeliveries.Add(new SmsDelivery
            {
                FinancerId = schedule.Loan.FinancerId,
                CustomerId = schedule.Loan.CustomerId,
                NotificationId = notification.Id,
                DestinationMasked = phone.Length > 4 ? $"***{phone[^4..]}" : "***",
                MessageType = reminderKey,
                Status = "Queued",
            });
        }

        var dueLoans = schedules.Where(x => x.DueDate <= upcomingThrough && x.Loan.AdminCollectionMonitoring).GroupBy(x => x.LoanId).ToList();
        var dueLoanIds = dueLoans.Select(x => x.Key).ToList();
        var existingCases = await db.CollectionCases.Include(x => x.Activities)
            .Where(x => dueLoanIds.Contains(x.LoanId)).ToDictionaryAsync(x => x.LoanId, ct);
        var agents = await db.Users.AsNoTracking().Include(x => x.UserRoles).ThenInclude(x => x.Role)
            .Where(x => x.Status == AccountStatus.Active && x.FinancerId == null && x.UserRoles.Any(r => r.Role.Name == "CollectionAgent"))
            .OrderBy(x => x.FirstName).ThenBy(x => x.LastName).ToListAsync(ct);
        var assignmentCounts = await db.CollectionCases.Where(x => x.AssignedTo.HasValue && x.Status != CollectionStatus.Closed && x.Status != CollectionStatus.Collected)
            .GroupBy(x => x.AssignedTo!.Value).Select(x => new { Agent = x.Key, Count = x.Count() }).ToDictionaryAsync(x => x.Agent, x => x.Count, ct);

        foreach (var group in dueLoans)
        {
            var loan = group.First().Loan;
            var dueAmount = group.Sum(x => x.PrincipalDue + x.InterestDue + x.FeesDue - x.AmountPaid);
            var overdueAmount = group.Where(x => x.DueDate < today).Sum(x => x.PrincipalDue + x.InterestDue + x.FeesDue - x.AmountPaid);
            var daysPastDue = group.Where(x => x.DueDate < today).Select(x => today.DayNumber - x.DueDate.DayNumber).DefaultIfEmpty(0).Max();
            if (!existingCases.TryGetValue(group.Key, out var collectionCase))
            {
                var agent = agents.OrderBy(x => assignmentCounts.GetValueOrDefault(x.Id)).ThenBy(x => x.Id).FirstOrDefault();
                collectionCase = new CollectionCase { LoanId = group.Key, DueAmount = dueAmount, OverdueAmount = overdueAmount, DaysPastDue = daysPastDue, AssignedTo = agent?.Id };
                collectionCase.Activities.Add(new CollectionActivity { Type = "CaseCreated", Notes = agent is null ? "Collection case created automatically; awaiting assignment." : $"Collection case created and assigned to {agent.FirstName} {agent.LastName}." });
                db.CollectionCases.Add(collectionCase);
                existingCases[group.Key] = collectionCase;
                if (agent is not null) assignmentCounts[agent.Id] = assignmentCounts.GetValueOrDefault(agent.Id) + 1;
            }
            else
            {
                collectionCase.DueAmount = dueAmount;
                collectionCase.OverdueAmount = overdueAmount;
                collectionCase.DaysPastDue = daysPastDue;
            }
            if ((daysPastDue >= escalationDays || collectionCase.PromiseToPayDate < today) && collectionCase.Status != CollectionStatus.Escalated)
            {
                collectionCase.Status = CollectionStatus.Escalated;
                collectionCase.Activities.Add(new CollectionActivity { Type = "Escalated", Notes = collectionCase.PromiseToPayDate < today ? "Promise-to-pay date was missed." : $"Automatically escalated after {daysPastDue} overdue days." });
            }
            if (collectionCase.NextFollowUpDate <= today)
            {
                var followUpKey = $"CollectionFollowUp:{collectionCase.NextFollowUpDate:yyyyMMdd}";
                if (!await ExistsAsync(db, nameof(CollectionCase), collectionCase.Id, followUpKey, ct))
                    db.Notifications.Add(new Notification
                    {
                        FinancerId = loan.FinancerId,
                        UserId = collectionCase.AssignedTo,
                        Title = "Collection follow-up due",
                        Message = $"Follow up with {loan.Customer.FullName} for loan {loan.LoanNumber}.",
                        Type = "Collections",
                        Channel = NotificationChannel.InApp,
                        EntityType = nameof(CollectionCase),
                        EntityId = collectionCase.Id,
                        DeliveryReference = followUpKey,
                        SentAt = DateTimeOffset.UtcNow,
                    });
            }
        }

        var invoices = await db.ServiceChargeInvoices
            .Where(x => x.Status != ScheduleStatus.Paid && x.Status != ScheduleStatus.Waived && x.DueDate <= upcomingThrough)
            .ToListAsync(ct);

        ApplyOverdueTransitions(schedules, invoices, today);

        foreach (var invoice in invoices)
        {
            var overdue = invoice.DueDate < today;
            var reminderKey = overdue ? "ServiceChargeOverdue" : "ServiceChargeDueUpcoming";
            if (await ExistsAsync(db, nameof(ServiceChargeInvoice), invoice.Id, reminderKey, ct))
                continue;

            db.Notifications.Add(new Notification
            {
                FinancerId = invoice.FinancerId,
                Title = overdue ? "Service-charge invoice overdue" : "Service-charge invoice due soon",
                Message = $"Invoice {invoice.InvoiceNumber} for ₹{invoice.ChargeAmount - invoice.CollectedAmount:N2} is {(overdue ? "overdue" : $"due on {invoice.DueDate:dd MMM yyyy}")}.",
                Type = "Service Charges",
                Channel = NotificationChannel.InApp,
                EntityType = nameof(ServiceChargeInvoice),
                EntityId = invoice.Id,
                DeliveryReference = reminderKey,
                SentAt = DateTimeOffset.UtcNow,
            });
        }

        await db.SaveChangesAsync(ct);
    }

    private static Task<bool> ExistsAsync(
        FinancerDbContext db,
        string entityType,
        Guid entityId,
        string reminderKey,
        CancellationToken ct
    ) => db.Notifications.AnyAsync(x =>
        x.EntityType == entityType && x.EntityId == entityId && x.DeliveryReference == reminderKey,
        ct);
}
