using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.FileProviders;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

var builder = WebApplication.CreateBuilder(args);

builder.WebHost.UseUrls("http://localhost:5076");

builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
    options.SerializerOptions.WriteIndented = true;
});

builder.Services.Configure<EmergencyNotificationOptions>(builder.Configuration.GetSection(EmergencyNotificationOptions.SectionName));
builder.Services.AddHttpClient();
builder.Services.AddSingleton<AddressValidationService>();
builder.Services.AddSingleton<EmergencyNotificationDispatcher>();
builder.Services.AddSingleton<FaceEncodingService>();
builder.Services.AddSingleton<NotificationSinkStore>();
builder.Services.AddSingleton<ResidentStore>();
builder.Services.AddHostedService<EmergencyMonitoringService>();

var app = builder.Build();
var frontendRoot = Path.GetFullPath(Path.Combine(app.Environment.ContentRootPath, ".."));

app.UseStaticFiles(new StaticFileOptions
{
    FileProvider = new PhysicalFileProvider(Path.Combine(frontendRoot, "css")),
    RequestPath = "/css",
});

app.UseStaticFiles(new StaticFileOptions
{
    FileProvider = new PhysicalFileProvider(Path.Combine(frontendRoot, "js")),
    RequestPath = "/js",
});

app.UseStaticFiles(new StaticFileOptions
{
    FileProvider = new PhysicalFileProvider(Path.Combine(frontendRoot, "imgs")),
    RequestPath = "/imgs",
});

app.MapGet("/", () => Results.File(Path.Combine(frontendRoot, "index.html"), "text/html"));
app.MapGet("/index.html", () => Results.File(Path.Combine(frontendRoot, "index.html"), "text/html"));

var faceEncodingService = app.Services.GetRequiredService<FaceEncodingService>();
await faceEncodingService.EnsureReadyAsync(CancellationToken.None);

app.MapGet("/api/health", async (ResidentStore store, FaceEncodingService encoder, CancellationToken cancellationToken) =>
{
    var counts = await store.GetResidentCountsAsync(cancellationToken);

    return TypedResults.Ok(new ApiHealthResponse(
        "ok",
        "http://localhost:5076",
        store.StorageDirectory,
        store.UploadsDirectory,
        encoder.ModelsDirectory,
        encoder.IsReady,
        encoder.StatusMessage,
        counts.ActiveResidents,
        counts.RevokedResidents,
        counts.PendingEmergencyAlerts));
});

app.MapGet("/api/residents", async (ResidentStore store, CancellationToken cancellationToken) =>
    TypedResults.Ok(await store.GetResidentsAsync(cancellationToken)));

app.MapGet("/api/privacy-events", async (ResidentStore store, CancellationToken cancellationToken) =>
    TypedResults.Ok(await store.GetPrivacyEventsAsync(cancellationToken)));

app.MapGet("/api/address-lookup", async (
    [FromQuery] string postalCode,
    AddressValidationService addressValidationService,
    CancellationToken cancellationToken) =>
{
    var result = await addressValidationService.LookupPostalCodeAsync(postalCode, cancellationToken);

    return result.Status switch
    {
        PostalCodeLookupStatus.Success => Results.Ok(new AddressLookupResponse(
            result.Value!.PostalCodeDigits,
            result.Value.Street,
            result.Value.Neighborhood,
            result.Value.City,
            result.Value.State,
            result.Value.Complement)),
        PostalCodeLookupStatus.Invalid => Results.ValidationProblem(new Dictionary<string, string[]>
        {
            ["postalCode"] = [result.Message ?? "Nao foi possivel validar o CEP informado."],
        }),
        _ => Results.Problem(
            statusCode: StatusCodes.Status503ServiceUnavailable,
            title: "Validacao de endereco indisponivel",
            detail: result.Message),
    };
});

app.MapGet("/api/emergency-alerts", async (ResidentStore store, CancellationToken cancellationToken) =>
    TypedResults.Ok(await store.GetEmergencyAlertsAsync(cancellationToken)));

app.MapPost("/api/emergency-alerts/process", async (ResidentStore store, CancellationToken cancellationToken) =>
{
    await store.EvaluateEmergencyAlertsAsync(cancellationToken);
    return TypedResults.Ok(await store.ProcessPendingEmergencyAlertsAsync(cancellationToken));
});

app.MapGet("/api/test-hooks/events", async (NotificationSinkStore sinkStore, CancellationToken cancellationToken) =>
    TypedResults.Ok(await sinkStore.GetEventsAsync(cancellationToken)));

app.MapPost("/api/test-hooks/emergency-contact", async (HttpRequest request, NotificationSinkStore sinkStore, CancellationToken cancellationToken) =>
{
    await sinkStore.RecordEventAsync(NotificationSinkTypes.EmergencyContact, request, cancellationToken);
    return Results.Accepted();
}).DisableAntiforgery();

app.MapPost("/api/test-hooks/public-agency", async (HttpRequest request, NotificationSinkStore sinkStore, CancellationToken cancellationToken) =>
{
    await sinkStore.RecordEventAsync(NotificationSinkTypes.PublicAgency, request, cancellationToken);
    return Results.Accepted();
}).DisableAntiforgery();

app.MapPost("/api/residents", async ([FromForm] ResidentRegistrationForm form, ResidentStore store, CancellationToken cancellationToken) =>
{
    var errors = ValidateRegistration(form);

    if (errors.Count > 0)
    {
        return Results.ValidationProblem(errors);
    }

    var result = await store.AddResidentAsync(form, cancellationToken);

    return result.Status switch
    {
        StoreOperationStatus.Success => Results.Created($"/api/residents/{result.Value!.Id}", result.Value),
        StoreOperationStatus.ValidationFailed => Results.ValidationProblem(new Dictionary<string, string[]>
        {
            [result.Field ?? "form"] = [result.Message ?? "Nao foi possivel processar os dados informados."],
        }),
        StoreOperationStatus.ServiceUnavailable => Results.Problem(
            statusCode: StatusCodes.Status503ServiceUnavailable,
            title: "Servico temporariamente indisponivel",
            detail: result.Message),
        _ => Results.Problem(
            statusCode: StatusCodes.Status500InternalServerError,
            title: "Falha ao registrar morador",
            detail: result.Message),
    };
}).DisableAntiforgery();

app.MapPost("/api/residents/{residentId:guid}/revoke", async (
    Guid residentId,
    [FromBody] PrivacyActionRequest request,
    ResidentStore store,
    CancellationToken cancellationToken) =>
{
    var result = await store.RevokeResidentAsync(residentId, request.Reason, cancellationToken);

    return result.Status switch
    {
        StoreOperationStatus.Success => Results.Ok(result.Value),
        StoreOperationStatus.NotFound => Results.NotFound(new { message = result.Message ?? "Morador nao encontrado." }),
        _ => Results.Problem(
            statusCode: StatusCodes.Status500InternalServerError,
            title: "Falha ao revogar consentimento",
            detail: result.Message),
    };
});

