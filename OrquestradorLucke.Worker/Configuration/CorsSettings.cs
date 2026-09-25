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
    public const string PolicyName = "LuckeWebClient";

    /// <summary>
    /// Origens liberadas (esquema + host + porta, ex.: <c>http://localhost:5173</c>). Vale para a API
    /// de gerenciamento e para o <c>negotiate</c> do SignalR; em desenvolvimento a lista traz as portas
    /// usuais dos servidores de front-end.
    /// </summary>
    public string[] AllowedOrigins { get; set; } = [];
}
