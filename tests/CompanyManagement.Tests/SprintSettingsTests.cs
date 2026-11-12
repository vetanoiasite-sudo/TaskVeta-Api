using CompanyManagement.Application.Features.Projects;
using CompanyManagement.Application.Features.Settings;
using CompanyManagement.Application.Features.Sprints;
using CompanyManagement.Domain.Entities;
using CompanyManagement.Domain.Enums;
using CompanyManagement.Domain.Security;

namespace CompanyManagement.Tests;

/// <summary>Sprint length and the optional capacity target are company settings (no fixed 2 weeks or 60/25/15).</summary>
public partial class DevProcessTests
{
    private static UpdateSettingsRequest SettingsWith(int sprintDays, CapacityTargetDto? target) =>
        new("Vetanoia", "UTC", false, 1, 24, 120, VerificationPolicy.Any, true, false, null, null, 150, 100, false, null, false, 30,
            sprintDays, target);

    [Fact]
    public void Sprints_are_one_week_and_have_no_capacity_target_by_default()
    {
        var s = new CompanySettings();
        Assert.Equal(7, s.SprintLengthDays);
        Assert.Null(CapacityTargetDto.From(s));
    }

    [Fact]
    public void Capacity_target_must_add_up_to_100_and_sprint_length_is_1_to_30_days()
    {
        var v = new UpdateSettingsRequestValidator();
        Assert.True(v.Validate(SettingsWith(7, null)).IsValid);
        Assert.True(v.Validate(SettingsWith(14, new CapacityTargetDto(50, 30, 20))).IsValid);
        Assert.False(v.Validate(SettingsWith(7, new CapacityTargetDto(60, 25, 10))).IsValid);
        Assert.False(v.Validate(SettingsWith(7, new CapacityTargetDto(120, -10, -10))).IsValid);
        Assert.False(v.Validate(SettingsWith(0, null)).IsValid);
        Assert.False(v.Validate(SettingsWith(31, null)).IsValid);
    }

    [Fact]
    public async Task Sprint_report_shows_the_configured_target_or_none()
    {
        var w = new World();
        var manager = w.As(Manager, Roles.Manager);
        var sprint = await manager.Sprints.CreateAsync(new SaveSprintRequest("S", null, new DateOnly(2026, 11, 15), new DateOnly(2026, 11, 21)));
        Assert.Null((await manager.Sprints.GetReportAsync(sprint.Id)).CapacityTarget);

        using (var db = w.NewContext())
        {
            var settings = new SettingsService(db, new NoAudit(), new FakeClock(w.Time));
            var saved = await settings.UpdateAsync(SettingsWith(7, new CapacityTargetDto(70, 20, 10)));
            Assert.Equal(new CapacityTargetDto(70, 20, 10), saved.CapacityTarget);
            Assert.Equal(7, (await settings.GetPublicAsync()).SprintLengthDays);
        }
        Assert.Equal(new CapacityTargetDto(70, 20, 10), (await w.As(Manager, Roles.Manager).Sprints.GetReportAsync(sprint.Id)).CapacityTarget);

        using (var db = w.NewContext())
            await new SettingsService(db, new NoAudit(), new FakeClock(w.Time)).UpdateAsync(SettingsWith(10, null));
        Assert.Null((await w.As(Manager, Roles.Manager).Sprints.GetReportAsync(sprint.Id)).CapacityTarget);
    }

    [Fact]
    public async Task Documents_are_listed_in_template_order_with_backend_and_frontend_categories()
    {
        var w = new World();
        var bro = w.As(BrOwner, Roles.Employee).Documents;
        foreach (var c in new[] { ProjectDocumentCategory.Other, ProjectDocumentCategory.Technical, ProjectDocumentCategory.Frontend,
                     ProjectDocumentCategory.Testing, ProjectDocumentCategory.Backend, ProjectDocumentCategory.Design, ProjectDocumentCategory.Discovery })
            await bro.CreateTextAsync(GA, new CreateTextDocumentRequest(c.ToString(), c, "x"));

        var order = (await bro.GetAllAsync(GA)).Select(d => d.Category).ToArray();
        Assert.Equal(new[] { ProjectDocumentCategory.Discovery, ProjectDocumentCategory.Design, ProjectDocumentCategory.Backend,
            ProjectDocumentCategory.Frontend, ProjectDocumentCategory.Testing, ProjectDocumentCategory.Technical, ProjectDocumentCategory.Other }, order);
        Assert.Equal(9, (int)ProjectDocumentCategory.Backend);   // stored as int: existing values never change
        Assert.Equal(10, (int)ProjectDocumentCategory.Frontend);
        Assert.Equal(4, (int)ProjectDocumentCategory.Technical);
    }
}
