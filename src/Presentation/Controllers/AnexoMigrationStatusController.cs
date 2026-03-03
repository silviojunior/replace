using Microsoft.AspNetCore.Mvc;
using RePlace.Application.UseCases;
using RePlace.Presentation.Dto;

namespace RePlace.Presentation.Controllers;

[ApiController]
[Route("api/migration")]
public class AnexoMigrationStatusController(IMigrationStatusUseCase statusService) : ControllerBase
{
    [HttpGet("status")]
    [ProducesResponseType(typeof(MigrationStatusResponseDto), 200)]
    public async Task<IActionResult> GetDetailedStatus()
    {
        var result = await statusService.GetDetailedStatusAsync();
        return Ok(result);
    }

    [HttpGet("simple/status")]
    [ProducesResponseType(typeof(MigrationStatsDto), 200)]
    public async Task<IActionResult> GetSimpleStatus()
    {
        var result = await statusService.GetSimpleStatusAsync();
        return Ok(result);
    }
}