# 03 — Fluxo ETL

ETL = **E**xtract, **T**ransform, **L**oad. No RePlace:

- **Extract** — ler o BLOB do MySQL.
- **Transform** — calcular MD5, montar nome de arquivo S3.
- **Load** — fazer upload para o S3 e marcar como concluído.

Existem três níveis de visão sobre esse fluxo: o **loop do BackgroundService**, o **processamento de um arquivo individual** e a **máquina de estados** do registro de status. Este documento cobre os três.

## Nível 1 — Loop do BackgroundService

![Fluxo ETL Completo](./flowcharts/2%20-%20Fluxo%20ETL%20Completo.png)

Implementação: [MigrationBackgroundService.ExecuteAsync](../src/Application/Services/MigrationBackgroundService.cs).

O loop principal faz, a cada iteração:

1. **Recarrega settings** do cache (que se auto-atualiza a cada 5 min).
2. **Verifica janela ativa** — se `ActiveWindowEnabled` e o horário atual está fora de `[start, end]` (UTC), dorme 5 minutos e volta ao topo.
3. **Decide o modo de execução**:
   - `LimitedExecution = true` → roda no máximo `MaxRecordsToProcess` arquivos e chama `StopAsync` (encerra o BackgroundService).
   - `LimitedExecution = false` → roda indefinidamente, lote após lote.
4. **Aguarda `BatchInterval`** (padrão 5s) e repete.
5. **Em exceção**: log + `Task.Delay(30s)` antes de tentar de novo (evita loop apertado em erro persistente).

> **Para o dev júnior**: `BackgroundService` é uma classe abstrata do .NET que faz exatamente isto — chama `ExecuteAsync` uma única vez ao subir a app e cancela o `CancellationToken` quando a app para. Tudo o que você precisa fazer é o `while` lá dentro.

### Por que existe o cache de settings?

`MigrationBackgroundService` consulta `settings` em cada iteração. Sem cache, isso seria um `SELECT * FROM migration_settings` por iteração — desnecessário, porque essa tabela quase nunca muda. O [MigrationSettingsCache](../src/Application/Services/MigrationSettingsCache.cs) guarda em memória e recarrega a cada 5 minutos. Dispara recarga assíncrona — nunca bloqueia o loop.

## Nível 2 — Seleção do batch

Antes de processar arquivos, o serviço precisa **reservar** um conjunto deles para si — sem que outro pod pegue os mesmos.

