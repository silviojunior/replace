namespace RePlace.Presentation.Dto;

public class MigrationStatsDto
{
    public int Total { get; set; }
    public int Completed { get; set; }
    public int Failed { get; set; }
    public int Pending { get; set; }
    public double ProgressPercentage { get; set; }
}