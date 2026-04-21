using System.Net.Http.Headers;
using System.Net.Mail;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;

sealed class EmergencyNotificationDispatcher
{
    private readonly IHttpClientFactory httpClientFactory;
    private readonly ILogger<EmergencyNotificationDispatcher> logger;
    private readonly IOptionsMonitor<EmergencyNotificationOptions> optionsMonitor;

    public EmergencyNotificationDispatcher(
        IHttpClientFactory httpClientFactory,
        IOptionsMonitor<EmergencyNotificationOptions> optionsMonitor,
        ILogger<EmergencyNotificationDispatcher> logger)
    {
        this.httpClientFactory = httpClientFactory;
        this.optionsMonitor = optionsMonitor;
        this.logger = logger;
    }

    public async Task<AlertDispatchOutcome> DispatchAsync(EmergencyAlertDispatchContext context, CancellationToken cancellationToken)
    {
        var options = ResolveEffectiveOptions(optionsMonitor.CurrentValue);
        var attemptedAt = DateTimeOffset.UtcNow;
        var deliveries = new List<AlertDispatchDeliveryOutcome>();

        if (!options.DispatchEnabled)
        {
            return BuildOutcome(
                attemptedAt,
                EmergencyAlertStatuses.NoChannelConfigured,
                "O despacho automatico esta desabilitado nas configuracoes.",
                deliveries);
        }

        if (!string.IsNullOrWhiteSpace(options.Webhook.ContactEndpointUrl))
        {
            deliveries.Add(await SendWebhookAsync(
                options.Webhook.ContactEndpointUrl,
                options.Webhook.AuthorizationHeaderValue,
                BuildWebhookPayload(context, NotificationTargetTypes.EmergencyContact),
                NotificationTargetTypes.EmergencyContact,
                NotificationChannelTypes.Webhook,
                cancellationToken));
        }

        if (options.Email.Enabled && !string.IsNullOrWhiteSpace(context.ContactEmail))
        {
            deliveries.Add(await SendEmailAsync(context, options.Email, cancellationToken));
        }

        if (options.Sms.Enabled && !string.IsNullOrWhiteSpace(context.ContactPhone))
        {
            deliveries.Add(await SendSmsAsync(context, options.Sms, cancellationToken));
        }

        if (string.Equals(context.AlertDestination, AlertDestinations.ContactAndPublicAgency, StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(options.Webhook.PublicAgencyEndpointUrl))
        {
            deliveries.Add(await SendWebhookAsync(
                options.Webhook.PublicAgencyEndpointUrl,
                options.Webhook.AuthorizationHeaderValue,
                BuildWebhookPayload(context, NotificationTargetTypes.PublicAgency),
                NotificationTargetTypes.PublicAgency,
                NotificationChannelTypes.Webhook,
                cancellationToken));
        }

        if (deliveries.Count == 0)
        {
            return BuildOutcome(
                attemptedAt,
                EmergencyAlertStatuses.NoChannelConfigured,
                "Nenhum canal de despacho foi configurado para este alerta.",
                deliveries);
        }

        var successfulDeliveries = deliveries.Count(delivery => delivery.Succeeded);
        var failedDeliveries = deliveries.Count - successfulDeliveries;
        var status = successfulDeliveries switch
        {
            0 => EmergencyAlertStatuses.DispatchFailed,
            _ when failedDeliveries > 0 => EmergencyAlertStatuses.PartiallyDispatched,
            _ => EmergencyAlertStatuses.Dispatched,
        };

        var summary = status switch
        {
            EmergencyAlertStatuses.Dispatched => $"Despacho concluido com {successfulDeliveries} entrega(s) bem-sucedida(s).",
            EmergencyAlertStatuses.PartiallyDispatched => $"Despacho parcial: {successfulDeliveries} entrega(s) bem-sucedida(s) e {failedDeliveries} falha(s).",
            _ => "Nenhuma entrega foi concluida com sucesso.",
        };

        return BuildOutcome(attemptedAt, status, summary, deliveries);
    }

    private async Task<AlertDispatchDeliveryOutcome> SendWebhookAsync(
        string endpointUrl,
        string? authorizationHeaderValue,
        EmergencyNotificationWebhookPayload payload,
        string targetType,
        string channel,
        CancellationToken cancellationToken)
    {
        var attemptedAt = DateTimeOffset.UtcNow;

        try
        {
            using var client = CreateHttpClient();
            using var request = new HttpRequestMessage(HttpMethod.Post, endpointUrl)
            {
                Content = JsonContent.Create(payload),
            };

            if (!string.IsNullOrWhiteSpace(authorizationHeaderValue))
            {
                request.Headers.Authorization = AuthenticationHeaderValue.Parse(authorizationHeaderValue);
            }

            using var response = await client.SendAsync(request, cancellationToken);
            var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);

            return new AlertDispatchDeliveryOutcome(
                Guid.NewGuid(),
                targetType,
                channel,
                MaskWebhookDestination(endpointUrl),
                attemptedAt,
                response.IsSuccessStatusCode,
                (int)response.StatusCode,
                response.IsSuccessStatusCode
                    ? $"Webhook aceito por {MaskWebhookDestination(endpointUrl)}."
                    : $"Webhook respondeu {(int)response.StatusCode}: {TrimMessage(responseBody)}");
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(exception, "Falha ao despachar webhook para {EndpointUrl}.", endpointUrl);
            return new AlertDispatchDeliveryOutcome(
                Guid.NewGuid(),
                targetType,
                channel,
                MaskWebhookDestination(endpointUrl),
                attemptedAt,
                false,
                null,
                $"Falha no webhook: {TrimMessage(exception.Message)}");
        }
    }

