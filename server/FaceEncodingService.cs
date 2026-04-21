using FaceRecognitionDotNet;

sealed class FaceEncodingService : IDisposable
{
    private const double RecognitionTolerance = 0.55;

    private static readonly FaceModelFile[] RequiredModels =
    [
        new(
            "shape_predictor_5_face_landmarks.dat",
            "https://raw.githubusercontent.com/ageitgey/face_recognition_models/master/face_recognition_models/models/shape_predictor_5_face_landmarks.dat"),
        new(
            "shape_predictor_68_face_landmarks.dat",
            "https://raw.githubusercontent.com/ageitgey/face_recognition_models/master/face_recognition_models/models/shape_predictor_68_face_landmarks.dat"),
        new(
            "dlib_face_recognition_resnet_model_v1.dat",
            "https://raw.githubusercontent.com/ageitgey/face_recognition_models/master/face_recognition_models/models/dlib_face_recognition_resnet_model_v1.dat"),
        new(
            "mmod_human_face_detector.dat",
            "https://raw.githubusercontent.com/ageitgey/face_recognition_models/master/face_recognition_models/models/mmod_human_face_detector.dat"),
    ];

    private readonly SemaphoreSlim bootstrapGate = new(1, 1);
    private readonly SemaphoreSlim inferenceGate = new(1, 1);
    private readonly IHttpClientFactory httpClientFactory;
    private FaceRecognition? faceRecognition;

    public FaceEncodingService(IWebHostEnvironment environment, IHttpClientFactory httpClientFactory)
    {
        this.httpClientFactory = httpClientFactory;
        ModelsDirectory = Path.Combine(environment.ContentRootPath, "App_Data", "models");
        Directory.CreateDirectory(ModelsDirectory);
    }

    public string ModelsDirectory { get; }

    public bool IsReady { get; private set; }

    public string StatusMessage { get; private set; } = "Bootstrap do encoder facial pendente.";

    public async Task EnsureReadyAsync(CancellationToken cancellationToken)
    {
        if (IsReady && faceRecognition is not null)
        {
            return;
        }

        await bootstrapGate.WaitAsync(cancellationToken);

        try
        {
            if (IsReady && faceRecognition is not null)
            {
                return;
            }

            StatusMessage = "Baixando e validando modelos de face encoding...";

            foreach (var model in RequiredModels)
            {
                await EnsureModelFileAsync(model, cancellationToken);
            }

            faceRecognition?.Dispose();
            faceRecognition = FaceRecognition.Create(ModelsDirectory);
            IsReady = true;
            StatusMessage = "Encoder facial real carregado com sucesso.";
        }
        catch (Exception exception)
        {
            faceRecognition?.Dispose();
            faceRecognition = null;
            IsReady = false;
            StatusMessage = $"Falha ao carregar encoder facial: {exception.Message}";
        }
        finally
        {
            bootstrapGate.Release();
        }
    }

    public async Task<FaceEncodingExtractionResult> ExtractEncodingAsync(string imagePath, CancellationToken cancellationToken)
    {
        await EnsureReadyAsync(cancellationToken);

        if (!IsReady || faceRecognition is null)
        {
            return FaceEncodingExtractionResult.ServiceUnavailable(StatusMessage);
        }

        await inferenceGate.WaitAsync(cancellationToken);

        try
        {
            return await Task.Run(() => ExtractEncodingCore(imagePath), cancellationToken);
        }
        finally
        {
            inferenceGate.Release();
        }
    }

    public async Task<FaceVerificationOutcome> VerifyAsync(
        string imagePath,
        IReadOnlyCollection<FaceVerificationCandidate> candidates,
        CancellationToken cancellationToken)
    {
        if (candidates.Count == 0)
        {
            return FaceVerificationOutcome.NoCandidates(
                "Nenhum cadastro ativo com biometria disponivel para comparacao.",
                0);
        }

        var extraction = await ExtractEncodingAsync(imagePath, cancellationToken);

        if (extraction.Status is FaceEncodingExtractionStatus.ServiceUnavailable)
        {
            return FaceVerificationOutcome.ServiceUnavailable(extraction.Message, extraction.FaceCount);
        }

        if (extraction.Status is FaceEncodingExtractionStatus.NoFace)
        {
            return FaceVerificationOutcome.NoFace(extraction.Message, extraction.FaceCount);
        }

        if (extraction.Status is FaceEncodingExtractionStatus.MultipleFaces)
        {
            return FaceVerificationOutcome.MultipleFaces(extraction.Message, extraction.FaceCount);
        }

        if (extraction.Status is FaceEncodingExtractionStatus.Failed || extraction.Encoding is null)
        {
            return FaceVerificationOutcome.Failed(extraction.Message, extraction.FaceCount);
        }

        await inferenceGate.WaitAsync(cancellationToken);

        try
        {
            return await Task.Run(
                () => CompareEncodingCore(extraction.Encoding, extraction.FaceCount, candidates),
                cancellationToken);
        }
        finally
        {
            inferenceGate.Release();
        }
    }

