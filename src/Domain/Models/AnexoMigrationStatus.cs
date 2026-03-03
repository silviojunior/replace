using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace RePlace.Domain.Models;

[Table("anexo_migration_status")]
public class AnexoMigrationStatus
{
    [Key]
    [Column("id")]
    public int Id { get; set; }
    
    [Required]
    [Column("anexo_id")]
    public int AnexoId { get; set; }
    
    [Required]
    [Column("nome_anexo")]
    [MaxLength(500)]
    public string NomeAnexo { get; set; } = string.Empty;
    
    [Column("checksum_origem")]
    [MaxLength(100)]
    public string ChecksumOrigem { get; set; } = string.Empty;
    
    [Column("checksum_destino")]
    [MaxLength(100)]
    public string ChecksumDestino { get; set; } = string.Empty;
    
    [Required]
    [Column("status")]
    public StatusEnum Status { get; set; }
    
    [Column("error_message")]
    [MaxLength(2000)]
    public string ErrorMessage { get; set; } = string.Empty;
    
    [Column("retry_count")]
    public int RetryCount { get; set; }

    [Column("processing_pod_id")]
    [MaxLength(500)]
    public string? ProcessingPodId { get; set; } = string.Empty;
    
    [Column("processing_started_at")]
    public DateTime? ProcessingStartedAt { get; set; }
    
    [Column("lock_expires_at")]
    public DateTime? LockExpiresAt { get; set; }
    
    [Column("created_at")]
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    
    [Column("updated_at")]
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    
    [Column("completed_at")]
    public DateTime? CompletedAt { get; set; }
    
}