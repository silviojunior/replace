namespace RePlace.Presentation.Dto;

public class MigrationStatusResponseDto
{
    public MigrationStatsDto Summary { get; set; } = new();
    public List<StatusDetailDto> Details { get; set; } = new();
    public LastProcessedFileDto? LastProcessedFile { get; set; }
}