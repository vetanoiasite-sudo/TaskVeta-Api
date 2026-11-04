using CompanyManagement.Application.Common;
using CompanyManagement.Domain.Entities;
using FluentValidation;
using Microsoft.EntityFrameworkCore;

namespace CompanyManagement.Application.Features.Organization;

public record DepartmentDto(int Id, string Name, string? NameAr, string Code, string? Description, bool IsActive,
    int? ManagerEmployeeId, string? ManagerName, int EmployeeCount, int TeamCount, DateTime CreatedAt);

public record SaveDepartmentRequest(string Name, string? NameAr, string Code, string? Description, int? ManagerEmployeeId, bool IsActive = true);

public record LookupDto(int Id, string Name, string? Code = null);

public class SaveDepartmentRequestValidator : AbstractValidator<SaveDepartmentRequest>
{
    public SaveDepartmentRequestValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(150);
        RuleFor(x => x.NameAr).MaximumLength(150);
        RuleFor(x => x.Code).NotEmpty().MaximumLength(20).Matches("^[A-Za-z0-9_-]+$");
        RuleFor(x => x.Description).MaximumLength(1000);
    }
}

public interface IDepartmentService
{
    Task<IReadOnlyList<DepartmentDto>> GetAllAsync(CancellationToken ct = default);
    Task<IReadOnlyList<LookupDto>> LookupAsync(CancellationToken ct = default);
    Task<DepartmentDto> GetAsync(int id, CancellationToken ct = default);
    Task<DepartmentDto> CreateAsync(SaveDepartmentRequest request, CancellationToken ct = default);
    Task<DepartmentDto> UpdateAsync(int id, SaveDepartmentRequest request, CancellationToken ct = default);
    Task DeleteAsync(int id, CancellationToken ct = default);
}

public class DepartmentService(IApplicationDbContext db, IAuditService audit, IAccessScope scope) : IDepartmentService
{
    private IQueryable<DepartmentDto> Project(IQueryable<Department> q) => q.Select(d => new DepartmentDto(
        d.Id, d.Name, d.NameAr, d.Code, d.Description, d.IsActive, d.ManagerEmployeeId,
        d.Manager != null ? d.Manager.FirstName + " " + d.Manager.LastName : null,
        d.Employees.Count(e => !e.IsDeleted), d.Teams.Count, d.CreatedAt));

    public async Task<IReadOnlyList<DepartmentDto>> GetAllAsync(CancellationToken ct = default) =>
        await Project(db.Departments.OrderBy(d => d.Name)).ToListAsync(ct);

    public async Task<IReadOnlyList<LookupDto>> LookupAsync(CancellationToken ct = default)
    {
        var allowed = await scope.AccessibleDepartmentIdsAsync(ct);
        var q = db.Departments.Where(d => d.IsActive);
        if (allowed is not null) q = q.Where(d => allowed.Contains(d.Id));
        return await q.OrderBy(d => d.Name).Select(d => new LookupDto(d.Id, d.Name, d.Code)).ToListAsync(ct);
    }

    public async Task<DepartmentDto> GetAsync(int id, CancellationToken ct = default) =>
        await Project(db.Departments.Where(d => d.Id == id)).FirstOrDefaultAsync(ct) ?? throw new NotFoundException("Department", id);

    public async Task<DepartmentDto> CreateAsync(SaveDepartmentRequest r, CancellationToken ct = default)
    {
        await ValidateAsync(null, r, ct);
        var d = new Department();
        Apply(d, r);
        db.Departments.Add(d);
        await db.SaveChangesAsync(ct);
        audit.Log("Create", nameof(Department), d.Id, newValues: r);
        await db.SaveChangesAsync(ct);
        return await GetAsync(d.Id, ct);
    }

    public async Task<DepartmentDto> UpdateAsync(int id, SaveDepartmentRequest r, CancellationToken ct = default)
    {
        var d = await db.Departments.FindAsync([id], ct) ?? throw new NotFoundException("Department", id);
        await ValidateAsync(id, r, ct);
        var old = new { d.Name, d.NameAr, d.Code, d.Description, d.ManagerEmployeeId, d.IsActive };
        Apply(d, r);
        audit.Log("Update", nameof(Department), id, old, r);
        await db.SaveChangesAsync(ct);
        return await GetAsync(id, ct);
    }

    public async Task DeleteAsync(int id, CancellationToken ct = default)
    {
        var d = await db.Departments.FindAsync([id], ct) ?? throw new NotFoundException("Department", id);
        if (await db.Employees.AnyAsync(e => e.DepartmentId == id && !e.IsDeleted, ct))
            throw new ConflictException("Department has employees. Move them to another department or deactivate it instead.");
        if (await db.Teams.AnyAsync(t => t.DepartmentId == id, ct))
            throw new ConflictException("Department has teams. Delete or move them first.");
        if (await db.Employees.AnyAsync(e => e.DepartmentId == id, ct) || await db.Tasks.AnyAsync(t => t.DepartmentId == id, ct))
        {
            d.IsActive = false; // historical references exist; keep the row
            audit.Log("Deactivate", nameof(Department), id, reason: "Delete requested; department has historical records.");
        }
        else
        {
            db.Departments.Remove(d);
            audit.Log("Delete", nameof(Department), id, new { d.Name, d.Code });
        }
        await db.SaveChangesAsync(ct);
    }

    private async Task ValidateAsync(int? id, SaveDepartmentRequest r, CancellationToken ct)
    {
        if (await db.Departments.AnyAsync(d => d.Code == r.Code && d.Id != id, ct))
            throw new ConflictException($"Department code '{r.Code}' is already used.");
        if (r.ManagerEmployeeId is int m && !await db.Employees.AnyAsync(e => e.Id == m && !e.IsDeleted, ct))
            throw new NotFoundException("Employee", m);
    }

    private static void Apply(Department d, SaveDepartmentRequest r)
    {
        d.Name = r.Name.Trim();
        d.NameAr = r.NameAr?.Trim();
        d.Code = r.Code.Trim().ToUpperInvariant();
        d.Description = r.Description;
        d.ManagerEmployeeId = r.ManagerEmployeeId;
        d.IsActive = r.IsActive;
    }
}
