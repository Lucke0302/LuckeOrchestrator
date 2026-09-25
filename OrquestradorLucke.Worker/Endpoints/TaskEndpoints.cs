using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using OrquestradorLucke.Application.Models;
using OrquestradorLucke.Application.Services;
using OrquestradorLucke.Infrastructure.Models;

namespace OrquestradorLucke.Worker.Endpoints;

/// <summary>
/// Minimal API de gerenciamento e revisão das tarefas — o back-end do painel web (substitui o DBeaver
/// na criação/consulta e a revisão manual do pull request).
/// </summary>
/// <remarks>
/// <para>Rotas (todas sob <c>/api/tasks</c>):</para>
/// <list type="bullet">
/// <item><description><c>GET /api/tasks?limit=50</c> — lista as tarefas, da mais recente para a mais antiga.</description></item>
/// <item><description><c>POST /api/tasks</c> — enfileira a tarefa (UUID e status <c>Pendente</c> gerados no domínio).</description></item>
/// <item><description><c>POST /api/tasks/{id}/accept</c> — mescla o PR com o token administrativo e marca a tarefa como <c>Aprovada</c>.</description></item>
/// <item><description><c>POST /api/tasks/{id}/reject</c> — fecha o PR com o motivo, alimenta a frustração do daemon e devolve a tarefa para <c>Pendente</c>.</description></item>
/// </list>
/// <para>
/// O log técnico fica no provider de <c>ILogger</c> (console/journald e hub do SignalR); as mensagens
/// de erro para o cliente são curtas e, quando a falha vem de exceção, trazem a mensagem original —
/// é uma API administrativa, e o painel precisa saber o que o GitHub recusou.
/// </para>
/// </remarks>
public static class TaskEndpoints
{
    /// <summary>Prefixo das rotas de gerenciamento de tarefas.</summary>
    public const string RoutePrefix = "/api/tasks";

    /// <summary>Registra as rotas de tarefas no host.</summary>
    /// <param name="endpoints">Construtor de rotas do host.</param>
    public static IEndpointRouteBuilder MapTaskEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        var group = endpoints.MapGroup(RoutePrefix);

        group.MapGet("/", GetTasksAsync);
        group.MapPost("/", CreateTaskAsync);
        group.MapPost("/{id:guid}/accept", AcceptTaskAsync);
        group.MapPost("/{id:guid}/reject", RejectTaskAsync);

        return endpoints;
    }

    /// <summary>Lista as tarefas para o painel (o teto de linhas é do caso de uso).</summary>
    private static async Task<IResult> GetTasksAsync(
        TaskReviewService reviewService,
        int? limit,
        CancellationToken cancellationToken)
    {
        var tasks = await reviewService.GetTasksAsync(limit, cancellationToken).ConfigureAwait(false);

        return Results.Ok(tasks.Select(AgentTaskResponse.From));
    }

    /// <summary>Enfileira uma tarefa nova: o laço do daemon a reivindica no próximo ciclo, sem reinício.</summary>
    private static async Task<IResult> CreateTaskAsync(
        CreateAgentTaskRequest request,
        TaskReviewService reviewService,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Payload))
        {
            return Results.Problem(
                title: "Payload obrigatório",
                detail: "O campo 'payload' é obrigatório para enfileirar uma tarefa.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        var task = await reviewService
            .CreateTaskAsync(request.Payload.Trim(), request.Complexidade, cancellationToken)
            .ConfigureAwait(false);

        loggerFactory
            .CreateLogger("OrquestradorLucke.Api.Tasks")
            .LogInformation(
                "Tarefa {TaskId} enfileirada pela API (complexidade {Complexidade}).",
                task.Id,
                task.Complexidade);

        return Results.Ok(AgentTaskResponse.From(task));
    }

    /// <summary>Aprova a entrega: merge do pull request com o token do revisor e status <c>Aprovada</c>.</summary>
    private static async Task<IResult> AcceptTaskAsync(
        Guid id,
        TaskReviewService reviewService,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
        => await ReviewAsync(
                id,
                "aprovação",
                reviewService,
                loggerFactory,
                (service, token) => service.AcceptAsync(id, token),
                cancellationToken)
            .ConfigureAwait(false);

    /// <summary>
    /// Rejeita a entrega: fecha o pull request com o motivo, registra a falha no medidor de frustração
    /// (o próximo ciclo tende a escalar para o modelo mais robusto) e devolve a tarefa para a fila.
    /// </summary>
    private static async Task<IResult> RejectTaskAsync(
        Guid id,
        RejectAgentTaskRequest request,
        TaskReviewService reviewService,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Motivo))
        {
            return Results.Problem(
                title: "Motivo obrigatório",
                detail: "O campo 'motivo' é obrigatório: ele fecha o pull request e alimenta o overdrive do agente.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        return await ReviewAsync(
                id,
                "rejeição",
                reviewService,
                loggerFactory,
                (service, token) => service.RejectAsync(id, request.Motivo.Trim(), token),
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Executa o caso de uso de revisão e traduz o desfecho em status HTTP: 200 com a tarefa quando a
    /// revisão foi aplicada, 404/409 quando não havia o que revisar e 502 quando o GitHub recusou.
    /// </summary>
    private static async Task<IResult> ReviewAsync(
        Guid taskId,
        string operation,
        TaskReviewService reviewService,
        ILoggerFactory loggerFactory,
        Func<TaskReviewService, CancellationToken, Task<TaskReviewResult>> applyReview,
        CancellationToken cancellationToken)
    {
        var logger = loggerFactory.CreateLogger("OrquestradorLucke.Api.Tasks");

        try
        {
            var result = await applyReview(reviewService, cancellationToken).ConfigureAwait(false);

            switch (result.Outcome)
            {
                case TaskReviewOutcome.Aprovada:
                case TaskReviewOutcome.Rejeitada:
                    logger.LogInformation(
                        "Tarefa {TaskId}: {Operation} aplicada pela API (status {Status}).",
                        taskId,
                        operation,
                        result.Task?.Status);

                    return Results.Ok(AgentTaskResponse.From(result.Task!));

                case TaskReviewOutcome.NaoEncontrada:
                    return Results.Problem(
                        title: "Tarefa não encontrada",
                        detail: result.Detail,
                        statusCode: StatusCodes.Status404NotFound);

                default:
                    return Results.Problem(
                        title: "Tarefa sem entrega para revisar",
                        detail: result.Detail,
                        statusCode: StatusCodes.Status409Conflict);
            }
        }
        catch (Exception ex)
        {
            // O erro do GitHub (conflito, branch protegida, token administrativo ausente) é levado ao
            // painel em vez de virar um 500 opaco: o revisor precisa saber o que ajustar.
            logger.LogError(
                ex,
                "Falha ao aplicar a {Operation} da tarefa {TaskId} pela API.",
                operation,
                taskId);

            return Results.Problem(
                title: $"Falha na {operation}",
                detail: ex.Message,
                statusCode: StatusCodes.Status502BadGateway);
        }
    }
}
