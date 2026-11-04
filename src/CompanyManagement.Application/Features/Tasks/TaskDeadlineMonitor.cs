using CompanyManagement.Application.Common;
using CompanyManagement.Domain.Entities;
using CompanyManagement.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CompanyManagement.Application.Features.Tasks;

public interface ITaskDeadlineMonitor
{
    /// <summary>Sends "deadline approaching" and "overdue" notifications once per task.</summary>
    Task<(int Reminders, int Overdue)> RunAsync(CancellationToken ct = default);
}

public class TaskDeadlineMonitor(
    IApplicationDbContext db, ICompanyClock clock, INotificationService notifications, ILogger<TaskDeadlineMonitor> logger) : ITaskDeadlineMonitor
{
    public async Task<(int Reminders, int Overdue)> RunAsync(CancellationToken ct = default)
    {
        var settings = await db.CompanySettings.AsNoTracking().OrderBy(s => s.Id).FirstOrDefaultAsync(ct) ?? new CompanySettings();
        var now = clock.UtcNow;
        var horizon = now.AddHours(settings.TaskDeadlineReminderHours);
        var open = new[] { TaskItemStatus.New, TaskItemStatus.Assigned, TaskItemStatus.InProgress, TaskItemStatus.Rejected };

        var upcoming = await db.Tasks.Include(t => t.Assignments)
            .Where(t => open.Contains(t.Status) && t.DueDate != null && t.DueDate >= now && t.DueDate <= horizon && t.DeadlineReminderSentAt == null)
            .ToListAsync(ct);
        foreach (var t in upcoming)
        {
            var hours = Math.Max(1, (int)Math.Round((t.DueDate!.Value - now).TotalHours));
            notifications.AddForEmployees(t.Assignments.Select(a => a.EmployeeId), NotificationType.TaskDeadlineApproaching,
                "Deadline approaching", $"\"{t.Title}\" is due in about {hours} hour(s).",
                "اقتراب موعد التسليم", $"موعد تسليم المهمة \"{t.Title}\" خلال {hours} ساعة تقريباً.", $"/tasks/{t.Id}", "Task", t.Id);
            t.DeadlineReminderSentAt = now;
        }

        var overdue = await db.Tasks.Include(t => t.Assignments)
            .Where(t => t.Status != TaskItemStatus.Completed && t.Status != TaskItemStatus.Cancelled && t.Status != TaskItemStatus.PendingReview
                        && t.DueDate != null && t.DueDate < now && t.OverdueNotifiedAt == null)
            .ToListAsync(ct);
        foreach (var t in overdue)
        {
            notifications.AddForEmployees(t.Assignments.Select(a => a.EmployeeId), NotificationType.TaskOverdue, "Task overdue",
                $"\"{t.Title}\" is past its due date.", "مهمة متأخرة", $"تجاوزت المهمة \"{t.Title}\" موعد التسليم.", $"/tasks/{t.Id}", "Task", t.Id);
            notifications.Add(t.AssignedByUserId, NotificationType.TaskOverdue, "Task overdue",
                $"\"{t.Title}\" assigned by you is overdue.", "مهمة متأخرة", $"المهمة \"{t.Title}\" التي أسندتها متأخرة.", $"/tasks/{t.Id}", "Task", t.Id);
            t.OverdueNotifiedAt = now;
            db.TaskHistory.Add(new TaskHistory { TaskId = t.Id, Action = "Overdue", Details = $"Due {t.DueDate:yyyy-MM-dd HH:mm} UTC" });
        }

        if (upcoming.Count + overdue.Count > 0)
        {
            try { await notifications.SaveAndDispatchAsync(ct); }
            catch (DbUpdateConcurrencyException ex) { logger.LogWarning(ex, "Task deadline monitor hit a concurrency conflict; will retry."); return (0, 0); }
            logger.LogInformation("Task deadline monitor: {Reminders} reminders, {Overdue} overdue", upcoming.Count, overdue.Count);
        }
        return (upcoming.Count, overdue.Count);
    }
}
