using CompanyManagement.Application.Common;
using CompanyManagement.Application.Features.Claude;
using CompanyManagement.Application.Features.Projects;
using CompanyManagement.Application.Features.Sprints;
using CompanyManagement.Application.Features.Tasks;
using CompanyManagement.Domain.Entities;
using CompanyManagement.Domain.Enums;
using CompanyManagement.Domain.Security;
using CompanyManagement.Infrastructure.Persistence;
using CompanyManagement.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace CompanyManagement.Tests;

/// <summary>
/// Task keys, types, project roles, sub-tasks, dependencies, blocked flag and sprints — the features that make
/// TaskVeta follow the company Way of Working. Runs the real services on an in-memory database.
/// </summary>
public partial class DevProcessTests
{
    // ------------------------------------------------------------------ test world

    private const int Admin = 1, BrOwner = 2, FeLead = 3, Dev1 = 4, Dev2 = 5, Outsider = 6, Manager = 7;
    private const int GA = 1, TV = 2;

    private sealed class World
    {
        private readonly string _db = Guid.NewGuid().ToString();
        public readonly FakeTimeProvider Time = new(new DateTimeOffset(2026, 11, 12, 10, 0, 0, TimeSpan.Zero));
        public readonly MemoryStorage Files = new();

        public AppDbContext NewContext(SourceStampInterceptor? interceptor = null)
        {
            var options = new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(_db);
            if (interceptor is not null) options.AddInterceptors(interceptor);
            return new(options.Options, Time);
        }

        public World()
        {
            using var db = NewContext();
            db.Departments.Add(new Department { Id = 1, Name = "Engineering", Code = "ENG" });
            void Person(int id, string name)
            {
                db.Users.Add(new ApplicationUser { Id = id, UserName = name, Email = $"{name}@x.test", FullName = name });
                if (id != Admin) db.Employees.Add(new Employee { Id = id, EmployeeCode = $"E{id}", FirstName = name, LastName = "X", DepartmentId = 1, UserId = id, Email = $"{name}@x.test" });
            }
            Person(Admin, "admin"); Person(BrOwner, "bro"); Person(FeLead, "felead"); Person(Dev1, "dev1");
            Person(Dev2, "dev2"); Person(Outsider, "outsider"); Person(Manager, "manager");
            db.Projects.Add(new Project { Id = GA, Name = "Garas Auto", Code = "GA", Status = ProjectStatus.Active });
            db.Projects.Add(new Project { Id = TV, Name = "TaskVeta", Code = "TV", Status = ProjectStatus.Active });
            db.ProjectMembers.Add(new ProjectMember { ProjectId = GA, EmployeeId = BrOwner, Role = ProjectRole.BrOwner });
            db.ProjectMembers.Add(new ProjectMember { ProjectId = GA, EmployeeId = FeLead, Role = ProjectRole.FrontendLead });
            db.ProjectMembers.Add(new ProjectMember { ProjectId = GA, EmployeeId = Dev1, Role = ProjectRole.Member });
            db.SaveChanges();
        }

        public Actor As(int userId, params string[] roles) => new(this, userId, roles);
        /// <summary>The same person, but acting through Claude (a personal key on /mcp).</summary>
        public Actor ViaClaude(int userId, params string[] roles) => new(this, userId, roles, "Claude");
        public Actor AsAdmin() => As(Admin, Roles.SuperAdmin);
    }

    private sealed class Actor
    {
        public readonly AppDbContext Db;
        public readonly TaskService Tasks;
        public readonly ProjectService Projects;
        public readonly SprintService Sprints;
        public readonly NotificationService Notifications;
        public readonly ProjectDocumentService Documents;
        public readonly PersonalKeyService Keys;

        public Actor(World w, int userId, string[] roles, string? via = null)
        {
            var user = new FakeUser(userId, roles, via);
            Db = w.NewContext(new SourceStampInterceptor(user));
            var scope = new AccessScope(Db, user);
            var access = new ProjectAccess(Db, scope, user);
            var clock = new FakeClock(w.Time);
            Notifications = new NotificationService(Db, new NullRealtime(), NullLogger<NotificationService>.Instance);
            Tasks = new TaskService(Db, scope, access, user, clock, new NoAudit(), Notifications, new NullStorage());
            Projects = new ProjectService(Db, new NoAudit(), Tasks, access, Notifications, user, w.Files);
            Sprints = new SprintService(Db, Tasks, new NoAudit(), user, clock);
            Documents = new ProjectDocumentService(Db, Tasks, access, user, w.Files, new DocumentTextExtractor(NullLogger<DocumentTextExtractor>.Instance), new NoAudit());
            Keys = new PersonalKeyService(Db, user, new TokenService(Microsoft.Extensions.Options.Options.Create(new JwtOptions { SigningKey = new string('k', 40) }), w.Time),
                new NoAudit(), clock);
        }
    }

