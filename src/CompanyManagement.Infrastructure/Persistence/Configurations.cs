using CompanyManagement.Domain.Entities;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CompanyManagement.Infrastructure.Persistence;

// ------------------------------------------------------------------ Identity

public class UserConfiguration : IEntityTypeConfiguration<ApplicationUser>
{
    public void Configure(EntityTypeBuilder<ApplicationUser> b)
    {
        b.ToTable("Users");
        b.Property(u => u.FullName).HasMaxLength(150).IsRequired();
        b.Property(u => u.PreferredLanguage).HasMaxLength(5).HasDefaultValue("en");
        b.HasIndex(u => u.IsActive);
    }
}

public class RoleConfiguration : IEntityTypeConfiguration<ApplicationRole>
{
    public void Configure(EntityTypeBuilder<ApplicationRole> b)
    {
        b.ToTable("Roles");
        b.Property(r => r.Description).HasMaxLength(300);
    }
}

public class UserRoleConfiguration : IEntityTypeConfiguration<IdentityUserRole<int>>
{
    public void Configure(EntityTypeBuilder<IdentityUserRole<int>> b) => b.ToTable("UserRoles");
}

public class UserClaimConfiguration : IEntityTypeConfiguration<IdentityUserClaim<int>>
{
    public void Configure(EntityTypeBuilder<IdentityUserClaim<int>> b) => b.ToTable("UserClaims");
}

public class UserLoginConfiguration : IEntityTypeConfiguration<IdentityUserLogin<int>>
{
    public void Configure(EntityTypeBuilder<IdentityUserLogin<int>> b) => b.ToTable("UserLogins");
}

public class UserTokenConfiguration : IEntityTypeConfiguration<IdentityUserToken<int>>
{
    public void Configure(EntityTypeBuilder<IdentityUserToken<int>> b) => b.ToTable("UserTokens");
}

public class RoleClaimConfiguration : IEntityTypeConfiguration<IdentityRoleClaim<int>>
{
    public void Configure(EntityTypeBuilder<IdentityRoleClaim<int>> b) => b.ToTable("RoleClaims");
}

public class RefreshTokenConfiguration : IEntityTypeConfiguration<RefreshToken>
{
    public void Configure(EntityTypeBuilder<RefreshToken> b)
    {
        b.ToTable("RefreshTokens");
        b.Property(t => t.TokenHash).HasMaxLength(128).IsRequired();
        b.Property(t => t.ReplacedByTokenHash).HasMaxLength(128);
        b.Property(t => t.RevokedReason).HasMaxLength(100);
        b.Property(t => t.CreatedByIp).HasMaxLength(64);
        b.HasIndex(t => t.TokenHash).IsUnique();
        b.HasIndex(t => new { t.UserId, t.ExpiresAt });
        b.HasIndex(t => t.FamilyId);
        b.HasOne(t => t.User).WithMany(u => u.RefreshTokens).HasForeignKey(t => t.UserId).OnDelete(DeleteBehavior.Cascade);
    }
}

// ------------------------------------------------------------------ Organization

public class DepartmentConfiguration : IEntityTypeConfiguration<Department>
{
    public void Configure(EntityTypeBuilder<Department> b)
    {
        b.ToTable("Departments");
        b.Property(d => d.Name).HasMaxLength(150).IsRequired();
        b.Property(d => d.NameAr).HasMaxLength(150);
        b.Property(d => d.Code).HasMaxLength(20).IsRequired();
        b.Property(d => d.Description).HasMaxLength(1000);
        b.HasIndex(d => d.Code).IsUnique();
        b.HasIndex(d => d.Name);
        b.HasOne(d => d.Manager).WithMany().HasForeignKey(d => d.ManagerEmployeeId).OnDelete(DeleteBehavior.NoAction);
    }
}

