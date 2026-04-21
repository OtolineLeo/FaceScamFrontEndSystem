using System.Globalization;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Serialization;

sealed class AddressValidationService
{
    private const string AllowedCity = "TERESOPOLIS";
    private const string AllowedStateCode = "RJ";
    private readonly IHttpClientFactory httpClientFactory;

    public AddressValidationService(IHttpClientFactory httpClientFactory)
    {
        this.httpClientFactory = httpClientFactory;
    }

    public async Task<PostalCodeLookupResult> LookupPostalCodeAsync(string? postalCode, CancellationToken cancellationToken)
    {
        var digits = NormalizeDigits(postalCode);

        if (digits.Length != 8)
        {
            return PostalCodeLookupResult.Invalid("Informe um CEP com 8 digitos.");
        }

        try
        {
            using var client = CreateClient();
            using var response = await client.GetAsync($"https://viacep.com.br/ws/{digits}/json/", cancellationToken);
            response.EnsureSuccessStatusCode();

            var payload = await response.Content.ReadFromJsonAsync<ViaCepResponse>(cancellationToken: cancellationToken);

            if (payload is null || payload.Erro)
            {
                return PostalCodeLookupResult.Invalid("Nao encontramos o CEP informado.");
            }

            if (!MatchesLocation(payload.Localidade, AllowedCity) || !string.Equals(payload.Uf, AllowedStateCode, StringComparison.OrdinalIgnoreCase))
            {
                return PostalCodeLookupResult.Invalid("O sistema aceita apenas enderecos de Teresopolis, Rio de Janeiro.");
            }

            return PostalCodeLookupResult.Success(new PostalCodeLookupValue(
                digits,
                payload.Logradouro?.Trim() ?? string.Empty,
                payload.Bairro?.Trim() ?? string.Empty,
                payload.Localidade?.Trim() ?? "Teresopolis",
                payload.Uf?.Trim() ?? "RJ",
                payload.Complemento?.Trim()));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return PostalCodeLookupResult.Unavailable("Nao foi possivel validar o CEP agora. Tente novamente em instantes.");
        }
    }

    public async Task<AddressValidationResult> ValidateResidenceAsync(ResidentRegistrationForm form, CancellationToken cancellationToken)
    {
        var lookup = await LookupPostalCodeAsync(form.PostalCode, cancellationToken);

        if (lookup.Status is PostalCodeLookupStatus.ServiceUnavailable)
        {
            return AddressValidationResult.ServiceUnavailable(lookup.Message, "postalCode");
        }

        if (lookup.Status is not PostalCodeLookupStatus.Success || lookup.Value is null)
        {
            return AddressValidationResult.Invalid(lookup.Message, "postalCode");
        }

        if (!string.IsNullOrWhiteSpace(lookup.Value.Street) && !MatchesLocation(form.Street, lookup.Value.Street))
        {
            return AddressValidationResult.Invalid("O logradouro nao corresponde ao CEP informado.", "street");
        }

        if (!string.IsNullOrWhiteSpace(lookup.Value.Neighborhood) && !MatchesLocation(form.Neighborhood, lookup.Value.Neighborhood))
        {
            return AddressValidationResult.Invalid("O bairro nao corresponde ao CEP informado.", "neighborhood");
        }

        var addressConfirmation = await ConfirmAddressAsync(
            form.Street!,
            form.Number!,
            form.Neighborhood!,
            lookup.Value,
            cancellationToken);

        if (addressConfirmation.Status is AddressConfirmationStatus.ServiceUnavailable)
        {
            return AddressValidationResult.ServiceUnavailable(addressConfirmation.Message, "street");
        }

        if (addressConfirmation.Status is not AddressConfirmationStatus.Success)
        {
            return AddressValidationResult.Invalid(addressConfirmation.Message, addressConfirmation.Field);
        }

        var normalizedStreet = string.IsNullOrWhiteSpace(lookup.Value.Street) ? form.Street!.Trim() : lookup.Value.Street;
        var normalizedNeighborhood = string.IsNullOrWhiteSpace(lookup.Value.Neighborhood) ? form.Neighborhood!.Trim() : lookup.Value.Neighborhood;
        var normalizedAddress = $"{normalizedStreet}, {form.Number!.Trim()} - {normalizedNeighborhood}, Teresopolis/RJ, CEP {FormatPostalCode(lookup.Value.PostalCodeDigits)}";

        return AddressValidationResult.Success(new ValidatedAddress(
            normalizedAddress,
            FormatPostalCode(lookup.Value.PostalCodeDigits),
            normalizedStreet,
            normalizedNeighborhood,
            "Teresopolis",
            "RJ"));
    }

