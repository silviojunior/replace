using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace RePlace.Domain.Models;

[Table("anexo")]
public class Anexo
{
    [Key]
    [Column("id")]
    public int Id { get; set; }
    
    [Required]
    [Column("nome")]
    [MaxLength(455)]
    public required string Nome { get; set; }
    
    [Column("tipo_anexo_id")]
    public int TipoAnexoId { get; set; }

    [Column("anexo")] 
    public byte[]? AnexoBlob { get; set; }

    [Column("timestamp")]
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;

    [Column("tipo")] 
    [MaxLength(100)] 
    public string Tipo { get; set; } = string.Empty;
    
    [Column("tamanho")]
    public int Tamanho { get; set; }
    
    [Column("filepath")]
    [MaxLength(500)]
    public string? Filepath { get; set; } = string.Empty;
    
}