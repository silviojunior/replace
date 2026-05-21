# 07 — Configuração e Deploy

## Como configuração funciona no RePlace

O `WebApplication.CreateBuilder` ([Program.cs:9](../Program.cs#L9)) usa o sistema de configuração padrão do .NET, que **encadeia múltiplas fontes** em ordem. Cada fonte sobrescreve as anteriores:

```
1. appsettings.json                          (base)
2. appsettings.{Environment}.json            (override por ambiente)
3. User Secrets                              (apenas Development)
4. Variáveis de ambiente                     (override final)
5. Argumentos de linha de comando            (override absoluto)
```

> **Para o dev júnior**: isso significa que você pode definir um valor no `appsettings.json` e sobrescrevê-lo via variável de ambiente em produção, **sem alterar o arquivo**. É a estratégia recomendada para credenciais.

## Os três `appsettings.*.json`

| Arquivo | Commited? | Contém o quê |
|---------|-----------|--------------|
| `appsettings.json` | sim | Logging base, `AllowedHosts`. **Sem credenciais nem connection string** |
| `appsettings.Development.json` | sim | Apenas overrides de logging para dev |
| `appsettings.Production.json` | sim | Apenas overrides de logging para produção |
| `appsettings.Local.json` | **NÃO** (em `.gitignore`) | Opcional — local dev sem env vars |

**Decisão importante**: o repositório **não** mantém credenciais em nenhum arquivo tracked. Toda configuração sensível vem por variável de ambiente, mesmo em dev.

## Variáveis de ambiente — padrão de nomenclatura

O .NET converte `__` (duplo underscore) em separador de seção. Para preencher:

```json
{
  "AWS": {
    "AccessKey": "..."
  }
}
```

a variável precisa se chamar:

```
AWS__AccessKey
```

> ⚠️ **`AWS_ACCESS_KEY` (underscore simples) NÃO funciona** — não é mapeado para `AWS:AccessKey`. Esse foi um erro comum durante a configuração inicial do projeto.

### Lista completa de variáveis

| Variável | Onde é usada | Obrigatória? |
|----------|--------------|--------------|
| `ASPNETCORE_ENVIRONMENT` | Seleciona qual `appsettings.{Env}.json` carregar | Sim |
| `ASPNETCORE_URLS` | Bindings HTTP (ex.: `http://+:8080`) | Em Docker, sim |
| `ConnectionStrings__DefaultConnection` | MySQL — string de conexão completa | **Sim** |
| `AWS__AccessKey` | Credencial AWS | **Sim** |
| `AWS__SecretKey` | Credencial AWS | **Sim** |
| `AWS__SessionToken` | Token de sessão AWS (opcional, para creds temporárias) | Não |
| `AWS__Region` | Região do bucket (ex.: `sa-east-1`) | **Sim** |
| `AWS__BucketName` | Nome do bucket de destino | **Sim** |

`.env.example` na raiz do projeto serve de template — **copie para `.env` e preencha**, ou use o mecanismo da sua IDE.

## Configuração local (desenvolvimento)

### Opção A — Run Configuration da IDE (Rider / Visual Studio)

A escolha do projeto. Em **Edit Run Configuration → Environment variables**, cole:

```
ASPNETCORE_ENVIRONMENT=Development
ConnectionStrings__DefaultConnection=Server=127.0.0.1;Port=3306;Database=laboratorio;Uid=root;Pwd=seu_pwd;AllowPublicKeyRetrieval=true;SslMode=None
AWS__AccessKey=...
AWS__SecretKey=...
AWS__Region=sa-east-1
AWS__BucketName=replace-storage
```

**Atenção**: use `__` (duplo underscore) exatamente como acima.

### Opção B — User Secrets do .NET

Alternativa nativa do .NET. Não precisa configurar nada na IDE:

```bash
dotnet user-secrets init
dotnet user-secrets set "ConnectionStrings:DefaultConnection" "Server=...;..."
dotnet user-secrets set "AWS:AccessKey" "..."
dotnet user-secrets set "AWS:SecretKey" "..."
dotnet user-secrets set "AWS:Region" "sa-east-1"
dotnet user-secrets set "AWS:BucketName" "replace-storage"
```

Os valores ficam em `%APPDATA%\Microsoft\UserSecrets\{id}\secrets.json` — **fora do repo**, carregados automaticamente em Development.

### Opção C — `appsettings.Local.json`

Para quem prefere arquivo. Crie na raiz:

```json
{
  "ConnectionStrings": {
    "DefaultConnection": "Server=..."
  },
  "AWS": {
    "AccessKey": "...",
    "SecretKey": "..."
  }
}
```

O arquivo já está no [`.gitignore`](../.gitignore). Configure no `Program.cs`/`appsettings` para carregá-lo (não está habilitado por padrão — exigiria `AddJsonFile("appsettings.Local.json", optional: true)`).

## Banco de dados local

Os scripts SQL ficam no diretório [`sql/`](../sql/) na raiz do projeto:

| Arquivo | Propósito |
|---------|-----------|
| [`sql/data.sql`](../sql/data.sql) | Schema (DDL) das tabelas de controle + seed de `migration_settings` |
| [`sql/scriptDML-replace.sql`](../sql/scriptDML-replace.sql) | DML utilitário (inspeção e reset) usado durante desenvolvimento |

Aplique o schema antes do primeiro `dotnet run`:

```bash
mysql -u root -p laboratorio < sql/data.sql
```

O script cria `anexo_migration_status` e `migration_settings`, mas **assume que a tabela `anexo` já existe** (vem do sistema legado). Em ambiente experimental, pode criar manualmente uma `anexo` simples:

```sql
CREATE TABLE anexo (
    id INT AUTO_INCREMENT PRIMARY KEY,
    nome VARCHAR(455) NOT NULL,
    tipo_anexo_id INT NOT NULL,
    anexo LONGBLOB,
    timestamp DATETIME DEFAULT CURRENT_TIMESTAMP,
    tipo VARCHAR(100),
    tamanho INT,
    filepath VARCHAR(500)
);
```

O `sql/data.sql` insere uma linha em `migration_settings` (`id = 1`) com valores padrão razoáveis para dev.

## Deploy com Docker

### Build

[Dockerfile](../Dockerfile) é multi-stage (build → publish → runtime):

```bash
docker build -t replace-api:latest .
docker tag replace-api:latest <registry>/replace-api:v1.0.0
docker push <registry>/replace-api:v1.0.0
```

**Por que multi-stage**: a imagem final usa só `aspnet:9.0` (runtime), não o `sdk:9.0` (que pesa muito mais). Resulta em imagem ~200MB vs ~800MB. Também roda como **usuário não-root** (`USER app`) por segurança.

### Run — variáveis individuais

```bash
docker run -d \
  -p 8080:8080 \
  -e ASPNETCORE_ENVIRONMENT=Production \
  -e ASPNETCORE_URLS=http://+:8080 \
  -e ConnectionStrings__DefaultConnection="Server=db;Port=3306;Database=laboratorio;Uid=user;Pwd=..." \
  -e AWS__AccessKey="..." \
  -e AWS__SecretKey="..." \
  -e AWS__Region="sa-east-1" \
  -e AWS__BucketName="replace-prod" \
  --name replace-api \
  replace-api:latest
```

### Run — `docker-compose` com `.env`

[docker-compose.yml](../docker-compose.yml) usa `env_file: .env` — **os nomes no `.env` precisam estar no formato `__`** (não há tradução):

```bash
cp .env.example .env
# editar .env com valores reais
docker-compose up -d
docker-compose logs -f
```

## Deploy em Kubernetes

Padrão recomendado:

- **Imagem** publicada em registry privado.
- **Secret** (`kubectl create secret generic replace-secrets --from-literal=AWS__AccessKey=...`) para credenciais.
- **ConfigMap** para variáveis não-sensíveis (`ASPNETCORE_URLS`, `AWS__Region`, `AWS__BucketName`).
- **Deployment** com `envFrom` apontando para ambos.
- **Liveness probe**: `GET /healthcheck`, `failureThreshold: 3`, `periodSeconds: 30`.
- **Replicas**: 2+ para aproveitar o lock distribuído.

### Identificação de pod

O `processing_pod_id` em `anexo_migration_status` é preenchido a partir da variável `POD_NAME` (ou `MachineName` se ausente). Em K8s, exponha via downward API:

```yaml
env:
  - name: POD_NAME
    valueFrom:
      fieldRef:
        fieldPath: metadata.name
```

Assim cada pod identifica seu próprio nome em logs e na coluna `processing_pod_id`.

## CI/CD — pipeline sugerido

Não há pipeline implementado, mas o setup recomendado:

```yaml
stages:
  - test
  - build
  - publish
  - deploy

test:
  - dotnet restore
  - dotnet test

build:
  - dotnet build -c Release

publish:
  - docker build -t replace-api:${SHA}
  - docker push ${REGISTRY}/replace-api:${SHA}

deploy:
  - kubectl set image deployment/replace-api app=${REGISTRY}/replace-api:${SHA}
```

Ambientes sugeridos:
- **Dev** — deploy automático em push para `develop`
- **Staging** — deploy automático em push para `main`
- **Production** — deploy manual com aprovação

## Hierarquia de overrides — exemplo prático

Suponha que `appsettings.json` define `AWS:Region = "us-east-1"`. Se você sobe o container com:

```
-e AWS__Region=sa-east-1
```

A região efetiva em runtime é `sa-east-1`. A variável de ambiente venceu.

Agora, se você adicionar `-- AWS:Region=eu-west-1` na linha de comando do `dotnet run`, **esse** vence — porque argumentos CLI são o último na cadeia.

## Próximo passo

Para subir o projeto local, rodar testes e diagnosticar problemas → [08 — Desenvolvimento e Testes](./08-desenvolvimento-testes.md).
