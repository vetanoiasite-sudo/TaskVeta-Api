using CompanyManagement.Application.Features.Attendance;
using CompanyManagement.Application.Features.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CompanyManagement.Infrastructure.BackgroundJobs;

/// <summary>Periodically finalizes attendance days (absences, missing check-outs).</summary>
public class AttendanceMonitorWorker(IServiceScopeFactory scopes, IConfiguration config, ILogger<AttendanceMonitorWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var minutes = Math.Max(1, config.GetValue("Jobs:AttendanceMonitorMinutes", 15));
        await Task.Delay(TimeSpan.FromSeconds(20), stoppingToken);
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(minutes));
        do
        {
            try
            {
                using var scope = scopes.CreateScope();
                await scope.ServiceProvider.GetRequiredService<IAttendanceMonitor>().RunAsync(3, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { logger.LogError(ex, "Attendance monitor run failed"); }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}

/// <summary>Periodically sends task deadline reminders and overdue notifications.</summary>
public class TaskDeadlineWorker(IServiceScopeFactory scopes, IConfiguration config, ILogger<TaskDeadlineWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var minutes = Math.Max(1, config.GetValue("Jobs:TaskDeadlineMinutes", 10));
        await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(minutes));
        do
        {
            try
            {
                using var scope = scopes.CreateScope();
                await scope.ServiceProvider.GetRequiredService<ITaskDeadlineMonitor>().RunAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { logger.LogError(ex, "Task deadline monitor run failed"); }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
