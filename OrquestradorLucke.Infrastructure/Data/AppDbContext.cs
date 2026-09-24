using Microsoft.EntityFrameworkCore;
using OrquestradorLucke.Domain;

namespace OrquestradorLucke.Infrastructure.Data;

/// <summary>
/// Contexto do EF Core para a persistência de tarefas no PostgreSQL, com o suporte vetorial do
/// pgvector habilitado para os embeddings dos modelos do roteador MoE. Vive na Infrastructure:
/// é o único ponto do sistema que conhece o EF Core.
/// </summary>
public sealed class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    /// <summary>Nome da connection string lida da seção <c>ConnectionStrings</c> da configuração.</summary>
    public const string ConnectionStringName = "DefaultConnection";

    /// <summary>Tarefas do orquestrador (fila persistida em <c>agent_tasks</c>).</summary>
    public DbSet<AgentTask> AgentTasks => Set<AgentTask>();

    /// <summary>Configura a extensão vetorial e o mapeamento explícito das entidades.</summary>
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // Suporte vetorial do PostgreSQL: a migration emite CREATE EXTENSION IF NOT EXISTS vector,
        // pré-requisito para as colunas de embedding usadas na busca semântica.
        modelBuilder.HasPostgresExtension("vector");

        ConfigureAgentTask(modelBuilder);
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
}
