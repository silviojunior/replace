# 08 — Desenvolvimento e Testes

## Pré-requisitos

- **.NET SDK 9.0+**
- **MySQL 8.0+** (local, container Docker, ou remoto)
- Credenciais AWS com permissão de `PutObject` no bucket de destino
- IDE: Rider, Visual Studio 2022 (17.12+) ou VS Code com extensão C# Dev Kit

## Setup em 5 passos

### 1. Clonar e restaurar

```bash
git clone <repo>
cd RePlace
dotnet restore
```

### 2. Subir MySQL local (via Docker)

```bash
docker run -d \
  --name replace-mysql \
  -e MYSQL_ROOT_PASSWORD=seu_pwd \
  -e MYSQL_DATABASE=laboratorio \
  -p 3306:3306 \
  mysql:8.0
```

### 3. Aplicar schema

```bash
mysql -h 127.0.0.1 -u root -p laboratorio < sql/data.sql
```

> Lembre-se: `sql/data.sql` assume que a tabela `anexo` já existe. Para ambiente puramente experimental, crie uma versão simples como descrito em [07 — Configuração](./07-configuracao-deploy.md#banco-de-dados-local).
>
> Para inspecionar estado ou resetar tabelas durante desenvolvimento, há comandos prontos em [`sql/scriptDML-replace.sql`](../sql/scriptDML-replace.sql) — seleção, limpeza de `anexo`/`anexo_migration_status`, reset de auto-increment e toggles de `migration_settings`.

### 3.1 Popular `anexo` com massa de teste (Phill)

O RePlace **só tem o que migrar se existirem linhas em `anexo` com `AnexoBlob` preenchido**. Se você está em qualquer um destes cenários:

- Tabela `anexo` **vazia** (primeira execução, base nova).
- BLOBs já foram **purgados** por uma execução anterior com `purge_files = true` (`anexo.anexo IS NULL`).
- Quer **regenerar** massa para repetir um teste de migração do zero.

…use o projeto irmão **[Phill](../../phill)** (Spring Boot, Java 17). Ele popula `anexo` com arquivos reais (PDFs, imagens, DOCX) embarcados, persistidos como `LONGBLOB`, até atingir um limite de tamanho configurável (padrão 500 MB).

**Resumo do uso:**

```bash
cd C:/dev/phill        # ou o caminho onde clonou o repo
# Definir env vars na Run Configuration da IDE (IntelliJ → Run/Debug Configurations):
#   DB_URL=jdbc:mysql://localhost:3306/laboratorio?useSSL=false&serverTimezone=UTC&allowPublicKeyRetrieval=true
#   DB_USERNAME=root
#   DB_PASSWORD=...
#   JPA_DDL_AUTO=update
#   PHILL_MAX_SIZE=500MB
mvnw.cmd spring-boot:run     # Windows  (ou ./mvnw spring-boot:run em Linux/macOS)
```

Após a execução, confirme com:

```sql
SELECT tipo_anexo_id, COUNT(*) AS qtd, SUM(tamanho) AS bytes
FROM anexo
GROUP BY tipo_anexo_id;
```

Documentação completa em [`phill/docs/phill-documentation.md`](../../phill/docs/phill-documentation.md).

> **Atenção — duplicidade**: o Phill *acrescenta* registros a cada execução (não há controle de unicidade). Se rodar várias vezes seguidas, faça `TRUNCATE TABLE anexo` antes para evitar inflar a base.
>
> **Após purge_files = true**: o Phill não "recompõe" os BLOBs nos registros antigos — ele cria registros *novos*. Os antigos continuam com `anexo IS NULL`. Para repetir um teste do zero, limpe `anexo` e `anexo_migration_status` antes:
> ```sql
> TRUNCATE TABLE anexo_migration_status;
> TRUNCATE TABLE anexo;
> ```

### 4. Configurar credenciais

Veja as opções em [07 — Configuração e Deploy](./07-configuracao-deploy.md#configuração-local-desenvolvimento). Em resumo: **Run Configuration da IDE** com variáveis no padrão `__` é a opção escolhida pelo projeto.

### 5. Rodar

```bash
dotnet run --environment Development
```

Saída esperada:

```
:::: REPLACE - INICIANDO PROCESSO DE ETL ::::
info: MigrationBackgroundService iniciado. Janela: 00:00:00-07:00:00, Batch: 100
info: [EXTRACT & TRANSFORM] - Iniciando o processamento em batch
...
```

Endpoints:
- http://localhost:5026/healthcheck
- http://localhost:5026/api/migration/status
- http://localhost:5026/api/migration/simple/status

> Por padrão, o profile `http` em [launchSettings.json](../Properties/launchSettings.json) sobe na porta 5026.

## Estrutura de testes

```
tests/
└── Unit/
    └── Services/
        ├── FileMigrationServiceTests.cs       (15 testes)
        ├── MigrationBackgroundServiceTests.cs (≈5 testes)
        └── MigrationStatusServiceTests.cs     (7 testes)
```

**Total: 27 testes** cobrindo as três classes-chave de Application/Services.

### Estratégia por classe

| Classe | Estratégia |
|--------|-----------|
| `FileMigrationService` | EF Core **InMemory** + `Mock<IS3Service>` (Moq) |
| `MigrationBackgroundService` | Mocks de `IServiceScopeFactory`, `IFileMigrationUseCase`, `IMigrationSettingsCache` |
| `MigrationStatusService` | EF Core **SQLite in-memory** (mais fiel a comportamentos de agregação) |

### Por que InMemory **e** SQLite?

- **InMemory provider** é mais rápido e ignora SQL — bom para testes de comportamento (transições de estado, fluxos).
- **SQLite in-memory** executa SQL real — bom para testes que dependem de `GROUP BY`, ordenação, tipos. Por isso `MigrationStatusServiceTests` o utiliza.

> O batch select com `FOR UPDATE SKIP LOCKED` **não é testado em integração** atualmente — nenhum dos providers suporta isso. É um gap conhecido. Testes manuais com várias réplicas contra MySQL real validam esse caminho.

## Rodar os testes

```bash
# Todos
dotnet test

# Com saída detalhada
dotnet test --logger "console;verbosity=detailed"

# Apenas uma classe
dotnet test --filter "FullyQualifiedName~FileMigrationServiceTests"

# Apenas um método
dotnet test --filter "FullyQualifiedName~ProcessSingleAsync_ProcessesFileSuccessfully"

# Em modo watch (re-roda ao salvar)
dotnet watch test
```

## Cobertura de código

```bash
# Coletar dados de cobertura
dotnet test --collect:"XPlat Code Coverage" --results-directory ./TestResults

# Instalar a ferramenta de relatório (uma vez)
dotnet tool install -g dotnet-reportgenerator-globaltool

# Gerar HTML
reportgenerator \
  -reports:"./TestResults/**/coverage.cobertura.xml" \
  -targetdir:"./CoverageReport" \
  -reporttypes:Html
```

Abra `CoverageReport/index.html`.

## Áreas cobertas

✅ Extração e validação de BLOB
✅ Cálculo de MD5 e comparação de checksum
✅ Upload simulado para S3
✅ Todas as transições de status (`MarkAs*`)
✅ Tratamento de erros (`MarkAsFailed`)
✅ Loop do BackgroundService (batch, limite, janela)
✅ Agregações de `/api/migration/status`
✅ Comportamento intencional de `ProgressPercentage = 0` no endpoint simples
✅ Persistência de `filepath`
✅ `purge_files` zerando o BLOB

## Áreas **não** cobertas (gaps conhecidos)

⚠️ Lock distribuído (`FOR UPDATE SKIP LOCKED`) — requer MySQL real
⚠️ Retry policies do Polly em cenários concretos do S3
⚠️ Controllers HTTP (`AnexoMigrationStatusController`)
⚠️ Health checks como endpoint completo
⚠️ Performance e carga
⚠️ Reinício e retomada (integration test end-to-end)

A expansão dessa cobertura está sugerida em [05 — Resiliência e Concorrência](./05-resiliencia-concorrencia.md#limites-conhecidos).

## Padrões de teste

Estilo seguido no projeto:

```csharp
[Fact]
public async Task Method_ExpectedBehavior_Context()
{
    // Arrange
    await using var context = CreateInMemoryContext();
    var service = new FileMigrationService(context, _s3ServiceMock.Object, _loggerMock.Object);
    // ... dados de teste

    // Act
    var result = await service.SomeMethod(...);

    // Assert
    Assert.Equal(expected, result);
}
```

- **Nome do método**: `O_que_resultado_quando`. Em pt-en misturado, está ok no projeto (`ProcessSingleAsync_MarksAsFailed_OnException`).
- **InMemory por teste**: cada teste cria seu próprio `DbContext` com banco isolado (`Guid.NewGuid().ToString()`). Sem efeito colateral entre testes.
- **Mocks via Moq**: nenhuma dependência externa (S3, MySQL real) é necessária para `dotnet test`.

## Troubleshooting comum

### Container .NET não inicia

```bash
docker logs replace-api      # Ver causa
docker inspect replace-api   # Configurações aplicadas
```

Causa frequente: variáveis de ambiente com `_` simples em vez de `__`. Veja [07 — Configuração](./07-configuracao-deploy.md#variáveis-de-ambiente--padrão-de-nomenclatura).

### Falha de conexão com MySQL

- Verifique `ConnectionStrings__DefaultConnection`.
- Para MySQL 8 com `caching_sha2_password`: incluir `AllowPublicKeyRetrieval=true;SslMode=None` na connection string para dev local.
- Teste a conexão fora do app: `mysql -h <host> -P <port> -u <user> -p`.

### Falha de upload para S3

- Confirme credenciais com `aws s3 ls s3://<bucket>` (AWS CLI configurada com as mesmas chaves).
- Confirme região: alguns buckets `us-east-1` aceitam clientes de qualquer região; `sa-east-1` exige cliente na região correta.
- Verifique `AccessDenied` no log — a política IAM precisa permitir `s3:PutObject` no bucket.

### Processamento muito lento

- Aumentar `batch_size` em `migration_settings` (default 100).
- Reduzir `batch_interval_seconds`.
- Rodar múltiplas réplicas (cada uma pega lote diferente — ver [05](./05-resiliencia-concorrencia.md)).
- Verificar latência do MySQL e do S3 separadamente.

### Locks expirados sem progresso

- Aumentar `lock_timeout_minutes` se uploads grandes demoram mais que 10 minutos.
- Verificar se algum pod está em estado de `CrashLoopBackOff` no K8s.
- (Futuro) — Implementar job de varredura para liberar locks órfãos.

### Arquivo individual repetidamente em `Failed`

Cheque `error_message` em `anexo_migration_status`. Causas comuns:
- BLOB vazio ou corrompido na origem (`Conteúdo do anexo vazio ou nulo`).
- ETag não bate (rede instável → relança automaticamente, mas se persistir é problema do bucket).
- `AccessDenied` ao tentar `PutObject` (problema de IAM, não vai resolver com retry).

Após 3 falhas, o anexo é abandonado. Reset manual:

```sql
UPDATE anexo_migration_status
SET status = 'Pending', retry_count = 0, error_message = ''
WHERE anexo_id = <id>;
```

## Comandos úteis (cheat sheet)

```bash
# Status do ETL
curl http://localhost:5026/api/migration/status | jq

# Health check
curl http://localhost:5026/healthcheck | jq

# Ver linhas com erro
mysql -e "SELECT anexo_id, retry_count, error_message FROM anexo_migration_status WHERE status='Failed'"

# Resetar TODOS os abandonados (cuidado)
mysql -e "UPDATE anexo_migration_status SET status='Pending', retry_count=0 WHERE status='Failed'"

# Pausar processamento imediato (sem redeployment)
mysql -e "UPDATE migration_settings SET active_window_enabled=TRUE, active_window_start='23:59:00', active_window_end='23:59:30' WHERE id=1"

# Acessar S3 e listar
aws s3 ls s3://<bucket>/documento/ --recursive
```

## Próximo passo

Você está no fim do caminho linear. Para revisitar áreas específicas, volte ao [índice](./README.md).
