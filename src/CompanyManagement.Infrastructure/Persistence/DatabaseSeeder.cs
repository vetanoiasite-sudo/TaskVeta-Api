using CompanyManagement.Application.Common;
using CompanyManagement.Application.Features.Settings;
using CompanyManagement.Domain.Entities;
using CompanyManagement.Domain.Enums;
using CompanyManagement.Domain.Security;
using CompanyManagement.Domain.Services;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace CompanyManagement.Infrastructure.Persistence;

/// <summary>
/// Applies migrations and seeds required reference data. Credentials are read from configuration
/// (user-secrets / environment variables) and are never hard-coded.
/// </summary>
public class DatabaseSeeder(
    AppDbContext db,
    UserManager<ApplicationUser> users,
    RoleManager<ApplicationRole> roles,
    IConfiguration config,
    ICompanyClock clock,
    ILogger<DatabaseSeeder> logger)
{
    public static async Task RunAsync(IServiceProvider services, CancellationToken ct = default)
    {
        using var scope = services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<DatabaseSeeder>().SeedAsync(ct);
    }

    public async Task SeedAsync(CancellationToken ct = default)
    {
        if (config.GetValue("Database:ApplyMigrationsOnStartup", true) && db.Database.IsRelational())
            await db.Database.MigrateAsync(ct);

        await SeedRolesAsync();
        await SeedSettingsAsync(ct);
        await SeedScheduleAsync(ct);
        var admin = await SeedSuperAdminAsync();
        if (config.GetValue("Seed:DemoData", false)) await SeedDemoAsync(admin, ct);
    }

    private async Task SeedRolesAsync()
    {
        var descriptions = new Dictionary<string, string>
        {
            [Roles.SuperAdmin] = "Full access to the entire system",
            [Roles.Admin] = "HR / administration: attendance for all employees, corrections, reports, tasks",
            [Roles.Manager] = "Manager / team leader: attendance and tasks of assigned department or teams",
            [Roles.Employee] = "Employee: own attendance and assigned tasks",
        };
        foreach (var (name, description) in descriptions)
            if (!await roles.RoleExistsAsync(name))
                await roles.CreateAsync(new ApplicationRole(name, description));
    }

    private async Task SeedSettingsAsync(CancellationToken ct)
    {
        if (await db.CompanySettings.AnyAsync(ct)) return;
        var tz = config["Company:TimeZoneId"];
        db.CompanySettings.Add(new CompanySettings
        {
            CompanyName = config["Company:Name"] ?? "Company HQ",
            TimeZoneId = !string.IsNullOrWhiteSpace(tz) && TimeZoneInfo.TryFindSystemTimeZoneById(tz, out _) ? tz : TimeZoneInfo.Local.Id,
            QrSecret = SettingsService.NewSecret(),
        });
        await db.SaveChangesAsync(ct);
    }

    private async Task SeedScheduleAsync(CancellationToken ct)
    {
        if (await db.WorkSchedules.AnyAsync(ct)) return;
        db.WorkSchedules.Add(new WorkSchedule
        {
            Name = "Standard (09:00–17:00)", StartTime = new TimeOnly(9, 0), EndTime = new TimeOnly(17, 0), LateGraceMinutes = 15,
            EarlyLeaveGraceMinutes = 10, HalfDayThresholdHours = 4, IsDefault = true,
            WeekendDaysMask = WorkSchedule.ToMask(ParseWeekend(config["Company:WeekendDays"]))
        });
        await db.SaveChangesAsync(ct);
    }

    private static IEnumerable<DayOfWeek> ParseWeekend(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? [DayOfWeek.Friday, DayOfWeek.Saturday]
            : value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(Enum.Parse<DayOfWeek>);

    private async Task<ApplicationUser?> SeedSuperAdminAsync()
    {
        var email = config["Seed:SuperAdmin:Email"];
        var password = config["Seed:SuperAdmin:Password"];
        if (string.IsNullOrWhiteSpace(email))
        {
            logger.LogWarning("Seed:SuperAdmin:Email is not configured; skipping Super Admin seeding.");
            return null;
        }

        var existing = await users.FindByEmailAsync(email);
        if (existing is not null)
        {
            if (!await users.IsInRoleAsync(existing, Roles.SuperAdmin)) await users.AddToRoleAsync(existing, Roles.SuperAdmin);
            return existing;
        }

        if (string.IsNullOrWhiteSpace(password))
        {
            logger.LogWarning("Seed:SuperAdmin:Password is not configured; Super Admin '{Email}' was not created. " +
                              "Set it with: dotnet user-secrets set \"Seed:SuperAdmin:Password\" \"<strong password>\"", email);
            return null;
        }

        var admin = new ApplicationUser
        {
            UserName = email, Email = email, FullName = config["Seed:SuperAdmin:FullName"] ?? "System Administrator",
            EmailConfirmed = true, IsActive = true, LockoutEnabled = true
        };
        var result = await users.CreateAsync(admin, password);
        if (!result.Succeeded)
        {
            logger.LogError("Failed to create Super Admin: {Errors}", string.Join("; ", result.Errors.Select(e => e.Description)));
            return null;
        }
        await users.AddToRoleAsync(admin, Roles.SuperAdmin);
        logger.LogInformation("Super Admin account {Email} created.", email);
        return admin;
    }

    // ------------------------------------------------------------------ demo data (optional)

    private async Task SeedDemoAsync(ApplicationUser? admin, CancellationToken ct)
    {
        if (await db.Departments.AnyAsync(ct)) return;
        var password = config["Seed:DemoPassword"];
        if (string.IsNullOrWhiteSpace(password))
        {
            logger.LogWarning("Seed:DemoData is enabled but Seed:DemoPassword is not set; demo accounts will not be created.");
        }
        logger.LogInformation("Seeding demo data...");

        var schedule = await db.WorkSchedules.FirstAsync(w => w.IsDefault, ct);
        var depts = new[]
        {
            new Department { Name = "Administration", NameAr = "الإدارة", Code = "ADM" },
            new Department { Name = "Human Resources", NameAr = "الموارد البشرية", Code = "HR" },
            new Department { Name = "Information Technology", NameAr = "تقنية المعلومات", Code = "IT" },
            new Department { Name = "Finance", NameAr = "المالية", Code = "FIN" },
        };
        db.Departments.AddRange(depts);
        await db.SaveChangesAsync(ct);

        var today = await clock.TodayAsync(ct);
        var people = new (string Code, string First, string Last, string Dept, string Title, string Role)[]
        {
            ("E1001", "Sara", "Al-Harbi", "HR", "HR Manager", Roles.Admin),
            ("E1002", "Omar", "Haddad", "IT", "IT Manager", Roles.Manager),
            ("E1003", "Lina", "Nasser", "IT", "Software Engineer", Roles.Employee),
            ("E1004", "Yousef", "Khalil", "IT", "Software Engineer", Roles.Employee),
            ("E1005", "Maha", "Saleh", "IT", "QA Engineer", Roles.Employee),
            ("E1006", "Khaled", "Mansour", "FIN", "Finance Manager", Roles.Manager),
            ("E1007", "Noor", "Ibrahim", "FIN", "Accountant", Roles.Employee),
            ("E1008", "Faisal", "Qasim", "FIN", "Accountant", Roles.Employee),
            ("E1009", "Rana", "Aziz", "ADM", "Office Coordinator", Roles.Employee),
            ("E1010", "Hadi", "Sabbagh", "HR", "HR Specialist", Roles.Employee),
        };

        var employees = new List<Employee>();
        foreach (var p in people)
        {
            var emp = new Employee
            {
                EmployeeCode = p.Code, FirstName = p.First, LastName = p.Last, Email = $"{p.First.ToLowerInvariant()}.{p.Last.ToLowerInvariant().Replace("-", "")}@company.local",
                JobTitle = p.Title, HireDate = today.AddYears(-1), DepartmentId = depts.First(d => d.Code == p.Dept).Id,
                WorkScheduleId = schedule.Id, IsActive = true
            };
            if (!string.IsNullOrWhiteSpace(password))
            {
                var user = new ApplicationUser { UserName = emp.Email, Email = emp.Email, FullName = emp.FullName, EmailConfirmed = true, IsActive = true, LockoutEnabled = true };
                var res = await users.CreateAsync(user, password);
                if (res.Succeeded)
                {
                    await users.AddToRoleAsync(user, p.Role);
                    emp.UserId = user.Id;
                }
            }
            employees.Add(emp);
        }
        db.Employees.AddRange(employees);
        await db.SaveChangesAsync(ct);

        Employee E(string code) => employees.First(e => e.EmployeeCode == code);
        depts.First(d => d.Code == "HR").ManagerEmployeeId = E("E1001").Id;
        depts.First(d => d.Code == "IT").ManagerEmployeeId = E("E1002").Id;
        depts.First(d => d.Code == "FIN").ManagerEmployeeId = E("E1006").Id;

        var dev = new Team { Name = "Development", NameAr = "التطوير", DepartmentId = depts.First(d => d.Code == "IT").Id, LeaderEmployeeId = E("E1002").Id };
        var accounting = new Team { Name = "Accounting", NameAr = "المحاسبة", DepartmentId = depts.First(d => d.Code == "FIN").Id, LeaderEmployeeId = E("E1006").Id };
        db.Teams.AddRange(dev, accounting);
        await db.SaveChangesAsync(ct);
        foreach (var code in new[] { "E1003", "E1004", "E1005" }) db.TeamMembers.Add(new TeamMember { TeamId = dev.Id, EmployeeId = E(code).Id, JoinedOn = today.AddYears(-1) });
        foreach (var code in new[] { "E1007", "E1008" }) db.TeamMembers.Add(new TeamMember { TeamId = accounting.Id, EmployeeId = E(code).Id, JoinedOn = today.AddYears(-1) });

        // Two weeks of realistic attendance history.
        var tz = await clock.GetTimeZoneAsync(ct);
        var rnd = new Random(42);
        for (var d = today.AddDays(-14); d < today; d = d.AddDays(1))
        {
            if (schedule.IsWeekend(d.DayOfWeek)) continue;
            foreach (var e in employees)
            {
                var roll = rnd.NextDouble();
                if (roll < 0.06) continue; // absent -> the monitor job will materialize it
                var inMinutes = roll < 0.18 ? rnd.Next(20, 70) : rnd.Next(-20, 12);
                var outMinutes = roll > 0.92 ? -rnd.Next(30, 120) : rnd.Next(-5, 45);
                var rec = new AttendanceRecord
                {
                    EmployeeId = e.Id, AttendanceDate = d, Status = AttendanceStatus.Present, CheckInSource = AttendanceSource.Web,
                    CheckInTime = clock.ToCompanyTime(d, schedule.StartTime.AddMinutes(inMinutes), tz),
                    CheckOutTime = clock.ToCompanyTime(d, schedule.EndTime.AddMinutes(outMinutes), tz),
                };
                AttendanceCalculator.Recalculate(rec, schedule);
                db.AttendanceRecords.Add(rec);
            }
        }

        if (admin is not null)
        {
            var now = clock.UtcNow;
            var samples = new (string Title, TaskPriority P, TaskItemStatus S, int DueDays, string[] Codes)[]
            {
                ("Prepare monthly attendance report", TaskPriority.High, TaskItemStatus.InProgress, 3, ["E1010"]),
                ("Upgrade office Wi-Fi access points", TaskPriority.Medium, TaskItemStatus.Assigned, 7, ["E1003", "E1004"]),
                ("Fix login page accessibility issues", TaskPriority.Urgent, TaskItemStatus.PendingReview, 1, ["E1005"]),
                ("Quarter-end reconciliation", TaskPriority.High, TaskItemStatus.InProgress, -1, ["E1007", "E1008"]),
                ("Organize onboarding for new hires", TaskPriority.Low, TaskItemStatus.Completed, -3, ["E1009"]),
            };
            foreach (var s in samples)
            {
                var assignees = s.Codes.Select(E).ToList();
                var task = new TaskItem
                {
                    Title = s.Title, Description = $"Demo task: {s.Title}.", Priority = s.P, Status = s.S, AssignedByUserId = admin.Id,
                    AssignedToEmployeeId = assignees[0].Id, DepartmentId = assignees[0].DepartmentId, StartDate = now.AddDays(-5),
                    DueDate = now.AddDays(s.DueDays), ProgressPercent = s.S switch { TaskItemStatus.Completed or TaskItemStatus.PendingReview => 100, TaskItemStatus.InProgress => 50, _ => 0 },
                    CompletionDate = s.S == TaskItemStatus.Completed ? now.AddDays(-4) : null
                };
                foreach (var a in assignees) task.Assignments.Add(new TaskAssignment { EmployeeId = a.Id, AssignedByUserId = admin.Id });
                task.History.Add(new TaskHistory { ChangedByUserId = admin.Id, Action = "Created", ToStatus = TaskItemStatus.Assigned });
                if (s.S != TaskItemStatus.Assigned)
                    task.History.Add(new TaskHistory { ChangedByUserId = admin.Id, Action = "StatusChanged", FromStatus = TaskItemStatus.Assigned, ToStatus = s.S });
                db.Tasks.Add(task);
            }
        }

        db.Holidays.Add(new Holiday { Name = "Company Foundation Day", NameAr = "يوم تأسيس الشركة", StartDate = today.AddDays(20), EndDate = today.AddDays(20) });
        await db.SaveChangesAsync(ct);
        logger.LogInformation("Demo data seeded: {Count} employees.", employees.Count);
    }
}
