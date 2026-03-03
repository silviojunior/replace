using RePlace.Presentation.Dto;

namespace RePlace.Application.UseCases;

public interface IMigrationStatusUseCase
{
    Task<MigrationStatusResponseDto> GetDetailedStatusAsync();
    Task<MigrationStatsDto> GetSimpleStatusAsync();
}