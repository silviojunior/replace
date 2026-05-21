# 06 — API e Health Checks

A superfície HTTP do RePlace é mínima: **três endpoints**, todos `GET`, todos somente-leitura. Não há endpoints para iniciar, parar ou disparar migração — o ETL roda em background continuamente (ver [03 — Fluxo ETL](./03-fluxo-etl.md)).

| Endpoint | Propósito |
|----------|-----------|
| `GET /healthcheck` | Saúde geral (MySQL + serviço de migração) |
| `GET /api/migration/status` | Estatísticas detalhadas |
| `GET /api/migration/simple/status` | Resumo rápido |

## `GET /healthcheck`

Endpoint padrão para Kubernetes (liveness/readiness probe), load balancers e monitoramento externo. Configurado em [Program.cs:42-76](../Program.cs#L42).

### Composição

Combina dois checks:

1. **`mysql`** — biblioteca `AspNetCore.HealthChecks.MySql`, testa conectividade ao banco. Tag: `database`, `critical`.
2. **`migration-status`** — [MigrationHealthCheck](../src/Infrastructure/Config/MigrationHealthCheck.cs), agrega contagens por status no banco. Tag: `background-service`.

### Resposta — saudável (HTTP 200)

```json
{
  "status": "Healthy",
  "checks": [
    {
      "name": "mysql",
      "status": "Healthy",
      "description": null,
      "duration": "00:00:00.0123456",
      "data": {}
    },
    {
      "name": "migration-status",
      "status": "Healthy",
      "description": "Migration service operational",
      "duration": "00:00:00.0234567",
      "data": {
        "total": 1500,
        "completed": 1200,
        "failed": 5,
        "pending": 295
      }
    }
  ],
  "totalDuration": "00:00:00.0358023"
}
```

### Resposta — não saudável (HTTP 503)

Mesma estrutura, mas com `status: "Unhealthy"` no check que falhou e `description` contendo a causa.

### Cenários esperados

| Cenário | MySQL | Migração | HTTP |
|---------|-------|----------|------|
| Normal | Healthy | Healthy | 200 |
| Banco fora do ar | Unhealthy | Unhealthy (não consegue agregar) | 503 |
| Banco ok, mas exceção na agregação | Healthy | Unhealthy | 503 |

> **Para K8s**: configure liveness para `/healthcheck` com timeout generoso (5s). Não use 503 como sinal para *matar* o pod imediatamente — espere algumas falhas consecutivas (`failureThreshold: 3`) para evitar restart em blip de rede.

## `GET /api/migration/status`

Endpoint detalhado, ideal para **dashboards** e diagnóstico aprofundado.

Controller: [AnexoMigrationStatusController.GetDetailedStatus](../src/Presentation/Controllers/AnexoMigrationStatusController.cs#L12).
Serviço: [MigrationStatusService.GetDetailedStatusAsync](../src/Application/Services/MigrationStatusService.cs#L11).

### Resposta

```json
{
  "summary": {
    "total": 1500,
    "completed": 1200,
    "failed": 5,
    "pending": 295,
    "progressPercentage": 80.0
  },
  "details": [
    { "status": "Completed",  "count": 1200, "lastUpdated": "2026-05-21T03:47:12Z" },
    { "status": "Failed",     "count": 5,    "lastUpdated": "2026-05-21T02:15:33Z" },
    { "status": "Pending",    "count": 295,  "lastUpdated": "2026-05-21T03:50:01Z" },
    { "status": "Extracting", "count": 0,    "lastUpdated": null },
    { "status": "Uploading",  "count": 0,    "lastUpdated": null }
  ],
  "lastProcessedFile": {
    "anexoId": 4087,
    "nomeAnexo": "planta_aprovada_2026.pdf",
    "completedAt": "2026-05-21T03:47:12Z"
  }
}
```

| Campo | Significado |
|-------|-------------|
| `summary.total` | Total de registros em `anexo_migration_status` (não em `anexo`) |
| `summary.completed` / `failed` / `pending` | Contagens diretas |
| `summary.progressPercentage` | `completed × 100 / total`, arredondado a 2 casas |
| `details[]` | Uma entrada por status presente; `lastUpdated` é o `MAX(updated_at)` daquela faixa |
| `lastProcessedFile` | Último `Completed` (mais recente por `CompletedAt`) — pode ser `null` se nada foi concluído |

> Note que `summary.total` **não** é o total de anexos no banco. É o total de **registros de status** — anexos que entraram em algum estado do fluxo. Para saber quanto ainda falta, é preciso comparar com `COUNT(*) FROM anexo`.

## `GET /api/migration/simple/status`

Endpoint simplificado — retorna apenas contagens, sem detalhe por status e sem `lastProcessedFile`.

Controller: [AnexoMigrationStatusController.GetSimpleStatus](../src/Presentation/Controllers/AnexoMigrationStatusController.cs#L20).

### Resposta

```json
{
  "total": 1500,
  "completed": 1200,
  "failed": 5,
  "pending": 295,
  "progressPercentage": 0
}
```

### Observação importante

`progressPercentage` é **sempre 0** no endpoint simples — por design. O endpoint é otimizado para scrapers de alta frequência que só querem contagens; o cálculo de progresso fica reservado ao `/api/migration/status`. Há um teste documentando esse contrato: `GetSimpleStatusAsync_AlwaysReturnsZeroProgress_ByDesign` em [MigrationStatusServiceTests.cs](../tests/Unit/Services/MigrationStatusServiceTests.cs).

Se você precisa do percentual, use o endpoint detalhado.

## Sem autenticação

Nenhum dos endpoints exige token, header ou credencial. Isso é **intencional**, dentro do escopo atual:

- O RePlace é desenhado para rodar em **rede interna** (cluster K8s, VPC privada).
- A API não expõe nem modifica dados de negócio — só consulta status agregado.
- Health checks precisam ser anônimos para load balancers funcionarem.

**Se o RePlace for exposto a uma rede pública**, adicionar autenticação (API Key, JWT, mTLS) **é obrigatório** — é uma das melhorias futuras listadas em [05 — Resiliência e Concorrência](./05-resiliencia-concorrencia.md#limites-conhecidos).

## Códigos de resposta

| Código | Quando |
|--------|--------|
| 200 OK | Todos os endpoints, em operação normal |
| 503 Service Unavailable | Apenas `/healthcheck`, quando MySQL ou migração reportam Unhealthy |
| 500 Internal Server Error | Exceção não tratada em qualquer endpoint (raro — agregações são resilientes) |

Não há 4xx — não há autenticação nem validação de entrada (não há entrada).

## Próximo passo

Para configurar credenciais e variáveis de ambiente → [07 — Configuração e Deploy](./07-configuracao-deploy.md).
