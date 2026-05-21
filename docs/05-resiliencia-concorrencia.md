# 05 — Resiliência e Concorrência

> **Este documento concentra as decisões arquiteturais que tornam o sistema confiável em produção.** É a leitura mais importante para entender o RePlace além da superfície.

São cinco decisões interligadas:

1. **Concorrência horizontal segura** via `FOR UPDATE SKIP LOCKED` + lock distribuído.
2. **Resiliência em camadas** — Polly no S3, EF Core no MySQL, retry de negócio por arquivo.
3. **Integridade por checksum** — MD5 local comparado ao ETag do S3.
4. **Paralelismo intra-batch** com scope de DI por task.
5. **Limpeza tardia** — `purge_files` só apaga o BLOB *depois* do sucesso confirmado.

## 1. Concorrência horizontal segura

### O problema

Em produção, o RePlace roda como múltiplas réplicas no Kubernetes. Se dois pods perguntarem "quais anexos pendentes existem?" ao mesmo tempo, podem pegar os mesmos. Resultado: mesmo arquivo enviado duas vezes para o S3, duas linhas em `anexo_migration_status`, concorrência em escrita no mesmo registro.

A solução tradicional seria uma **fila externa** (RabbitMQ, SQS) com consumidores competindo. O RePlace evita essa dependência usando o próprio MySQL como coordenador.

### Como funciona — `FOR UPDATE SKIP LOCKED`