    private async Task<AlertDispatchDeliveryOutcome> SendEmailAsync(
        EmergencyAlertDispatchContext context,
        EmailNotificationOptions options,
        CancellationToken cancellationToken)
    {
        var attemptedAt = DateTimeOffset.UtcNow;
        var destination = MaskEmail(context.ContactEmail);

        if (string.IsNullOrWhiteSpace(options.Host) ||
            string.IsNullOrWhiteSpace(options.FromAddress) ||
            options.Port <= 0 ||
            string.IsNullOrWhiteSpace(context.ContactEmail))
        {
            return new AlertDispatchDeliveryOutcome(
                Guid.NewGuid(),
                NotificationTargetTypes.EmergencyContact,
                NotificationChannelTypes.Email,
                destination,
                attemptedAt,
                false,
                null,
                "Configuracao de e-mail incompleta para despacho.");
        }

        try
        {
            using var message = new MailMessage(options.FromAddress, context.ContactEmail)
            {
                Subject = $"Alerta de ausencia de reconhecimento facial - {context.ResidentName}",
                Body = BuildHumanMessage(context),
                IsBodyHtml = false,
            };

            using var client = new SmtpClient(options.Host, options.Port)
            {
                EnableSsl = options.EnableSsl,
                DeliveryMethod = SmtpDeliveryMethod.Network,
            };

            if (!string.IsNullOrWhiteSpace(options.UserName) && !string.IsNullOrWhiteSpace(options.Password))
            {
                client.Credentials = new System.Net.NetworkCredential(options.UserName, options.Password);
            }

            cancellationToken.ThrowIfCancellationRequested();
            await client.SendMailAsync(message, cancellationToken);

            return new AlertDispatchDeliveryOutcome(
                Guid.NewGuid(),
                NotificationTargetTypes.EmergencyContact,
                NotificationChannelTypes.Email,
                destination,
                attemptedAt,
                true,
                250,
                "E-mail enviado com sucesso.");
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(exception, "Falha ao enviar e-mail para {Destination}.", destination);
            return new AlertDispatchDeliveryOutcome(
                Guid.NewGuid(),
                NotificationTargetTypes.EmergencyContact,
                NotificationChannelTypes.Email,
                destination,
                attemptedAt,
                false,
                null,
                $"Falha no e-mail: {TrimMessage(exception.Message)}");
        }
    }

