namespace RePlace.Domain.Models;

public enum StatusEnum
{
    Pending, // 0 - Aguardando processamento
    Extracting, // 1 - Extraindo BLOB do MySQL
    Extracted, // 2 - Extração concluída, checksum calculado
    Uploading, // 3 - Enviando para S3
    Completed, // 4 - MIgração concluída com sucesso
    Failed // 5 - Falha no processamento (retry possível)
}