    private async Task<AddressConfirmationResult> ConfirmAddressAsync(
        string street,
        string number,
        string neighborhood,
        PostalCodeLookupValue lookup,
        CancellationToken cancellationToken)
    {
        try
        {
            using var client = CreateClient();
            var query = BuildNominatimQuery(street, number, lookup.PostalCodeDigits);
            using var response = await client.GetAsync(query, cancellationToken);
            response.EnsureSuccessStatusCode();

            var payload = await response.Content.ReadFromJsonAsync<List<NominatimResponse>>(cancellationToken: cancellationToken);
            var firstResult = payload?.FirstOrDefault();

            if (firstResult?.Address is null)
            {
                return AddressConfirmationResult.Invalid("Nao foi possivel confirmar esse endereco em Teresopolis/RJ.", "street");
            }

            var resultCity = firstResult.Address.City ?? firstResult.Address.Town ?? firstResult.Address.Municipality ?? firstResult.Address.Village;

            if (!MatchesLocation(resultCity, AllowedCity) || !MatchesLocation(firstResult.Address.State, "RIO DE JANEIRO"))
            {
                return AddressConfirmationResult.Invalid("O endereco informado nao pertence a Teresopolis, Rio de Janeiro.", "street");
            }

            if (!string.IsNullOrWhiteSpace(firstResult.Address.Postcode) && NormalizeDigits(firstResult.Address.Postcode) != lookup.PostalCodeDigits)
            {
                return AddressConfirmationResult.Invalid("O CEP retornado nao confere com o endereco informado.", "postalCode");
            }

            if (!string.IsNullOrWhiteSpace(firstResult.Address.HouseNumber) && NormalizeDigits(firstResult.Address.HouseNumber) != NormalizeDigits(number))
            {
                return AddressConfirmationResult.Invalid("O numero do endereco nao foi confirmado para o logradouro informado.", "number");
            }

            if (!string.IsNullOrWhiteSpace(firstResult.Address.Road) && !MatchesLocation(firstResult.Address.Road, street))
            {
                return AddressConfirmationResult.Invalid("Nao foi possivel confirmar o logradouro informado.", "street");
            }

            if (!string.IsNullOrWhiteSpace(firstResult.Address.Suburb) && !MatchesLocation(firstResult.Address.Suburb, neighborhood))
            {
                return AddressConfirmationResult.Invalid("Nao foi possivel confirmar o bairro informado.", "neighborhood");
            }

            return AddressConfirmationResult.Success();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return AddressConfirmationResult.ServiceUnavailable("Nao foi possivel consultar o validador externo de endereco no momento.", "street");
        }
    }

    private static string BuildNominatimQuery(string street, string number, string postalCodeDigits)
    {
        var streetQuery = Uri.EscapeDataString($"{street} {number}");
        var cityQuery = Uri.EscapeDataString("Teresopolis");
        var stateQuery = Uri.EscapeDataString("Rio de Janeiro");
        var postalCodeQuery = Uri.EscapeDataString(FormatPostalCode(postalCodeDigits));

        return $"https://nominatim.openstreetmap.org/search?format=jsonv2&limit=1&addressdetails=1&countrycodes=br&street={streetQuery}&city={cityQuery}&state={stateQuery}&postalcode={postalCodeQuery}";
    }

    private HttpClient CreateClient()
    {
        var client = httpClientFactory.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(12);
        client.DefaultRequestHeaders.UserAgent.ParseAdd("OverTimeFaceAccess/1.0");
        return client;
    }

    private static string FormatPostalCode(string digits) => $"{digits[..5]}-{digits[5..]}";

