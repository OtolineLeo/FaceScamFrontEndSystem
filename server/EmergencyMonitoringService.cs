using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

sealed class EmergencyMonitoringService : BackgroundService
{
    private static readonly TimeSpan PollingInterval = TimeSpan.FromMinutes(15);
    private readonly ILogger<EmergencyMonitoringService> logger;
    private readonly ResidentStore residentStore;

    public EmergencyMonitoringService(ResidentStore residentStore, ILogger<EmergencyMonitoringService> logger)
    {
        this.residentStore = residentStore;
        this.logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await RunCheckAsync(stoppingToken);

        using var timer = new PeriodicTimer(PollingInterval);

        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            await RunCheckAsync(stoppingToken);
        }
    }

    private async Task RunCheckAsync(CancellationToken cancellationToken)
    {
        try
        {
            await residentStore.EvaluateEmergencyAlertsAsync(cancellationToken);
            var processing = await residentStore.ProcessPendingEmergencyAlertsAsync(cancellationToken);

            if (processing.PendingAlerts > 0)
            {
                logger.LogInformation(
                    "Despacho de alertas executado: {PendingAlerts} alerta(s), {SuccessfulDeliveries} entrega(s) bem-sucedida(s), {FailedDeliveries} falha(s).",
                    processing.PendingAlerts,
                    processing.SuccessfulDeliveries,
                    processing.FailedDeliveries);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Falha ao avaliar alertas automáticos de ausencia de reconhecimento.");
        }
    }
}