Implementação: [FileMigrationService.GetNextBatchAsync](../src/Application/Services/FileMigrationService.cs#L45).

```sql
SELECT a.*
FROM anexo a
LEFT JOIN anexo_migration_status s ON a.id = s.anexo_id
WHERE s.id IS NULL
   OR (s.status = 'Failed' AND s.retry_count < 3)
ORDER BY a.id ASC
LIMIT 100
FOR UPDATE SKIP LOCKED;
```

Duas cláusulas no fim mudam tudo:

- **`FOR UPDATE`** — pede um lock de escrita em cada linha retornada. Enquanto a transação que pediu não terminar (`COMMIT`/`ROLLBACK`), nenhuma outra transação consegue um lock de escrita nessas linhas.
- **`SKIP LOCKED`** — se uma linha já está locked por outra transação, **ignore-a** e siga em frente. Não espera, não bloqueia, não retorna.

**Efeito combinado**:

```
Pod A: SELECT ... LIMIT 100 FOR UPDATE SKIP LOCKED  →  ids 1..100 (lock)
Pod B: SELECT ... LIMIT 100 FOR UPDATE SKIP LOCKED  →  ids 101..200 (pula 1..100, pega os próximos)
Pod C: SELECT ... LIMIT 100 FOR UPDATE SKIP LOCKED  →  ids 201..300
```

**Sem coordenação externa.** Sem disputa. Sem espera. Cada pod sai com seu lote distinto e exclusivo.

### Por que a query também roda dentro de `ExecutionStrategy`

```csharp
var executionStrategy = _dbContext.Database.CreateExecutionStrategy();
return await executionStrategy.ExecuteAsync(async () => { ... });
```

`EnableRetryOnFailure` (ver [Program.cs:21](../Program.cs#L21)) precisa orquestrar transações em caso de falha transitória. Sem `ExecutionStrategy`, queries customizadas com `FromSqlRaw` falham com erro do EF Core. **Não é detalhe estético — é obrigatório**.

### Lock como dado, não como mecanismo

Além do `FOR UPDATE`, o RePlace grava o lock como **dado persistente** em `anexo_migration_status`:

```csharp
status.ProcessingPodId = podId;
status.ProcessingStartedAt = DateTime.UtcNow;
status.LockExpiresAt = lockExpires;
```

O `FOR UPDATE` só vale **enquanto a transação está aberta** — segundos. Após o `COMMIT`, a linha está liberada no MySQL. O que **continua** identificando "estou processando" é o trio `ProcessingPodId` + `ProcessingStartedAt` + `LockExpiresAt`. Isso permite:

- Diagnóstico: quem está processando o quê, há quanto tempo.
- Detecção de pod morto (lock expirado sem heartbeat).
- Futuras evoluções: redistribuição de lotes órfãos (não implementada ainda).

## 2. Resiliência em camadas

Falhas têm três naturezas distintas no RePlace, e cada uma tem o seu retry:

### Camada S3 — falhas transitórias da AWS

Polly com backoff exponencial: [FileMigrationService.cs:28-42](../src/Application/Services/FileMigrationService.cs#L28).

```csharp
_s3RetryPolicy = Policy
    .Handle<AmazonS3Exception>(ex =>
        ex.ErrorCode == "RequestTimeout" ||
        ex.ErrorCode == "ServiceUnavailable" ||
        ex.ErrorCode == "SlowDown" ||
        (int)ex.StatusCode >= 500)
    .Or<HttpRequestException>()
    .WaitAndRetryAsync(
        retryCount: 3,
        sleepDurationProvider: retryAttempt =>
            TimeSpan.FromSeconds(Math.Pow(2, retryAttempt))); // 2s, 4s, 8s
```

**Filtra** o que retentar: timeouts, 5xx, throttle (`SlowDown`). Não retenta `AccessDenied` ou `NoSuchBucket` — esses são erros de configuração, retry é desperdício.

### Camada MySQL — falhas transitórias do banco

Configurado no DbContext: [Program.cs:21-24](../Program.cs#L21).

```csharp
mysqlOptions.EnableRetryOnFailure(
    maxRetryCount: 3,
    maxRetryDelay: TimeSpan.FromSeconds(5),
    errorNumbersToAdd: null);
```

EF Core já conhece os códigos de erro transitórios do MySQL (lock wait timeout, connection refused, etc.). Quando um deles aparece, EF re-executa a operação automaticamente.

### Camada de negócio — falha do arquivo

Se um arquivo específico falha após os retries da camada S3, ele vai para `Failed` com `retry_count++`. Na próxima passagem do batch, é re-selecionado **se `retry_count < 3`**. Veja [FileMigrationService.cs:50-63](../src/Application/Services/FileMigrationService.cs#L50).

**Resumo**: até 3 retentativas de S3 × até 3 retentativas de negócio = até 9 tentativas reais de upload, distribuídas no tempo, antes do abandono.

## 3. Integridade por checksum

Implementação: [FileMigrationService.UploadToS3AndValidate](../src/Application/Services/FileMigrationService.cs#L200).

```csharp
var response = await _s3Service.UploadFileAsync(stream, fileName, contentType);
var etag = response.ETag?.Trim('"');

return etag != expectedChecksum
    ? throw new InvalidOperationException($"Incompatibilidade em soma de verificação. S3: {etag}, Local: {expectedChecksum}")
    : etag;
```

### O fluxo

1. **Lê o BLOB** do MySQL.
2. **Calcula MD5** local → vira `checksum_origem`.
3. **Sobe para S3**.
4. **S3 devolve ETag** — para uploads simples (não multipart), o ETag **é** o MD5 do conteúdo recebido.
5. **Compara**: se diferente, lança exceção → arquivo cai em `Failed`.
6. Se igual, ETag vira `checksum_destino` no banco — prova permanente da integridade.

### Por que isso importa

Sem essa verificação, uma rede instável pode entregar bytes parciais. O S3 confirma o `PutObject` com sucesso, o RePlace marca `Completed`, e o arquivo no S3 está **corrompido**. Anos depois, alguém tenta abrir e não funciona. Com a comparação:

- Bytes corrompidos no caminho → ETag diferente → falha imediata, retry → eventualmente o arquivo bom é confirmado.
- Bug aleatório na rede → mesmo tratamento.

> ⚠️ **Limite conhecido**: para arquivos muito grandes (>5GB), uploads multipart fazem o ETag deixar de ser o MD5 do conteúdo (passa a ser o hash dos hashes das partes). O RePlace atualmente usa upload simples, então esse caso não acontece, mas se o tamanho médio dos anexos aumentar muito, esse contrato precisa ser revisitado.

## 4. Paralelismo intra-batch

```csharp
var tasks = batch.Select(anexo => ProcessSingleAsync(anexo, ct));
var results = await Task.WhenAll(tasks);
```

Os arquivos de um mesmo batch são processados **em paralelo**, não em série. Para um `batch_size = 100`, isso significa 100 tasks .NET concorrendo por banda e CPU.

### Por que cada task abre seu próprio scope

```csharp
private async Task<bool> ProcessSingleAsync(Anexo anexo, CancellationToken ct)
{
    using var scope = scopeFactory.CreateScope();
    var fileService = scope.ServiceProvider.GetRequiredService<IFileMigrationUseCase>();
    ...
}
```

`AppDbContext` é **scoped** e **não é thread-safe**. Sem scope próprio por task, todas as 100 tasks pegariam o *mesmo* DbContext (o do escopo do batch), tentariam fazer queries concorrentes, e o EF Core lançaria `InvalidOperationException`.

> **Para o dev júnior**: pense em DbContext como uma conexão lógica com o banco. Duas pessoas não podem estar "no meio de digitar" uma query nessa conexão ao mesmo tempo. Cada thread precisa do seu.

## 5. Limpeza tardia — `purge_files`

```csharp
if (settings.PurgeFiles)
    await RemoveFileFromDatabase(anexo.Id);

await MarkAsCompleted(anexo.Id, etag);
```

A flag `purge_files`, quando ativa, zera `anexo.AnexoBlob` (`= null`). **Importante**: isso só acontece **depois** que:

1. Upload para S3 retornou sucesso.
2. ETag bateu com o MD5 local.
3. `filepath` foi gravado em `anexo`.

Só então o BLOB é apagado. Se qualquer passo falhar, o BLOB permanece — você nunca perde dados sem ter certeza de que o destino está bom.

> Ordem invertida (apagar antes de confirmar o upload) seria uma forma rápida de **perder arquivos para sempre** em caso de falha. O sistema é deliberadamente conservador.

## Saúde e observabilidade

### Health check

[MigrationHealthCheck](../src/Infrastructure/Config/MigrationHealthCheck.cs) agrega contagens por status e responde no endpoint `/healthcheck` ([Program.cs:42-49](../Program.cs#L42)). Composição:

- Verificação de **conectividade MySQL** (via `AspNetCore.HealthChecks.MySql`).
- Verificação do **serviço de migração** (a custom acima).

Resposta JSON inclui contagens por status — útil para Kubernetes liveness/readiness e para dashboards externos.

### Logs

Estruturados via `ILogger<T>`. Cada arquivo logado carrega o `anexo_id`. Nível controlado por ambiente:
- `Development`: `Information`
- `Production`: `Warning` (apenas erros e eventos importantes)

## Limites conhecidos

| Limite | Impacto | Mitigação atual / sugestão |
|--------|---------|----------------------------|
| Sem rollback automático | Arquivos migrados não voltam ao MySQL | Aceito por design — recuperação seria via re-download do S3 |
| Sem autenticação na API | Endpoints abertos a quem alcançar a rede | Rodar em rede privada / mesh interna |
| Sem detecção de lock órfão | Pod morto deixa `Pending` indefinidamente até `retry_count` ser usado | Migração futura: job de limpeza por `lock_expires_at` |
| Sem métricas externas | Sem Prometheus/Grafana | Endpoint `/api/migration/status` serve para scraping ad-hoc |

## Próximo passo

Para usar a API REST e o health check → [06 — API e Health Checks](./06-api-health.md).
