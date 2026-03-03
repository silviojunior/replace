# RePlace API - Documentação Técnica

## 1. Visão Geral e Contexto

### Problema que Resolve
O RePlace API é uma solução de migração de dados que resolve o problema de armazenamento de arquivos binários (anexos) em banco de dados MySQL. A aplicação extrai arquivos armazenados como BLOBs no MySQL e os migra para o Amazon S3, otimizando custos de armazenamento e melhorando a performance do banco de dados.

### Utilizadores Principais
- **Sistemas Internos**: Outros sistemas que consomem os endpoints REST para monitorar o status da migração
- **Administradores**: Equipes de infraestrutura e DevOps que monitoram a saúde da aplicação via health checks
- **Serviço Background**: Processo automatizado que executa a migração de forma contínua e controlada

### Objetivos de Negócio e Técnicos
- **Redução de Custos**: Migrar armazenamento de arquivos do MySQL (caro) para S3 (econômico)
- **Performance**: Melhorar a performance do banco de dados removendo dados binários pesados
- **Escalabilidade**: Permitir crescimento sustentável do volume de arquivos
- **Confiabilidade**: Garantir integridade dos dados através de checksums MD5
- **Rastreabilidade**: Manter histórico completo do processo de migração com status detalhados

---

## 2. Arquitetura e Tecnologias

### Estilo Arquitetural
**Clean Architecture / Arquitetura em Camadas**

A aplicação segue os princípios de Clean Architecture com separação clara de responsabilidades:

```
┌─────────────────────────────────────────────────────────┐
│                    Presentation Layer                    │
│              (Controllers, DTOs, HTTP API)               │
└─────────────────────────────────────────────────────────┘
                            ↓
┌─────────────────────────────────────────────────────────┐
│                   Application Layer                      │
│         (Use Cases, Services, Business Logic)            │
└─────────────────────────────────────────────────────────┘
                            ↓
┌─────────────────────────────────────────────────────────┐
│                     Domain Layer                         │
│              (Entities, Enums, Models)                   │
└─────────────────────────────────────────────────────────┘
                            ↓
┌─────────────────────────────────────────────────────────┐
│                  Infrastructure Layer                    │
│         (Database, S3, External Services)                │
└─────────────────────────────────────────────────────────┘
```

**Camadas:**
- **Presentation**: Controllers REST e DTOs para comunicação externa
- **Application**: Lógica de negócio, casos de uso e serviços
- **Domain**: Modelos de domínio e regras de negócio centrais
- **Infrastructure**: Implementações de acesso a dados e serviços externos

### Stack Tecnológica

