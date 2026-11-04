using CompanyManagement.Application.Common;
using CompanyManagement.Application.Features.Organization;
using CompanyManagement.Domain.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace CompanyManagement.API.Controllers;

[ApiController]
[Route("api/employees")]
public class EmployeesController(IEmployeeService employees) : ControllerBase
{
    [HttpGet]
    [Authorize(Policy = Permissions.EmployeesView)]
    public async Task<ActionResult<PagedResult<EmployeeDto>>> Get([FromQuery] EmployeeQuery query, CancellationToken ct) =>
        Ok(await employees.GetPagedAsync(query, ct));

    /// <summary>Id/name pairs of employees visible to the current user (for dropdowns).</summary>
    [HttpGet("lookup")]
    public async Task<ActionResult<IReadOnlyList<LookupDto>>> Lookup([FromQuery] int? departmentId, [FromQuery] int? teamId, CancellationToken ct) =>
        Ok(await employees.LookupAsync(departmentId, teamId, ct));

    [HttpGet("{id:int}")]
    public async Task<ActionResult<EmployeeDto>> GetById(int id, CancellationToken ct) => Ok(await employees.GetAsync(id, ct));

    [HttpPost]
    [Authorize(Policy = Permissions.EmployeesManage)]
    public async Task<ActionResult<EmployeeDto>> Create(CreateEmployeeRequest request, CancellationToken ct)
    {
        var created = await employees.CreateAsync(request, ct);
        return CreatedAtAction(nameof(GetById), new { id = created.Id }, created);
    }

    [HttpPut("{id:int}")]
    [Authorize(Policy = Permissions.EmployeesManage)]
    public async Task<ActionResult<EmployeeDto>> Update(int id, UpdateEmployeeRequest request, CancellationToken ct) =>
        Ok(await employees.UpdateAsync(id, request, ct));

    [HttpDelete("{id:int}")]
    [Authorize(Policy = Permissions.EmployeesManage)]
    public async Task<IActionResult> Delete(int id, CancellationToken ct)
    {
        await employees.DeleteAsync(id, ct);
        return NoContent();
    }
}

[ApiController]
[Route("api/departments")]
public class DepartmentsController(IDepartmentService departments) : ControllerBase
{
    [HttpGet]
    [Authorize(Policy = Permissions.EmployeesView)]
    public async Task<ActionResult<IReadOnlyList<DepartmentDto>>> Get(CancellationToken ct) => Ok(await departments.GetAllAsync(ct));

    [HttpGet("lookup")]
    public async Task<ActionResult<IReadOnlyList<LookupDto>>> Lookup(CancellationToken ct) => Ok(await departments.LookupAsync(ct));

    [HttpGet("{id:int}")]
    [Authorize(Policy = Permissions.EmployeesView)]
    public async Task<ActionResult<DepartmentDto>> GetById(int id, CancellationToken ct) => Ok(await departments.GetAsync(id, ct));

    [HttpPost]
    [Authorize(Policy = Permissions.DepartmentsManage)]
    public async Task<ActionResult<DepartmentDto>> Create(SaveDepartmentRequest request, CancellationToken ct)
    {
        var d = await departments.CreateAsync(request, ct);
        return CreatedAtAction(nameof(GetById), new { id = d.Id }, d);
    }

    [HttpPut("{id:int}")]
    [Authorize(Policy = Permissions.DepartmentsManage)]
    public async Task<ActionResult<DepartmentDto>> Update(int id, SaveDepartmentRequest request, CancellationToken ct) =>
        Ok(await departments.UpdateAsync(id, request, ct));

    [HttpDelete("{id:int}")]
    [Authorize(Policy = Permissions.DepartmentsManage)]
    public async Task<IActionResult> Delete(int id, CancellationToken ct)
    {
        await departments.DeleteAsync(id, ct);
        return NoContent();
    }
}

