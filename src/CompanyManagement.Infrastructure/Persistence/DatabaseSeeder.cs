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
/// Applies migrations and seeds required reference data (roles, settings, default schedule).
/// No user accounts are created here: they live only in the database and are managed from the Users page.
/// </summary>
public class DatabaseSeeder(
    AppDbContext db,
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
        await clock.GetTimeZoneAsync(ct); // primes the company time zone used to read attendance times
        await WarnIfNoAccountsAsync(ct);
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

    private async Task WarnIfNoAccountsAsync(CancellationToken ct)
    {
        if (!await db.Users.AnyAsync(ct))
            logger.LogWarning("No user accounts exist yet. Create the first administrator with: " +
                              "dotnet run --project src/CompanyManagement.API -- create-admin");
    }

    /// <summary>
    /// One-time bootstrap for a fresh database: creates a Super Admin from values typed at the console.
    /// Nothing is read from code or configuration; afterwards accounts are managed in the Users page.
    /// </summary>
    public static async Task<int> CreateAdminInteractiveAsync(IServiceProvider services, CancellationToken ct = default)
    {
        using var scope = services.CreateScope();
        var seeder = scope.ServiceProvider.GetRequiredService<DatabaseSeeder>();
        await seeder.SeedAsync(ct);
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();

        Console.Write("Administrator email: ");
        var email = Console.ReadLine()?.Trim();
        Console.Write("Full name: ");
        var fullName = Console.ReadLine()?.Trim();
        var password = ReadSecret("Password: ");
        var confirm = ReadSecret("Confirm password: ");
        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(fullName) || string.IsNullOrEmpty(password))
        {
            Console.Error.WriteLine("Email, full name and password are required.");
            return 1;
        }
        if (password != confirm)
        {
            Console.Error.WriteLine("Passwords do not match.");
            return 1;
        }
        if (await userManager.FindByEmailAsync(email) is not null)
        {
            Console.Error.WriteLine($"A user with email {email} already exists. Reset its password from the Users page instead.");
            return 1;
        }

        var admin = new ApplicationUser
        {
            UserName = email, Email = email, FullName = fullName, EmailConfirmed = true, IsActive = true, LockoutEnabled = true
        };
        var result = await userManager.CreateAsync(admin, password);
        if (!result.Succeeded)
        {
            Console.Error.WriteLine("Could not create the account: " + string.Join(" ", result.Errors.Select(e => e.Description)));
            return 1;
        }
        await userManager.AddToRoleAsync(admin, Roles.SuperAdmin);
        Console.WriteLine($"Super Admin {email} created. Sign in and manage all other accounts from the Users page.");
        return 0;
    }

    private static string ReadSecret(string prompt)
    {
        Console.Write(prompt);
        if (Console.IsInputRedirected) return Console.ReadLine() ?? "";
        var chars = new List<char>();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter) break;
            if (key.Key == ConsoleKey.Backspace) { if (chars.Count > 0) chars.RemoveAt(chars.Count - 1); continue; }
            if (!char.IsControl(key.KeyChar)) chars.Add(key.KeyChar);
        }
        Console.WriteLine();
        return new string([.. chars]);
    }
}
