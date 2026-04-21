using System.Text;
using System.Text.Json;

sealed class NotificationSinkStore
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly JsonSerializerOptions jsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };
    private readonly string sinkFilePath;

    public NotificationSinkStore(IWebHostEnvironment environment)
    {
        var dataDirectory = Path.Combine(environment.ContentRootPath, "App_Data");
        Directory.CreateDirectory(dataDirectory);
        sinkFilePath = Path.Combine(dataDirectory, "notification-sink-events.json");

        if (!File.Exists(sinkFilePath))
        {
            File.WriteAllText(sinkFilePath, "[]");
        }
    }

    public async Task<IReadOnlyList<NotificationSinkEventRecord>> GetEventsAsync(CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);

        try
        {
            return await LoadEventsUnsafeAsync(cancellationToken);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<NotificationSinkEventRecord> RecordEventAsync(string sinkType, HttpRequest request, CancellationToken cancellationToken)
    {
        var payload = await ReadBodyAsync(request, cancellationToken);
        var sinkEvent = new NotificationSinkEventRecord
        {
            EventId = Guid.NewGuid(),
            SinkType = sinkType,
            ReceivedAt = DateTimeOffset.UtcNow,
            Method = request.Method,
            Path = request.Path.Value ?? string.Empty,
            QueryString = request.QueryString.Value ?? string.Empty,
            Payload = payload,
            Headers = request.Headers
                .ToDictionary(
                    pair => pair.Key,
                    pair => string.Join(", ", pair.Value.ToArray()),
                    StringComparer.OrdinalIgnoreCase),
        };

        await gate.WaitAsync(cancellationToken);

        try
        {
            var events = await LoadEventsUnsafeAsync(cancellationToken);
            events.Add(sinkEvent);
            await SaveEventsUnsafeAsync(events, cancellationToken);
        }
        finally
        {
            gate.Release();
        }

        return sinkEvent;
    }

    private async Task<List<NotificationSinkEventRecord>> LoadEventsUnsafeAsync(CancellationToken cancellationToken)
    {
        await using var stream = File.Open(sinkFilePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        var items = await JsonSerializer.DeserializeAsync<List<NotificationSinkEventRecord>>(stream, jsonOptions, cancellationToken);
        return items ?? [];
    }

    private async Task SaveEventsUnsafeAsync(List<NotificationSinkEventRecord> events, CancellationToken cancellationToken)
    {
        await using var stream = File.Open(sinkFilePath, FileMode.Create, FileAccess.Write, FileShare.None);
        await JsonSerializer.SerializeAsync(stream, events, jsonOptions, cancellationToken);
    }

    private static async Task<string> ReadBodyAsync(HttpRequest request, CancellationToken cancellationToken)
    {
        request.EnableBuffering();
        request.Body.Position = 0;

        using var reader = new StreamReader(request.Body, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, leaveOpen: true);
        var payload = await reader.ReadToEndAsync(cancellationToken);
        request.Body.Position = 0;
        return payload;
    }
}

static class NotificationSinkTypes
{
    public const string EmergencyContact = "contato_emergencia";
    public const string PublicAgency = "orgao_publico";
}

sealed class NotificationSinkEventRecord
{
    public Guid EventId { get; set; }

    public string SinkType { get; set; } = string.Empty;

    public DateTimeOffset ReceivedAt { get; set; }

    public string Method { get; set; } = string.Empty;

    public string Path { get; set; } = string.Empty;

    public string QueryString { get; set; } = string.Empty;

    public string Payload { get; set; } = string.Empty;

    public Dictionary<string, string> Headers { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}
