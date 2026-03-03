using System.Reflection;
using Microsoft.EntityFrameworkCore;
using RePlace.Domain.Models;

namespace RePlace.Infrastructure.Data;

public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<AnexoMigrationStatus>()
            .Property(e => e.Status)
            .HasConversion<string>();
        
        base.OnModelCreating(modelBuilder);
        
        modelBuilder.ApplyConfigurationsFromAssembly(Assembly.GetExecutingAssembly());
    }

    public DbSet<AnexoMigrationStatus> AnexoMigrationStatus { get; set; }
    public DbSet<Anexo> Anexos { get; set; }
    public DbSet<MigrationSettings> MigrationSettings { get; set; }
}