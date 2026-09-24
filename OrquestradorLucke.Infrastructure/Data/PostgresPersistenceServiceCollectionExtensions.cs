using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace OrquestradorLucke.Infrastructure.Data;

/// <summary>
/// Composição da persistência PostgreSQL. Fica na Infrastructure — única camada autorizada a
/// conhecer o EF Core — para que o host apenas injete a connection string e registre os serviços.
/// </summary>
public static class PostgresPersistenceServiceCollectionExtensions
{
    /// <summary>
    /// Registra o <see cref="AppDbContext"/> (EF Core + Npgsql) como Scoped com o plugin vetorial do
    /// pgvector habilitado.
    /// </summary>
    /// <param name="services">Coleção de serviços do host.</param>
    /// <param name="connectionString">
    /// Connection string do PostgreSQL, obtida da configuração (<c>ConnectionStrings:DefaultConnection</c>) —
    /// nunca hardcoded.
    /// </param>
    public static IServiceCollection AddPostgresPersistence(this IServiceCollection services, string connectionString)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        services.AddDbContext<AppDbContext>(options => options
            .UseNpgsql(connectionString, npgsqlOptions => npgsqlOptions.UseVector()));

        return services;
    }
}