    private async Task<AlertDispatchDeliveryOutcome> SendSmsAsync(
        EmergencyAlertDispatchContext context,
        SmsNotificationOptions options,
        CancellationToken cancellationToken)
    {
        var attemptedAt = DateTimeOffset.UtcNow;
        var destination = MaskPhone(context.ContactPhone);

        if (!string.Equals(options.Provider, SmsProviderOptions.Twilio, StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(options.TwilioAccountSid) ||
            string.IsNullOrWhiteSpace(options.TwilioAuthToken) ||
            string.IsNullOrWhiteSpace(options.FromPhoneNumber) ||
            string.IsNullOrWhiteSpace(context.ContactPhone))
        {
            return new AlertDispatchDeliveryOutcome(
                Guid.NewGuid(),
                NotificationTargetTypes.EmergencyContact,
                NotificationChannelTypes.Sms,
                destination,
                attemptedAt,
                false,
                null,
                "Configuracao de SMS incompleta para despacho.");
        }

        try
        {
            using var client = CreateHttpClient();
            var requestUri = $"https://api.twilio.com/2010-04-01/Accounts/{options.TwilioAccountSid}/Messages.json";
            using var request = new HttpRequestMessage(HttpMethod.Post, requestUri)
            {
                Content = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["To"] = NormalizePhoneNumber(context.ContactPhone),
                    ["From"] = NormalizePhoneNumber(options.FromPhoneNumber),
                    ["Body"] = BuildSmsMessage(context),
                }),
            };

            var authToken = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{options.TwilioAccountSid}:{options.TwilioAuthToken}"));
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic", authToken);

            using var response = await client.SendAsync(request, cancellationToken);
            var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);

