using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace OrquestradorLucke.Infrastructure.Data;

/// <summary>
/// Fábrica usada exclusivamente em tempo de design pelo EF Core CLI (migrations). A connection
/// string é lida da variável de ambiente equivalente a <c>ConnectionStrings:DefaultConnection</c>
/// — sem valor hardcoded — e o provedor é configurado com o plugin vetorial do pgvector, igual ao
/// runtime registrado em <c>AddPostgresPersistence</c>.
/// </summary>
/// <remarks>
/// Comandos (executados da raiz da solução, após <c>dotnet tool update --global dotnet-ef --version 10.0.4</c>):
/// <code>
/// $env:ConnectionStrings__DefaultConnection='Host=localhost;Database=lucke;Username=postgres;Password=...'
/// dotnet ef migrations add &lt;Nome&gt;        --project OrquestradorLucke.Infrastructure --output-dir Data/Migrations
/// dotnet ef migrations add AddHnswIndex  --project OrquestradorLucke.Infrastructure --output-dir Data/Migrations
/// dotnet ef migrations add AddQuotaState  --project OrquestradorLucke.Infrastructure --output-dir Data/Migrations
/// dotnet ef migrations add AddUsersTable  --project OrquestradorLucke.Infrastructure --output-dir Data/Migrations
/// dotnet ef migrations list              --project OrquestradorLucke.Infrastructure
/// dotnet ef database update              --project OrquestradorLucke.Infrastructure
/// dotnet ef database drop --force        --project OrquestradorLucke.Infrastructure
/// dotnet ef migrations script            --project OrquestradorLucke.Infrastructure --idempotent -o migration.sql
/// dotnet ef migrations script &lt;De&gt; &lt;Ate&gt;   --project OrquestradorLucke.Infrastructure --no-build
/// </code>
/// A connection string só é exigida para <c>database update</c>/<c>drop</c>/<c>script</c> contra um banco real
/// (<c>migrations add</c> e <c>list</c> apenas validam a configuração do provedor quando o banco ainda
/// não existe). Sem <c>--startup-project</c>, o EF Core CLI usa esta fábrica e dispensa o host do Worker.
/// </remarks>
public sealed class AppDbContextFactory : IDesignTimeDbContextFactory<AppDbContext>
{
    /// <summary>Variável de ambiente correspondente à configuração <c>ConnectionStrings:DefaultConnection</c>.</summary>
    private const string ConnectionStringEnvironmentVariable = "ConnectionStrings__" + AppDbContext.ConnectionStringName;

    /// <summary>Cria o contexto para os comandos <c>dotnet ef migrations</c>/<c>dotnet ef database</c>.</summary>
    /// <param name="args">Argumentos repassados pelo EF Core CLI (não utilizados).</param>
    /// <exception cref="InvalidOperationException">Quando a variável de ambiente da connection string não está definida.</exception>
    public AppDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable(ConnectionStringEnvironmentVariable);

        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException(
                $"Defina a variável de ambiente '{ConnectionStringEnvironmentVariable}' para usar o EF Core CLI " +
                "(ex.: $env:ConnectionStrings__DefaultConnection='Host=localhost;Database=lucke;Username=postgres;Password=...').");
        }

        var optionsBuilder = new DbContextOptionsBuilder<AppDbContext>();
        optionsBuilder.UseNpgsql(connectionString, npgsqlOptions => npgsqlOptions.UseVector());

        return new AppDbContext(optionsBuilder.Options);
    }
}
