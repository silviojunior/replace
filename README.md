# RePlace API

API .NET 9 que migra arquivos binários (anexos) armazenados como `BLOB` no MySQL para o Amazon S3, com lock distribuído, verificação de integridade por checksum e operação contínua via background service.

> **Não é uma API de arquivos.** É um agente de migração que roda sozinho e expõe endpoints REST somente para consulta de status e health check.

## Quick Start

```bash
# 1. Restaurar dependências
dotnet restore

# 2. Subir MySQL e aplicar schema
mysql -u root -p laboratorio < data.sql

# 3. Configurar variáveis de ambiente (na Run Configuration da IDE)
#    Use padrão `__` (duplo underscore) — ver docs/07
#    ASPNETCORE_ENVIRONMENT=Development
#    ConnectionStrings__DefaultConnection=Server=...
#    AWS__AccessKey=... / AWS__SecretKey=... / AWS__Region=... / AWS__BucketName=...

# 4. Rodar
dotnet run --environment Development
```

Endpoints (porta padrão `5026`):
- `GET /healthcheck` — saúde geral
- `GET /api/migration/status` — estatísticas detalhadas
- `GET /api/migration/simple/status` — resumo rápido

## Documentação

Toda a documentação técnica está em [`docs/`](./docs/README.md), organizada em sequência progressiva. Por onde começar:

- **Primeira leitura / dev júnior**: [docs/README.md](./docs/README.md) e siga a ordem dos documentos.
- **Dev sênior com pressa**: [docs/02-arquitetura.md](./docs/02-arquitetura.md) → [docs/05-resiliencia-concorrencia.md](./docs/05-resiliencia-concorrencia.md).
- **Vou implantar**: [docs/07-configuracao-deploy.md](./docs/07-configuracao-deploy.md).

## Estrutura do projeto

```
RePlace/
├── docs/                       # Documentação técnica completa
├── src/
│   ├── Application/            # Use cases, serviços e BackgroundService
│   ├── Domain/                 # Entidades e enums
│   ├── Infrastructure/         # EF Core, S3, health checks
│   └── Presentation/           # Controllers e DTOs
├── tests/Unit/                 # 27 testes unitários
├── Program.cs                  # Entry point + DI
├── Dockerfile                  # Build multi-stage
├── docker-compose.yml          # Orquestração local
├── data.sql                    # Schema MySQL
└── .env.example                # Template de variáveis de ambiente
```

## Stack

.NET 9 · ASP.NET Core · EF Core (Pomelo MySQL) · AWS S3 SDK · Polly · xUnit · Docker

## Testes

```bash
dotnet test                                  # roda os 27 testes
dotnet test --collect:"XPlat Code Coverage"  # com cobertura
```

Detalhes em [docs/08-desenvolvimento-testes.md](./docs/08-desenvolvimento-testes.md).

## Licença

Uso interno. Não distribuir sem autorização.