            return new AlertDispatchDeliveryOutcome(
                Guid.NewGuid(),
                NotificationTargetTypes.EmergencyContact,
                NotificationChannelTypes.Sms,
                destination,
                attemptedAt,
                response.IsSuccessStatusCode,
                (int)response.StatusCode,
                response.IsSuccessStatusCode
                    ? "SMS enviado com sucesso."
                    : $"SMS respondeu {(int)response.StatusCode}: {TrimMessage(responseBody)}");
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(exception, "Falha ao enviar SMS para {Destination}.", destination);
            return new AlertDispatchDeliveryOutcome(
                Guid.NewGuid(),
                NotificationTargetTypes.EmergencyContact,
                NotificationChannelTypes.Sms,
                destination,
                attemptedAt,
                false,
                null,
                $"Falha no SMS: {TrimMessage(exception.Message)}");
        }
    }

    private static AlertDispatchOutcome BuildOutcome(
        DateTimeOffset attemptedAt,
        string status,
        string summary,
        IReadOnlyList<AlertDispatchDeliveryOutcome> deliveries) =>
        new(
            attemptedAt,
            DateTimeOffset.UtcNow,
            status,
            summary,
            deliveries.Count(delivery => delivery.Succeeded),
            deliveries.Count(delivery => !delivery.Succeeded),
            deliveries);

    private static EmergencyNotificationWebhookPayload BuildWebhookPayload(EmergencyAlertDispatchContext context, string targetType) =>
        new(
            context.AlertId,
            targetType,
            context.ResidentId,
            context.ResidentName,
            context.CpfMasked,
            context.Address,
            context.ContactName,
            MaskPhone(context.ContactPhone),
            MaskEmail(context.ContactEmail),
            context.ContactRelationship,
            context.AlertDestination,
            context.DestinationDescription,
            context.ReferenceTime,
            context.TriggeredAt,
            context.HoursWithoutRecognition,
            BuildHumanMessage(context));

    private HttpClient CreateHttpClient()
    {
        var client = httpClientFactory.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(15);
        client.DefaultRequestHeaders.UserAgent.ParseAdd("OverTimeFaceAccess/1.0");
        return client;
    }

    private static string BuildHumanMessage(EmergencyAlertDispatchContext context) =>
        $"Alerta automatico do OverTime: {context.ResidentName} esta sem reconhecimento facial ha {context.HoursWithoutRecognition} horas. Ultima referencia: {context.ReferenceTime:dd/MM/yyyy HH:mm}. Endereco cadastrado: {context.Address}. Contato relacionado: {context.ContactName} ({context.ContactRelationship}).";

    private static string BuildSmsMessage(EmergencyAlertDispatchContext context) =>
        $"OverTime: {context.ResidentName} esta sem reconhecimento facial ha {context.HoursWithoutRecognition}h. Ultima referencia em {context.ReferenceTime:dd/MM HH:mm}. Endereco: {context.Address}.";

    private static string NormalizePhoneNumber(string? phone)
    {
        var digits = new string((phone ?? string.Empty).Where(char.IsDigit).ToArray());

        if (digits.StartsWith("55", StringComparison.Ordinal))
        {
            return $"+{digits}";
        }

        return $"+55{digits}";
    }

    private static string MaskPhone(string? phone)
    {
        var digits = new string((phone ?? string.Empty).Where(char.IsDigit).ToArray());

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

    private static string MaskWebhookDestination(string endpointUrl)
    {
        if (Uri.TryCreate(endpointUrl, UriKind.Absolute, out var uri))
        {
            return uri.GetLeftPart(UriPartial.Path);
        }

        return endpointUrl;
    }

    private static string TrimMessage(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return "sem detalhes adicionais";
        }

        var trimmed = message.Trim();
        return trimmed.Length <= 220 ? trimmed : trimmed[..220];
    }

    private static EmergencyNotificationOptions ResolveEffectiveOptions(EmergencyNotificationOptions source)
    {
        var resolved = new EmergencyNotificationOptions
        {
            DispatchEnabled = source.DispatchEnabled,
            Webhook = new WebhookNotificationOptions
            {
                ContactEndpointUrl = FirstDefinedValue(
                    source.Webhook.ContactEndpointUrl,
                    "EmergencyNotifications__Webhook__ContactEndpointUrl",
                    "OVERTIME__EMERGENCY_WEBHOOK_CONTACT_URL"),
                PublicAgencyEndpointUrl = FirstDefinedValue(
                    source.Webhook.PublicAgencyEndpointUrl,
                    "EmergencyNotifications__Webhook__PublicAgencyEndpointUrl",
                    "OVERTIME__EMERGENCY_WEBHOOK_PUBLIC_AGENCY_URL"),
                AuthorizationHeaderValue = FirstDefinedValue(
                    source.Webhook.AuthorizationHeaderValue,
                    "EmergencyNotifications__Webhook__AuthorizationHeaderValue",
                    "OVERTIME__EMERGENCY_WEBHOOK_AUTHORIZATION_HEADER"),
            },
            Email = new EmailNotificationOptions
            {
                Host = FirstDefinedValue(
                    source.Email.Host,
                    "EmergencyNotifications__Email__Host",
                    "Email__Smtp__Host"),
                Port = FirstDefinedInt(
                    source.Email.Port,
                    "EmergencyNotifications__Email__Port",
                    "Email__Smtp__Port"),
                EnableSsl = FirstDefinedBool(
                    source.Email.EnableSsl,
                    "EmergencyNotifications__Email__EnableSsl",
                    "Email__Smtp__EnableSsl"),
                FromAddress = FirstDefinedValue(
                    source.Email.FromAddress,
                    "EmergencyNotifications__Email__FromAddress",
                    "Email__FromAddress"),
                UserName = FirstDefinedValue(
                    source.Email.UserName,
                    "EmergencyNotifications__Email__UserName",
                    "Email__Smtp__User"),
                Password = FirstDefinedValue(
                    source.Email.Password,
                    "EmergencyNotifications__Email__Password",
                    "Email__Smtp__Password"),
            },
            Sms = new SmsNotificationOptions
            {
                Provider = FirstDefinedValue(
                    source.Sms.Provider,
                    "EmergencyNotifications__Sms__Provider",
                    "OVERTIME__TWILIO_PROVIDER") ?? source.Sms.Provider,
                TwilioAccountSid = FirstDefinedValue(
                    source.Sms.TwilioAccountSid,
                    "EmergencyNotifications__Sms__TwilioAccountSid",
                    "TWILIO_ACCOUNT_SID",
                    "OVERTIME__TWILIO_ACCOUNT_SID"),
                TwilioAuthToken = FirstDefinedValue(
                    source.Sms.TwilioAuthToken,
                    "EmergencyNotifications__Sms__TwilioAuthToken",
                    "TWILIO_AUTH_TOKEN",
                    "OVERTIME__TWILIO_AUTH_TOKEN"),
                FromPhoneNumber = FirstDefinedValue(
                    source.Sms.FromPhoneNumber,
                    "EmergencyNotifications__Sms__FromPhoneNumber",
                    "TWILIO_FROM_PHONE_NUMBER",
                    "OVERTIME__TWILIO_FROM_PHONE_NUMBER"),
            },
        };

        resolved.Email.Enabled = source.Email.Enabled || IsEmailConfigured(resolved.Email);
        resolved.Sms.Enabled = source.Sms.Enabled || IsSmsConfigured(resolved.Sms);

        return resolved;
    }

    private static string? FirstDefinedValue(string? configuredValue, params string[] environmentVariableNames)
    {
        if (!string.IsNullOrWhiteSpace(configuredValue))
        {
            return configuredValue.Trim();
        }

        foreach (var environmentVariableName in environmentVariableNames)
        {
            var value = Environment.GetEnvironmentVariable(environmentVariableName);

            if (!string.IsNullOrWhiteSpace(value))
            {
                return value.Trim();
            }
        }

        return null;
    }

    private static int FirstDefinedInt(int configuredValue, params string[] environmentVariableNames)
    {
        foreach (var environmentVariableName in environmentVariableNames)
        {
            var value = Environment.GetEnvironmentVariable(environmentVariableName);

            if (int.TryParse(value, out var parsed) && parsed > 0)
            {
                return parsed;
            }
        }

        return configuredValue;
    }

    private static bool FirstDefinedBool(bool configuredValue, params string[] environmentVariableNames)
    {
        foreach (var environmentVariableName in environmentVariableNames)
        {
            var value = Environment.GetEnvironmentVariable(environmentVariableName);

            if (bool.TryParse(value, out var parsed))
            {
                return parsed;
            }
        }

        return configuredValue;
    }

    private static bool IsEmailConfigured(EmailNotificationOptions options) =>
        !string.IsNullOrWhiteSpace(options.Host) &&
        !string.IsNullOrWhiteSpace(options.FromAddress) &&
        options.Port > 0;

    private static bool IsSmsConfigured(SmsNotificationOptions options) =>
        string.Equals(options.Provider, SmsProviderOptions.Twilio, StringComparison.OrdinalIgnoreCase) &&
        !string.IsNullOrWhiteSpace(options.TwilioAccountSid) &&
        !string.IsNullOrWhiteSpace(options.TwilioAuthToken) &&
        !string.IsNullOrWhiteSpace(options.FromPhoneNumber);
}