Implementação: [FileMigrationService.GetNextBatchAsync](../src/Application/Services/FileMigrationService.cs#L45).

```sql
SELECT a.*
FROM anexo a
LEFT JOIN anexo_migration_status s ON a.id = s.anexo_id
WHERE s.id IS NULL
   OR (s.status = 'Failed' AND s.retry_count < 3)
ORDER BY a.id ASC
LIMIT {batch_size}
FOR UPDATE SKIP LOCKED;
```

Duas condições de elegibilidade:
- **`s.id IS NULL`** — anexo nunca foi processado (nem tem linha em `anexo_migration_status`).
- **`s.status = 'Failed' AND s.retry_count < 3`** — falhou antes mas ainda tem orçamento de retry.

Após selecionar, o serviço marca cada anexo com `Pending`, grava `processing_pod_id` e `lock_expires_at`, e devolve a lista para processamento.

> **Sobre `FOR UPDATE SKIP LOCKED` (conceito sênior, explicação curta)**: é uma cláusula do MySQL que (1) coloca um lock de escrita nas linhas retornadas e (2) **pula** as linhas que já estão locked por outra transação, em vez de esperar. O efeito prático: cada pod pega um lote diferente, sem coordenação externa e sem fila. Quem chegou primeiro pega; quem chegou depois ignora aquelas linhas. Detalhes em [05 — Resiliência e Concorrência](./05-resiliencia-concorrencia.md).

## Nível 3 — Processamento de um arquivo

![Processamento de Arquivo Individual](./flowcharts/3%20-%20Processamento%20de%20Arquivo%20Individual.png)

Implementação: [FileMigrationService.ProcessSingleAsync](../src/Application/Services/FileMigrationService.cs#L116).

Sequência (cada `MarkAs*` é um `UPDATE` em `anexo_migration_status`):

| # | Ação | Estado resultante |
|---|------|-------------------|
| 1 | `MarkAsExtracting` | `Extracting` |
| 2 | `ExtractAndValidate` — lê BLOB, calcula MD5 | (em memória) |
| 3 | `MarkAsExtracted` — grava `checksum_origem` | `Extracted` |
| 4 | `MarkAsUploading` | `Uploading` |
| 5 | `UploadToS3AndValidate` — sobe via Polly retry, compara ETag vs MD5 | (S3 com objeto) |
| 6 | `SaveFilepath` — grava `anexo.filepath` | (anexo atualizado) |
| 7 | `RemoveFileFromDatabase` — só se `purge_files=true`: zera `anexo.AnexoBlob` | (BLOB nulo) |
| 8 | `MarkAsCompleted` — grava `checksum_destino` e `completed_at` | `Completed` |

**Em qualquer exceção** entre 1 e 8: cai no `catch`, faz log, chama `MarkAsFailed` (que escreve `error_message`, incrementa `retry_count`, vira `Failed`) e relança. O lote continua processando os outros arquivos.

### Por que checksum?

S3 retorna no header `ETag` o MD5 do conteúdo recebido (para uploads simples, não multipart). Comparar `ETag` com o MD5 calculado localmente prova que **o byte que saiu daqui é o mesmo que chegou lá**. Se algum bit corrompeu no caminho, o upload é abortado e o arquivo vai para `Failed`. Sem essa checagem, um upload "bem-sucedido" mas truncado passaria despercebido.

### Paralelismo dentro do batch

Os arquivos do batch são processados em paralelo:

```csharp
var tasks = batch.Select(anexo => ProcessSingleAsync(anexo, ct));
var results = await Task.WhenAll(tasks);
```

Cada `ProcessSingleAsync` abre seu próprio scope de DI ([MigrationBackgroundService.cs:142-157](../src/Application/Services/MigrationBackgroundService.cs#L142)) — isso é obrigatório porque o `DbContext` do EF **não é thread-safe**. Sem scope próprio, várias tasks compartilhariam o mesmo DbContext e quebrariam.

## Nível 4 — Máquina de estados

![Diagrama de Estados](./flowcharts/4%20-%20Diagrama%20de%20Estados.png)

O campo `status` em `anexo_migration_status` é uma enum (`StatusEnum`):

```
Pending → Extracting → Extracted → Uploading → Completed
                                       │
                                       └─→ Failed
                                            │
                                            ├─ retry_count < 3 → Pending (re-selecionado pelo batch)
                                            └─ retry_count ≥ 3 → ABANDONED (não processa mais)
```

> **"Abandoned" não é um estado de banco** — é o nome conceitual do registro que permanece em `Failed` mas não é mais selecionado pelo batch, porque a query exige `retry_count < 3`.

### Onde cada transição acontece

| Transição | Método | Linha |
|-----------|--------|-------|
| (novo) → `Pending` | `UpdateStatusForBatchAsync` | [FileMigrationService.cs:82](../src/Application/Services/FileMigrationService.cs#L82) |
| `Pending` → `Extracting` | `MarkAsExtracting` | [:221](../src/Application/Services/FileMigrationService.cs#L221) |
| `Extracting` → `Extracted` | `MarkAsExtracted` | [:227](../src/Application/Services/FileMigrationService.cs#L227) |
| `Extracted` → `Uploading` | `MarkAsUploading` | [:234](../src/Application/Services/FileMigrationService.cs#L234) |
| `Uploading` → `Completed` | `MarkAsCompleted` | [:240](../src/Application/Services/FileMigrationService.cs#L240) |
| qualquer → `Failed` | `MarkAsFailed` | [:250](../src/Application/Services/FileMigrationService.cs#L250) |

### Política de retry — detalhe importante

`retry_count` é incrementado **apenas em `MarkAsFailed`**. O batch select re-seleciona o registro `Failed` e volta o status para `Pending` **sem** incrementar — assim, três falhas reais resultam em três retries reais, conforme o filtro `retry_count < 3`.


## Continuação após reinício / scale

Quando um pod reinicia ou um novo pod sobe, o ETL **retoma de onde parou** automaticamente. Isso não requer estado externo — está implícito na query do batch:

- Anexos já `Completed` não voltam (não passam no `WHERE`).
- Anexos em `Pending`/`Extracting`/`Extracted`/`Uploading` com lock expirado podem ser re-selecionados quando o filtro precisar.
- Anexos `Failed` com `retry_count < 3` entram na próxima janela.

A ordem do `ORDER BY a.id ASC` garante avanço determinístico — **sem repetições, sem buracos**.

## Próximo passo

Para entender as tabelas e a estrutura no S3 → [04 — Modelo de Dados](./04-modelo-dados.md).
Para entender lock distribuído, retry e checksum em profundidade → [05 — Resiliência e Concorrência](./05-resiliencia-concorrencia.md).
