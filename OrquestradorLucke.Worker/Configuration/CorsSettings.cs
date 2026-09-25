namespace OrquestradorLucke.Worker.Configuration;

/// <summary>
/// Origens autorizadas a chamar a API e o hub de logs do navegador (CORS). Nenhuma origem é
/// hardcoded no código: a lista vem de <c>Cors:AllowedOrigins</c> no appsettings/variáveis de
/// ambiente, e uma lista vazia simplesmente não libera nenhuma origem cruzada.
/// </summary>
public sealed class CorsSettings
{
    public const string SectionName = "Cors";

    /// <summary>Nome da política aplicada pelo host (minimal APIs + SignalR compartilham a mesma).</summary>
    public const string PolicyName = "WebDashboardPolicy";

    /// <summary>
    /// Origens liberadas (esquema + host + porta, ex.: <c>http://localhost:5173</c>). Vale para a API
    /// de gerenciamento e para o <c>negotiate</c> do SignalR; em desenvolvimento a lista traz as
    /// portas usuais dos servidores de front-end — o servidor do Vite (<c>5173</c>) e o
    /// <c>vite preview</c> (<c>4173</c>) entram com o equivalente em <c>127.0.0.1</c>.
    /// </summary>
    /// <remarks>
    /// As duas formas de configuração se somam: os itens do array do <c>appsettings.json</c> (as
    /// portas usuais de desenvolvimento) e o valor escalar de <c>Cors__AllowedOrigins</c> — variável
    /// de ambiente, onde várias origens entram separadas por vírgula, o caminho para acrescentar a
    /// URL do painel publicado (Vercel) sem tocar no código
    /// (<c>Cors__AllowedOrigins=https://painel.vercel.app,http://localhost:4173</c>). O split, o
    /// recorte de espaços e a deduplicação acontecem na composição da política.
    /// </remarks>
    public string[] AllowedOrigins { get; set; } = [];
}