sealed class EmergencyNotificationOptions
{
    public const string SectionName = "EmergencyNotifications";

    public bool DispatchEnabled { get; set; } = true;

    public WebhookNotificationOptions Webhook { get; set; } = new();

    public EmailNotificationOptions Email { get; set; } = new();

    public SmsNotificationOptions Sms { get; set; } = new();
}

sealed class WebhookNotificationOptions
{
    public string? ContactEndpointUrl { get; set; }

    public string? PublicAgencyEndpointUrl { get; set; }

    public string? AuthorizationHeaderValue { get; set; }
}

sealed class EmailNotificationOptions
{
    public bool Enabled { get; set; }

    public string? Host { get; set; }

    public int Port { get; set; } = 587;

    public bool EnableSsl { get; set; } = true;

    public string? FromAddress { get; set; }

    public string? UserName { get; set; }

    public string? Password { get; set; }
}

sealed class SmsNotificationOptions
{
    public bool Enabled { get; set; }

    public string Provider { get; set; } = SmsProviderOptions.Twilio;

    public string? TwilioAccountSid { get; set; }

    public string? TwilioAuthToken { get; set; }

    public string? FromPhoneNumber { get; set; }
}

static class SmsProviderOptions
{
    public const string Twilio = "twilio";
}

static class NotificationTargetTypes
{
    public const string EmergencyContact = "contato_emergencia";
    public const string PublicAgency = "orgao_publico";
}

static class NotificationChannelTypes
{
    public const string Webhook = "webhook";
    public const string Email = "email";
    public const string Sms = "sms";
}

sealed record EmergencyAlertDispatchContext(
    Guid AlertId,
    Guid ResidentId,
    string ResidentName,
    string CpfMasked,
    string Address,
    string ContactName,
    string? ContactPhone,
    string? ContactEmail,
    string ContactRelationship,
    string AlertDestination,
    string DestinationDescription,
    DateTimeOffset ReferenceTime,
    DateTimeOffset TriggeredAt,
    int HoursWithoutRecognition);

sealed record AlertDispatchOutcome(
    DateTimeOffset AttemptedAt,
    DateTimeOffset CompletedAt,
    string Status,
    string Summary,
    int SuccessfulDeliveries,
    int FailedDeliveries,
    IReadOnlyList<AlertDispatchDeliveryOutcome> Deliveries);

sealed record AlertDispatchDeliveryOutcome(
    Guid DeliveryId,
    string TargetType,
    string Channel,
    string Destination,
    DateTimeOffset AttemptedAt,
    bool Succeeded,
    int? StatusCode,
    string Message);

sealed record EmergencyNotificationWebhookPayload(
    Guid AlertId,
    string TargetType,
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
    string Message);