[ApiController]
[Route("api/teams")]
public class TeamsController(ITeamService teams) : ControllerBase
{
    [HttpGet]
    [Authorize(Policy = Permissions.EmployeesView)]
    public async Task<ActionResult<IReadOnlyList<TeamDto>>> Get([FromQuery] int? departmentId, CancellationToken ct) =>
        Ok(await teams.GetAllAsync(departmentId, ct));

    [HttpGet("lookup")]
    public async Task<ActionResult<IReadOnlyList<LookupDto>>> Lookup([FromQuery] int? departmentId, CancellationToken ct) =>
        Ok(await teams.LookupAsync(departmentId, ct));

    [HttpGet("{id:int}")]
    [Authorize(Policy = Permissions.EmployeesView)]
    public async Task<ActionResult<TeamDetailsDto>> GetById(int id, CancellationToken ct) => Ok(await teams.GetAsync(id, ct));

    [HttpPost]
    [Authorize(Policy = Permissions.TeamsManage)]
    public async Task<ActionResult<TeamDetailsDto>> Create(SaveTeamRequest request, CancellationToken ct)
    {
        var t = await teams.CreateAsync(request, ct);
        return CreatedAtAction(nameof(GetById), new { id = t.Team.Id }, t);
    }

    [HttpPut("{id:int}")]
    [Authorize(Policy = Permissions.TeamsManage)]
    public async Task<ActionResult<TeamDetailsDto>> Update(int id, SaveTeamRequest request, CancellationToken ct) =>
        Ok(await teams.UpdateAsync(id, request, ct));

    [HttpDelete("{id:int}")]
    [Authorize(Policy = Permissions.TeamsManage)]
    public async Task<IActionResult> Delete(int id, CancellationToken ct)
    {
        await teams.DeleteAsync(id, ct);
        return NoContent();
    }

    [HttpPost("{id:int}/members/{employeeId:int}")]
    [Authorize(Policy = Permissions.TeamsManage)]
    public async Task<IActionResult> AddMember(int id, int employeeId, CancellationToken ct)
    {
        await teams.AddMemberAsync(id, employeeId, ct);
        return NoContent();
    }

    [HttpDelete("{id:int}/members/{employeeId:int}")]
    [Authorize(Policy = Permissions.TeamsManage)]
    public async Task<IActionResult> RemoveMember(int id, int employeeId, CancellationToken ct)
    {
        await teams.RemoveMemberAsync(id, employeeId, ct);
        return NoContent();
    }
}

[ApiController]
[Route("api/users")]
[Authorize(Policy = Permissions.UsersManage)]
public class UsersController(IUserService users) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<PagedResult<UserDto>>> Get([FromQuery] UserQuery query, CancellationToken ct) => Ok(await users.GetPagedAsync(query, ct));

    [HttpGet("{id:int}")]
    public async Task<ActionResult<UserDto>> GetById(int id, CancellationToken ct) => Ok(await users.GetAsync(id, ct));

    [HttpPut("{id:int}")]
    public async Task<ActionResult<UserDto>> Update(int id, UpdateUserRequest request, CancellationToken ct) => Ok(await users.UpdateAsync(id, request, ct));

    [HttpPost("{id:int}/reset-password")]
    public async Task<IActionResult> ResetPassword(int id, ResetPasswordRequest request, CancellationToken ct)
    {
        await users.ResetPasswordAsync(id, request, ct);
        return NoContent();
    }

    [HttpPost("{id:int}/unlock")]
    public async Task<IActionResult> Unlock(int id, CancellationToken ct)
    {
        await users.UnlockAsync(id, ct);
        return NoContent();
    }

    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Delete(int id, CancellationToken ct)
    {
        await users.DeleteAsync(id, ct);
        return NoContent();
    }
}

[ApiController]
[Route("api/roles")]
[Authorize(Policy = Permissions.UsersManage)]
public class RolesController(IUserService users) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<RoleDto>>> Get(CancellationToken ct) => Ok(await users.GetRolesAsync(ct));
}