    private sealed class FakeUser(int id, string[] roles, string? via = null) : ICurrentUser
    {
        public string? Via => via;
        public int? UserId => id;
        public string? UserName => $"user{id}";
        public string? IpAddress => null;
        public bool IsAuthenticated => true;
        public IReadOnlyCollection<string> Roles => roles;
        public bool IsInRole(string role) => roles.Contains(role);
        public bool HasPermission(string permission) => Permissions.ForRoles(roles).Contains(permission);
    }

    private sealed class FakeClock(TimeProvider time) : ICompanyClock
    {
        public DateTime UtcNow => time.GetUtcNow().UtcDateTime;
        public Task<TimeZoneInfo> GetTimeZoneAsync(CancellationToken ct = default) => Task.FromResult(TimeZoneInfo.Utc);
        public Task<DateTimeOffset> NowAsync(CancellationToken ct = default) => Task.FromResult(time.GetUtcNow());
        public Task<DateOnly> TodayAsync(CancellationToken ct = default) => Task.FromResult(DateOnly.FromDateTime(UtcNow));
        public DateTimeOffset ToCompanyTime(DateTime utc, TimeZoneInfo tz) => new(utc, TimeSpan.Zero);
        public DateTimeOffset ToCompanyTime(DateOnly date, TimeOnly time1, TimeZoneInfo tz) => new(date.ToDateTime(time1), TimeSpan.Zero);
        public void InvalidateCache() { }
    }

