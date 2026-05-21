# 04 — Modelo de Dados

Três tabelas no MySQL e uma estrutura de pastas no S3. Schema canônico em [`sql/data.sql`](../sql/data.sql). Comandos DML utilitários (inspeção e reset durante desenvolvimento) em [`sql/scriptDML-replace.sql`](../sql/scriptDML-replace.sql).

## Visão geral

```
┌─────────────────┐         ┌────────────────────────────┐
│ anexo           │ 1───────│ anexo_migration_status     │
│ (origem)        │   N     │ (estado da migração)       │
└─────────────────┘         └────────────────────────────┘

┌──────────────────────────┐
│ migration_settings       │   ← singleton (id = 1)
│ (configuração runtime)   │
└──────────────────────────┘
```

## Tabela `anexo` (origem)

A tabela do sistema legado. **O RePlace não cria nem altera o schema desta tabela** — apenas adiciona um índice em `filepath` e lê/escreve em colunas existentes.

Mapeamento: [Anexo.cs](../src/Domain/Models/Anexo.cs).

| Coluna | Tipo | Papel |
|--------|------|-------|
| `id` | INT (PK) | Identificador |
| `nome` | VARCHAR(455) | Nome original do arquivo |
| `tipo_anexo_id` | INT | Categoria (define prefixo no S3 — ver abaixo) |
| `anexo` | BLOB | **Conteúdo do arquivo** — origem da migração |
| `timestamp` | DATETIME | Quando foi criado |
| `tipo` | VARCHAR(100) | MIME type (`application/pdf`, `image/png`, etc.) |
| `tamanho` | INT | Tamanho em bytes |
| `filepath` | VARCHAR(500) | **Preenchido pelo RePlace** após upload bem-sucedido — caminho no S3 |

A coluna `filepath` é a **ponte entre o estado antigo e o novo**: depois da migração, o sistema legado pode usar `filepath` para construir uma URL de download do S3.

