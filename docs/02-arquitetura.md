# 02 — Arquitetura

## Estilo arquitetural

O RePlace segue **Clean Architecture** — também conhecida como arquitetura em camadas com dependências apontando para o domínio. A regra básica:

> Camada externa conhece a camada interna, nunca o contrário.

Em prática, isso significa que `Domain` (modelos de negócio) não importa nada de `Infrastructure` (banco, S3), e `Application` (regras) não importa `Presentation` (HTTP).

## As quatro camadas

```
┌──────────────────────────────────────────────────────────┐
│  Presentation                                            │
│  Controllers, DTOs — fronteira HTTP                      │
└──────────────────────────────────────────────────────────┘
                      │ usa
                      ▼
┌──────────────────────────────────────────────────────────┐
│  Application                                             │
│  Use cases, serviços, BackgroundService — regras         │
└──────────────────────────────────────────────────────────┘
                      │ usa
                      ▼
┌──────────────────────────────────────────────────────────┐
│  Domain                                                  │
│  Entidades (Anexo, AnexoMigrationStatus), enums          │
└──────────────────────────────────────────────────────────┘
                      ▲
                      │ implementa
┌──────────────────────────────────────────────────────────┐
│  Infrastructure                                          │
│  EF Core DbContext, S3Service, HealthCheck — adapters    │
└──────────────────────────────────────────────────────────┘
```

> **Para o dev júnior**: pense na seta como "depende de". `Presentation` só precisa saber que existe um caso de uso (`IFileMigrationUseCase`). Não sabe quem implementa nem onde os dados estão.

### Mapeamento físico (pastas)

```
src/
├── Domain/Models/           ← Camada Domain
│   ├── Anexo.cs
│   ├── AnexoMigrationStatus.cs
│   ├── MigrationSettings.cs
│   └── StatusEnum.cs
│
├── Application/             ← Camada Application
│   ├── UseCases/            ← Interfaces (contratos)
│   │   ├── IFileMigrationUseCase.cs
│   │   └── IMigrationStatusUseCase.cs
│   └── Services/            ← Implementações
│       ├── FileMigrationService.cs
│       ├── MigrationBackgroundService.cs
│       ├── MigrationSettingsCache.cs
│       └── MigrationStatusService.cs
│
├── Infrastructure/          ← Camada Infrastructure
│   ├── Data/
│   │   └── AppDbContext.cs
│   └── Config/
│       ├── S3Service.cs / IS3Service.cs
│       └── MigrationHealthCheck.cs
│
└── Presentation/            ← Camada Presentation
    ├── Controllers/
    │   └── AnexoMigrationStatusController.cs
    └── Dto/
        ├── MigrationStatsDto.cs
        ├── MigrationStatusResponseDto.cs
        ├── StatusDetailDto.cs
        └── LastProcessedFileDto.cs
```

## Composição (Program.cs)

Tudo é amarrado por **injeção de dependência** em [Program.cs](../Program.cs):

```csharp
builder.Services.AddDbContext<AppDbContext>(...);            // Infrastructure
builder.Services.AddSingleton<IMigrationSettingsCache, ...>();// Application
builder.Services.AddScoped<IS3Service, S3Service>();          // Infrastructure
builder.Services.AddScoped<IFileMigrationUseCase, FileMigrationService>();
builder.Services.AddScoped<IMigrationStatusUseCase, MigrationStatusService>();
builder.Services.AddHostedService<MigrationBackgroundService>();
```

Três tempos de vida diferentes, propositais:

- **Singleton** — `MigrationSettingsCache`: existe uma única instância para toda a aplicação. Mantém as configurações em memória e recarrega a cada 5 minutos sem precisar consultar o banco a cada uso.
- **Scoped** — serviços que tocam `DbContext`: um *escopo* por requisição HTTP / por iteração de batch. Garante que `DbContext` (não thread-safe) viva o tempo certo.
- **HostedService** — `MigrationBackgroundService`: ciclo de vida ligado à aplicação. Inicia ao subir, para quando o processo é encerrado.

## Stack tecnológica

| Categoria | Tecnologia | Versão |
|-----------|------------|--------|
| Runtime | .NET | 9.0 |
| Framework Web | ASP.NET Core | 9.0 |
| ORM | Entity Framework Core (Pomelo MySQL) | 9.0.x |
| Banco | MySQL | 8.0+ |
| Object Storage | AWS S3 (SDK oficial) | 4.0.x |
| Resiliência | Polly | 8.6.5 |
| Health Checks | AspNetCore.HealthChecks.MySql | 9.0.0 |
| Containerização | Docker (multi-stage) | — |
| Testes | xUnit + Moq + EF InMemory/SQLite | — |

## Comunicação entre camadas

### Interna (dentro do processo)

- **Interfaces como contrato**: nenhum serviço de aplicação depende de um tipo concreto de infra. Tudo passa por `IS3Service`, `IFileMigrationUseCase`, `IMigrationSettingsCache`. Isso é o que torna os testes possíveis sem MySQL nem AWS reais.
- **EF Core** atua como Repository: o `DbContext` expõe `DbSet<T>` e a Application escreve queries LINQ. Para casos específicos (lock distribuído), usamos SQL bruto via `FromSqlRaw` — ver [05 — Resiliência e Concorrência](./05-resiliencia-concorrencia.md).

### Externa (saindo do processo)

- **AWS S3** via SDK oficial (`AmazonS3Client`) com retry policy do Polly por cima.
- **MySQL** via Pomelo, com retry on failure habilitado no DbContext.

### Comunicação assíncrona

Não há filas (RabbitMQ, SQS) nem eventos de domínio. O motor de assincronia é o próprio **`BackgroundService`** com um loop `while (!stoppingToken.IsCancellationRequested)`. Estado e progresso vivem no banco (`anexo_migration_status`).

## Padrões aplicados

| Padrão | Onde |
|--------|------|
| Repository (implícito via EF) | `AppDbContext` |
| Dependency Injection | `Program.cs` + construtores |
| Use Case (Ports & Adapters) | Interfaces em `Application/UseCases/` |
| Singleton com refresh | `MigrationSettingsCache` |
| Retry / Backoff | `Polly` em `FileMigrationService` |
| Lock distribuído | `FOR UPDATE SKIP LOCKED` + `processing_pod_id` |
| State Machine | `StatusEnum` + transições em `FileMigrationService` |

## Próximo passo

Para entender o ciclo de vida de um arquivo dentro do sistema → [03 — Fluxo ETL](./03-fluxo-etl.md).
