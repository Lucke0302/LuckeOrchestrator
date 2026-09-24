namespace OrquestradorLucke.Domain;

/// <summary>
/// Encapsula a mecânica de frustração de uma execução. Cada falha
/// (erro de compilação, retorno vazio etc.) incrementa o contador e, ao atingir
/// o limite máximo configurado, o circuito desarma (overdrive) e a tarefa deve
/// ser encaminhada ao modelo mais robusto.
/// </summary>
public sealed class FrustrationTracker
{
    /// <summary>
    /// Quantidade máxima de motivos mantidos em <see cref="HistoricoFalhas"/> (os mais recentes): o
    /// contexto entregue ao overdrive não precisa de mais do que isso e o histórico fica com memória
    /// limitada em um daemon de longa duração.
    /// </summary>
    private const int MaximoHistoricoFalhas = 10;

    /// <param name="limiteMaximo">
    /// Quantidade de falhas toleradas antes de disparar o overdrive.
    /// Deve ser fornecido pela configuração (IOptions) — sem valores hardcoded.
    /// </param>
    /// <exception cref="ArgumentOutOfRangeException">Quando <paramref name="limiteMaximo"/> for menor ou igual a zero.</exception>
    public FrustrationTracker(int limiteMaximo)
    {
        if (limiteMaximo <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(limiteMaximo),
                limiteMaximo,
                "O limite máximo de falhas deve ser maior que zero.");
        }

        LimiteMaximo = limiteMaximo;
    }

    /// <summary>Quantidade de falhas acumuladas desde o último sucesso ou reinício.</summary>
    public int ContadorAtual { get; private set; }

    /// <summary>Quantidade de falhas toleradas antes de disparar o overdrive.</summary>
    public int LimiteMaximo { get; }

    /// <summary>
    /// Motivos das falhas acumuladas desde o último sucesso (memória do overdrive). É o que o worker
    /// formata em contexto para o modelo mais robusto, para que ele não repita o erro do expert menor.
    /// </summary>
    public List<string> HistoricoFalhas { get; } = [];

    /// <summary>Indica que o circuito desarmou e a tarefa deve ir para o modelo mais robusto.</summary>
    public bool OverdriveDisparado => ContadorAtual >= LimiteMaximo;

    /// <summary>Falhas que ainda podem ocorrer antes do overdrive.</summary>
    public int TentativasRestantes => Math.Max(0, LimiteMaximo - ContadorAtual);

    /// <summary>Registra uma falha e informa se o limite foi atingido nesta chamada.</summary>
    /// <returns><c>true</c> quando o overdrive está disparado após o registro.</returns>
    public bool RegistrarFalha()
    {
        if (!OverdriveDisparado)
        {
            ContadorAtual++;
        }

        return OverdriveDisparado;
    }

    /// <summary>
    /// Registra uma falha junto do motivo observado (erro de compilação, retorno vazio, resposta
    /// fora do contrato etc.), alimentando o <see cref="HistoricoFalhas"/> usado pelo overdrive.
    /// </summary>
    /// <param name="motivo">Descrição da falha, já contextualizada pelo chamador.</param>
    /// <returns><c>true</c> quando o overdrive está disparado após o registro.</returns>
    /// <exception cref="ArgumentException">Quando <paramref name="motivo"/> for nulo, vazio ou apenas espaços.</exception>
    public bool RegistrarFalha(string motivo)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(motivo);

        // Histórico limitado: um daemon que roda por meses acumularia memória sem limite (o ciclo de
        // frustração só zera no primeiro sucesso). As entradas mais antigas são descartadas porque o
        // prompt do overdrive só precisa dos erros recentes.
        if (HistoricoFalhas.Count >= MaximoHistoricoFalhas)
        {
            HistoricoFalhas.RemoveAt(0);
        }

        HistoricoFalhas.Add(motivo);

        return RegistrarFalha();
    }

    /// <summary>Zera o contador após uma execução bem-sucedida (e limpa a memória de falhas).</summary>
    public void RegistrarSucesso() => Reiniciar();

    /// <summary>Zera o contador e limpa o histórico (ex.: reinício do ciclo de execução da tarefa).</summary>
    public void Reiniciar()
    {
        ContadorAtual = 0;
        HistoricoFalhas.Clear();
    }
}