**Linguagem e Framework:**
- **.NET 9.0** (C#)
- **ASP.NET Core Web API**

**Banco de Dados:**
- **MySQL** (via Pomelo.EntityFrameworkCore.MySql 9.0.0)
- **Entity Framework Core 9.0.12** (ORM)

**Cloud e Armazenamento:**
- **AWS S3** (AWSSDK.S3 4.0.17.1)
- **Amazon SDK** para integração com serviços AWS

**Bibliotecas e Ferramentas:**
- **Polly 8.6.5**: Resiliência e retry policies
- **AspNetCore.HealthChecks.MySql 9.0.0**: Monitoramento de saúde
- **Microsoft.AspNetCore.OpenApi 9.0.12**: Documentação de API

**Testes:**
- **xUnit 2.9.3**: Framework de testes
- **Moq 4.20.72**: Biblioteca de mocking
- **Coverlet 6.0.2**: Cobertura de código
- **EF Core InMemory/SQLite**: Bancos de dados para testes

**Containerização:**
- **Docker**: Multi-stage build para otimização

### Comunicação entre Componentes

**Interna:**
- **Injeção de Dependência**: Comunicação entre camadas via interfaces
- **Background Service**: Processamento assíncrono contínuo
- **Entity Framework Core**: Acesso ao banco de dados

**Externa:**
- **REST API**: Endpoints HTTP para consulta de status
- **AWS S3 SDK**: Upload de arquivos via API da AWS
- **Health Check Endpoints**: Monitoramento via HTTP

---

## 3. Dados e Fluxo de Informação

### Armazenamento de Dados

**Banco de Dados MySQL:**

1. **Tabela `anexo`** (Origem dos dados)
   - Armazena arquivos como BLOBs
   - Campos: id, nome, tipo_anexo_id, anexo (BLOB), timestamp, tipo, tamanho, filepath
   - Campo `filepath`: Armazena o caminho do arquivo no S3 após migração

2. **Tabela `anexo_migration_status`** (Controle de migração)
   - Rastreia o status de cada arquivo migrado
   - Estados: Pending, Extracting, Extracted, Uploading, Completed, Failed
   - Campos de controle: checksum_origem, checksum_destino, retry_count, error_message
   - Lock distribuído: processing_pod_id, lock_expires_at

3. **Tabela `migration_settings`** (Configuração singleton)
   - Controla parâmetros da migração
   - Janela de execução (active_window_start/end)
   - Tamanho de lote (batch_size)
   - Limites de execução (limited_execution, max_records_to_process)
   - Limpeza de arquivos (purge_files): Remove BLOBs do MySQL após migração bem-sucedida

**Amazon S3:**
- Estrutura de pastas por tipo de anexo:
  - `imagem/` (tipo 1)
  - `planta_laboratorio_aprovado/` (tipo 2)
  - `documento/` (tipo 3)
  - `planta_laboratorio_ressalvas/` (tipo 4)
  - `termo_de_autorizacao_de_laboratorio/` (tipo 5)
  - `outros/` (demais tipos)
- Nomenclatura: `{tipo}/{anexo_id}_{nome_arquivo}`

**Cache em Memória:**
- MigrationSettingsCache: Cache de configurações com refresh a cada 5 minutos

### Fluxo Principal da Aplicação

**Processo ETL (Extract, Transform, Load):**

```
┌──────────────────────────────────────────────────────────────┐
│  1. INICIALIZAÇÃO                                            │
│  - Background Service inicia                                 │
│  - Carrega configurações do banco                            │
│  - Verifica janela de execução                               │
└──────────────────────────────────────────────────────────────┘
                            ↓
┌──────────────────────────────────────────────────────────────┐
│  2. SELEÇÃO DE LOTE (Batch)                                  │
│  - Query com FOR UPDATE SKIP LOCKED                          │
│  - Seleciona N registros pendentes ou com falha (retry < 3)  │
│  - Cria/atualiza status com lock distribuído                 │
└──────────────────────────────────────────────────────────────┘
                            ↓
┌──────────────────────────────────────────────────────────────┐
│  3. EXTRAÇÃO (Extract)                                       │
│  - Status: Extracting                                        │
│  - Lê BLOB do MySQL                                          │
│  - Calcula checksum MD5                                      │
│  - Status: Extracted                                         │
└──────────────────────────────────────────────────────────────┘
                            ↓
┌──────────────────────────────────────────────────────────────┐
│  4. UPLOAD (Load)                                            │
│  - Status: Uploading                                         │
│  - Upload para S3 com retry policy (Polly)                   │
│  - Valida ETag (checksum) do S3                              │
│  - Salva filepath no registro do anexo                       │
│  - Remove BLOB do MySQL (se purge_files=true)                │
│  - Status: Completed                                         │
└──────────────────────────────────────────────────────────────┘
                            ↓
┌──────────────────────────────────────────────────────────────┐
│  5. CONTROLE                                                 │
│  - Aguarda intervalo configurado (batch_interval_seconds)    │
│  - Verifica limite de registros (se limited_execution=true)  │
│  - Retorna ao passo 2                                        │
└──────────────────────────────────────────────────────────────┘
```

**Fluxo de Erro:**
- Exceções capturadas e registradas
- Status alterado para `Failed` com mensagem de erro
- Contador de retry incrementado
- Máximo de 3 tentativas por arquivo
- Após 3 falhas, arquivo não é mais processado automaticamente

**Processamento Paralelo:**
- Cada lote é processado em paralelo usando `Task.WhenAll`
- Cada arquivo é processado em uma task independente

### Integrações com Serviços Externos

**AWS S3:**
- **Propósito**: Armazenamento de arquivos migrados
- **Autenticação**: Access Key + Secret Key (ou Session Token para credenciais temporárias)
- **Região**: us-east-1 (configurável)
- **Retry Policy**: 3 tentativas com backoff exponencial
- **Validação**: Comparação de checksums MD5

**MySQL:**
- **Propósito**: Origem dos dados e controle de migração
- **Resiliência**: Retry automático do EF Core (3 tentativas, 5s delay)
- **Timeout**: 30 segundos por comando
- **Lock Distribuído**: FOR UPDATE SKIP LOCKED para concorrência

---

## 4. Implementação e Desenvolvimento

### Preparação do Ambiente de Desenvolvimento

**Pré-requisitos:**
- .NET SDK 9.0 ou superior
- MySQL 8.0+
- Conta AWS com acesso ao S3
- IDE (Visual Studio, Rider, ou VS Code)

**Passo a Passo:**

1. **Clone o repositório**
   ```bash
   git clone <repository-url>
   cd RePlace
   ```

2. **Configure o banco de dados**
   ```bash
   # Execute o script SQL para criar as tabelas
   mysql -u <usuario> -p <database> < data.sql
   ```

3. **Configure as credenciais locais**
   
   Crie o arquivo `appsettings.Local.json` na raiz do projeto:
   ```json
   {
     "ConnectionStrings": {
       "DefaultConnection": "Server=localhost;Port=3306;Database=laboratorios;Uid=seu_usuario;Pwd=sua_senha"
     },
     "AWS": {
       "AccessKey": "SEU_ACCESS_KEY",
       "SecretKey": "SEU_SECRET_KEY",
       "SessionToken": "",
       "Region": "us-east-1",
       "BucketName": "seu-bucket-dev"
     }
   }
   ```
   
   **IMPORTANTE**: Este arquivo está no `.gitignore` e não deve ser commitado.

4. **Restaure as dependências**
   ```bash
   dotnet restore
   ```

5. **Execute a aplicação**
   ```bash
   dotnet run --environment Development
   ```

6. **Acesse os endpoints**
   - Health Check: `http://localhost:5000/healthcheck`
   - Status Detalhado: `http://localhost:5000/api/migration/status`
   - Status Simples: `http://localhost:5000/api/migration/simple/status`

### Variáveis de Ambiente Necessárias

**Desenvolvimento Local:**
Configuradas via `appsettings.Development.json` ou `appsettings.Local.json`

**Produção:**
Injetadas via variáveis de ambiente:

| Variável | Descrição | Exemplo |
|----------|-----------|---------|
| `ASPNETCORE_ENVIRONMENT` | Ambiente de execução | Production |
| `ASPNETCORE_URLS` | URLs de binding | http://+:8080 |
| `ConnectionStrings__DefaultConnection` | String de conexão MySQL | Server=host;Port=3306;Database=db;Uid=user;Pwd=pass |
| `AWS__AccessKey` | AWS Access Key | AKIAIOSFODNN7EXAMPLE |
| `AWS__SecretKey` | AWS Secret Key | wJalrXUtnFEMI/K7MDENG/bPxRfiCYEXAMPLEKEY |
| `AWS__SessionToken` | Token de sessão (opcional) | IQoJb3JpZ2luX2VjEOv... |
| `AWS__Region` | Região AWS | us-east-1 |
| `AWS__BucketName` | Nome do bucket S3 | replace-hml |

**Hierarquia de Configuração:**
1. `appsettings.json` (base, sem credenciais)
2. `appsettings.{Environment}.json` (específico do ambiente)
3. `appsettings.Local.json` (desenvolvimento local, não commitado)
4. Variáveis de ambiente (Docker)

### Testes

**Estrutura de Testes:**
O projeto possui uma suite completa de testes unitários implementada na pasta `tests/`.

**Framework e Ferramentas:**
- **xUnit 2.9.3**: Framework de testes
- **Moq 4.20.72**: Biblioteca de mocking
- **Microsoft.EntityFrameworkCore.InMemory 9.0.12**: Banco de dados em memória para testes
- **Microsoft.EntityFrameworkCore.Sqlite 9.0.12**: SQLite para testes de integração
- **Coverlet 6.0.2**: Cobertura de código
- **Microsoft.NET.Test.Sdk 18.0.1**: SDK de testes

**Organização dos Testes:**
```
tests/
└── Unit/
    └── Services/
        ├── FileMigrationServiceTests.cs
        ├── MigrationBackgroundServiceTests.cs
        └── MigrationStatusServiceTests.cs
```

#### 1. FileMigrationServiceTests

**Cobertura: 15 testes unitários**

Testa o serviço principal de migração de arquivos, cobrindo:

**Validação e Extração:**
- `ExtractAndValidate_ThrowsException_WhenAnexoIsNull`: Valida exceção quando anexo é nulo
- `ExtractAndValidate_ThrowsException_WhenAnexoIsEmpty`: Valida exceção quando anexo está vazio
- `ExtractAndValidate_ReturnsFileAndChecksum_WhenValid`: Valida extração e cálculo de checksum MD5

**Upload para S3:**
- `UploadToS3AndValidate_ReturnsETag_WhenChecksumMatches`: Valida upload bem-sucedido com checksum correto
- `UploadToS3AndValidate_ThrowsException_WhenChecksumMismatch`: Valida falha quando checksums não coincidem

**Processamento Completo:**
- `ProcessSingleAsync_ProcessesFileSuccessfully`: Testa fluxo completo de migração com sucesso
- `ProcessSingleAsync_MarksAsFailed_OnException`: Valida marcação de falha em caso de exceção

**Transições de Status:**
- `MarkAsExtracting_UpdatesStatusToExtracting`: Valida mudança para status Extracting
- `MarkAsExtracted_UpdatesStatusAndChecksum`: Valida mudança para Extracted com checksum
- `MarkAsUploading_UpdatesStatusToUploading`: Valida mudança para status Uploading
- `MarkAsCompleted_UpdatesStatusChecksumAndCompletedAt`: Valida conclusão com checksum e timestamp
- `MarkAsFailed_UpdatesStatusErrorMessageAndRetryCount`: Valida falha com mensagem de erro e contador

**Tratamento de Erros:**
- `MarkAsExtracting_ThrowsException_WhenStatusNotFound`: Valida exceção quando status não existe

**Técnicas Utilizadas:**
- Mock do serviço S3 (IS3Service)
- Banco de dados InMemory para isolamento
- Validação de estados e transições
- Testes de cenários de sucesso e falha

#### 2. MigrationBackgroundServiceTests

**Cobertura: 6 testes unitários**

Testa o serviço background que orquestra o processo de migração:

**Processamento de Lotes:**
- `ProcessBatchAsync_DoesNothing_WhenNoBatchAvailable`: Valida comportamento quando não há registros
- `ProcessBatchAsync_ProcessesBatch_Successfully`: Valida processamento bem-sucedido de lote

**Execução Limitada:**
- `ProcessLimitedBatchAsync_StopsAfterMaxRecords`: Valida parada após atingir limite de registros

**Janela de Execução:**
- `ShouldProcess_RespectsActiveWindow`: Valida respeito à janela de processamento configurada

**Resiliência:**
- `ProcessSingleAsync_ContinuesOnError`: Valida que o processamento continua mesmo com erros individuais

**Técnicas Utilizadas:**
- Mock de IServiceScopeFactory para injeção de dependência
- Mock de IFileMigrationUseCase
- Mock de IMigrationSettingsCache
- Testes assíncronos com CancellationToken
- Validação de comportamento temporal

#### 3. MigrationStatusServiceTests

**Cobertura: 7 testes unitários**

Testa o serviço de consulta de status da migração:

**Status Detalhado:**
- `GetDetailedStatusAsync_ReturnsEmptyStats_WhenNoData`: Valida resposta vazia sem dados
- `GetDetailedStatusAsync_ReturnsCorrectStats_WithData`: Valida estatísticas corretas com dados
- `GetDetailedStatusAsync_CalculatesProgressCorrectly`: Valida cálculo de percentual de progresso
- `GetDetailedStatusAsync_ReturnsLastProcessedFile`: Valida retorno do último arquivo processado

**Status Simples:**
- `GetSimpleStatusAsync_ReturnsEmptyStats_WhenNoData`: Valida resposta vazia sem dados
- `GetSimpleStatusAsync_ReturnsCorrectStats_WithData`: Valida estatísticas corretas com dados

**Técnicas Utilizadas:**
- SQLite em memória para testes mais realistas
- Validação de agregações e cálculos
- Testes de ordenação e filtros
- Validação de DTOs

#### Executar Testes

**Comandos:**
```bash
# Executar todos os testes
dotnet test

# Executar com detalhes
dotnet test --logger "console;verbosity=detailed"

# Executar com cobertura de código
dotnet test --collect:"XPlat Code Coverage"

# Executar testes específicos
dotnet test --filter "FullyQualifiedName~FileMigrationServiceTests"

# Executar no modo watch (desenvolvimento)
dotnet watch test
```

**Cobertura de Código:**
```bash
# Gerar relatório de cobertura
dotnet test --collect:"XPlat Code Coverage" --results-directory ./TestResults

# Instalar ferramenta de relatório (uma vez)
dotnet tool install -g dotnet-reportgenerator-globaltool

# Gerar relatório HTML
reportgenerator -reports:"./TestResults/**/coverage.cobertura.xml" -targetdir:"./CoverageReport" -reporttypes:Html
```

#### Estatísticas de Cobertura

**Total de Testes: 28 testes unitários**

| Serviço | Testes | Cobertura Principal |
|---------|--------|---------------------|
| FileMigrationService | 15 | Extração, validação, upload, transições de status |
| MigrationBackgroundService | 6 | Orquestração, lotes, janela de execução, resiliência |
| MigrationStatusService | 7 | Consultas, agregações, cálculos de progresso |

**Áreas Cobertas:**
- ✅ Validação de entrada e dados
- ✅ Cálculo de checksums MD5
- ✅ Upload para S3 (mockado)
- ✅ Transições de status
- ✅ Tratamento de erros
- ✅ Processamento em lote
- ✅ Janela de execução
- ✅ Execução limitada
- ✅ Consultas e estatísticas
- ✅ Cálculo de progresso

**Áreas para Expansão Futura:**
- ⚠️ Testes de integração end-to-end
- ⚠️ Testes de performance e carga
- ⚠️ Testes de concorrência (lock distribuído)
- ⚠️ Testes de retry policies do Polly
- ⚠️ Testes de controllers (API)
- ⚠️ Testes de health checks
- ⚠️ Testes de funcionalidade purge_files

---

## 5. Deployment e Infraestrutura

### Hospedagem
**Ambiente de Produção**
- Containerização com Docker
- Escalabilidade horizontal
- Alta disponibilidade

### Processo de Deploy

**1. Build da Imagem Docker**

```bash
# Build local
docker build -t replace-api:latest .

# Build com tag de versão
docker build -t replace-api:v1.0.0 .

# Push para registry
docker tag replace-api:latest <registry>/replace-api:latest
docker push <registry>/replace-api:latest
```

**1. Build da Imagem Docker**

```bash
# Build local
docker build -t replace-api:latest .

# Build com tag de versão
docker build -t replace-api:v1.0.0 .

# Push para registry
docker tag replace-api:latest <registry>/replace-api:latest
docker push <registry>/replace-api:latest
```

**Dockerfile Multi-Stage:**
- **Stage 1 (build)**: Compila o código com SDK
- **Stage 2 (publish)**: Publica a aplicação
- **Stage 3 (final)**: Imagem runtime otimizada com ASP.NET Core Runtime
- **Segurança**: Executa como usuário não-root (`USER app`)
- **Variáveis de Ambiente**: Suporta configuração via variáveis de ambiente

**2. Executar Container**

**Opção 1: Docker Run (Produção)**
```bash
# Executar container com variáveis de ambiente
docker run -d \
  -p 8080:8080 \
  -e ASPNETCORE_ENVIRONMENT=Production \
  -e ASPNETCORE_URLS=http://+:8080 \
  -e ConnectionStrings__DefaultConnection="Server=host;Port=3306;Database=db;Uid=user;Pwd=pass" \
  -e AWS__AccessKey="AKIAIOSFODNN7EXAMPLE" \
  -e AWS__SecretKey="wJalrXUtnFEMI/K7MDENG/bPxRfiCYEXAMPLEKEY" \
  -e AWS__Region="us-east-1" \
  -e AWS__BucketName="replace-hml" \
  --name replace-api \
  replace-api:latest
```

**Opção 2: Docker Run (Development)**
```bash
# Executar em modo Development
docker run -d \
  -p 8080:8080 \
  -e ASPNETCORE_ENVIRONMENT=Development \
  -e ASPNETCORE_URLS=http://+:8080 \
  -e ConnectionStrings__DefaultConnection="Server=host;Port=3306;Database=db;Uid=user;Pwd=pass" \
  -e AWS__AccessKey="AKIAIOSFODNN7EXAMPLE" \
  -e AWS__SecretKey="wJalrXUtnFEMI/K7MDENG/bPxRfiCYEXAMPLEKEY" \
  -e AWS__SessionToken="IQoJb3JpZ2luX2VjEOv..." \
  -e AWS__Region="us-east-1" \
  -e AWS__BucketName="replace-hml" \
  --name replace-api \
  replace-api:latest
```

**Opção 3: Docker Compose**
```bash
# Criar arquivo .env a partir do template
cp .env.example .env
# Editar .env com suas credenciais

# Iniciar com docker-compose
docker-compose up -d

# Ver logs
docker-compose logs -f

# Parar
docker-compose down
```

**Comandos Básicos:**
```bash
# Ver logs
docker logs -f replace-api

# Parar container
docker stop replace-api

# Remover container
docker rm replace-api
```

**3. Monitoramento**

```bash
# Health check
curl http://localhost:8080/healthcheck

# Ver logs
docker logs -f replace-api

# Estatísticas de recursos
docker stats replace-api
```

### Health Checks

**Endpoint de Health Check:**
- Endpoint: `/healthcheck`
- Método: GET
- Retorno: Status HTTP 200 (saudável) ou 503 (não saudável)

**Verificações Realizadas:**
1. Conectividade com MySQL
2. Status do serviço de migração
3. Estatísticas de processamento

### Estratégia de CI/CD

**Recomendações:**

1. **Pipeline Sugerido**
   ```yaml
   # Exemplo conceitual
   stages:
     - build
     - test
     - publish
     - deploy
   
   build:
     - dotnet restore
     - dotnet build -c Release
   
   test:
     - dotnet test
   
   publish:
     - docker build -t replace-api:${CI_COMMIT_TAG}
     - docker push replace-api:${CI_COMMIT_TAG}
   
   deploy:
     - docker pull replace-api:${CI_COMMIT_TAG}
     - docker stop replace-api || true
     - docker rm replace-api || true
     - docker run -d --name replace-api replace-api:${CI_COMMIT_TAG}
   ```

2. **Ambientes**
   - **Development**: Deploy automático em commits na branch `develop`
   - **Staging**: Deploy automático em commits na branch `main`
   - **Production**: Deploy manual com aprovação

---

## 6. Aspectos Adicionais de Arquitetura

### Padrões de Design Implementados

**1. Repository Pattern**
- Entity Framework Core atua como repository
- Abstração do acesso a dados

**2. Dependency Injection**
- Inversão de controle total
- Facilita testes e manutenção

**3. Use Case Pattern**
- Interfaces definem contratos de casos de uso
- Separação clara de responsabilidades

**4. Singleton Pattern**
- `MigrationSettings`: Tabela singleton (id=1)
- `MigrationSettingsCache`: Cache singleton em memória

**5. Retry Pattern (Polly)**
- Resiliência em chamadas S3
- Backoff exponencial

**6. Lock Distribuído**
- Controle de concorrência entre múltiplos pods
- `FOR UPDATE SKIP LOCKED` no MySQL
- Timeout de lock configurável

### Resiliência e Confiabilidade

**Estratégias Implementadas:**

1. **Retry Automático**
   - S3: 3 tentativas com backoff exponencial
   - MySQL: 3 tentativas com delay de 5s (EF Core)

2. **Validação de Integridade**
   - Checksum MD5 na origem
   - Validação de ETag no S3
   - Comparação de checksums

3. **Controle de Falhas**
   - Máximo de 3 tentativas por arquivo
   - Mensagens de erro detalhadas
   - Status de falha persistido

4. **Lock Distribuído**
   - Previne processamento duplicado
   - Timeout automático de locks expirados
   - Identificação por instância

5. **Health Checks**
   - Monitoramento contínuo
   - Restart automático em caso de falha
   - Métricas de processamento

### Segurança

**Boas Práticas Implementadas:**

1. **Credenciais**
   - Nunca commitadas no código
   - Armazenadas em Kubernetes Secrets
   - Suporte a credenciais temporárias (Session Token)

2. **Docker Container**
   - Execução como usuário não-root
   - Imagem base oficial da Microsoft
   - Multi-stage build para reduzir superfície de ataque

3. **Banco de Dados**
   - Usuário com privilégios mínimos necessários
   - Timeout de comandos configurado
   - Prepared statements via EF Core

4. **API**
   - Sem autenticação implementada (assumindo rede interna)
   - Recomendação: Implementar autenticação JWT ou API Key

### Performance e Escalabilidade

**Otimizações:**

1. **Processamento em Lote**
   - Batch size configurável (padrão: 100)
   - Processamento paralelo dentro do lote

2. **Cache de Configurações**
   - Refresh a cada 5 minutos
   - Reduz carga no banco de dados

3. **Janela de Execução**
   - Processamento em horários de baixa demanda
   - Configurável por ambiente

4. **Escalabilidade Horizontal**
   - Múltiplas instâncias processando simultaneamente
   - Lock distribuído previne conflitos

5. **Queries Otimizadas**
   - `FOR UPDATE SKIP LOCKED` para concorrência
   - Índices em colunas de busca (status, anexo_id)
   - `AsNoTracking()` para queries read-only

### Monitoramento e Observabilidade

**Logs:**
- Estruturados via ILogger
- Níveis configuráveis por ambiente
- Informações de contexto (anexo_id, instance_id)

**Métricas Disponíveis:**
- Total de registros processados
- Taxa de sucesso/falha
- Progresso percentual
- Último arquivo processado

**Endpoints de Monitoramento:**
- `/healthcheck`: Status geral do sistema
- `/api/migration/status`: Estatísticas detalhadas
- `/api/migration/simple/status`: Resumo rápido

### Limitações e Melhorias Futuras

**Limitações Conhecidas:**

1. **Sem Rollback Automático**
   - Arquivos migrados não são removidos do MySQL automaticamente
   - Requer processo manual de limpeza

2. **Sem Autenticação**
   - Endpoints públicos (assumindo rede interna)

3. **Cobertura de Testes Parcial**
   - 28 testes unitários implementados
   - Faltam testes de integração end-to-end
   - Faltam testes de controllers e health checks
   - Faltam testes de concorrência e performance

4. **Logs Básicos**
   - Sem integração com sistemas de observabilidade (Prometheus, Grafana)

**Melhorias Sugeridas:**

1. **Expandir Testes**
   - Testes de integração end-to-end
   - Testes de controllers e API
   - Testes de concorrência (lock distribuído)
   - Testes de performance e carga
   - Aumentar cobertura para 90%+

2. **Adicionar Autenticação**
   - JWT ou API Key
   - Rate limiting

3. **Observabilidade Avançada**
   - Integração com Prometheus
   - Dashboards no Grafana
   - Distributed tracing (OpenTelemetry)

4. **Limpeza Automática Aprimorada**
   - ✅ Funcionalidade básica implementada (purge_files)
   - ⚠️ Adicionar política de retenção configurável
   - ⚠️ Adicionar opção de limpeza em lote para registros antigos

5. **Notificações**
   - Alertas em caso de falhas críticas
   - Relatórios periódicos de progresso

6. **Compressão**
   - Comprimir arquivos antes do upload para S3
   - Reduzir custos de armazenamento e transferência

7. **Suporte a Múltiplos Buckets**
   - Separação por ambiente ou tipo de arquivo
   - Configuração mais flexível

---

## 7. Configurações Avançadas

### Parâmetros de Migração (Tabela migration_settings)

| Parâmetro | Tipo | Padrão | Descrição |
|-----------|------|--------|-----------|
| `active_window_enabled` | boolean | true | Habilita janela de execução |
| `active_window_start` | string | 00:00:00 | Início da janela (UTC) |
| `active_window_end` | string | 07:00:00 | Fim da janela (UTC) |
| `batch_size` | int | 100 | Quantidade de registros por lote |
| `batch_interval_seconds` | int | 5 | Intervalo entre lotes |
| `lock_timeout_minutes` | int | 10 | Timeout do lock distribuído |
| `limited_execution` | boolean | false | Limita quantidade total de registros |
| `purge_files` | boolean | false | Remove BLOBs do MySQL após migração |
| `max_records_to_process` | int | null | Máximo de registros (se limited_execution=true) |

**Exemplo de Ajuste:**
```sql
UPDATE migration_settings 
SET batch_size = 50, 
    batch_interval_seconds = 10,
    active_window_enabled = false
WHERE id = 1;
```

### Estados do Processo de Migração

```
Pending → Extracting → Extracted → Uploading → Completed
                                              ↓
                                           Failed (retry_count < 3)
                                              ↓
                                           Pending (retry)
```

**Descrição dos Estados:**
- **Pending**: Aguardando processamento
- **Extracting**: Extraindo BLOB do MySQL
- **Extracted**: Extração concluída, checksum calculado
- **Uploading**: Enviando para S3
- **Completed**: Migração concluída com sucesso
- **Failed**: Falha no processo (máximo 3 tentativas)

---

## 8. Troubleshooting

### Problemas Comuns

**1. Container não inicia**
```bash
# Verificar logs
docker logs replace-api

# Verificar status
docker ps -a

# Inspecionar container
docker inspect replace-api
```

**2. Falhas de conexão com MySQL**
- Verificar string de conexão
- Validar credenciais
- Testar conectividade de rede
- Verificar firewall/security groups

**3. Falhas de upload para S3**
- Validar credenciais AWS
- Verificar permissões do bucket
- Confirmar região correta
- Verificar políticas de bucket

**4. Processamento lento**
- Aumentar `batch_size`
- Reduzir `batch_interval_seconds`
- Executar múltiplas instâncias
- Verificar performance do MySQL

**5. Locks expirados**
- Aumentar `lock_timeout_minutes`
- Verificar se containers estão travando
- Analisar logs de erro

### Comandos Úteis

```bash
# Status do container
docker ps

# Logs em tempo real
docker logs -f replace-api

# Executar comando no container
docker exec -it replace-api /bin/bash

# Reiniciar container
docker restart replace-api

# Ver configurações
docker inspect replace-api

# Testar health check
curl http://localhost:8080/healthcheck

# Ver estatísticas de migração
curl http://localhost:8080/api/migration/status
```

---

## 9. Referências e Recursos

### Documentação Oficial
- [.NET 9.0 Documentation](https://docs.microsoft.com/dotnet/)
- [Entity Framework Core](https://docs.microsoft.com/ef/core/)
- [AWS SDK for .NET](https://docs.aws.amazon.com/sdk-for-net/)
- [Polly Documentation](https://github.com/App-vNext/Polly)
- [Docker Documentation](https://docs.docker.com/)

### Arquivos de Configuração
- `appsettings.json`: Configuração base
- `appsettings.Development.json`: Ambiente de desenvolvimento
- `appsettings.Production.json`: Ambiente de produção
- `Dockerfile`: Build da imagem Docker
- `docker-compose.yml`: Orquestração com Docker Compose
- `.env.example`: Template de variáveis de ambiente
- `data.sql`: Schema do banco de dados

### Estrutura de Diretórios
```
RePlace/
├── src/
│   ├── Application/
│   │   ├── Services/          # Implementações de serviços
│   │   └── UseCases/          # Interfaces de casos de uso
│   ├── Domain/
│   │   └── Models/            # Entidades de domínio
│   ├── Infrastructure/
│   │   ├── Config/            # Configurações de infraestrutura
│   │   └── Data/              # Contexto do banco de dados
│   └── Presentation/
│       ├── Controllers/       # Controllers REST
│       └── Dto/               # Data Transfer Objects
├── tests/
│   ├── Unit/
│   │   └── Services/
│   │       ├── FileMigrationServiceTests.cs
│   │       ├── MigrationBackgroundServiceTests.cs
│   │       └── MigrationStatusServiceTests.cs
│   └── Integration/       # Preparado para testes futuros
├── Properties/
│   └── launchSettings.json
├── appsettings.*.json     # Configurações por ambiente
├── Dockerfile             # Build da imagem
├── data.sql               # Schema do banco
├── Program.cs             # Entry point
├── RePlace.csproj         # Projeto .NET
└── RePlace.sln            # Solution
```

---

## Conclusão

O RePlace API é uma solução robusta e escalável para migração de arquivos do MySQL para S3, implementando boas práticas de arquitetura de software, resiliência e observabilidade. A aplicação foi projetada para operar de forma autônoma em ambientes containerizados, com controle fino sobre o processo de migração e capacidade de escalar horizontalmente conforme a demanda.

**Pontos Fortes:**
- Arquitetura limpa e bem estruturada
- Resiliência com retry policies
- Lock distribuído para concorrência
- Validação de integridade com checksums
- Configuração flexível e dinâmica
- Pronto para produção com Docker
- **Suite de testes unitários abrangente (28 testes)**
- **Cobertura de serviços críticos com mocks e banco em memória**

**Próximos Passos Recomendados:**
1. Expandir testes de integração end-to-end
2. Adicionar testes de controllers e health checks
3. Implementar testes de concorrência e performance
4. Adicionar autenticação e autorização
5. Integrar com ferramentas de observabilidade
6. Implementar limpeza automática de BLOBs migrados
7. Adicionar compressão de arquivos
