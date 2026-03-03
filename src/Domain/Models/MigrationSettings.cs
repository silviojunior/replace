using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Text.Json.Serialization;

namespace RePlace.Domain.Models;

[Table("migration_settings")]
public class MigrationSettings
{
    [Key]
    [Column("id")]
    public int Id { get; set; }
    
    [Required]
    [Column("active_window_enabled")]
    public bool ActiveWindowEnabled { get; set; } = true;
    
    [Required]
    [Column("active_window_start")]
    [MaxLength(8)]
    public string ActiveWindowStart { get; set; } = "00:00:00";
    
    [Required]
    [Column("active_window_end")]
    [MaxLength(8)]
    public string ActiveWindowEnd { get; set; } = "07:00:00";
    
    [Required]
    [Column("batch_size")]
    public int BatchSize { get; set; } = 100;
    
    [Required]
    [Column("batch_interval_seconds")]
    public int BatchIntervalSeconds { get; set; } = 5;
    
    [Required]
    [Column("lock_timeout_minutes")]
    public int LockTimeoutMinutes { get; set; } = 10;
    
    [Required]
    [Column("limited_execution")]
    public bool LimitedExecution { get; set; }
    
    [Column("purge_files")]
    public bool PurgeFiles { get; set; }
    
    [Column("max_records_to_process")]
    public int? MaxRecordsToProcess { get; set; }
    
    [Column("created_at")]
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    
    [Column("updated_at")]
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    
    [JsonIgnore]
    [NotMapped]
    public TimeSpan ActiveWindowStartTime => TimeSpan.Parse(ActiveWindowStart);
    
    [JsonIgnore]
    [NotMapped]
    public TimeSpan ActiveWindowEndTime => TimeSpan.Parse(ActiveWindowEnd);
    
    [JsonIgnore]
    [NotMapped]
    public TimeSpan BatchInterval => TimeSpan.FromSeconds(BatchIntervalSeconds);
    
    [JsonIgnore]
    [NotMapped]
    public TimeSpan LockTimeout => TimeSpan.FromMinutes(LockTimeoutMinutes);
}