namespace RePlace.Presentation.Dto;

public class LastProcessedFileDto
{
    public int AnexoId { get; set; }
    public string NomeAnexo { get; set; } = string.Empty;
    public DateTime? CompletedAt { get; set; }
}