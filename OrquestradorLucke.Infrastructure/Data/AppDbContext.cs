using Microsoft.EntityFrameworkCore;
using OrquestradorLucke.Domain;
using OrquestradorLucke.Infrastructure.Configuration;
using Pgvector;

namespace OrquestradorLucke.Infrastructure.Data;

/// <summary>
/// Contexto do EF Core para a persistência de tarefas e do índice vetorial no PostgreSQL, com o
/// suporte do pgvector habilitado. Vive na Infrastructure: é o único ponto do sistema que conhece o
/// EF Core.
/// </summary>
public sealed class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    /// <summary>Nome da connection string lida da seção <c>ConnectionStrings</c> da configuração.</summary>
    public const string ConnectionStringName = "DefaultConnection";

    /// <summary>Tarefas do orquestrador (fila persistida em <c>agent_tasks</c>).</summary>
    public DbSet<AgentTask> AgentTasks => Set<AgentTask>();

    /// <summary>Documentos de código indexados para o RAG (tabela <c>code_documents</c>).</summary>
    public DbSet<CodeDocument> CodeDocuments => Set<CodeDocument>();

    /// <summary>Configura a extensão vetorial e o mapeamento explícito das entidades.</summary>
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // Suporte vetorial do PostgreSQL: a migration emite CREATE EXTENSION IF NOT EXISTS vector,
        // pré-requisito para a coluna de embedding do índice do RAG.
        modelBuilder.HasPostgresExtension("vector");

        ConfigureAgentTask(modelBuilder);
        ConfigureCodeDocument(modelBuilder);
    }

    /// <summary>
    /// Mapeamento explícito de <see cref="AgentTask"/>. A entidade do Domain é um record imutável
    /// com propriedades <c>init</c>-only: o EF Core materializa a instância pelo construtor sem
    /// parâmetros gerado pelo próprio record e escreve cada valor pelo acessor da propriedade —
    /// logo não há construtor parametrizado a ligar, e nenhuma propriedade precisa de setter público.
    /// </summary>
    private static void ConfigureAgentTask(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<AgentTask>(entity =>
        {
            entity.ToTable("agent_tasks");
            entity.HasKey(task => task.Id).HasName("pk_agent_tasks");

            // O identificador é gerado pelo domínio (Guid.NewGuid()), não pelo banco.
            entity.Property(task => task.Id)
                .HasColumnName("id")
                .ValueGeneratedNever();

            entity.Property(task => task.Payload)
                .HasColumnName("payload")
                .IsRequired();

            entity.Property(task => task.Complexidade)
                .HasColumnName("complexidade")
                .HasConversion<string>()
                .HasMaxLength(16)
                .IsRequired();

            entity.Property(task => task.Status)
                .HasColumnName("status")
                .HasConversion<string>()
                .HasMaxLength(16)
                .IsRequired();

            entity.Property(task => task.CriadoEm)
                .HasColumnName("criado_em")
                .IsRequired();

            entity.Property(task => task.AtualizadoEm)
                .HasColumnName("atualizado_em");

            entity.Property(task => task.Branch)
                .HasColumnName("branch")
                .HasMaxLength(255);

            entity.Property(task => task.PullRequestUrl)
                .HasColumnName("pull_request_url")
                .HasMaxLength(2048);

            // Índice que atende o dequeue: status Pendente ordenado por criado_em.
            entity.HasIndex(task => new { task.Status, task.CriadoEm })
                .HasDatabaseName("ix_agent_tasks_status_criado_em");
        });
    }

    /// <summary>
    /// Mapeamento explícito de <see cref="CodeDocument"/> (índice do RAG).
    /// </summary>
    /// <remarks>
    /// O Domain expõe o vetor como <see cref="ReadOnlyMemory{T}"/> e não pode conhecer o pgvector;
    /// por isso a coluna é configurada com um conversor para <c>Pgvector.Vector</c> — o CLR type que
    /// o provider sabe mapear para <c>vector(d)</c>. Os índices de distância do pgvector (ex.: hnsw)
    /// não são criados aqui: a base é pequena e um seq scan ordenado por <c>&lt;=&gt;</c> é suficiente.
    /// </remarks>
    private static void ConfigureCodeDocument(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<CodeDocument>(entity =>
        {
            entity.ToTable("code_documents");
            entity.HasKey(document => document.Id).HasName("pk_code_documents");

            // O identificador é gerado pelo domínio (Guid.NewGuid()), não pelo banco.
            entity.Property(document => document.Id)
                .HasColumnName("id")
                .ValueGeneratedNever();

            entity.Property(document => document.FilePath)
                .HasColumnName("file_path")
                .HasMaxLength(512)
                .IsRequired();

            entity.Property(document => document.ContentHash)
                .HasColumnName("content_hash")
                .HasMaxLength(64)
                .IsRequired();

            entity.Property(document => document.Content)
                .HasColumnName("content")
                .IsRequired();

            entity.Property(document => document.Embedding)
                .HasColumnName("embedding")
                .HasColumnType($"vector({ModelCatalog.EmbeddingDimensions})")
                .HasConversion(
                    embedding => new Vector(embedding.ToArray()),
                    vector => new ReadOnlyMemory<float>(vector.ToArray()));

            // Chave natural do índice: o upsert procura o documento pelo caminho do arquivo.
            entity.HasIndex(document => document.FilePath)
                .IsUnique()
                .HasDatabaseName("ux_code_documents_file_path");
        });
    }
}
