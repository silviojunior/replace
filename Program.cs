using System.Text.Json;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.EntityFrameworkCore;
using RePlace.Application.Services;
using RePlace.Application.UseCases;
using RePlace.Infrastructure.Config;
using RePlace.Infrastructure.Data;

var builder = WebApplication.CreateBuilder(args);

// Configuração do MySQL
builder.Services.AddDbContext<AppDbContext>(options =>
{
    var connectionString = builder.Configuration.GetConnectionString("DefaultConnection");
    
    options.UseMySql(
        connectionString,
        ServerVersion.AutoDetect(connectionString),
        mysqlOptions =>
        {
            mysqlOptions.EnableRetryOnFailure(
                maxRetryCount: 3,
                maxRetryDelay: TimeSpan.FromSeconds(5),
                errorNumbersToAdd: null);
            
            mysqlOptions.CommandTimeout(30);
        }
    );
});

// Adicionando os serviços no container
builder.Services.AddOpenApi();
builder.Services.AddSingleton<IMigrationSettingsCache, MigrationSettingsCache>();
builder.Services.AddScoped<IS3Service, S3Service>();
builder.Services.AddScoped<IFileMigrationUseCase, FileMigrationService>();
builder.Services.AddScoped<IMigrationStatusUseCase, MigrationStatusService>();
builder.Services.AddHostedService<MigrationBackgroundService>();
builder.Services.AddControllers();
builder.Services.AddHttpClient();

// Registrar health checks
builder.Services.AddHealthChecks()
    .AddMySql(
        connectionString: builder.Configuration.GetConnectionString("DefaultConnection")!,
        name: "mysql",
        tags: ["database", "critical"])
    .AddCheck<MigrationHealthCheck>(
        name: "migration-status",
        tags: ["background-service"]);

var app = builder.Build();

app.MapControllers();

// Mapear endpoint
app.MapHealthChecks("/healthcheck", new HealthCheckOptions
{
    ResponseWriter = async (context, report) =>
    {
        context.Response.ContentType = "application/json";
        var result = JsonSerializer.Serialize(new
        {
            status = report.Status.ToString(),
            checks = report.Entries.Select(e => new
            {
                name = e.Key,
                status = e.Value.Status.ToString(),
                description = e.Value.Description,
                duration = e.Value.Duration.ToString(),
                data = e.Value.Data
            }),
            totalDuration = report.TotalDuration.ToString()
        });
        await context.Response.WriteAsync(result);
    }
});

app.UseHttpsRedirection();
app.Run();