    public void Dispose()
    {
        faceRecognition?.Dispose();
        bootstrapGate.Dispose();
        inferenceGate.Dispose();
    }

    private static int BuildConfidence(double distance, bool matched)
    {
        if (!matched)
        {
            return 0;
        }

        var normalized = Math.Clamp((RecognitionTolerance - distance) / RecognitionTolerance, 0, 1);
        return (int)Math.Round(normalized * 100, MidpointRounding.AwayFromZero);
    }

    private FaceVerificationOutcome CompareEncodingCore(
        double[] probeEncodingRaw,
        int faceCount,
        IReadOnlyCollection<FaceVerificationCandidate> candidates)
    {
        using var probeEncoding = FaceRecognition.LoadFaceEncoding(probeEncodingRaw);

        FaceVerificationCandidate? bestCandidate = null;
        double bestDistance = double.MaxValue;

        foreach (var candidate in candidates)
        {
            using var knownEncoding = FaceRecognition.LoadFaceEncoding(candidate.Encoding);
            var distance = FaceRecognition.FaceDistance(knownEncoding, probeEncoding);

            if (distance < bestDistance)
            {
                bestDistance = distance;
                bestCandidate = candidate;
            }
        }

        if (bestCandidate is null)
        {
            return FaceVerificationOutcome.NoCandidates(
                "Nenhum cadastro ativo com biometria disponivel para comparacao.",
                faceCount);
        }

        var matched = bestDistance <= RecognitionTolerance;
        var roundedDistance = Math.Round(bestDistance, 4, MidpointRounding.AwayFromZero);
        var confidence = BuildConfidence(bestDistance, matched);

        return matched
            ? FaceVerificationOutcome.Success(
                bestCandidate.ResidentId,
                bestCandidate.Name,
                bestCandidate.Purpose,
                roundedDistance,
                confidence,
                faceCount,
                $"Match biometrico confirmado dentro do limiar {RecognitionTolerance:0.00}.")
            : FaceVerificationOutcome.Success(
                null,
                null,
                null,
                roundedDistance,
                confidence,
                faceCount,
                $"Nenhum match dentro do limiar biometrico. Melhor distancia encontrada: {roundedDistance:0.0000}.");
    }

    private async Task EnsureModelFileAsync(FaceModelFile model, CancellationToken cancellationToken)
    {
        var targetPath = Path.Combine(ModelsDirectory, model.FileName);

        if (File.Exists(targetPath))
        {
            return;
        }

        var temporaryPath = $"{targetPath}.download";
        StatusMessage = $"Baixando modelo {model.FileName}...";

        try
        {
            using var client = httpClientFactory.CreateClient();
            client.DefaultRequestHeaders.UserAgent.ParseAdd("OverTimeFaceAccess/1.0");

            using var response = await client.GetAsync(
                model.DownloadUrl,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);

            response.EnsureSuccessStatusCode();

            await using var responseStream = await response.Content.ReadAsStreamAsync(cancellationToken);
            await using var fileStream = File.Open(temporaryPath, FileMode.Create, FileAccess.Write, FileShare.None);
            await responseStream.CopyToAsync(fileStream, cancellationToken);
            await fileStream.FlushAsync(cancellationToken);
        }
        catch
        {
            TryDeleteFile(temporaryPath);
            throw;
        }

        if (File.Exists(targetPath))
        {
            File.Delete(targetPath);
        }

        File.Move(temporaryPath, targetPath);
    }