app.MapDelete("/api/residents/{residentId:guid}", async (
    Guid residentId,
    [FromBody] PrivacyActionRequest request,
    ResidentStore store,
    CancellationToken cancellationToken) =>
{
    var result = await store.DeleteResidentAsync(residentId, request.Reason, cancellationToken);

    return result.Status switch
    {
        StoreOperationStatus.Success => Results.Ok(result.Value),
        StoreOperationStatus.NotFound => Results.NotFound(new { message = result.Message ?? "Morador nao encontrado." }),
        _ => Results.Problem(
            statusCode: StatusCodes.Status500InternalServerError,
            title: "Falha ao excluir cadastro",
            detail: result.Message),
    };
});

app.MapPost("/api/recognitions/verify", async (
    [FromForm] RecognitionVerificationForm form,
    ResidentStore store,
    CancellationToken cancellationToken) =>
{
    var errors = ValidateRecognition(form);

    if (errors.Count > 0)
    {
        return Results.ValidationProblem(errors);
    }

    var result = await store.VerifyRecognitionAsync(form, cancellationToken);

    return result.Status switch
    {
        StoreOperationStatus.Success => Results.Ok(result.Value),
        StoreOperationStatus.ValidationFailed => Results.ValidationProblem(new Dictionary<string, string[]>
        {
            ["photo"] = [result.Message ?? "Nao foi possivel comparar a captura enviada."],
        }),
        StoreOperationStatus.ServiceUnavailable => Results.Problem(
            statusCode: StatusCodes.Status503ServiceUnavailable,
            title: "Encoder facial indisponivel",
            detail: result.Message),
        _ => Results.Problem(
            statusCode: StatusCodes.Status500InternalServerError,
            title: "Falha ao verificar reconhecimento",
            detail: result.Message),
    };
}).DisableAntiforgery();

app.Run();

static Dictionary<string, string[]> ValidateRecognition(RecognitionVerificationForm form)
{
    var errors = new Dictionary<string, string[]>();

    if (form.Photo is null || form.Photo.Length == 0)
    {
        errors["photo"] = ["Envie uma captura valida para comparar com os cadastros ativos."];
    }
    else if (!IsSupportedImage(form.Photo))
    {
        errors["photo"] = ["Envie uma captura JPG, JPEG, PNG ou WEBP."];
    }

    return errors;
}

static Dictionary<string, string[]> ValidateRegistration(ResidentRegistrationForm form)
{
    var errors = new Dictionary<string, string[]>();

    if (!HasValidFullName(form.Name))
    {
        errors["name"] = ["Informe nome e sobrenome do titular."];
    }

    if (!IsValidCpf(form.Cpf))
    {
        errors["cpf"] = ["Informe um CPF valido com 11 digitos."];
    }

    if (NormalizeDigits(form.PostalCode).Length != 8)
    {
        errors["postalCode"] = ["Informe um CEP valido com 8 digitos."];
    }

    if (string.IsNullOrWhiteSpace(form.Street) || form.Street.Trim().Length < 4)
    {
        errors["street"] = ["Informe o logradouro completo do titular."];
    }

    if (string.IsNullOrWhiteSpace(form.Number) || !form.Number.Any(char.IsDigit))
    {
        errors["number"] = ["Informe o numero do endereco."];
    }

    if (string.IsNullOrWhiteSpace(form.Neighborhood) || form.Neighborhood.Trim().Length < 3)
    {
        errors["neighborhood"] = ["Informe o bairro do titular."];
    }

    if (!HasValidFullName(form.EmergencyContactName))
    {
        errors["emergencyContactName"] = ["Informe nome e sobrenome do contato de emergencia."];
    }

    var emergencyPhoneDigits = NormalizeDigits(form.EmergencyContactPhone);

    if (emergencyPhoneDigits.Length < 10 || emergencyPhoneDigits.Length > 11)
    {
        errors["emergencyContactPhone"] = ["Informe um telefone de emergencia valido com DDD."];
    }

    if (!string.IsNullOrWhiteSpace(form.EmergencyContactEmail) && !IsValidEmail(form.EmergencyContactEmail))
    {
        errors["emergencyContactEmail"] = ["Informe um e-mail valido para o contato de emergencia."];
    }

    if (string.IsNullOrWhiteSpace(form.EmergencyContactRelationship))
    {
        errors["emergencyContactRelationship"] = ["Informe a relacao do contato de emergencia com o titular."];
    }

    if (!IsValidAlertDestination(form.AlertDestination))
    {
        errors["alertDestination"] = ["Selecione quem deve ser acionado em caso de ausencia de reconhecimento."];
    }

    if (!ConsentGranted(form.Consent))
    {
        errors["consent"] = ["O consentimento explicito e obrigatorio para este piloto."];
    }

    if (form.Photo is null || form.Photo.Length == 0)
    {
        errors["photo"] = ["Anexe uma foto de referencia para concluir o cadastro."];
    }
    else if (!IsSupportedImage(form.Photo))
    {
        errors["photo"] = ["Envie uma imagem JPG, JPEG, PNG ou WEBP."];
    }

    return errors;
}

static bool HasValidFullName(string? fullName)
{
    if (string.IsNullOrWhiteSpace(fullName))
    {
        return false;
    }

    var parts = fullName
        .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Where(part => part.Any(char.IsLetter))
        .ToArray();

    if (parts.Length < 2)
    {
        return false;
    }

    return parts[0].Count(char.IsLetter) >= 2 && parts[^1].Count(char.IsLetter) >= 2;
}

static bool IsValidAlertDestination(string? value) =>
    string.Equals(value, AlertDestinations.ContactOnly, StringComparison.OrdinalIgnoreCase) ||
    string.Equals(value, AlertDestinations.ContactAndPublicAgency, StringComparison.OrdinalIgnoreCase);

static bool IsValidEmail(string? value)
{
    if (string.IsNullOrWhiteSpace(value))
    {
        return false;
    }

    var trimmed = value.Trim();
    var atIndex = trimmed.IndexOf('@');
    var lastDotIndex = trimmed.LastIndexOf('.');

    return atIndex > 0 &&
           lastDotIndex > atIndex + 1 &&
           lastDotIndex < trimmed.Length - 1 &&
           !trimmed.Any(char.IsWhiteSpace);
}

static bool IsValidCpf(string? cpf)
{
    var digits = NormalizeDigits(cpf);

    if (digits.Length != 11 || digits.Distinct().Count() == 1)
    {
        return false;
    }

    var firstDigit = CalculateCpfDigit(digits[..9], 10);
    var secondDigit = CalculateCpfDigit(digits[..10], 11);

    return digits[9] - '0' == firstDigit && digits[10] - '0' == secondDigit;
}

static int CalculateCpfDigit(string source, int weight)
{
    var sum = 0;

    for (var index = 0; index < source.Length; index++)
    {
        sum += (source[index] - '0') * (weight - index);
    }

    var remainder = sum % 11;
    return remainder < 2 ? 0 : 11 - remainder;
}

