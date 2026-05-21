# Documentação — RePlace API

Bem-vindo à documentação técnica do **RePlace**, a API .NET responsável por migrar arquivos binários (anexos) armazenados como BLOB no MySQL para o Amazon S3.

Esta documentação é organizada em sequência progressiva. A leitura na ordem dá o entendimento completo do sistema; cada documento também funciona isoladamente para consulta pontual.

## Índice

| # | Documento | Descrição |
|---|-----------|-----------|
| 01 | [Visão Geral](./01-visao-geral.md) | O problema que o RePlace resolve, atores, objetivos e diagrama de contexto |
| 02 | [Arquitetura](./02-arquitetura.md) | Clean Architecture, camadas, stack tecnológica e responsabilidades |
| 03 | [Fluxo ETL](./03-fluxo-etl.md) | Loop do BackgroundService, processamento de arquivo individual e máquina de estados |
| 04 | [Modelo de Dados](./04-modelo-dados.md) | Tabelas MySQL, organização do S3 e parâmetros da `migration_settings` |
| 05 | [Resiliência e Concorrência](./05-resiliencia-concorrencia.md) | **Os pontos arquiteturais críticos**: lock distribuído, retries em camadas, integridade por checksum |
| 06 | [API e Health Checks](./06-api-health.md) | Endpoints REST, formato de resposta e monitoramento de saúde |
| 07 | [Configuração e Deploy](./07-configuracao-deploy.md) | `appsettings`, variáveis de ambiente, Docker e Kubernetes |
| 08 | [Desenvolvimento e Testes](./08-desenvolvimento-testes.md) | Setup local, execução de testes e troubleshooting |

## Diagramas

Quatro fluxogramas em [`flowcharts/`](./flowcharts/) ilustram visualmente o sistema. Cada documento referencia o(s) diagrama(s) relevante(s):

| Imagem | Onde é discutida |
|--------|------------------|
| [1 - Contexto do Sistema](./flowcharts/1%20-%20Contexto%20do%20Sistema.png) | [01 — Visão Geral](./01-visao-geral.md) |
| [2 - Fluxo ETL Completo](./flowcharts/2%20-%20Fluxo%20ETL%20Completo.png) | [03 — Fluxo ETL](./03-fluxo-etl.md) |
| [3 - Processamento de Arquivo Individual](./flowcharts/3%20-%20Processamento%20de%20Arquivo%20Individual.png) | [03 — Fluxo ETL](./03-fluxo-etl.md) |
| [4 - Diagrama de Estados](./flowcharts/4%20-%20Diagrama%20de%20Estados.png) | [03 — Fluxo ETL](./03-fluxo-etl.md) |

## Como ler esta documentação

- **Dev sênior com pressa**: comece por [02-arquitetura.md](./02-arquitetura.md) e [05-resiliencia-concorrencia.md](./05-resiliencia-concorrencia.md) — concentram as decisões não-óbvias.
- **Dev júnior / primeira leitura**: siga a ordem do índice. Cada documento parte do conceito antes de mostrar o código.
- **Vou subir o projeto pela primeira vez**: pule para [08-desenvolvimento-testes.md](./08-desenvolvimento-testes.md).
- **Vou implantar em produção**: vá para [07-configuracao-deploy.md](./07-configuracao-deploy.md).

## Convenções

- Referências a código usam o formato `arquivo:linha`, ex.: [FileMigrationService.cs:117](../src/Application/Services/FileMigrationService.cs#L117).
- Termos técnicos em inglês (`BLOB`, `checksum`, `lock`, `batch`) são mantidos quando são o jargão padrão do ecossistema.
- Quando um conceito sênior aparece pela primeira vez (ex.: `FOR UPDATE SKIP LOCKED`), há uma explicação curta do que é e por que está sendo usado, para leitores júnior.
