using CompanyManagement.Application.Features.Claude;
using Microsoft.AspNetCore.Mvc;

namespace CompanyManagement.API.Controllers;

/// <summary>
/// The current user's personal keys for Claude. A key lets Claude Code / Claude Desktop use TaskVeta (only the /mcp endpoint)
/// with exactly the user's permissions. The full key is returned once, when it is created.
/// </summary>
[ApiController]
[Route("api/personal-keys")]
public class PersonalKeysController(IPersonalKeyService keys) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<PersonalKeyDto>>> Get(CancellationToken ct) => Ok(await keys.GetMineAsync(ct));

    [HttpPost]
    public async Task<ActionResult<CreatedPersonalKeyDto>> Create(CreatePersonalKeyRequest request, CancellationToken ct) =>
        Ok(await keys.CreateAsync(request, ct));

    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Revoke(int id, CancellationToken ct)
    {
        await keys.RevokeAsync(id, ct);
        return NoContent();
    }
}