static string NormalizeDigits(string? value) =>
    string.IsNullOrWhiteSpace(value)
        ? string.Empty
        : new string(value.Where(char.IsDigit).ToArray());

static bool ConsentGranted(string? consent) =>
    string.Equals(consent, "on", StringComparison.OrdinalIgnoreCase) ||
    string.Equals(consent, "true", StringComparison.OrdinalIgnoreCase);

static bool IsSupportedImage(IFormFile photo)
{
    var extension = Path.GetExtension(photo.FileName);

    return extension.Equals(".jpg", StringComparison.OrdinalIgnoreCase) ||
           extension.Equals(".jpeg", StringComparison.OrdinalIgnoreCase) ||
           extension.Equals(".png", StringComparison.OrdinalIgnoreCase) ||
           extension.Equals(".webp", StringComparison.OrdinalIgnoreCase);
}

sealed class ResidentStore
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly JsonSerializerOptions jsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    private const string MonitoringContext = "moradia individual em Teresopolis/RJ";
    private const string MonitoringPurpose = "checagem de bem-estar com reconhecimento facial a cada 48 horas";
    private static readonly TimeSpan InactivityWindow = TimeSpan.FromHours(48);

    private readonly AddressValidationService addressValidationService;
    private readonly EmergencyNotificationDispatcher emergencyNotificationDispatcher;
    private readonly FaceEncodingService faceEncodingService;
    private readonly string emergencyAlertsFilePath;
    private readonly string residentsFilePath;
    private readonly string recognitionsFilePath;
    private readonly string privacyEventsFilePath;

    public ResidentStore(
        IWebHostEnvironment environment,
        FaceEncodingService faceEncodingService,
        AddressValidationService addressValidationService,
        EmergencyNotificationDispatcher emergencyNotificationDispatcher)
    {
        this.faceEncodingService = faceEncodingService;
        this.addressValidationService = addressValidationService;
        this.emergencyNotificationDispatcher = emergencyNotificationDispatcher;
        StorageDirectory = Path.Combine(environment.ContentRootPath, "App_Data");
        UploadsDirectory = Path.Combine(StorageDirectory, "uploads");
        ProbeDirectory = Path.Combine(StorageDirectory, "probes");
        emergencyAlertsFilePath = Path.Combine(StorageDirectory, "emergency-alerts.json");
        residentsFilePath = Path.Combine(StorageDirectory, "residents.json");
        recognitionsFilePath = Path.Combine(StorageDirectory, "recognitions.json");
        privacyEventsFilePath = Path.Combine(StorageDirectory, "privacy-events.json");

        Directory.CreateDirectory(StorageDirectory);
        Directory.CreateDirectory(UploadsDirectory);
        Directory.CreateDirectory(ProbeDirectory);

        EnsureJsonFileExists(residentsFilePath);
        EnsureJsonFileExists(recognitionsFilePath);
        EnsureJsonFileExists(privacyEventsFilePath);
        EnsureJsonFileExists(emergencyAlertsFilePath);
    }

    public string StorageDirectory { get; }

    public string UploadsDirectory { get; }

    public string ProbeDirectory { get; }

    public async Task<ResidentCountSnapshot> GetResidentCountsAsync(CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);

        try
        {
            var residents = await LoadResidentsUnsafeAsync(cancellationToken);
            var now = DateTimeOffset.UtcNow;
            return new ResidentCountSnapshot(
                residents.Count(IsActive),
                residents.Count(IsRevoked),
                residents.Count(resident => RequiresEmergencyAlert(resident, now)));
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<IReadOnlyList<PrivacyEventResponse>> GetPrivacyEventsAsync(CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);

        try
        {
            var events = await LoadPrivacyEventsUnsafeAsync(cancellationToken);

            return events
                .OrderByDescending(item => item.OccurredAt)
                .Select(item => new PrivacyEventResponse(
                    item.EventId,
                    item.ResidentId,
                    item.EventType,
                    item.CpfMasked,
                    item.Reason,
                    item.OccurredAt,
                    item.PhotoDeleted,
                    item.FaceEncodingDeleted,
                    item.StatusAfterAction))
                .ToArray();
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<IReadOnlyList<EmergencyAlertResponse>> GetEmergencyAlertsAsync(CancellationToken cancellationToken)
    {
        await EvaluateEmergencyAlertsAsync(cancellationToken);
        await gate.WaitAsync(cancellationToken);

        try
        {
            var alerts = await LoadEmergencyAlertsUnsafeAsync(cancellationToken);

            return alerts
                .OrderByDescending(item => item.TriggeredAt)
                .Select(item => new EmergencyAlertResponse(
                    item.AlertId,
                    item.ResidentId,
                    item.ResidentName,
                    item.CpfMasked,
                    item.Address,
                    item.ContactName,
                    item.ContactPhoneMasked,
                    MaskEmail(item.ContactEmail),
                    item.ContactRelationship,
                    item.AlertDestination,
                    item.DestinationDescription,
                    item.ReferenceTime,
                    item.TriggeredAt,
                    item.HoursWithoutRecognition,
                    item.LastDispatchAttemptAt,
                    item.DispatchedAt,
                    item.DispatchSummary,
                    item.DeliveryAttempts.Count(delivery => delivery.Succeeded),
                    item.DeliveryAttempts.Count(delivery => !delivery.Succeeded),
                    item.Status))
                .ToArray();
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task EvaluateEmergencyAlertsAsync(CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);

        try
        {
            var residents = await LoadResidentsUnsafeAsync(cancellationToken);
            var alerts = await LoadEmergencyAlertsUnsafeAsync(cancellationToken);
            var now = DateTimeOffset.UtcNow;
            var residentsChanged = false;
            var alertsChanged = false;

            foreach (var resident in residents.Where(IsActive))
            {
                if (!RequiresEmergencyAlert(resident, now))
                {
                    continue;
                }

                var referenceTime = resident.LastRecognitionAt ?? resident.CreatedAt;
                var alertThreshold = referenceTime.Add(InactivityWindow);

                if (resident.LastEmergencyAlertAt is not null && resident.LastEmergencyAlertAt >= alertThreshold)
                {
                    continue;
                }

                var triggeredAt = now;

                alerts.Add(new EmergencyAlertRecord
                {
                    AlertId = Guid.NewGuid(),
                    ResidentId = resident.Id,
                    ResidentName = resident.Name,
                    CpfMasked = resident.CpfMasked,
                    Address = resident.Address,
                    ContactName = resident.EmergencyContactName,
                    ContactPhone = resident.EmergencyContactPhone,
                    ContactPhoneMasked = MaskPhone(resident.EmergencyContactPhone),
                    ContactEmail = resident.EmergencyContactEmail,
                    ContactRelationship = resident.EmergencyContactRelationship,
                    AlertDestination = resident.AlertDestination,
                    DestinationDescription = BuildAlertDestinationDescription(resident.AlertDestination),
                    ReferenceTime = referenceTime,
                    TriggeredAt = triggeredAt,
                    HoursWithoutRecognition = (int)Math.Floor((triggeredAt - referenceTime).TotalHours),
                    Status = EmergencyAlertStatuses.PendingDispatch,
                });

                resident.LastEmergencyAlertAt = triggeredAt;
                residentsChanged = true;
                alertsChanged = true;
            }

            if (residentsChanged)
            {
                await SaveResidentsUnsafeAsync(residents, cancellationToken);
            }

            if (alertsChanged)
            {
                await SaveEmergencyAlertsUnsafeAsync(alerts, cancellationToken);
            }
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<EmergencyAlertProcessingResponse> ProcessPendingEmergencyAlertsAsync(CancellationToken cancellationToken)
    {
        List<EmergencyAlertDispatchContext> dispatchContexts;

        await gate.WaitAsync(cancellationToken);

        try
        {
            var alerts = await LoadEmergencyAlertsUnsafeAsync(cancellationToken);
            var residents = await LoadResidentsUnsafeAsync(cancellationToken);

            dispatchContexts = alerts
                .Where(ShouldDispatchAlert)
                .Select(alert => BuildDispatchContext(alert, residents))
                .Where(context => context is not null)
                .Cast<EmergencyAlertDispatchContext>()
                .ToList();
        }
        finally
        {
            gate.Release();
        }

        var outcomes = new List<(Guid AlertId, AlertDispatchOutcome Outcome)>();

        foreach (var dispatchContext in dispatchContexts)
        {
            outcomes.Add((dispatchContext.AlertId, await emergencyNotificationDispatcher.DispatchAsync(dispatchContext, cancellationToken)));
        }

        await gate.WaitAsync(cancellationToken);

        try
        {
            var alerts = await LoadEmergencyAlertsUnsafeAsync(cancellationToken);

            foreach (var (alertId, outcome) in outcomes)
            {
                var alert = alerts.FirstOrDefault(item => item.AlertId == alertId);

                if (alert is null)
                {
                    continue;
                }

                ApplyDispatchOutcome(alert, outcome);
            }

            if (outcomes.Count > 0)
            {
                await SaveEmergencyAlertsUnsafeAsync(alerts, cancellationToken);
            }

            return new EmergencyAlertProcessingResponse(
                dispatchContexts.Count,
                outcomes.Count(outcome => outcome.Outcome.SuccessfulDeliveries > 0),
                outcomes.Count(outcome => outcome.Outcome.FailedDeliveries > 0),
                outcomes.Sum(outcome => outcome.Outcome.SuccessfulDeliveries),
                outcomes.Sum(outcome => outcome.Outcome.FailedDeliveries));
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<IReadOnlyList<ResidentSummary>> GetResidentsAsync(CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);

        try
        {
            var residents = await LoadResidentsUnsafeAsync(cancellationToken);

            return residents
                .OrderByDescending(record => record.CreatedAt)
                .Select(ToSummary)
                .ToArray();
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<StoreOperation<ResidentSummary>> AddResidentAsync(ResidentRegistrationForm form, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(form.Photo);

        var storedPhotoFileName = $"{DateTime.UtcNow:yyyyMMddHHmmss}_{Guid.NewGuid():N}{GetSafeExtension(form.Photo.FileName)}";
        var storedPhotoPath = Path.Combine(UploadsDirectory, storedPhotoFileName);

        await using (var uploadStream = File.Open(storedPhotoPath, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            await form.Photo.CopyToAsync(uploadStream, cancellationToken);
        }

        try
        {
            var validatedAddress = await addressValidationService.ValidateResidenceAsync(form, cancellationToken);

            if (validatedAddress.Status is AddressValidationStatus.ServiceUnavailable)
            {
                TryDeleteFile(storedPhotoPath);
                return new StoreOperation<ResidentSummary>(StoreOperationStatus.ServiceUnavailable, null, validatedAddress.Message, validatedAddress.Field);
            }

            if (validatedAddress.Status is not AddressValidationStatus.Success || validatedAddress.Value is null)
            {
                TryDeleteFile(storedPhotoPath);
                return new StoreOperation<ResidentSummary>(StoreOperationStatus.ValidationFailed, null, validatedAddress.Message, validatedAddress.Field);
            }

            var extraction = await faceEncodingService.ExtractEncodingAsync(storedPhotoPath, cancellationToken);

            if (extraction.Status is FaceEncodingExtractionStatus.ServiceUnavailable)
            {
                TryDeleteFile(storedPhotoPath);
                return new StoreOperation<ResidentSummary>(StoreOperationStatus.ServiceUnavailable, null, extraction.Message, "photo");
            }

            if (extraction.Status is not FaceEncodingExtractionStatus.Success || extraction.Encoding is null)
            {
                TryDeleteFile(storedPhotoPath);
                return new StoreOperation<ResidentSummary>(StoreOperationStatus.ValidationFailed, null, extraction.Message, "photo");
            }

            var resident = new ResidentRecord
            {
                Id = Guid.NewGuid(),
                Name = form.Name!.Trim(),
                CpfMasked = MaskCpf(form.Cpf!),
                Address = validatedAddress.Value.NormalizedAddress,
                PostalCode = validatedAddress.Value.PostalCode,
                Street = validatedAddress.Value.Street,
                Number = form.Number!.Trim(),
                Neighborhood = validatedAddress.Value.Neighborhood,
                City = validatedAddress.Value.City,
                State = validatedAddress.Value.State,
                Context = MonitoringContext,
                Purpose = MonitoringPurpose,
                EmergencyContactName = form.EmergencyContactName!.Trim(),
                EmergencyContactPhone = ExtractDigits(form.EmergencyContactPhone),
                EmergencyContactEmail = NormalizeOptionalText(form.EmergencyContactEmail),
                EmergencyContactRelationship = form.EmergencyContactRelationship!.Trim(),
                AlertDestination = NormalizeAlertDestination(form.AlertDestination),
                PhotoName = form.Photo.FileName,
                StoredPhotoFileName = storedPhotoFileName,
                FaceToken = BuildFaceToken(extraction.Encoding),
                FaceEncoding = extraction.Encoding,
                CreatedAt = DateTimeOffset.UtcNow,
                Status = ResidentStatuses.Active,
            };

            await gate.WaitAsync(cancellationToken);

            try
            {
                var residents = await LoadResidentsUnsafeAsync(cancellationToken);
                residents.Add(resident);
                await SaveResidentsUnsafeAsync(residents, cancellationToken);
            }
            finally
            {
                gate.Release();
            }

            return new StoreOperation<ResidentSummary>(StoreOperationStatus.Success, ToSummary(resident));
        }
        catch
        {
            TryDeleteFile(storedPhotoPath);
            throw;
        }
    }

    public async Task<StoreOperation<ResidentSummary>> RevokeResidentAsync(
        Guid residentId,
        string? reason,
        CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);

        try
        {
            var residents = await LoadResidentsUnsafeAsync(cancellationToken);
            var resident = residents.FirstOrDefault(item => item.Id == residentId);

            if (resident is null)
            {
                return new StoreOperation<ResidentSummary>(StoreOperationStatus.NotFound, null, "Morador nao encontrado para revogacao.");
            }

            if (IsRevoked(resident))
            {
                return new StoreOperation<ResidentSummary>(StoreOperationStatus.Success, ToSummary(resident));
            }

            var occurredAt = DateTimeOffset.UtcNow;
            var photoDeleted = DeleteStoredPhoto(resident.StoredPhotoFileName);
            var encodingDeleted = resident.FaceEncoding is { Length: 128 };

            resident.Status = ResidentStatuses.Revoked;
            resident.RevokedAt = occurredAt;
            resident.RevocationReason = NormalizeReason(reason, "Consentimento revogado pelo titular.");
            resident.FaceEncoding = null;
            resident.FaceToken = "revogado";
            resident.StoredPhotoFileName = null;
            resident.PhotoName = null;

            await SaveResidentsUnsafeAsync(residents, cancellationToken);

            var privacyEvents = await LoadPrivacyEventsUnsafeAsync(cancellationToken);
            privacyEvents.Add(new PrivacyEventRecord
            {
                EventId = Guid.NewGuid(),
                ResidentId = resident.Id,
                EventType = PrivacyEventTypes.ConsentRevoked,
                CpfMasked = resident.CpfMasked,
                Reason = resident.RevocationReason,
                OccurredAt = occurredAt,
                PhotoDeleted = photoDeleted,
                FaceEncodingDeleted = encodingDeleted,
                StatusAfterAction = resident.Status,
            });
            await SavePrivacyEventsUnsafeAsync(privacyEvents, cancellationToken);

            return new StoreOperation<ResidentSummary>(StoreOperationStatus.Success, ToSummary(resident));
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<StoreOperation<DeleteResidentResponse>> DeleteResidentAsync(
        Guid residentId,
        string? reason,
        CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);

        try
        {
            var residents = await LoadResidentsUnsafeAsync(cancellationToken);
            var resident = residents.FirstOrDefault(item => item.Id == residentId);

            if (resident is null)
            {
                return new StoreOperation<DeleteResidentResponse>(StoreOperationStatus.NotFound, null, "Morador nao encontrado para exclusao.");
            }

            var occurredAt = DateTimeOffset.UtcNow;
            var normalizedReason = NormalizeReason(reason, "Exclusao solicitada pelo titular.");
            var photoDeleted = DeleteStoredPhoto(resident.StoredPhotoFileName);
            var encodingDeleted = resident.FaceEncoding is { Length: 128 };

            residents.Remove(resident);
            await SaveResidentsUnsafeAsync(residents, cancellationToken);

            var privacyEvents = await LoadPrivacyEventsUnsafeAsync(cancellationToken);
            privacyEvents.Add(new PrivacyEventRecord
            {
                EventId = Guid.NewGuid(),
                ResidentId = resident.Id,
                EventType = PrivacyEventTypes.RecordDeleted,
                CpfMasked = resident.CpfMasked,
                Reason = normalizedReason,
                OccurredAt = occurredAt,
                PhotoDeleted = photoDeleted,
                FaceEncodingDeleted = encodingDeleted,
                StatusAfterAction = ResidentStatuses.Deleted,
            });
            await SavePrivacyEventsUnsafeAsync(privacyEvents, cancellationToken);

            return new StoreOperation<DeleteResidentResponse>(
                StoreOperationStatus.Success,
                new DeleteResidentResponse(resident.Id, occurredAt, normalizedReason));
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<StoreOperation<RecognitionVerificationResponse>> VerifyRecognitionAsync(
        RecognitionVerificationForm form,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(form.Photo);

        var probeFileName = $"{DateTime.UtcNow:yyyyMMddHHmmss}_{Guid.NewGuid():N}{GetSafeExtension(form.Photo.FileName)}";
        var probePath = Path.Combine(ProbeDirectory, probeFileName);

        try
        {
            await using (var probeStream = File.Open(probePath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                await form.Photo.CopyToAsync(probeStream, cancellationToken);
            }

            List<ResidentRecord> residents;

            await gate.WaitAsync(cancellationToken);

            try
            {
                residents = await LoadResidentsUnsafeAsync(cancellationToken);
            }
            finally
            {
                gate.Release();
            }

            var candidates = residents
                .Where(record => IsActive(record) && record.FaceEncoding is { Length: 128 })
                .Select(record => new FaceVerificationCandidate(
                    record.Id,
                    record.Name,
                    record.Purpose,
                    record.FaceEncoding!))
                .ToArray();

            var outcome = await faceEncodingService.VerifyAsync(probePath, candidates, cancellationToken);
            var processedAt = DateTimeOffset.UtcNow;

            await gate.WaitAsync(cancellationToken);

            try
            {
                var logs = await LoadRecognitionsUnsafeAsync(cancellationToken);
                logs.Add(new RecognitionLogRecord
                {
                    EventId = Guid.NewGuid(),
                    MatchedResidentId = outcome.ResidentId,
                    Name = outcome.Name,
                    MatchFound = outcome.MatchFound,
                    Confidence = outcome.Confidence,
                    Distance = outcome.Distance,
                    FacesDetected = outcome.FacesDetected,
                    ProcessedAt = processedAt,
                    Message = outcome.Message,
                });

                if (outcome.MatchFound && outcome.ResidentId is not null)
                {
                    var trackedResidents = await LoadResidentsUnsafeAsync(cancellationToken);
                    var matchedResident = trackedResidents.FirstOrDefault(item => item.Id == outcome.ResidentId);

                    if (matchedResident is not null)
                    {
                        matchedResident.LastRecognitionAt = processedAt;
                        await SaveResidentsUnsafeAsync(trackedResidents, cancellationToken);
                    }
                }

                await SaveRecognitionsUnsafeAsync(logs, cancellationToken);
            }
            finally
            {
                gate.Release();
            }

            return outcome.Status switch
            {
                FaceVerificationStatus.Success => new StoreOperation<RecognitionVerificationResponse>(
                    StoreOperationStatus.Success,
                    new RecognitionVerificationResponse(
                        outcome.MatchFound,
                        outcome.Message,
                        processedAt,
                        outcome.FacesDetected,
                        outcome.Confidence,
                        outcome.Distance,
                        outcome.ResidentId,
                        outcome.Name,
                        outcome.Purpose)),
                FaceVerificationStatus.ServiceUnavailable => new StoreOperation<RecognitionVerificationResponse>(
                    StoreOperationStatus.ServiceUnavailable,
                    null,
                    outcome.Message),
                _ => new StoreOperation<RecognitionVerificationResponse>(
                    StoreOperationStatus.ValidationFailed,
                    null,
                    outcome.Message),
            };
        }
        finally
        {
            TryDeleteFile(probePath);
        }
    }

    private async Task<List<PrivacyEventRecord>> LoadPrivacyEventsUnsafeAsync(CancellationToken cancellationToken)
    {
        await using var stream = File.Open(privacyEventsFilePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        var items = await JsonSerializer.DeserializeAsync<List<PrivacyEventRecord>>(stream, jsonOptions, cancellationToken);
        return items ?? [];
    }

    private async Task<List<EmergencyAlertRecord>> LoadEmergencyAlertsUnsafeAsync(CancellationToken cancellationToken)
    {
        await using var stream = File.Open(emergencyAlertsFilePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        var items = await JsonSerializer.DeserializeAsync<List<EmergencyAlertRecord>>(stream, jsonOptions, cancellationToken);

        foreach (var item in items ?? [])
        {
            item.DeliveryAttempts ??= [];
            item.DispatchSummary ??= string.Empty;
            item.Status = string.IsNullOrWhiteSpace(item.Status)
                ? EmergencyAlertStatuses.PendingDispatch
                : item.Status;
        }

        return items ?? [];
    }

    private async Task<List<RecognitionLogRecord>> LoadRecognitionsUnsafeAsync(CancellationToken cancellationToken)
    {
        await using var stream = File.Open(recognitionsFilePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        var items = await JsonSerializer.DeserializeAsync<List<RecognitionLogRecord>>(stream, jsonOptions, cancellationToken);
        return items ?? [];
    }

    private async Task<List<ResidentRecord>> LoadResidentsUnsafeAsync(CancellationToken cancellationToken)
    {
        await using var stream = File.Open(residentsFilePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        var residents = await JsonSerializer.DeserializeAsync<List<ResidentRecord>>(stream, jsonOptions, cancellationToken) ?? [];

        if (NormalizeResidents(residents))
        {
            await SaveResidentsUnsafeAsync(residents, cancellationToken);
        }

        return residents;
    }

    private async Task SavePrivacyEventsUnsafeAsync(List<PrivacyEventRecord> items, CancellationToken cancellationToken)
    {
        await using var stream = File.Open(privacyEventsFilePath, FileMode.Create, FileAccess.Write, FileShare.None);
        await JsonSerializer.SerializeAsync(stream, items, jsonOptions, cancellationToken);
    }

    private async Task SaveEmergencyAlertsUnsafeAsync(List<EmergencyAlertRecord> items, CancellationToken cancellationToken)
    {
        await using var stream = File.Open(emergencyAlertsFilePath, FileMode.Create, FileAccess.Write, FileShare.None);
        await JsonSerializer.SerializeAsync(stream, items, jsonOptions, cancellationToken);
    }

    private async Task SaveRecognitionsUnsafeAsync(List<RecognitionLogRecord> items, CancellationToken cancellationToken)
    {
        await using var stream = File.Open(recognitionsFilePath, FileMode.Create, FileAccess.Write, FileShare.None);
        await JsonSerializer.SerializeAsync(stream, items, jsonOptions, cancellationToken);
    }

    private async Task SaveResidentsUnsafeAsync(List<ResidentRecord> residents, CancellationToken cancellationToken)
    {
        await using var stream = File.Open(residentsFilePath, FileMode.Create, FileAccess.Write, FileShare.None);
        await JsonSerializer.SerializeAsync(stream, residents, jsonOptions, cancellationToken);
    }

    private static string BuildFaceToken(double[] encoding)
    {
        var bytes = new byte[encoding.Length * sizeof(double)];
        Buffer.BlockCopy(encoding, 0, bytes, 0, bytes.Length);
        var hash = SHA256.HashData(bytes);
        return $"face_{Convert.ToHexString(hash)[..16].ToLowerInvariant()}";
    }

    private bool DeleteStoredPhoto(string? storedPhotoFileName)
    {
        if (string.IsNullOrWhiteSpace(storedPhotoFileName))
        {
            return false;
        }

        return TryDeleteFile(Path.Combine(UploadsDirectory, storedPhotoFileName));
    }

    private static void EnsureJsonFileExists(string filePath)
    {
        if (!File.Exists(filePath))
        {
            File.WriteAllText(filePath, "[]");
        }
    }

    private static string GetSafeExtension(string fileName)
    {
        var extension = Path.GetExtension(fileName);

        return string.IsNullOrWhiteSpace(extension)
            ? ".jpg"
            : extension.ToLowerInvariant();
    }

    private static bool IsActive(ResidentRecord resident) =>
        string.Equals(resident.Status, ResidentStatuses.Active, StringComparison.OrdinalIgnoreCase);

    private static bool IsRevoked(ResidentRecord resident) =>
        string.Equals(resident.Status, ResidentStatuses.Revoked, StringComparison.OrdinalIgnoreCase);

    private static string MaskCpf(string cpf)
    {
        var digits = new string(cpf.Where(char.IsDigit).ToArray());

        return digits.Length == 11
            ? $"***.***.***-{digits[^2..]}"
            : "Nao informado";
    }

    private static string NormalizeReason(string? reason, string fallback) =>
        string.IsNullOrWhiteSpace(reason)
            ? fallback
            : reason.Trim();

    private static string? NormalizeOptionalText(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? null
            : value.Trim();

    private static string NormalizeAlertDestination(string? value) =>
        string.Equals(value, AlertDestinations.ContactAndPublicAgency, StringComparison.OrdinalIgnoreCase)
            ? AlertDestinations.ContactAndPublicAgency
            : AlertDestinations.ContactOnly;

    private static bool RequiresEmergencyAlert(ResidentRecord resident, DateTimeOffset now) =>
        IsActive(resident) && now - (resident.LastRecognitionAt ?? resident.CreatedAt) >= InactivityWindow;

    private static string BuildAlertDestinationDescription(string? destination) =>
        string.Equals(destination, AlertDestinations.ContactAndPublicAgency, StringComparison.OrdinalIgnoreCase)
            ? "Contato de emergencia e orgao publico de apoio em Teresopolis/RJ"
            : "Contato de emergencia cadastrado";

    private static string MaskPhone(string? phone)
    {
        var digits = ExtractDigits(phone);

        return digits.Length switch
        {
            10 => $"({digits[..2]}) ****-{digits[^4..]}",
            11 => $"({digits[..2]}) *****-{digits[^4..]}",
            _ => "Nao informado",
        };
    }

    private static string MaskEmail(string? email)
    {
        if (string.IsNullOrWhiteSpace(email) || !email.Contains('@', StringComparison.Ordinal))
        {
            return "Nao informado";
        }

        var parts = email.Split('@', 2, StringSplitOptions.TrimEntries);
        var userPart = parts[0];
        var domainPart = parts[1];

        if (userPart.Length <= 2)
        {
            return $"**@{domainPart}";
        }

        return $"{userPart[..1]}***{userPart[^1..]}@{domainPart}";
    }

    private static string ExtractDigits(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? string.Empty
            : new string(value.Where(char.IsDigit).ToArray());

    private static bool ShouldDispatchAlert(EmergencyAlertRecord alert) =>
        alert.Status is EmergencyAlertStatuses.PendingDispatch or EmergencyAlertStatuses.DispatchFailed or EmergencyAlertStatuses.PartiallyDispatched or EmergencyAlertStatuses.NoChannelConfigured;

    private static EmergencyAlertDispatchContext? BuildDispatchContext(EmergencyAlertRecord alert, IReadOnlyCollection<ResidentRecord> residents)
    {
        var resident = residents.FirstOrDefault(item => item.Id == alert.ResidentId);

        var contactPhone = resident?.EmergencyContactPhone ?? alert.ContactPhone;
        var contactEmail = resident?.EmergencyContactEmail ?? alert.ContactEmail;

        return new EmergencyAlertDispatchContext(
            alert.AlertId,
            alert.ResidentId,
            alert.ResidentName,
            alert.CpfMasked,
            alert.Address,
            alert.ContactName,
            contactPhone,
            contactEmail,
            alert.ContactRelationship,
            alert.AlertDestination,
            alert.DestinationDescription,
            alert.ReferenceTime,
            alert.TriggeredAt,
            alert.HoursWithoutRecognition);
    }

    private static void ApplyDispatchOutcome(EmergencyAlertRecord alert, AlertDispatchOutcome outcome)
    {
        alert.FirstDispatchAttemptAt ??= outcome.AttemptedAt;
        alert.LastDispatchAttemptAt = outcome.AttemptedAt;
        alert.DispatchSummary = outcome.Summary;
        alert.Status = outcome.Status;

        if (outcome.SuccessfulDeliveries > 0)
        {
            alert.DispatchedAt = outcome.CompletedAt;
        }

        alert.DeliveryAttempts.AddRange(outcome.Deliveries.Select(delivery => new EmergencyAlertDeliveryRecord
        {
            DeliveryId = delivery.DeliveryId,
            TargetType = delivery.TargetType,
            Channel = delivery.Channel,
            Destination = delivery.Destination,
            AttemptedAt = delivery.AttemptedAt,
            Succeeded = delivery.Succeeded,
            StatusCode = delivery.StatusCode,
            Message = delivery.Message,
        }));
    }

    private static ResidentSummary ToSummary(ResidentRecord resident) =>
        new(
            resident.Id,
            resident.Name,
            resident.CpfMasked,
            resident.Address,
            resident.Context,
            resident.Purpose,
            resident.PhotoName,
            resident.FaceToken,
            resident.CreatedAt,
            resident.Status,
            resident.RevokedAt,
            resident.RevocationReason,
            resident.PostalCode,
            resident.Street,
            resident.Number,
            resident.Neighborhood,
            resident.City,
            resident.State,
            resident.EmergencyContactName,
            MaskPhone(resident.EmergencyContactPhone),
            MaskEmail(resident.EmergencyContactEmail),
            resident.EmergencyContactRelationship,
            resident.AlertDestination,
            resident.LastRecognitionAt,
            resident.LastEmergencyAlertAt,
            RequiresEmergencyAlert(resident, DateTimeOffset.UtcNow),
            resident.FaceEncoding is { Length: 128 } && IsActive(resident));

    private static bool TryDeleteFile(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return false;
        }

        try
        {
            File.Delete(path);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private bool NormalizeResidents(List<ResidentRecord> residents)
    {
        var changed = false;

        foreach (var resident in residents)
        {
            if (resident.Id == Guid.Empty)
            {
                resident.Id = Guid.NewGuid();
                changed = true;
            }

            if (string.IsNullOrWhiteSpace(resident.Status))
            {
                resident.Status = ResidentStatuses.Active;
                changed = true;
            }

            if (string.IsNullOrWhiteSpace(resident.FaceToken))
            {
                resident.FaceToken = resident.FaceEncoding is { Length: 128 }
                    ? BuildFaceToken(resident.FaceEncoding)
                    : "indisponivel";
                changed = true;
            }

            if (string.IsNullOrWhiteSpace(resident.Context))
            {
                resident.Context = MonitoringContext;
                changed = true;
            }

            if (string.IsNullOrWhiteSpace(resident.Purpose))
            {
                resident.Purpose = MonitoringPurpose;
                changed = true;
            }

            if (string.IsNullOrWhiteSpace(resident.City))
            {
                resident.City = "Teresopolis";
                changed = true;
            }

            if (string.IsNullOrWhiteSpace(resident.State))
            {
                resident.State = "RJ";
                changed = true;
            }

            if (string.IsNullOrWhiteSpace(resident.AlertDestination))
            {
                resident.AlertDestination = AlertDestinations.ContactOnly;
                changed = true;
            }

            if (IsRevoked(resident) && resident.FaceEncoding is { Length: > 0 })
            {
                resident.FaceEncoding = null;
                changed = true;
            }
        }

        return changed;
    }
}

enum StoreOperationStatus
{
    Success,
    NotFound,
    ValidationFailed,
    ServiceUnavailable,
}

sealed record StoreOperation<T>(StoreOperationStatus Status, T? Value = default, string? Message = null, string? Field = null);

sealed class ResidentRegistrationForm
{
    public string? Name { get; set; }

    public string? Cpf { get; set; }

    public string? PostalCode { get; set; }

    public string? Street { get; set; }

    public string? Number { get; set; }

    public string? Neighborhood { get; set; }

    public string? EmergencyContactName { get; set; }

    public string? EmergencyContactPhone { get; set; }

    public string? EmergencyContactEmail { get; set; }

    public string? EmergencyContactRelationship { get; set; }

    public string? AlertDestination { get; set; }

    public string? Consent { get; set; }

    public IFormFile? Photo { get; set; }
}

sealed class RecognitionVerificationForm
{
    public IFormFile? Photo { get; set; }
}

sealed class PrivacyActionRequest
{
    public string? Reason { get; set; }
}

sealed class ResidentRecord
{
    public Guid Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public string CpfMasked { get; set; } = string.Empty;

    public string Address { get; set; } = string.Empty;

    public string PostalCode { get; set; } = string.Empty;

    public string Street { get; set; } = string.Empty;

    public string Number { get; set; } = string.Empty;

    public string Neighborhood { get; set; } = string.Empty;

    public string City { get; set; } = string.Empty;

    public string State { get; set; } = string.Empty;

    public string Context { get; set; } = string.Empty;

    public string Purpose { get; set; } = string.Empty;

    public string EmergencyContactName { get; set; } = string.Empty;

    public string EmergencyContactPhone { get; set; } = string.Empty;

    public string? EmergencyContactEmail { get; set; }

    public string EmergencyContactRelationship { get; set; } = string.Empty;

    public string AlertDestination { get; set; } = AlertDestinations.ContactOnly;

    public string? PhotoName { get; set; }

    public string? StoredPhotoFileName { get; set; }

    public string FaceToken { get; set; } = string.Empty;

    public double[]? FaceEncoding { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public string Status { get; set; } = ResidentStatuses.Active;

    public DateTimeOffset? RevokedAt { get; set; }

    public string? RevocationReason { get; set; }

    public DateTimeOffset? LastRecognitionAt { get; set; }

    public DateTimeOffset? LastEmergencyAlertAt { get; set; }
}

sealed class RecognitionLogRecord
{
    public Guid EventId { get; set; }

    public Guid? MatchedResidentId { get; set; }

    public string? Name { get; set; }

    public bool MatchFound { get; set; }

    public int Confidence { get; set; }

    public double? Distance { get; set; }

    public int FacesDetected { get; set; }

    public DateTimeOffset ProcessedAt { get; set; }

    public string Message { get; set; } = string.Empty;
}

sealed class PrivacyEventRecord
{
    public Guid EventId { get; set; }

    public Guid ResidentId { get; set; }

    public string EventType { get; set; } = string.Empty;

    public string CpfMasked { get; set; } = string.Empty;

    public string Reason { get; set; } = string.Empty;

    public DateTimeOffset OccurredAt { get; set; }

    public bool PhotoDeleted { get; set; }

    public bool FaceEncodingDeleted { get; set; }

    public string StatusAfterAction { get; set; } = string.Empty;
}

sealed class EmergencyAlertRecord
{
    public Guid AlertId { get; set; }

    public Guid ResidentId { get; set; }

    public string ResidentName { get; set; } = string.Empty;

    public string CpfMasked { get; set; } = string.Empty;

    public string Address { get; set; } = string.Empty;

    public string ContactName { get; set; } = string.Empty;

    public string ContactPhone { get; set; } = string.Empty;

    public string ContactPhoneMasked { get; set; } = string.Empty;

    public string? ContactEmail { get; set; }

    public string ContactRelationship { get; set; } = string.Empty;

    public string AlertDestination { get; set; } = AlertDestinations.ContactOnly;

    public string DestinationDescription { get; set; } = string.Empty;

    public DateTimeOffset ReferenceTime { get; set; }

    public DateTimeOffset TriggeredAt { get; set; }

    public int HoursWithoutRecognition { get; set; }

    public DateTimeOffset? FirstDispatchAttemptAt { get; set; }

    public DateTimeOffset? LastDispatchAttemptAt { get; set; }

    public DateTimeOffset? DispatchedAt { get; set; }

    public string DispatchSummary { get; set; } = string.Empty;

    public List<EmergencyAlertDeliveryRecord> DeliveryAttempts { get; set; } = [];

    public string Status { get; set; } = EmergencyAlertStatuses.PendingDispatch;
}

sealed class EmergencyAlertDeliveryRecord
{
    public Guid DeliveryId { get; set; }

    public string TargetType { get; set; } = string.Empty;

    public string Channel { get; set; } = string.Empty;

    public string Destination { get; set; } = string.Empty;

    public DateTimeOffset AttemptedAt { get; set; }

    public bool Succeeded { get; set; }

    public int? StatusCode { get; set; }

    public string Message { get; set; } = string.Empty;
}

static class ResidentStatuses
{
    public const string Active = "ativo";
    public const string Revoked = "revogado";
    public const string Deleted = "excluido";
}

static class PrivacyEventTypes
{
    public const string ConsentRevoked = "consent_revoked";
    public const string RecordDeleted = "record_deleted";
}

static class AlertDestinations
{
    public const string ContactOnly = "contato_emergencia";
    public const string ContactAndPublicAgency = "contato_e_orgao_publico";
}

static class EmergencyAlertStatuses
{
    public const string PendingDispatch = "acionamento_automatico_pendente";
    public const string Dispatched = "acionado_com_sucesso";
    public const string PartiallyDispatched = "acionamento_parcial";
    public const string DispatchFailed = "falha_no_acionamento";
    public const string NoChannelConfigured = "nenhum_canal_configurado";
}

sealed record ApiHealthResponse(
    string Status,
    string BaseUrl,
    string StorageDirectory,
    string UploadsDirectory,
    string ModelsDirectory,
    bool FaceEncodingReady,
    string FaceEncodingStatus,
    int ActiveResidents,
    int RevokedResidents,
    int PendingEmergencyAlerts);

sealed record AddressLookupResponse(
    string PostalCode,
    string Street,
    string Neighborhood,
    string City,
    string State,
    string? Complement);

sealed record DeleteResidentResponse(Guid ResidentId, DateTimeOffset DeletedAt, string Reason);

sealed record EmergencyAlertProcessingResponse(
    int PendingAlerts,
    int AlertsWithSuccessfulDispatch,
    int AlertsWithFailedDispatch,
    int SuccessfulDeliveries,
    int FailedDeliveries);

sealed record EmergencyAlertResponse(
    Guid AlertId,
    Guid ResidentId,
    string ResidentName,
    string CpfMasked,
    string Address,
    string ContactName,
    string ContactPhoneMasked,
    string ContactEmailMasked,
    string ContactRelationship,
    string AlertDestination,
    string DestinationDescription,
    DateTimeOffset ReferenceTime,
    DateTimeOffset TriggeredAt,
    int HoursWithoutRecognition,
    DateTimeOffset? LastDispatchAttemptAt,
    DateTimeOffset? DispatchedAt,
    string DispatchSummary,
    int SuccessfulDeliveries,
    int FailedDeliveries,
    string Status);

sealed record PrivacyEventResponse(
    Guid EventId,
    Guid ResidentId,
    string EventType,
    string CpfMasked,
    string Reason,
    DateTimeOffset OccurredAt,
    bool PhotoDeleted,
    bool FaceEncodingDeleted,
    string StatusAfterAction);

sealed record RecognitionVerificationResponse(
    bool MatchFound,
    string Message,
    DateTimeOffset ProcessedAt,
    int FacesDetected,
    int Confidence,
    double? Distance,
    Guid? ResidentId,
    string? Name,
    string? Purpose);

sealed record ResidentCountSnapshot(int ActiveResidents, int RevokedResidents, int PendingEmergencyAlerts);

sealed record ResidentSummary(
    Guid Id,
    string Name,
    string CpfMasked,
    string Address,
    string Context,
    string Purpose,
    string? PhotoName,
    string FaceToken,
    DateTimeOffset CreatedAt,
    string Status,
    DateTimeOffset? RevokedAt,
    string? RevocationReason,
    string PostalCode,
    string Street,
    string Number,
    string Neighborhood,
    string City,
    string State,
    string EmergencyContactName,
    string EmergencyContactPhoneMasked,
    string EmergencyContactEmailMasked,
    string EmergencyContactRelationship,
    string AlertDestination,
    DateTimeOffset? LastRecognitionAt,
    DateTimeOffset? LastEmergencyAlertAt,
    bool RequiresEmergencyAlert,
    bool BiometricReady);
