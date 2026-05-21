# 01 — Visão Geral

## O problema

Sistemas legados costumam guardar arquivos (PDFs, imagens, plantas, termos) **dentro do banco de dados**, como colunas `BLOB`. Isso funciona — até começar a doer:

- **Custo**: armazenamento em banco transacional é caro por GB.
- **Performance**: backups demoram, replicação fica pesada, queries simples passam a competir por I/O com leituras de arquivo.
- **Escalabilidade**: vertical apenas. Não dá para "particionar" arquivos como se particiona linhas.

O caminho usual é mover esses arquivos para um **object storage** (S3, GCS, Azure Blob). O RePlace faz exatamente essa mudança, **sem interrupção do sistema legado**: ele lê os BLOBs do MySQL, copia para o S3, registra o caminho de volta na linha original e — opcionalmente — zera o BLOB.

## O que é o RePlace

Uma **API .NET 9** com um **serviço em background contínuo** que executa migração em lotes, com:

- **Lock distribuído** para rodar várias instâncias em paralelo sem duplicar trabalho.
- **Verificação de integridade** (checksum MD5 comparado ao ETag do S3) em cada arquivo.
- **Retry automático** com backoff exponencial para falhas transitórias do S3 e do MySQL.
- **Janela de execução configurável** (ex.: rodar só entre 00:00 e 07:00 UTC).
- **Endpoints REST** apenas para consulta de status — não há API de upload/disparo. O ETL roda sozinho.

> O RePlace não é uma API de arquivos. É um **agente de migração** com janela de consulta HTTP.

## Quem usa

| Ator | Como interage |
|------|---------------|
| **Sistemas internos** | Consomem `GET /api/migration/status` para acompanhar o progresso |
| **Administradores / DevOps** | Monitoram `GET /healthcheck` (compatível com K8s liveness/readiness) |
| **Operadores de banco** | Ajustam parâmetros de execução escrevendo na tabela `migration_settings` |
| **Kubernetes** | Hospeda múltiplas réplicas da imagem Docker; o lock distribuído coordena |

Não há login: a API é projetada para rodar em **rede interna**. Se for exposta, autenticação precisa ser adicionada (ver [05 — Resiliência e Concorrência](./05-resiliencia-concorrencia.md) sobre limites atuais).

## Objetivos

- **Reduzir custo** migrando bytes do banco transacional para object storage.
- **Preservar rastreabilidade**: nenhuma linha de `anexo` é apagada — apenas o BLOB é zerado (opcionalmente). O caminho do S3 fica gravado em `anexo.filepath`.
- **Garantir integridade**: nenhum arquivo é considerado migrado sem checksum batendo entre origem e destino.
- **Tolerar falhas**: pods reiniciam, S3 retorna 503, MySQL fecha conexão — o sistema retoma de onde parou, sem reprocessar o que já deu certo.
- **Operar em horário programável**: o trabalho pode ser limitado a janelas de baixa carga.

## Diagrama de Contexto

![Contexto do Sistema](./flowcharts/1%20-%20Contexto%20do%20Sistema.png)

**Como ler o diagrama:**

- **Setas sólidas → RePlace API**: chamadas que entram (consultas HTTP de outros sistemas, health-check do administrador).
- **Setas saindo da API**: leituras dos BLOBs no MySQL e uploads para o S3.
- **Setas pontilhadas**: dependência de hospedagem (Kubernetes).

A API tem **uma fonte (MySQL)** e **um destino (S3)**. Toda a inteligência fica entre os dois: controle de batch, retry, lock, checksum e estado.

## O que NÃO é escopo do RePlace

- **Não é proxy de download**: nada lê arquivos do S3 *através* da API. Quem precisa de um arquivo migrado deve ler a coluna `anexo.filepath` e baixar diretamente.
- **Não faz limpeza retroativa**: arquivos que já foram migrados (e que tinham o BLOB zerado por `purge_files`) não voltam ao banco.
- **Não migra estrutura**: o RePlace assume que as tabelas `anexo`, `anexo_migration_status` e `migration_settings` já existem (ver [04 — Modelo de Dados](./04-modelo-dados.md) e [`data.sql`](../data.sql)).
- **Não envia notificações**: falhas precisam ser observadas via logs ou pelo endpoint de status.

## Próximo passo

Para entender **como** essa migração foi organizada em código → [02 — Arquitetura](./02-arquitetura.md).