    private FaceEncodingExtractionResult ExtractEncodingCore(string imagePath)
    {
        using var image = FaceRecognition.LoadImageFile(imagePath);
        var locations = faceRecognition!.FaceLocations(image, 1, Model.Hog).ToArray();

        if (locations.Length == 0)
        {
            return FaceEncodingExtractionResult.NoFace("Nenhum rosto foi detectado na imagem enviada.");
        }

        if (locations.Length > 1)
        {
            return FaceEncodingExtractionResult.MultipleFaces(
                $"Foram detectados {locations.Length} rostos. Envie uma imagem com apenas um rosto.",
                locations.Length);
        }

        var encodings = faceRecognition
            .FaceEncodings(image, locations, 1, PredictorModel.Small, Model.Hog)
            .ToArray();

        try
        {
            if (encodings.Length == 0)
            {
                return FaceEncodingExtractionResult.NoFace("Nao foi possivel gerar o encoding facial da imagem.");
            }

            return FaceEncodingExtractionResult.Success(encodings[0].GetRawEncoding(), locations.Length);
        }
        catch (Exception exception)
        {
            return FaceEncodingExtractionResult.Failed($"Falha ao gerar o encoding facial: {exception.Message}", locations.Length);
        }
        finally
        {
            foreach (var encoding in encodings)
            {
                encoding.Dispose();
            }
        }
    }

    private static void TryDeleteFile(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return;
        }

        try
        {
            File.Delete(path);
        }
        catch
        {
        }
    }

    private sealed record FaceModelFile(string FileName, string DownloadUrl);
}

enum FaceEncodingExtractionStatus
{
    Success,
    ServiceUnavailable,
    NoFace,
    MultipleFaces,
    Failed,
}

sealed record FaceEncodingExtractionResult(
    FaceEncodingExtractionStatus Status,
    double[]? Encoding,
    int FaceCount,
    string Message)
{
    public static FaceEncodingExtractionResult Success(double[] encoding, int faceCount) =>
        new(FaceEncodingExtractionStatus.Success, encoding, faceCount, "Encoding facial gerado com sucesso.");

    public static FaceEncodingExtractionResult ServiceUnavailable(string message) =>
        new(FaceEncodingExtractionStatus.ServiceUnavailable, null, 0, message);

    public static FaceEncodingExtractionResult NoFace(string message, int faceCount = 0) =>
        new(FaceEncodingExtractionStatus.NoFace, null, faceCount, message);

    public static FaceEncodingExtractionResult MultipleFaces(string message, int faceCount) =>
        new(FaceEncodingExtractionStatus.MultipleFaces, null, faceCount, message);

    public static FaceEncodingExtractionResult Failed(string message, int faceCount = 0) =>
        new(FaceEncodingExtractionStatus.Failed, null, faceCount, message);
}

enum FaceVerificationStatus
{
    Success,
    ServiceUnavailable,
    NoCandidates,
    NoFace,
    MultipleFaces,
    Failed,
}

sealed record FaceVerificationOutcome(
    FaceVerificationStatus Status,
    bool MatchFound,
    Guid? ResidentId,
    string? Name,
    string? Purpose,
    double? Distance,
    int Confidence,
    int FacesDetected,
    string Message)
{
    public static FaceVerificationOutcome Success(
        Guid? residentId,
        string? name,
        string? purpose,
        double? distance,
        int confidence,
        int facesDetected,
        string message) =>
        new(FaceVerificationStatus.Success, residentId is not null, residentId, name, purpose, distance, confidence, facesDetected, message);

    public static FaceVerificationOutcome ServiceUnavailable(string message, int facesDetected) =>
        new(FaceVerificationStatus.ServiceUnavailable, false, null, null, null, null, 0, facesDetected, message);

    public static FaceVerificationOutcome NoCandidates(string message, int facesDetected) =>
        new(FaceVerificationStatus.NoCandidates, false, null, null, null, null, 0, facesDetected, message);

    public static FaceVerificationOutcome NoFace(string message, int facesDetected) =>
        new(FaceVerificationStatus.NoFace, false, null, null, null, null, 0, facesDetected, message);

    public static FaceVerificationOutcome MultipleFaces(string message, int facesDetected) =>
        new(FaceVerificationStatus.MultipleFaces, false, null, null, null, null, 0, facesDetected, message);

    public static FaceVerificationOutcome Failed(string message, int facesDetected) =>
        new(FaceVerificationStatus.Failed, false, null, null, null, null, 0, facesDetected, message);
}

sealed record FaceVerificationCandidate(Guid ResidentId, string Name, string Purpose, double[] Encoding);
