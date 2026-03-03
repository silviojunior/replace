# RePlace API

API para migração de arquivos do MySQL para S3.

## Configuração de Ambientes

### Desenvolvimento Local

1. Crie um arquivo `appsettings.Local.json` (não commitado):
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

**IMPORTANTE**: Este arquivo está no `.gitignore` e não deve ser commitado. Nunca commite credenciais reais nos arquivos de configuração.

2. Execute:
```bash
dotnet run --environment Development
```

### Produção (Kubernetes)

As credenciais são injetadas via variáveis de ambiente do Kubernetes Secret.

```bash
# Build da imagem
docker build -t replace-api:latest .

# Deploy
kubectl apply -f k8s-secret.yaml
kubectl apply -f k8s-deployment.yaml
```

## Hierarquia de Configuração

1. `appsettings.json` - Base (sem credenciais)
2. `appsettings.{Environment}.json` - Específico do ambiente
3. `appsettings.Local.json` - Local (não commitado)
4. Variáveis de ambiente - Kubernetes/Docker

**ATENÇÃO**: O arquivo `appsettings.Development.json` contém credenciais temporárias da AWS. Estas credenciais devem ser rotacionadas regularmente e nunca devem ser commitadas em produção.

## Parâmetros de Migração

A tabela `migration_settings` controla o comportamento da migração:

- `active_window_enabled`: Habilita janela de execução
- `active_window_start/end`: Horário de processamento (UTC)
- `batch_size`: Quantidade de registros por lote (padrão: 100)
- `batch_interval_seconds`: Intervalo entre lotes (padrão: 5)
- `lock_timeout_minutes`: Timeout do lock distribuído (padrão: 10)
- `limited_execution`: Limita quantidade total de registros
- `purge_files`: Remove BLOBs do MySQL após migração bem-sucedida
- `max_records_to_process`: Máximo de registros (se limited_execution=true)

## Funcionalidades

- Migração de arquivos BLOB do MySQL para S3
- Validação de integridade com checksums MD5
- Lock distribuído para processamento concorrente
- Retry automático com backoff exponencial
- Janela de execução configurável
- Limpeza automática de BLOBs (opcional via purge_files)
- Rastreamento de filepath no S3

## Health Check

- Endpoint: `GET /healthcheck`
- Verifica: MySQL, S3, Status da migração

## Endpoints da API

### Status Detalhado
```bash
GET /api/migration/status
```
Retorna estatísticas completas da migração com detalhes por status.

### Status Simples
```bash
GET /api/migration/simple/status
```
Retorna resumo rápido das estatísticas.

## Executar Testes

```bash
# Executar todos os testes
dotnet test

# Executar com detalhes
dotnet test --logger "console;verbosity=detailed"

# Executar com cobertura de código
dotnet test --collect:"XPlat Code Coverage"
```

## Estrutura do Projeto

```
RePlace/
├── src/
│   ├── Application/       # Serviços e casos de uso
│   ├── Domain/            # Modelos de domínio
│   ├── Infrastructure/    # Acesso a dados e serviços externos
│   └── Presentation/      # Controllers e DTOs
├── tests/
│   └── Unit/              # Testes unitários (28 testes)
├── docs/                  # Documentação técnica completa
├── Dockerfile             # Build da imagem Docker
├── docker-compose.yml     # Orquestração
└── data.sql               # Schema do banco de dados
```

## Documentação Completa

Para documentação técnica detalhada, consulte:
- [docs/replace-doc.md](docs/replace-doc.md) - Documentação técnica completa