    private sealed class NoAudit : IAuditService
    {
        public void Log(string action, string entityName, object? entityId, object? oldValues = null, object? newValues = null, string? reason = null) { }
        public Task LogNowAsync(string action, string entityName, object? entityId, object? newValues = null, string? reason = null, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class NullRealtime : IRealtimeNotifier
    {
        public Task PushAsync(int userId, object payload, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class MemoryStorage : IFileStorage
    {
        public readonly Dictionary<string, byte[]> Saved = new();
        public async Task<string> SaveAsync(Stream content, string extension, CancellationToken ct = default)
        {
            using var ms = new MemoryStream();
            await content.CopyToAsync(ms, ct);
            var name = Guid.NewGuid().ToString("N") + extension;
            Saved[name] = ms.ToArray();
            return name;
        }
        public Stream OpenRead(string storedFileName) => new MemoryStream(Saved[storedFileName]);
        public void Delete(string storedFileName) => Saved.Remove(storedFileName);
    }

    private sealed class NullStorage : IFileStorage
    {
        public Task<string> SaveAsync(Stream content, string extension, CancellationToken ct = default) => Task.FromResult("f" + extension);
        public Stream OpenRead(string storedFileName) => new MemoryStream();
        public void Delete(string storedFileName) { }
    }

    private static CreateTaskRequest NewTask(string title, int? project = null, int[]? assignees = null, TaskType type = TaskType.Feature,
        int? parent = null, decimal? estimate = null, int? sprint = null, string[]? labels = null) =>
        new(title, null, TaskPriority.Medium, null, null, null, assignees ?? [], ProjectId: project, Type: type, ParentTaskId: parent,
            EstimateDays: estimate, SprintId: sprint, Labels: labels);

    private static UpdateTaskRequest Edit(TaskDetailsDto d, int? project) => new(d.Task.Title, d.Description, d.Task.Priority, d.Task.StartDate,
        d.Task.DueDate, d.Task.DepartmentId, d.Task.Assignees.Select(a => a.EmployeeId).ToArray(), d.RowVersion, d.Task.TeamId, project,
        d.Task.Type, d.Task.SprintId, d.Task.ParentTaskId, d.Task.EstimateDays, d.Task.Labels, d.Task.StoryRef, d.Task.ScreenRef,
        d.DesignUrl, d.PullRequestUrl);

    // ------------------------------------------------------------------ task keys

    [Theory]
    [InlineData("GA-12", "GA", 12)]
    [InlineData("ga-3", "GA", 3)]
    [InlineData("GA-WEB-7", "GA-WEB", 7)]
    [InlineData("#45", null, 45)]
    [InlineData("45", null, 45)]
    public void Task_keys_parse(string key, string? code, int number)
    {
        Assert.True(TaskService.TryParseKey(key, out var c, out var n));
        Assert.Equal(code, c);
        Assert.Equal(number, n);
    }

    [Theory]
    [InlineData("")]
    [InlineData("GA-")]
    [InlineData("GA-0")]
    [InlineData("GA-x")]
    [InlineData("hello")]
    public void Invalid_task_keys_are_rejected(string key) => Assert.False(TaskService.TryParseKey(key, out _, out _));

    [Fact]
    public async Task Tasks_are_numbered_per_project_and_found_by_key()
    {
        var w = new World();
        var admin = w.AsAdmin();
        var a1 = await admin.Tasks.CreateAsync(NewTask("first", GA));
        var a2 = await admin.Tasks.CreateAsync(NewTask("second", GA));
        var t1 = await admin.Tasks.CreateAsync(NewTask("other project", TV));
        var loose = await admin.Tasks.CreateAsync(NewTask("no project"));

        Assert.Equal("GA-1", a1.Task.Key);
        Assert.Equal("GA-2", a2.Task.Key);
        Assert.Equal("TV-1", t1.Task.Key);
        Assert.Equal($"#{loose.Task.Id}", loose.Task.Key);

        Assert.Equal(a2.Task.Id, (await w.AsAdmin().Tasks.GetByKeyAsync("ga-2")).Task.Id);
        var search = await w.AsAdmin().Tasks.GetPagedAsync(new TaskQuery { Search = "GA-2" });
        Assert.Contains(search.Items, t => t.Id == a2.Task.Id);
        await Assert.ThrowsAsync<NotFoundException>(() => w.AsAdmin().Tasks.GetByKeyAsync("GA-99"));
    }

    [Fact]
    public async Task Moving_a_task_to_another_project_gives_it_a_new_key()
    {
        var w = new World();
        await w.AsAdmin().Tasks.CreateAsync(NewTask("tv task", TV));
        var task = await w.AsAdmin().Tasks.CreateAsync(NewTask("moves", GA));
        Assert.Equal("GA-1", task.Task.Key);

        var moved = await w.AsAdmin().Tasks.UpdateAsync(task.Task.Id, Edit(task, TV));
        Assert.Equal("TV-2", moved.Task.Key);
    }

    // ------------------------------------------------------------------ project roles

    [Fact]
    public async Task Project_leads_create_and_review_tasks_in_their_own_project_only()
    {
        var w = new World();
        // FeLead is a plain Employee globally, but Frontend Lead of GA.
        var created = await w.As(FeLead, Roles.Employee).Tasks.CreateAsync(NewTask("build login", GA, [Dev1]));
        Assert.Equal("GA-1", created.Task.Key);
        Assert.True(created.Permissions.CanReview);

        await Assert.ThrowsAsync<ForbiddenException>(() => w.As(FeLead, Roles.Employee).Tasks.CreateAsync(NewTask("not mine", TV, [Dev1])));
        // Outsider is not a member of GA, so the lead cannot assign them.
        await Assert.ThrowsAsync<ForbiddenException>(() => w.As(FeLead, Roles.Employee).Tasks.CreateAsync(NewTask("x", GA, [Outsider])));

        var dev = w.As(Dev1, Roles.Employee);
        await dev.Tasks.ChangeStatusAsync(created.Task.Id, new UpdateTaskStatusRequest(TaskItemStatus.InProgress, null, null));
        await w.As(Dev1, Roles.Employee).Tasks.ChangeStatusAsync(created.Task.Id, new UpdateTaskStatusRequest(TaskItemStatus.PendingReview, null, null));
        var approved = await w.As(FeLead, Roles.Employee).Tasks.ChangeStatusAsync(created.Task.Id,
            new UpdateTaskStatusRequest(TaskItemStatus.Completed, "good", null, 8));
        Assert.Equal(TaskItemStatus.Completed, approved.Task.Status);
    }

    [Fact]
    public async Task Project_members_see_all_project_tasks_and_outsiders_do_not()
    {
        var w = new World();
        var task = await w.AsAdmin().Tasks.CreateAsync(NewTask("for dev2", GA, [Dev2]));

        var member = await w.As(Dev1, Roles.Employee).Tasks.GetAsync(task.Task.Id);
        Assert.False(member.Permissions.CanEdit); // a plain member can read, not edit
        await Assert.ThrowsAsync<NotFoundException>(() => w.As(Outsider, Roles.Employee).Tasks.GetAsync(task.Task.Id));

        var projects = await w.As(Dev1, Roles.Employee).Projects.GetAllAsync();
        Assert.Contains(projects, p => p.Id == GA && p.MyRoles.Contains(ProjectRole.Member));
        Assert.DoesNotContain(projects, p => p.Id == TV);
    }

    [Fact]
    public async Task Only_admins_creators_and_br_owners_manage_a_project()
    {
        var w = new World();
        var added = await w.As(BrOwner, Roles.Employee).Projects.AddMemberAsync(GA, new AddProjectMemberRequest(Dev2, ProjectRole.BackendLead));
        Assert.Contains(added.Members, m => m.EmployeeId == Dev2 && m.Role == ProjectRole.BackendLead);
        Assert.True(added.CanManage);

        await Assert.ThrowsAsync<ForbiddenException>(() =>
            w.As(FeLead, Roles.Employee).Projects.AddMemberAsync(GA, new AddProjectMemberRequest(Outsider, ProjectRole.Member)));

        // Leads (Leader/Manager) may create projects; the creator then manages it.
        var created = await w.As(Manager, Roles.Manager).Projects.CreateAsync(new SaveProjectRequest("New", null, "nw", null, ProjectStatus.Planned, null, null));
        Assert.Equal("NW", created.Code);
        Assert.True(created.CanManage);
        await Assert.ThrowsAsync<ForbiddenException>(() =>
            w.As(Dev1, Roles.Employee).Projects.CreateAsync(new SaveProjectRequest("No", null, "NO", null, ProjectStatus.Planned, null, null)));
    }

    [Fact]
    public async Task A_project_code_cannot_change_once_it_has_tasks()
    {
        var w = new World();
        await w.AsAdmin().Tasks.CreateAsync(NewTask("t", GA));
        await Assert.ThrowsAsync<BadRequestException>(() => w.AsAdmin().Projects.UpdateAsync(GA,
            new SaveProjectRequest("Garas Auto", null, "GAR", null, ProjectStatus.Active, null, null)));
        var renamed = await w.AsAdmin().Projects.UpdateAsync(GA,
            new SaveProjectRequest("Garas Auto 2", null, "GA", null, ProjectStatus.Active, null, null, "Client", "https://github.com/x/ga-web"));
        Assert.Equal("Client", renamed.ClientName);
    }

    // ------------------------------------------------------------------ sub-tasks & dependencies

    [Fact]
    public async Task Subtasks_are_one_level_deep_and_in_the_parents_project()
    {
        var w = new World();
        var parent = await w.AsAdmin().Tasks.CreateAsync(NewTask("feature", GA));
        var child = await w.AsAdmin().Tasks.CreateAsync(NewTask("api", GA, parent: parent.Task.Id));
        Assert.Equal(parent.Task.Id, child.Task.ParentTaskId);

        await Assert.ThrowsAsync<BadRequestException>(() => w.AsAdmin().Tasks.CreateAsync(NewTask("grandchild", GA, parent: child.Task.Id)));
        await Assert.ThrowsAsync<BadRequestException>(() => w.AsAdmin().Tasks.CreateAsync(NewTask("other project", TV, parent: parent.Task.Id)));

        var details = await w.AsAdmin().Tasks.GetAsync(parent.Task.Id);
        Assert.Single(details.Subtasks);
        Assert.Equal(1, details.Task.SubtaskCount);
    }

    [Fact]
    public async Task Dependencies_are_tracked_and_cycles_rejected()
    {
        var w = new World();
        var api = await w.AsAdmin().Tasks.CreateAsync(NewTask("API", GA, type: TaskType.Feature));
        var screen = await w.AsAdmin().Tasks.CreateAsync(NewTask("Screen", GA));

        var waiting = await w.AsAdmin().Tasks.AddDependencyAsync(screen.Task.Id, new AddDependencyRequest(api.Task.Id));
        Assert.Equal(1, waiting.Task.OpenDependencyCount);
        Assert.Contains(waiting.BlockedBy, t => t.Id == api.Task.Id);
        Assert.Contains((await w.AsAdmin().Tasks.GetAsync(api.Task.Id)).Blocks, t => t.Id == screen.Task.Id);

        await Assert.ThrowsAsync<BadRequestException>(() => w.AsAdmin().Tasks.AddDependencyAsync(api.Task.Id, new AddDependencyRequest(screen.Task.Id)));
        await Assert.ThrowsAsync<BadRequestException>(() => w.AsAdmin().Tasks.AddDependencyAsync(api.Task.Id, new AddDependencyRequest(api.Task.Id)));

        var removed = await w.AsAdmin().Tasks.RemoveDependencyAsync(screen.Task.Id, api.Task.Id);
        Assert.Equal(0, removed.Task.OpenDependencyCount);
    }

    // ------------------------------------------------------------------ blocked

    [Fact]
    public async Task Blocking_notifies_leads_and_is_cleared_when_the_task_is_done()
    {
        var w = new World();
        var task = await w.As(FeLead, Roles.Employee).Tasks.CreateAsync(NewTask("screen", GA, [Dev1]));

        var blocked = await w.As(Dev1, Roles.Employee).Tasks.SetBlockedAsync(task.Task.Id, new SetTaskBlockedRequest(true, "Waiting for API"));
        Assert.True(blocked.Task.IsBlocked);
        Assert.Equal("Waiting for API", blocked.Task.BlockedReason);
        await using (var db = w.NewContext())
            Assert.True(await db.Notifications.AnyAsync(n => n.UserId == FeLead && n.Type == NotificationType.TaskBlocked));

        await Assert.ThrowsAsync<NotFoundException>(() =>
            w.As(Outsider, Roles.Employee).Tasks.SetBlockedAsync(task.Task.Id, new SetTaskBlockedRequest(false, null)));

        var dev = w.As(Dev1, Roles.Employee).Tasks;
        await dev.ChangeStatusAsync(task.Task.Id, new UpdateTaskStatusRequest(TaskItemStatus.InProgress, null, null));
        await w.As(Dev1, Roles.Employee).Tasks.ChangeStatusAsync(task.Task.Id, new UpdateTaskStatusRequest(TaskItemStatus.PendingReview, null, null));
        var done = await w.As(FeLead, Roles.Employee).Tasks.ChangeStatusAsync(task.Task.Id, new UpdateTaskStatusRequest(TaskItemStatus.Completed, null, null, 9));
        Assert.False(done.Task.IsBlocked);
    }

    [Fact]
    public void Blocking_needs_a_reason()
    {
        var v = new SetTaskBlockedRequestValidator();
        Assert.False(v.Validate(new SetTaskBlockedRequest(true, " ")).IsValid);
        Assert.True(v.Validate(new SetTaskBlockedRequest(true, "API not ready")).IsValid);
        Assert.True(v.Validate(new SetTaskBlockedRequest(false, null)).IsValid);
    }

    // ------------------------------------------------------------------ labels & validation

    [Fact]
    public async Task Labels_are_normalized_and_filterable()
    {
        var w = new World();
        await w.AsAdmin().Tasks.CreateAsync(NewTask("a", GA, labels: ["Payments", " payments ", "RTL"]));
        await w.AsAdmin().Tasks.CreateAsync(NewTask("b", GA, labels: ["rtl"]));

        var labels = await w.AsAdmin().Tasks.GetLabelsAsync();
        Assert.Equal(["payments", "rtl"], labels);
        var rtl = await w.AsAdmin().Tasks.GetPagedAsync(new TaskQuery { Label = "RTL" });
        Assert.Equal(2, rtl.TotalCount);
    }

    [Theory]
    [InlineData(0.5, true)]
    [InlineData(3, true)]
    [InlineData(0.25, false)]
    [InlineData(0, false)]
    [InlineData(31, false)]
    public void Estimates_use_half_day_steps(double days, bool valid) =>
        Assert.Equal(valid, new CreateTaskRequestValidator().Validate(NewTask("t", estimate: (decimal)days)).IsValid);

    [Fact]
    public void References_and_links_are_validated()
    {
        var v = new CreateTaskRequestValidator();
        Assert.True(v.Validate(NewTask("t") with { StoryRef = "US-CRM-12", ScreenRef = "SCR-CRM-03-W", DesignUrl = "https://figma.com/x" }).IsValid);
        Assert.False(v.Validate(NewTask("t") with { StoryRef = "story twelve" }).IsValid);
        Assert.False(v.Validate(NewTask("t") with { PullRequestUrl = "javascript:alert(1)" }).IsValid);
        Assert.False(v.Validate(NewTask("t") with { Type = (TaskType)99 }).IsValid);
    }

    // ------------------------------------------------------------------ sprints

    [Fact]
    public async Task One_sprint_is_active_and_completing_it_carries_unfinished_work_over()
    {
        var w = new World();
        var lead = w.As(Manager, Roles.Manager);
        var s1 = await lead.Sprints.CreateAsync(new SaveSprintRequest("Sprint 1", "Login", new DateOnly(2026, 11, 15), new DateOnly(2026, 11, 26)));
        var s2 = await w.As(Manager, Roles.Manager).Sprints.CreateAsync(new SaveSprintRequest("Sprint 2", null, new DateOnly(2026, 11, 29), new DateOnly(2026, 12, 10)));
        await Assert.ThrowsAsync<ConflictException>(() => w.As(Manager, Roles.Manager).Sprints.CreateAsync(
            new SaveSprintRequest("Overlaps", null, new DateOnly(2026, 11, 20), new DateOnly(2026, 11, 30))));

        var done = await w.AsAdmin().Tasks.CreateAsync(NewTask("done", GA, [Dev1], estimate: 2));
        var open = await w.AsAdmin().Tasks.CreateAsync(NewTask("open", GA, [Dev1], estimate: 1));
        // Planning goes through task visibility: the manager doesn't manage Dev1, so they can't plan Dev1's tasks.
        await Assert.ThrowsAsync<NotFoundException>(() => w.As(Manager, Roles.Manager).Sprints.PlanTasksAsync(s1.Id, new PlanTasksRequest([done.Task.Id])));
        await w.AsAdmin().Sprints.PlanTasksAsync(s1.Id, new PlanTasksRequest([done.Task.Id, open.Task.Id]));

        await w.As(Manager, Roles.Manager).Sprints.StartAsync(s1.Id);
        await Assert.ThrowsAsync<ConflictException>(() => w.As(Manager, Roles.Manager).Sprints.StartAsync(s2.Id));
        await Assert.ThrowsAsync<ForbiddenException>(() => w.As(Dev1, Roles.Employee).Sprints.StartAsync(s2.Id));

        await w.As(Dev1, Roles.Employee).Tasks.ChangeStatusAsync(done.Task.Id, new UpdateTaskStatusRequest(TaskItemStatus.InProgress, null, null));
        await w.As(Dev1, Roles.Employee).Tasks.ChangeStatusAsync(done.Task.Id, new UpdateTaskStatusRequest(TaskItemStatus.PendingReview, null, null));
        await w.AsAdmin().Tasks.ChangeStatusAsync(done.Task.Id, new UpdateTaskStatusRequest(TaskItemStatus.Completed, null, null, 7));

        var completed = await w.As(Manager, Roles.Manager).Sprints.CompleteAsync(s1.Id, new CompleteSprintRequest(s2.Id));
        Assert.Equal(SprintStatus.Completed, completed.Status);
        Assert.Equal(s2.Id, (await w.AsAdmin().Tasks.GetAsync(open.Task.Id)).Task.SprintId);
        Assert.Equal(s1.Id, (await w.AsAdmin().Tasks.GetAsync(done.Task.Id)).Task.SprintId);
        await Assert.ThrowsAsync<BadRequestException>(() => w.AsAdmin().Tasks.SetSprintAsync(open.Task.Id, new SetTaskSprintRequest(s1.Id)));
    }

    [Fact]
    public async Task Sprint_report_shows_progress_capacity_split_and_workload()
    {
        var w = new World();
        var s = await w.As(Manager, Roles.Manager).Sprints.CreateAsync(new SaveSprintRequest("S", null, new DateOnly(2026, 11, 15), new DateOnly(2026, 11, 26)));
        await w.AsAdmin().Tasks.CreateAsync(NewTask("feature", GA, [Dev1], TaskType.Feature, estimate: 3, sprint: s.Id));
        await w.AsAdmin().Tasks.CreateAsync(NewTask("bug", GA, [Dev2], TaskType.Bug, estimate: 1, sprint: s.Id));
        var blocked = await w.AsAdmin().Tasks.CreateAsync(NewTask("no estimate", GA, [Dev1], TaskType.TechDebt, sprint: s.Id));
        await w.AsAdmin().Tasks.SetBlockedAsync(blocked.Task.Id, new SetTaskBlockedRequest(true, "waiting"));

        var r = await w.AsAdmin().Sprints.GetReportAsync(s.Id);
        Assert.Equal(3, r.Total);
        Assert.Equal(4, r.EstimateDays);
        Assert.Equal(75, r.CapacitySplit["new"]);
        Assert.Equal(25, r.CapacitySplit["bugs"]);
        Assert.Equal(1, r.Unestimated);
        Assert.Equal(1, r.Blocked);
        Assert.Equal(blocked.Task.Id, r.OpenTasks[0].Id); // blocked work first
        Assert.Contains(r.ByAssignee, a => a.Label == "dev1 X" && a.Tasks == 2 && a.EstimateDays == 3);
    }

    [Fact]
    public async Task Backlog_lists_open_tasks_without_a_sprint()
    {
        var w = new World();
        var s = await w.As(Manager, Roles.Manager).Sprints.CreateAsync(new SaveSprintRequest("S", null, new DateOnly(2026, 11, 15), new DateOnly(2026, 11, 26)));
        await w.AsAdmin().Tasks.CreateAsync(NewTask("planned", GA, sprint: s.Id));
        var backlog = await w.AsAdmin().Tasks.CreateAsync(NewTask("backlog", GA));

        var result = await w.AsAdmin().Tasks.GetPagedAsync(new TaskQuery { Backlog = true });
        Assert.Equal([backlog.Task.Id], result.Items.Select(t => t.Id));
    }

    // ------------------------------------------------------------------ files, voice notes, emoji

    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 0x0D, 0x49, 0x48, 0x44, 0x52];
    private static readonly byte[] Webm = [0x1A, 0x45, 0xDF, 0xA3, 0x9F, 0x42, 0x86, 0x81, 0x01, 0x42, 0xF7, 0x81, 0x01, 0x42, 0xF2, 0x81];

    [Theory]
    [InlineData(".png", AttachmentKind.Image)]
    [InlineData(".heic", AttachmentKind.Image)]
    [InlineData(".webp", AttachmentKind.Image)]
    [InlineData(".m4a", AttachmentKind.Audio)]
    [InlineData(".webm", AttachmentKind.Audio)]
    [InlineData(".mp4", AttachmentKind.Video)]
    [InlineData(".pdf", AttachmentKind.Document)]
    public void Attachment_kinds_follow_the_extension(string ext, AttachmentKind kind)
    {
        Assert.True(AttachmentRules.IsAllowed(ext));
        Assert.Equal(kind, AttachmentRules.KindOf(ext));
    }

    [Fact]
    public void Screen_recordings_in_webm_are_video_and_risky_types_are_refused()
    {
        Assert.Equal(AttachmentKind.Video, AttachmentRules.KindOf(".webm", "video/webm"));
        Assert.Equal("video/webm", AttachmentRules.SafeContentType(".webm", AttachmentKind.Video));
        Assert.False(AttachmentRules.IsAllowed(".svg"));
        Assert.False(AttachmentRules.IsAllowed(".html"));
        Assert.False(AttachmentRules.IsAllowed(".exe"));
        Assert.False(AttachmentRules.IsInlineSafe("application/pdf"));
        Assert.True(AttachmentRules.IsInlineSafe("audio/webm"));
    }

    [Fact]
    public void Media_content_must_match_its_extension()
    {
        Assert.True(AttachmentRules.SignatureMatches(".png", Png));
        Assert.True(AttachmentRules.SignatureMatches(".webm", Webm));
        Assert.False(AttachmentRules.SignatureMatches(".png", "<html><script>"u8.ToArray()));
        Assert.False(AttachmentRules.SignatureMatches(".mp3", "hello world....."u8.ToArray()));
        Assert.True(AttachmentRules.SignatureMatches(".pdf", "anything"u8.ToArray())); // documents are download-only
    }

    [Fact]
    public async Task Fake_images_and_oversized_files_are_rejected()
    {
        var w = new World();
        var task = await w.AsAdmin().Tasks.CreateAsync(NewTask("t", GA, [Dev1]));
        var dev = w.As(Dev1, Roles.Employee).Tasks;
        await Assert.ThrowsAsync<BadRequestException>(() =>
            dev.AddAttachmentAsync(task.Task.Id, "shot.png", "image/png", 20, new MemoryStream("<html>not an image</html>"u8.ToArray())));
        await Assert.ThrowsAsync<BadRequestException>(() =>
            dev.AddAttachmentAsync(task.Task.Id, "big.png", "image/png", AttachmentRules.MaxImageBytes + 1, new MemoryStream(Png)));
        await Assert.ThrowsAsync<BadRequestException>(() =>
            dev.AddAttachmentAsync(task.Task.Id, "page.svg", "image/svg+xml", 10, new MemoryStream(Png)));

        var ok = await w.As(Dev1, Roles.Employee).Tasks.AddAttachmentAsync(task.Task.Id, "shot.png", "text/html", Png.Length, new MemoryStream(Png));
        Assert.Equal("image/png", ok.ContentType); // the server decides the content type, not the browser
        Assert.Equal(AttachmentKind.Image, ok.Kind);
    }

    [Fact]
    public async Task A_voice_note_can_be_sent_as_a_comment_without_text()
    {
        var w = new World();
        var task = await w.AsAdmin().Tasks.CreateAsync(NewTask("t", GA, [Dev1]));
        var voice = await w.As(Dev1, Roles.Employee).Tasks.AddAttachmentAsync(task.Task.Id, "voice.webm", "audio/webm;codecs=opus", Webm.Length,
            new MemoryStream(Webm), durationSeconds: 12);
        Assert.Equal(AttachmentKind.Audio, voice.Kind);
        Assert.Equal(12, voice.DurationSeconds);

        Assert.False(new AddCommentRequestValidator().Validate(new AddCommentRequest("", null, null)).IsValid);
        Assert.True(new AddCommentRequestValidator().Validate(new AddCommentRequest("", null, [voice.Id])).IsValid);

        var comment = await w.As(Dev1, Roles.Employee).Tasks.AddCommentAsync(task.Task.Id, new AddCommentRequest(null, null, [voice.Id]));
        Assert.Equal("", comment.Content);
        Assert.Single(comment.Attachments);

        var details = await w.AsAdmin().Tasks.GetAsync(task.Task.Id);
        Assert.Equal(voice.Id, details.Comments.Single().Attachments.Single().Id);
        Assert.Equal(details.Comments.Single().Id, details.Attachments.Single().CommentId);

        // A file can't be re-used by another comment, nor attached by someone who didn't upload it.
        await Assert.ThrowsAsync<BadRequestException>(() =>
            w.As(Dev1, Roles.Employee).Tasks.AddCommentAsync(task.Task.Id, new AddCommentRequest("again", null, [voice.Id])));
        var other = await w.AsAdmin().Tasks.AddAttachmentAsync(task.Task.Id, "a.png", "image/png", Png.Length, new MemoryStream(Png));
        await Assert.ThrowsAsync<BadRequestException>(() =>
            w.As(Dev1, Roles.Employee).Tasks.AddCommentAsync(task.Task.Id, new AddCommentRequest("x", null, [other.Id])));
    }

    [Fact]
    public async Task Emoji_reactions_toggle_and_show_who_reacted()
    {
        var w = new World();
        var task = await w.AsAdmin().Tasks.CreateAsync(NewTask("t", GA, [Dev1]));
        var comment = await w.As(Dev1, Roles.Employee).Tasks.AddCommentAsync(task.Task.Id, new AddCommentRequest("Done 🎉"));

        await w.AsAdmin().Tasks.ToggleReactionAsync(task.Task.Id, comment.Id, new ToggleReactionRequest("👍"));
        var r = await w.As(Dev1, Roles.Employee).Tasks.ToggleReactionAsync(task.Task.Id, comment.Id, new ToggleReactionRequest("👍"));
        Assert.Equal(2, r.Single().Count);
        r = await w.As(Dev1, Roles.Employee).Tasks.ToggleReactionAsync(task.Task.Id, comment.Id, new ToggleReactionRequest("👍"));
        Assert.Equal(1, r.Single().Count); // second click removes it
        await w.As(Dev1, Roles.Employee).Tasks.ToggleReactionAsync(task.Task.Id, comment.Id, new ToggleReactionRequest("❤️"));

        var details = await w.AsAdmin().Tasks.GetAsync(task.Task.Id);
        Assert.Equal("Done 🎉", details.Comments.Single().Content);
        Assert.Equal(["👍", "❤️"], details.Comments.Single().Reactions.Select(x => x.Emoji));
        await Assert.ThrowsAsync<NotFoundException>(() =>
            w.As(Outsider, Roles.Employee).Tasks.ToggleReactionAsync(task.Task.Id, comment.Id, new ToggleReactionRequest("👍")));
    }

    [Theory]
    [InlineData("👍", true)]
    [InlineData("❤️", true)]
    [InlineData("👍🏽", true)]
    [InlineData("👨‍💻", true)]
    [InlineData("a", false)]
    [InlineData("<b>", false)]
    [InlineData("", false)]
    public void Only_emojis_are_accepted_as_reactions(string emoji, bool valid) =>
        Assert.Equal(valid, new ToggleReactionRequestValidator().Validate(new ToggleReactionRequest(emoji)).IsValid);

    // ------------------------------------------------------------------ permissions

    [Fact]
    public void Leads_and_managers_can_create_projects_and_run_sprints_but_employees_cannot()
    {
        Assert.Contains(Permissions.ProjectsCreate, Permissions.ForRoles([Roles.Leader]));
        Assert.Contains(Permissions.SprintsManage, Permissions.ForRoles([Roles.Manager]));
        Assert.DoesNotContain(Permissions.ProjectsCreate, Permissions.ForRoles([Roles.Employee]));
        Assert.DoesNotContain(Permissions.SprintsManage, Permissions.ForRoles([Roles.Employee]));
        Assert.Contains(Permissions.ProjectsCreate, Permissions.All);
    }
}