    private static bool MatchesLocation(string? left, string? right)
    {
        var normalizedLeft = NormalizeText(left);
        var normalizedRight = NormalizeText(right);

        if (string.IsNullOrWhiteSpace(normalizedLeft) || string.IsNullOrWhiteSpace(normalizedRight))
        {
            return false;
        }

        return normalizedLeft == normalizedRight ||
               normalizedLeft.Contains(normalizedRight, StringComparison.OrdinalIgnoreCase) ||
               normalizedRight.Contains(normalizedLeft, StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeDigits(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? string.Empty
            : new string(value.Where(char.IsDigit).ToArray());

    private static string NormalizeText(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var normalized = value.Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder();

        foreach (var character in normalized)
        {
            var category = CharUnicodeInfo.GetUnicodeCategory(character);

            if (category == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            if (char.IsLetterOrDigit(character))
            {
                builder.Append(char.ToUpperInvariant(character));
                continue;
            }

            builder.Append(' ');
        }

        return string.Join(' ', builder.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    private sealed class ViaCepResponse
    {
        [JsonPropertyName("logradouro")]
        public string? Logradouro { get; set; }

        [JsonPropertyName("complemento")]
        public string? Complemento { get; set; }

        [JsonPropertyName("bairro")]
        public string? Bairro { get; set; }

        [JsonPropertyName("localidade")]
        public string? Localidade { get; set; }

        [JsonPropertyName("uf")]
        public string? Uf { get; set; }

        [JsonPropertyName("erro")]
        public bool Erro { get; set; }
    }

    private sealed class NominatimResponse
    {
        [JsonPropertyName("address")]
        public NominatimAddress? Address { get; set; }
    }

    private sealed class NominatimAddress
    {
        [JsonPropertyName("road")]
        public string? Road { get; set; }

        [JsonPropertyName("house_number")]
        public string? HouseNumber { get; set; }

        [JsonPropertyName("suburb")]
        public string? Suburb { get; set; }

        [JsonPropertyName("city")]
        public string? City { get; set; }

        [JsonPropertyName("town")]
        public string? Town { get; set; }

        [JsonPropertyName("village")]
        public string? Village { get; set; }

        [JsonPropertyName("municipality")]
        public string? Municipality { get; set; }

        [JsonPropertyName("state")]
        public string? State { get; set; }

        [JsonPropertyName("postcode")]
        public string? Postcode { get; set; }
    }
}

enum PostalCodeLookupStatus
{
    Success,
    Invalid,
    ServiceUnavailable,
}

sealed record PostalCodeLookupResult(PostalCodeLookupStatus Status, PostalCodeLookupValue? Value = null, string? Message = null)
{
    public static PostalCodeLookupResult Success(PostalCodeLookupValue value) => new(PostalCodeLookupStatus.Success, value);

    public static PostalCodeLookupResult Invalid(string? message) => new(PostalCodeLookupStatus.Invalid, null, message);

    public static PostalCodeLookupResult Unavailable(string? message) => new(PostalCodeLookupStatus.ServiceUnavailable, null, message);
}

sealed record PostalCodeLookupValue(
    string PostalCodeDigits,
    string Street,
    string Neighborhood,
    string City,
    string State,
    string? Complement);

enum AddressValidationStatus
{
    Success,
    Invalid,
    ServiceUnavailable,
}

sealed record AddressValidationResult(
    AddressValidationStatus Status,
    ValidatedAddress? Value = null,
    string? Message = null,
    string? Field = null)
{
    public static AddressValidationResult Success(ValidatedAddress value) => new(AddressValidationStatus.Success, value);

    public static AddressValidationResult Invalid(string? message, string? field) => new(AddressValidationStatus.Invalid, null, message, field);

    public static AddressValidationResult ServiceUnavailable(string? message, string? field) => new(AddressValidationStatus.ServiceUnavailable, null, message, field);
}

sealed record ValidatedAddress(
    string NormalizedAddress,
    string PostalCode,
    string Street,
    string Neighborhood,
    string City,
    string State);

enum AddressConfirmationStatus
{
    Success,
    Invalid,
    ServiceUnavailable,
}

sealed record AddressConfirmationResult(AddressConfirmationStatus Status, string? Message = null, string? Field = null)
{
    public static AddressConfirmationResult Success() => new(AddressConfirmationStatus.Success);

    public static AddressConfirmationResult Invalid(string? message, string? field) => new(AddressConfirmationStatus.Invalid, message, field);

    public static AddressConfirmationResult ServiceUnavailable(string? message, string? field) => new(AddressConfirmationStatus.ServiceUnavailable, message, field);
}