public class TeamConfiguration : IEntityTypeConfiguration<Team>
{
    public void Configure(EntityTypeBuilder<Team> b)
    {
        b.ToTable("Teams");
        b.Property(t => t.Name).HasMaxLength(150).IsRequired();
        b.Property(t => t.NameAr).HasMaxLength(150);
        b.Property(t => t.Description).HasMaxLength(1000);
        b.HasIndex(t => new { t.DepartmentId, t.Name }).IsUnique();
        b.HasOne(t => t.Department).WithMany(d => d.Teams).HasForeignKey(t => t.DepartmentId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne(t => t.Leader).WithMany().HasForeignKey(t => t.LeaderEmployeeId).OnDelete(DeleteBehavior.NoAction);
    }
}

public class TeamMemberConfiguration : IEntityTypeConfiguration<TeamMember>
{
    public void Configure(EntityTypeBuilder<TeamMember> b)
    {
        b.ToTable("TeamMembers");
        b.HasIndex(m => new { m.TeamId, m.EmployeeId }).IsUnique();
        b.HasIndex(m => m.EmployeeId);
        b.HasOne(m => m.Team).WithMany(t => t.Members).HasForeignKey(m => m.TeamId).OnDelete(DeleteBehavior.Cascade);
        b.HasOne(m => m.Employee).WithMany(e => e.TeamMemberships).HasForeignKey(m => m.EmployeeId).OnDelete(DeleteBehavior.Cascade);
    }
}

public class EmployeeConfiguration : IEntityTypeConfiguration<Employee>
{
    public void Configure(EntityTypeBuilder<Employee> b)
    {
        b.ToTable("Employees");
        b.Ignore(e => e.FullName);
        b.Property(e => e.EmployeeCode).HasMaxLength(30).IsRequired();
        b.Property(e => e.FirstName).HasMaxLength(75).IsRequired();
        b.Property(e => e.LastName).HasMaxLength(75).IsRequired();
        b.Property(e => e.Email).HasMaxLength(256).IsRequired();
        b.Property(e => e.Phone).HasMaxLength(30);
        b.Property(e => e.JobTitle).HasMaxLength(100);
        b.HasIndex(e => e.EmployeeCode).IsUnique();
        b.HasIndex(e => e.Email);
        b.HasIndex(e => e.UserId).IsUnique().HasFilter("\"UserId\" IS NOT NULL");
        b.HasIndex(e => new { e.DepartmentId, e.IsActive, e.IsDeleted });
        b.HasIndex(e => new { e.FirstName, e.LastName });
        b.HasOne(e => e.Department).WithMany(d => d.Employees).HasForeignKey(e => e.DepartmentId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne(e => e.WorkSchedule).WithMany().HasForeignKey(e => e.WorkScheduleId).OnDelete(DeleteBehavior.SetNull);
        b.HasOne(e => e.User).WithOne(u => u.Employee).HasForeignKey<Employee>(e => e.UserId).OnDelete(DeleteBehavior.SetNull);
    }
}

// ------------------------------------------------------------------ Attendance

public class AttendanceRecordConfiguration : IEntityTypeConfiguration<AttendanceRecord>
{
    public void Configure(EntityTypeBuilder<AttendanceRecord> b)
    {
        b.ToTable("AttendanceRecords");
        // One record per employee per working day (per shift when multiple shifts are enabled).
        b.HasIndex(a => new { a.EmployeeId, a.AttendanceDate, a.ShiftNumber }).IsUnique();
        b.HasIndex(a => new { a.AttendanceDate, a.Status });
        b.HasIndex(a => new { a.AttendanceDate, a.EmployeeId }).IncludeProperties(a => new { a.CheckInTime, a.CheckOutTime, a.Status, a.LateMinutes });
        b.HasIndex(a => a.MissingCheckOut).HasFilter("\"MissingCheckOut\"");
        b.Property(a => a.WorkingHours).HasPrecision(6, 2);
        b.Property(a => a.Notes).HasMaxLength(500);
        b.Property(a => a.CheckInIp).HasMaxLength(64);
        b.Property(a => a.CheckOutIp).HasMaxLength(64);
        b.Property(a => a.RowVersion).IsRowVersion();
        b.HasOne(a => a.Employee).WithMany(e => e.AttendanceRecords).HasForeignKey(a => a.EmployeeId).OnDelete(DeleteBehavior.Restrict);
    }
}

public class WorkScheduleConfiguration : IEntityTypeConfiguration<WorkSchedule>
{
    public void Configure(EntityTypeBuilder<WorkSchedule> b)
    {
        b.ToTable("WorkSchedules");
        b.Ignore(w => w.WeekendDays);
        b.Ignore(w => w.ScheduledHours);
        b.Property(w => w.Name).HasMaxLength(100).IsRequired();
        b.Property(w => w.HalfDayThresholdHours).HasPrecision(5, 2);
        b.HasIndex(w => w.Name).IsUnique();
        b.HasIndex(w => w.IsDefault).IsUnique().HasFilter("\"IsDefault\"");
    }
}

public class HolidayConfiguration : IEntityTypeConfiguration<Holiday>
{
    public void Configure(EntityTypeBuilder<Holiday> b)
    {
        b.ToTable("Holidays");
        b.Property(h => h.Name).HasMaxLength(150).IsRequired();
        b.Property(h => h.NameAr).HasMaxLength(150);
        b.Property(h => h.Description).HasMaxLength(500);
        b.HasIndex(h => new { h.StartDate, h.EndDate });
        b.ToTable(t => t.HasCheckConstraint("CK_Holidays_Range", "\"EndDate\" >= \"StartDate\""));
    }
}

public class LeaveRequestConfiguration : IEntityTypeConfiguration<LeaveRequest>
{
    public void Configure(EntityTypeBuilder<LeaveRequest> b)
    {
        b.ToTable("LeaveRequests");
        b.Property(l => l.Reason).HasMaxLength(1000);
        b.Property(l => l.ReviewNotes).HasMaxLength(1000);
        b.HasIndex(l => new { l.EmployeeId, l.Status, l.StartDate, l.EndDate });
        b.HasOne(l => l.Employee).WithMany(e => e.LeaveRequests).HasForeignKey(l => l.EmployeeId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<ApplicationUser>().WithMany().HasForeignKey(l => l.ReviewedByUserId).OnDelete(DeleteBehavior.NoAction);
        b.ToTable(t => t.HasCheckConstraint("CK_LeaveRequests_Range", "\"EndDate\" >= \"StartDate\""));
    }
}

public class CompanySettingsConfiguration : IEntityTypeConfiguration<CompanySettings>
{
    public void Configure(EntityTypeBuilder<CompanySettings> b)
    {
        b.ToTable("SystemSettings");
        b.Ignore(s => s.EnabledMethods);
        b.Property(s => s.CompanyName).HasMaxLength(150);
        b.Property(s => s.TimeZoneId).HasMaxLength(100);
        b.Property(s => s.AllowedIpRanges).HasMaxLength(4000);
        b.Property(s => s.QrSecret).HasMaxLength(200);
    }
}

// ------------------------------------------------------------------ Tasks

public class TaskItemConfiguration : IEntityTypeConfiguration<TaskItem>
{
    public void Configure(EntityTypeBuilder<TaskItem> b)
    {
        b.ToTable("Tasks", t =>
        {
            t.HasCheckConstraint("CK_Tasks_Progress", "\"ProgressPercent\" BETWEEN 0 AND 100");
            t.HasCheckConstraint("CK_Tasks_Rating", "\"Rating\" IS NULL OR \"Rating\" BETWEEN 1 AND 10");
            t.HasCheckConstraint("CK_Tasks_Estimate", "\"EstimateDays\" IS NULL OR (\"EstimateDays\" > 0 AND \"EstimateDays\" <= 30)");
        });
        b.Ignore(t => t.IsClosed);
        b.Property(t => t.Title).HasMaxLength(200).IsRequired();
        b.Property(t => t.Description).HasMaxLength(4000);
        b.Property(t => t.ReviewNotes).HasMaxLength(2000);
        b.Property(t => t.RowVersion).IsRowVersion();
        b.HasIndex(t => new { t.Status, t.DueDate });
        b.HasIndex(t => t.AssignedByUserId);
        b.HasIndex(t => t.DepartmentId);
        b.HasIndex(t => t.TeamId);
        b.HasIndex(t => t.CreatedAt);
        b.HasOne(t => t.AssignedBy).WithMany().HasForeignKey(t => t.AssignedByUserId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne(t => t.AssignedTo).WithMany().HasForeignKey(t => t.AssignedToEmployeeId).OnDelete(DeleteBehavior.NoAction);
        b.HasOne(t => t.Department).WithMany().HasForeignKey(t => t.DepartmentId).OnDelete(DeleteBehavior.NoAction);
        b.HasOne(t => t.Team).WithMany().HasForeignKey(t => t.TeamId).OnDelete(DeleteBehavior.SetNull);
        b.HasIndex(t => t.ProjectId);
        b.HasOne(t => t.Project).WithMany(p => p.Tasks).HasForeignKey(t => t.ProjectId).OnDelete(DeleteBehavior.SetNull);

        // Task key = project code + Number (GA-123): unique inside a project.
        b.HasIndex(t => new { t.ProjectId, t.Number }).IsUnique().HasFilter("\"Number\" IS NOT NULL");
        b.HasIndex(t => t.Type);
        b.HasIndex(t => t.SprintId);
        b.HasOne(t => t.Sprint).WithMany(s => s.Tasks).HasForeignKey(t => t.SprintId).OnDelete(DeleteBehavior.SetNull);
        b.HasIndex(t => t.ParentTaskId);
        b.HasOne(t => t.ParentTask).WithMany(t => t.Subtasks).HasForeignKey(t => t.ParentTaskId).OnDelete(DeleteBehavior.SetNull);
        b.Property(t => t.EstimateDays).HasPrecision(4, 1);
        b.Property(t => t.StoryRef).HasMaxLength(50);
        b.Property(t => t.ScreenRef).HasMaxLength(50);
        b.Property(t => t.DesignUrl).HasMaxLength(500);
        b.Property(t => t.PullRequestUrl).HasMaxLength(500);
        b.Property(t => t.BlockedReason).HasMaxLength(500);
    }
}

public class TaskCommentReactionConfiguration : IEntityTypeConfiguration<TaskCommentReaction>
{
    public void Configure(EntityTypeBuilder<TaskCommentReaction> b)
    {
        b.ToTable("TaskCommentReactions");
        b.Property(r => r.Emoji).HasMaxLength(TaskCommentReaction.MaxEmojiLength).IsRequired();
        b.HasIndex(r => new { r.CommentId, r.UserId, r.Emoji }).IsUnique();
        b.HasOne(r => r.Comment).WithMany(c => c.Reactions).HasForeignKey(r => r.CommentId).OnDelete(DeleteBehavior.Cascade);
        b.HasOne(r => r.User).WithMany().HasForeignKey(r => r.UserId).OnDelete(DeleteBehavior.Cascade);
    }
}

public class TaskDependencyConfiguration : IEntityTypeConfiguration<TaskDependency>
{
    public void Configure(EntityTypeBuilder<TaskDependency> b)
    {
        b.ToTable("TaskDependencies", t => t.HasCheckConstraint("CK_TaskDependencies_NotSelf", "\"TaskId\" <> \"BlockedByTaskId\""));
        b.HasIndex(d => new { d.TaskId, d.BlockedByTaskId }).IsUnique();
        b.HasIndex(d => d.BlockedByTaskId);
        b.HasOne(d => d.Task).WithMany(t => t.BlockedBy).HasForeignKey(d => d.TaskId).OnDelete(DeleteBehavior.Cascade);
        b.HasOne(d => d.BlockedByTask).WithMany(t => t.Blocks).HasForeignKey(d => d.BlockedByTaskId).OnDelete(DeleteBehavior.Cascade);
    }
}

public class ProjectConfiguration : IEntityTypeConfiguration<Project>
{
    public void Configure(EntityTypeBuilder<Project> b)
    {
        b.ToTable("Projects", t => t.HasCheckConstraint("CK_Projects_Dates", "\"EndDate\" IS NULL OR \"StartDate\" IS NULL OR \"EndDate\" >= \"StartDate\""));
        b.Property(p => p.Name).HasMaxLength(150).IsRequired();
        b.Property(p => p.NameAr).HasMaxLength(150);
        b.Property(p => p.Code).HasMaxLength(20).IsRequired();
        b.Property(p => p.Description).HasMaxLength(2000);
        b.Property(p => p.ClientName).HasMaxLength(150);
        b.Property(p => p.RepositoryUrl).HasMaxLength(500);
        b.Property(p => p.DesignUrl).HasMaxLength(500);
        b.Property(p => p.DocsUrl).HasMaxLength(500);
        b.HasIndex(p => p.Code).IsUnique();
        b.HasIndex(p => p.Name);
        b.HasOne(p => p.CreatedBy).WithMany().HasForeignKey(p => p.CreatedByUserId).OnDelete(DeleteBehavior.SetNull);
    }
}

public class ProjectMemberConfiguration : IEntityTypeConfiguration<ProjectMember>
{
    public void Configure(EntityTypeBuilder<ProjectMember> b)
    {
        b.ToTable("ProjectMembers");
        b.HasIndex(m => new { m.ProjectId, m.EmployeeId, m.Role }).IsUnique();
        b.HasIndex(m => m.EmployeeId);
        b.HasOne(m => m.Project).WithMany(p => p.Members).HasForeignKey(m => m.ProjectId).OnDelete(DeleteBehavior.Cascade);
        b.HasOne(m => m.Employee).WithMany().HasForeignKey(m => m.EmployeeId).OnDelete(DeleteBehavior.Cascade);
    }
}

public class SprintConfiguration : IEntityTypeConfiguration<Sprint>
{
    public void Configure(EntityTypeBuilder<Sprint> b)
    {
        b.ToTable("Sprints", t => t.HasCheckConstraint("CK_Sprints_Dates", "\"EndDate\" >= \"StartDate\""));
        b.Property(s => s.Name).HasMaxLength(100).IsRequired();
        b.Property(s => s.Goal).HasMaxLength(1000);
        b.HasIndex(s => s.StartDate);
        // At most one active sprint at a time (Status 2 = Active).
        b.HasIndex(s => s.Status, "IX_Sprints_SingleActive").IsUnique().HasFilter("\"Status\" = 2");
    }
}

public class TaskAssignmentConfiguration : IEntityTypeConfiguration<TaskAssignment>
{
    public void Configure(EntityTypeBuilder<TaskAssignment> b)
    {
        b.ToTable("TaskAssignments");
        b.HasIndex(a => new { a.TaskId, a.EmployeeId }).IsUnique();
        b.HasIndex(a => a.EmployeeId);
        b.HasOne(a => a.Task).WithMany(t => t.Assignments).HasForeignKey(a => a.TaskId).OnDelete(DeleteBehavior.Cascade);
        b.HasOne(a => a.Employee).WithMany().HasForeignKey(a => a.EmployeeId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<ApplicationUser>().WithMany().HasForeignKey(a => a.AssignedByUserId).OnDelete(DeleteBehavior.NoAction);
    }
}

public class TaskCommentConfiguration : IEntityTypeConfiguration<TaskComment>
{
    public void Configure(EntityTypeBuilder<TaskComment> b)
    {
        b.ToTable("TaskComments");
        b.Property(c => c.Content).HasMaxLength(4000).IsRequired();
        b.HasIndex(c => new { c.TaskId, c.CreatedAt });
        b.HasOne(c => c.Task).WithMany(t => t.Comments).HasForeignKey(c => c.TaskId).OnDelete(DeleteBehavior.Cascade);
        b.HasOne(c => c.User).WithMany().HasForeignKey(c => c.UserId).OnDelete(DeleteBehavior.Restrict);
    }
}

public class TaskReviewConfiguration : IEntityTypeConfiguration<TaskReview>
{
    public void Configure(EntityTypeBuilder<TaskReview> b)
    {
        b.ToTable("TaskReviews", t => t.HasCheckConstraint("CK_TaskReviews_Rating", "\"Rating\" IS NULL OR \"Rating\" BETWEEN 1 AND 10"));
        b.Property(r => r.Notes).HasMaxLength(2000);
        b.HasIndex(r => new { r.TaskId, r.CreatedAt });
        b.HasIndex(r => r.CreatedAt);
        b.HasOne(r => r.Task).WithMany(t => t.Reviews).HasForeignKey(r => r.TaskId).OnDelete(DeleteBehavior.Cascade);
        b.HasOne(r => r.Reviewer).WithMany().HasForeignKey(r => r.ReviewerUserId).OnDelete(DeleteBehavior.Restrict);
    }
}

public class TaskViewConfiguration : IEntityTypeConfiguration<TaskView>
{
    public void Configure(EntityTypeBuilder<TaskView> b)
    {
        b.ToTable("TaskViews");
        b.HasIndex(v => new { v.TaskId, v.UserId }).IsUnique();
        b.HasIndex(v => v.UserId);
        b.HasOne(v => v.Task).WithMany().HasForeignKey(v => v.TaskId).OnDelete(DeleteBehavior.Cascade);
        b.HasOne(v => v.User).WithMany().HasForeignKey(v => v.UserId).OnDelete(DeleteBehavior.Cascade);
    }
}

public class TaskAttachmentConfiguration : IEntityTypeConfiguration<TaskAttachment>
{
    public void Configure(EntityTypeBuilder<TaskAttachment> b)
    {
        b.ToTable("TaskAttachments");
        b.Property(a => a.FileName).HasMaxLength(255).IsRequired();
        b.Property(a => a.StoredFileName).HasMaxLength(100).IsRequired();
        b.Property(a => a.ContentType).HasMaxLength(150).IsRequired();
        b.HasIndex(a => a.StoredFileName).IsUnique();
        b.HasIndex(a => a.TaskId);
        b.HasOne(a => a.Task).WithMany(t => t.Attachments).HasForeignKey(a => a.TaskId).OnDelete(DeleteBehavior.Cascade);
        b.HasOne(a => a.UploadedBy).WithMany().HasForeignKey(a => a.UploadedByUserId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(a => a.CommentId);
        b.HasOne(a => a.Comment).WithMany(c => c.Attachments).HasForeignKey(a => a.CommentId).OnDelete(DeleteBehavior.SetNull);
    }
}

public class TaskHistoryConfiguration : IEntityTypeConfiguration<TaskHistory>
{
    public void Configure(EntityTypeBuilder<TaskHistory> b)
    {
        b.ToTable("TaskHistory");
        b.Property(h => h.Action).HasMaxLength(50).IsRequired();
        b.Property(h => h.Details).HasMaxLength(2000);
        b.HasIndex(h => new { h.TaskId, h.CreatedAt });
        b.HasOne(h => h.Task).WithMany(t => t.History).HasForeignKey(h => h.TaskId).OnDelete(DeleteBehavior.Cascade);
        b.HasOne(h => h.ChangedBy).WithMany().HasForeignKey(h => h.ChangedByUserId).OnDelete(DeleteBehavior.NoAction);
    }
}

// ------------------------------------------------------------------ System

public class NotificationConfiguration : IEntityTypeConfiguration<Notification>
{
    public void Configure(EntityTypeBuilder<Notification> b)
    {
        b.ToTable("Notifications");
        b.Property(n => n.Title).HasMaxLength(200).IsRequired();
        b.Property(n => n.Message).HasMaxLength(1000).IsRequired();
        b.Property(n => n.TitleAr).HasMaxLength(200);
        b.Property(n => n.MessageAr).HasMaxLength(1000);
        b.Property(n => n.Link).HasMaxLength(300);
        b.Property(n => n.RelatedEntityType).HasMaxLength(50);
        b.HasIndex(n => new { n.UserId, n.IsRead, n.CreatedAt });
        b.HasOne(n => n.User).WithMany().HasForeignKey(n => n.UserId).OnDelete(DeleteBehavior.Cascade);
    }
}

public class AuditLogConfiguration : IEntityTypeConfiguration<AuditLog>
{
    public void Configure(EntityTypeBuilder<AuditLog> b)
    {
        b.ToTable("AuditLogs");
        b.Property(a => a.UserName).HasMaxLength(256);
        b.Property(a => a.Action).HasMaxLength(50).IsRequired();
        b.Property(a => a.EntityName).HasMaxLength(100).IsRequired();
        b.Property(a => a.EntityId).HasMaxLength(50);
        b.Property(a => a.Reason).HasMaxLength(1000);
        b.Property(a => a.IpAddress).HasMaxLength(64);
        b.HasIndex(a => new { a.EntityName, a.EntityId });
        b.HasIndex(a => a.CreatedAt);
        b.HasIndex(a => a.UserId);
    }
}