Após `MarkAsCompleted`, **se `purge_files = true`**, o RePlace zera `anexo` (BLOB) mas preserva `filepath`. Veja [FileMigrationService.cs:139-141](../src/Application/Services/FileMigrationService.cs#L139).

> **Para regenerar massa em ambiente local** (tabela `anexo` vazia ou com BLOBs já purgados), use o projeto irmão **Phill** — ele popula `anexo` com arquivos reais. Instruções em [08 — Desenvolvimento e Testes, §3.1](./08-desenvolvimento-testes.md#31-popular-anexo-com-massa-de-teste-phill).

## Tabela `anexo_migration_status` (controle)

A "fonte da verdade" do processo. Cada anexo elegível recebe **uma única linha** aqui, atualizada conforme avança nos estados.

Mapeamento: [AnexoMigrationStatus.cs](../src/Domain/Models/AnexoMigrationStatus.cs).

| Coluna | Tipo | Papel |
|--------|------|-------|
| `id` | INT (PK) | Identificador do registro de status |
| `anexo_id` | INT (FK → anexo.id) | A qual anexo se refere — `ON DELETE CASCADE` |
| `nome_anexo` | VARCHAR(500) | Snapshot do nome (evita JOIN para exibir no /status) |
| `status` | ENUM (string no DB) | Estado atual da máquina (ver [03 — Fluxo ETL](./03-fluxo-etl.md)) |
| `checksum_origem` | VARCHAR(100) | MD5 calculado do BLOB lido do MySQL |
| `checksum_destino` | VARCHAR(100) | ETag retornado pelo S3 (deve bater com origem) |
| `retry_count` | INT | Número de falhas acumuladas (limite 3) |
| `error_message` | TEXT | Última mensagem de erro |
| `processing_pod_id` | VARCHAR(500) | Quem está processando (`POD_NAME` ou `MachineName`) |
| `processing_started_at` | DATETIME | Início da reserva atual |
| `lock_expires_at` | DATETIME | Quando o lock expira (heartbeat para detecção de pod morto) |
| `created_at` / `updated_at` | DATETIME | Auditoria |
| `completed_at` | DATETIME | Quando virou `Completed` (NULL antes disso) |

### Índices

```sql
INDEX idx_status (status)
INDEX idx_arquivo_id (anexo_id)
```

`idx_status` serve à query do batch e às agregações de `/api/migration/status`. `idx_arquivo_id` serve ao lookup de status por anexo (`MarkAs*`).

### Tipo `status` — string em vez de int

O EF Core grava a enum como **string** no banco, configurado em [AppDbContext.cs:11-13](../src/Infrastructure/Data/AppDbContext.cs#L11):

```csharp
modelBuilder.Entity<AnexoMigrationStatus>()
    .Property(e => e.Status)
    .HasConversion<string>();
```

Por quê: ler `Completed` no banco é mais útil para um operador de banco do que `4`. E o schema usa `ENUM('Pending', ...)`, que valida do lado do DB também.

## Tabela `migration_settings` (configuração)

Singleton: existe **uma única linha** (`id = 1`), garantido por `CHECK (id = 1)`. Toda a configuração de runtime do ETL vem daqui.

Mapeamento: [MigrationSettings.cs](../src/Domain/Models/MigrationSettings.cs).

| Coluna | Tipo | Default | O que controla |
|--------|------|---------|----------------|
| `active_window_enabled` | BOOLEAN | TRUE | Se `false`, ETL roda 24/7 |
| `active_window_start` | VARCHAR(8) | `'00:00:00'` | Início da janela (UTC) |
| `active_window_end` | VARCHAR(8) | `'07:00:00'` | Fim da janela (UTC) |
| `batch_size` | INT | 100 | Quantos anexos por lote |
| `batch_interval_seconds` | INT | 5 | Pausa entre lotes |
| `lock_timeout_minutes` | INT | 10 | Quanto tempo um lote fica reservado para o pod |
| `limited_execution` | BOOLEAN | FALSE | Se `true`, encerra após `max_records_to_process` |
| `max_records_to_process` | INT | NULL | Limite total (apenas com `limited_execution=true`) |
| `purge_files` | BOOLEAN | FALSE | Se `true`, zera o BLOB após migração bem-sucedida |

### Como ajustar em runtime

```sql
UPDATE migration_settings
SET batch_size = 50,
    batch_interval_seconds = 10,
    active_window_enabled = FALSE
WHERE id = 1;
```

Em até **5 minutos** (intervalo do cache), o BackgroundService aplica os novos valores. Não precisa reiniciar pod.

### Recomendações de tuning

| Situação | Ajuste sugerido |
|----------|-----------------|
| MySQL com carga alta de produção | `active_window_enabled = TRUE`, janela em horário ocioso |
| Bucket S3 lento ou throttling | Aumentar `batch_interval_seconds`, reduzir `batch_size` |
| Quero fazer um "teste de produção" | `limited_execution = TRUE`, `max_records_to_process = 100`, `purge_files = FALSE` |
| Migração final em massa | `active_window_enabled = FALSE`, `batch_size = 200+`, várias réplicas no K8s |
| Pods estão "perdendo" lotes (lock expirando antes do fim) | Aumentar `lock_timeout_minutes` |

## Organização no S3

Estrutura de pastas determinada pelo `tipo_anexo_id` ([FileMigrationService.cs:172-185](../src/Application/Services/FileMigrationService.cs#L172)):

```
{bucket}/
├── imagem/                                  ← tipo 1
│   └── {anexo_id}_{nome_original}
├── planta_laboratorio_aprovado/             ← tipo 2
├── documento/                               ← tipo 3
├── planta_laboratorio_ressalvas/            ← tipo 4
├── termo_de_autorizacao_de_laboratorio/     ← tipo 5
└── outros/                                  ← demais tipos
```

**Convenção de nome**: `{prefixo}/{anexo_id}_{nome}`. Inclui o `id` para garantir unicidade — dois anexos podem ter o mesmo `nome`, mas nunca o mesmo `id`. O nome bruto é mantido para legibilidade humana no console do S3.

> **Observação operacional**: o RePlace **não cria** as pastas. Isso é automático no S3 (não existem pastas de verdade — só prefixos no nome do objeto).

## Próximo passo

Para entender lock distribuído, retry e checksum em profundidade → [05 — Resiliência e Concorrência](./05-resiliencia-concorrencia.md).
