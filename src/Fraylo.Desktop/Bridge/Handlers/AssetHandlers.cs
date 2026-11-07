using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Fraylo.Application.Assets;
using Fraylo.Application.Projects;
using Fraylo.Application.Scenes;
using Fraylo.Contracts.Assets;
using Fraylo.Contracts.Bridge;

namespace Fraylo.Desktop.Bridge.Handlers;

/// <summary>
/// Bridge Handlers tiếp nhận và điều phối các lệnh quản lý tài nguyên media (asset.*)
/// theo contracts.md v1.0.4 và implementation/T09b-media-assets.md.
/// </summary>
public sealed class AssetHandlers
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly AssetService _assetService;
    private readonly Func<string?>? _audioPicker;
    private readonly Func<string?>? _imagePicker;
    private readonly Func<string?>? _videoPicker;
    private readonly bool _allowExplicitPath;

    public AssetHandlers(
        AssetService assetService,
        Func<string?>? audioPicker = null,
        Func<string?>? imagePicker = null,
        bool allowExplicitPath = false,
        Func<string?>? videoPicker = null)
    {
        _assetService = assetService ?? throw new ArgumentNullException(nameof(assetService));
        _audioPicker = audioPicker;
        _imagePicker = imagePicker;
        _allowExplicitPath = allowExplicitPath;
        _videoPicker = videoPicker;
    }

    /// <summary>
    /// Đăng ký toàn bộ danh mục lệnh asset.* vào BridgeDispatcher và mở Allow-list.
    /// </summary>
    public void Register(IBridgeDispatcher dispatcher)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);

        dispatcher.AllowCommand("asset.import_audio");
        dispatcher.AllowCommand("asset.select_reference_image");
        dispatcher.AllowCommand("asset.get_metadata");
        dispatcher.AllowCommand("asset.open_folder");
        dispatcher.AllowCommand("asset.list");
        dispatcher.AllowCommand("asset.get_preview");
        dispatcher.AllowCommand("asset.upload");

        dispatcher.RegisterHandler("asset.import_audio", HandleImportAudioAsync);
        dispatcher.RegisterHandler("asset.select_reference_image", HandleSelectReferenceImageAsync);
        dispatcher.RegisterHandler("asset.get_metadata", HandleGetMetadataAsync);
        dispatcher.RegisterHandler("asset.open_folder", HandleOpenFolderAsync);
        dispatcher.RegisterHandler("asset.list", HandleListAssetsAsync);
        dispatcher.RegisterHandler("asset.get_preview", HandleGetAssetPreviewAsync);
        dispatcher.RegisterHandler("asset.upload", HandleUploadAssetAsync);
    }

    /// <summary>
    /// Hủy đăng ký danh mục lệnh asset.* khỏi BridgeDispatcher.
    /// </summary>
    public void Unregister(IBridgeDispatcher dispatcher)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);

        dispatcher.UnregisterHandler("asset.import_audio");
        dispatcher.UnregisterHandler("asset.select_reference_image");
        dispatcher.UnregisterHandler("asset.get_metadata");
        dispatcher.UnregisterHandler("asset.open_folder");
        dispatcher.UnregisterHandler("asset.list");
        dispatcher.UnregisterHandler("asset.get_preview");
        dispatcher.UnregisterHandler("asset.upload");
    }

    public async Task<ResponseEnvelope> HandleImportAudioAsync(RequestEnvelope request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        try
        {
            var payload = ParsePayload<ImportAudioRequest>(request);
            if (payload is null ||
                string.IsNullOrWhiteSpace(payload.ProjectId) ||
                string.IsNullOrWhiteSpace(payload.SceneId))
            {
                return ResponseEnvelopeHelper.CreateError(
                    request.Id,
                    "INVALID_ARGUMENT",
                    "Payload thiếu các trường bắt buộc (projectId, sceneId).",
                    retryable: false);
            }

            string? sourceFilePath = null;
            if (_allowExplicitPath && !string.IsNullOrWhiteSpace(payload.SourceFilePath))
            {
                sourceFilePath = payload.SourceFilePath;
            }
            else
            {
                sourceFilePath = _audioPicker != null ? _audioPicker() : ShowAudioOpenFileDialog();
            }

            if (string.IsNullOrWhiteSpace(sourceFilePath))
            {
                // Người dùng hủy hộp thoại chọn file -> không thay đổi Scene
                return ResponseEnvelopeHelper.CreateSuccess(request.Id, new { asset = (AssetDto?)null, canceled = true });
            }

            var importRequest = new ImportAudioRequest(
                ProjectId: payload.ProjectId,
                SceneId: payload.SceneId,
                SourceFilePath: sourceFilePath
            );

            var result = await _assetService.ImportAudioAsync(importRequest, cancellationToken);
            return ResponseEnvelopeHelper.CreateSuccess(request.Id, result);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return MapExceptionToError(request.Id, ex);
        }
    }

    public async Task<ResponseEnvelope> HandleSelectReferenceImageAsync(RequestEnvelope request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        try
        {
            var payload = ParsePayload<SelectReferenceImageRequest>(request);
            if (payload is null ||
                string.IsNullOrWhiteSpace(payload.ProjectId) ||
                string.IsNullOrWhiteSpace(payload.SceneId))
            {
                return ResponseEnvelopeHelper.CreateError(
                    request.Id,
                    "INVALID_ARGUMENT",
                    "Payload thiếu các trường bắt buộc (projectId, sceneId).",
                    retryable: false);
            }

            string? sourceFilePath = null;
            if (_allowExplicitPath && !string.IsNullOrWhiteSpace(payload.SourceFilePath))
            {
                sourceFilePath = payload.SourceFilePath;
            }
            else
            {
                sourceFilePath = _imagePicker != null ? _imagePicker() : ShowImageOpenFileDialog();
            }

            if (string.IsNullOrWhiteSpace(sourceFilePath))
            {
                // Người dùng hủy hộp thoại chọn file -> không thay đổi Scene
                return ResponseEnvelopeHelper.CreateSuccess(request.Id, new { asset = (AssetDto?)null, canceled = true });
            }

            var imageRequest = new SelectReferenceImageRequest(
                ProjectId: payload.ProjectId,
                SceneId: payload.SceneId,
                SourceFilePath: sourceFilePath
            );

            var result = await _assetService.SelectReferenceImageAsync(imageRequest, cancellationToken);
            return ResponseEnvelopeHelper.CreateSuccess(request.Id, result);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return MapExceptionToError(request.Id, ex);
        }
    }

    public async Task<ResponseEnvelope> HandleGetMetadataAsync(RequestEnvelope request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        try
        {
            var payload = ParsePayload<GetAssetMetadataRequest>(request);
            if (payload is null ||
                string.IsNullOrWhiteSpace(payload.AssetId))
            {
                return ResponseEnvelopeHelper.CreateError(
                    request.Id,
                    "INVALID_ARGUMENT",
                    "Payload thiếu trường 'assetId'.",
                    retryable: false);
            }

            var result = await _assetService.GetMetadataAsync(payload, cancellationToken);
            return ResponseEnvelopeHelper.CreateSuccess(request.Id, result);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return MapExceptionToError(request.Id, ex);
        }
    }

    public async Task<ResponseEnvelope> HandleOpenFolderAsync(RequestEnvelope request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        try
        {
            var payload = ParsePayload<OpenFolderRequest>(request);
            if (payload is null ||
                string.IsNullOrWhiteSpace(payload.AssetId))
            {
                return ResponseEnvelopeHelper.CreateError(
                    request.Id,
                    "INVALID_ARGUMENT",
                    "Payload thiếu trường 'assetId'.",
                    retryable: false);
            }

            var result = await _assetService.OpenFolderAsync(payload, cancellationToken);
            return ResponseEnvelopeHelper.CreateSuccess(request.Id, result);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return MapExceptionToError(request.Id, ex);
        }
    }

    public async Task<ResponseEnvelope> HandleListAssetsAsync(RequestEnvelope request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        try
        {
            var payload = ParsePayload<ListAssetsRequest>(request);
            if (payload is null)
            {
                return ResponseEnvelopeHelper.CreateError(
                    request.Id,
                    "INVALID_ARGUMENT",
                    "Payload không hợp lệ.",
                    retryable: false);
            }

            var result = await _assetService.ListAssetsAsync(payload, cancellationToken);
            return ResponseEnvelopeHelper.CreateSuccess(request.Id, result);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return MapExceptionToError(request.Id, ex);
        }
    }

    public async Task<ResponseEnvelope> HandleGetAssetPreviewAsync(RequestEnvelope request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        try
        {
            var payload = ParsePayload<GetAssetPreviewRequest>(request);
            if (payload is null ||
                string.IsNullOrWhiteSpace(payload.AssetId))
            {
                return ResponseEnvelopeHelper.CreateError(
                    request.Id,
                    "INVALID_ARGUMENT",
                    "Payload thiếu trường 'assetId'.",
                    retryable: false);
            }

            var result = await _assetService.GetAssetPreviewAsync(payload, cancellationToken);
            return ResponseEnvelopeHelper.CreateSuccess(request.Id, result);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return MapExceptionToError(request.Id, ex);
        }
    }

    public async Task<ResponseEnvelope> HandleUploadAssetAsync(RequestEnvelope request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        try
        {
            var payload = ParsePayload<UploadAssetRequest>(request);
            if (payload is null)
            {
                return ResponseEnvelopeHelper.CreateError(
                    request.Id,
                    "INVALID_ARGUMENT",
                    "Payload không hợp lệ.",
                    retryable: false);
            }

            var assetType = string.IsNullOrWhiteSpace(payload.AssetType) ? "Image" : payload.AssetType;
            bool isImageUpload = string.Equals(assetType, "Image", StringComparison.OrdinalIgnoreCase);
            bool isVideoUpload = string.Equals(assetType, "Video", StringComparison.OrdinalIgnoreCase);
            if (!isImageUpload && !isVideoUpload && !string.Equals(assetType, "Audio", StringComparison.OrdinalIgnoreCase))
                return ResponseEnvelopeHelper.CreateError(request.Id, "INVALID_ARGUMENT", "Loại tài nguyên không hợp lệ.", retryable: false);
            if (isVideoUpload && (payload.Multiple || !string.IsNullOrWhiteSpace(payload.Base64Data)))
                return ResponseEnvelopeHelper.CreateError(request.Id, "INVALID_ARGUMENT", "Hãy chọn một video MP4 qua hộp thoại.", retryable: false);
            bool pickFromDialog = !(_allowExplicitPath && !string.IsNullOrWhiteSpace(payload.SourceFilePath))
                && string.IsNullOrWhiteSpace(payload.Base64Data);
            if (payload.Multiple && isImageUpload && pickFromDialog)
            {
                return await UploadManyImagesAsync(request.Id, payload, cancellationToken);
            }

            string? sourceFilePath = null;
            if (_allowExplicitPath && !string.IsNullOrWhiteSpace(payload.SourceFilePath))
            {
                sourceFilePath = payload.SourceFilePath;
            }
            else if (!string.IsNullOrWhiteSpace(payload.Base64Data))
            {
                sourceFilePath = null;
            }
            else
            {
                bool isAudio = string.Equals(payload.AssetType, "Audio", StringComparison.OrdinalIgnoreCase);
                if (isAudio)
                {
                    sourceFilePath = _audioPicker != null ? _audioPicker() : ShowAudioOpenFileDialog();
                }
                else if (isVideoUpload)
                {
                    sourceFilePath = _videoPicker != null ? _videoPicker() : ShowVideoOpenFileDialog();
                }
                else
                {
                    sourceFilePath = _imagePicker != null ? _imagePicker() : ShowImageOpenFileDialog();
                }

                if (string.IsNullOrWhiteSpace(sourceFilePath))
                {
                    return ResponseEnvelopeHelper.CreateSuccess(request.Id, new UploadAssetResponse(null, Canceled: true));
                }
            }

            var uploadRequest = new UploadAssetRequest(
                ProjectId: payload.ProjectId,
                AssetType: string.IsNullOrWhiteSpace(payload.AssetType) ? "Image" : payload.AssetType,
                FileName: payload.FileName,
                Base64Data: payload.Base64Data,
                SourceFilePath: sourceFilePath
            );

            var result = await _assetService.UploadAssetAsync(uploadRequest, cancellationToken);
            return ResponseEnvelopeHelper.CreateSuccess(request.Id, result);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return MapExceptionToError(request.Id, ex);
        }
    }

    private async Task<ResponseEnvelope> UploadManyImagesAsync(Guid requestId, UploadAssetRequest payload, CancellationToken cancellationToken)
    {
        var paths = _imagePicker != null
            ? (_imagePicker() is { } picked ? new[] { picked } : Array.Empty<string>())
            : ShowImageOpenFileDialogs(multiselect: true);
        if (paths.Length == 0)
        {
            return ResponseEnvelopeHelper.CreateSuccess(requestId, new UploadAssetResponse(null, Canceled: true));
        }

        var assets = new List<AssetDto>();
        var errors = new List<string>();
        foreach (var path in paths)
        {
            try
            {
                var result = await _assetService.UploadAssetAsync(
                    new UploadAssetRequest(payload.ProjectId, "Image", SourceFilePath: path), cancellationToken);
                if (result.Asset is not null) assets.Add(result.Asset);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                errors.Add($"{Path.GetFileName(path)}: {ex.Message}");
            }
        }

        return ResponseEnvelopeHelper.CreateSuccess(requestId, new UploadAssetResponse(assets.FirstOrDefault(), false, assets, errors));
    }

    private static string? ShowAudioOpenFileDialog()
    {
        string? selected = null;
        if (OperatingSystem.IsWindows())
        {
            var thread = new Thread(() =>
            {
                try
                {
                    var dialogType = Type.GetType("Microsoft.Win32.OpenFileDialog, PresentationFramework");
                    if (dialogType != null)
                    {
                        dynamic dialog = Activator.CreateInstance(dialogType)!;
                        dialog.Title = "Chọn tệp âm thanh";
                        dialog.Filter = "Tệp âm thanh (*.mp3;*.wav)|*.mp3;*.wav|Tất cả tệp (*.*)|*.*";
                        dialog.Multiselect = false;
                        dialog.CheckFileExists = true;
                        bool? res = dialog.ShowDialog();
                        if (res == true)
                        {
                            selected = (string)dialog.FileName;
                        }
                    }
                }
                catch
                {
                    selected = null;
                }
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            thread.Join(TimeSpan.FromSeconds(60));
        }
        return selected;
    }

    private static string? ShowImageOpenFileDialog() => ShowImageOpenFileDialogs(multiselect: false).FirstOrDefault();

    private static string[] ShowImageOpenFileDialogs(bool multiselect) => ShowMediaOpenFileDialogs(multiselect,
        multiselect ? "Chọn một hoặc nhiều ảnh" : "Chọn tệp hình ảnh tham chiếu",
        "Tệp hình ảnh (*.png;*.jpg;*.jpeg;*.webp)|*.png;*.jpg;*.jpeg;*.webp|Tất cả tệp (*.*)|*.*");

    private static string? ShowVideoOpenFileDialog() => ShowMediaOpenFileDialogs(false,
        "Chọn video mẫu chuyển động", "Video MP4 (*.mp4)|*.mp4").FirstOrDefault();

    private static string[] ShowMediaOpenFileDialogs(bool multiselect, string title, string filter)
    {
        string[] selected = [];
        if (OperatingSystem.IsWindows())
        {
            var thread = new Thread(() =>
            {
                try
                {
                    var dialogType = Type.GetType("Microsoft.Win32.OpenFileDialog, PresentationFramework");
                    if (dialogType != null)
                    {
                        dynamic dialog = Activator.CreateInstance(dialogType)!;
                        dialog.Title = title;
                        dialog.Filter = filter;
                        dialog.Multiselect = multiselect;
                        dialog.CheckFileExists = true;
                        bool? res = dialog.ShowDialog();
                        if (res == true)
                        {
                            selected = (string[])dialog.FileNames;
                        }
                    }
                }
                catch
                {
                    selected = [];
                }
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            thread.Join(TimeSpan.FromSeconds(60));
        }
        return selected;
    }

    private static T? ParsePayload<T>(RequestEnvelope request) where T : class
    {
        var element = request.Payload;
        if (element.ValueKind == JsonValueKind.Null || element.ValueKind == JsonValueKind.Undefined)
        {
            return null;
        }

        return JsonSerializer.Deserialize<T>(element.GetRawText(), JsonOptions);
    }

    private static ResponseEnvelope MapExceptionToError(Guid requestId, Exception ex)
    {
        return ex switch
        {
            AssetValidationException validation =>
                ResponseEnvelopeHelper.CreateError(requestId, "VALIDATION_FAILED", validation.Message, retryable: false),

            AssetNotFoundException notFound =>
                ResponseEnvelopeHelper.CreateError(requestId, "ENTITY_NOT_FOUND", notFound.Message, retryable: false),

            SceneNotFoundException sceneNotFound =>
                ResponseEnvelopeHelper.CreateError(requestId, "ENTITY_NOT_FOUND", sceneNotFound.Message, retryable: false),

            ProjectNotFoundException prjNotFound =>
                ResponseEnvelopeHelper.CreateError(requestId, "ENTITY_NOT_FOUND", prjNotFound.Message, retryable: false),

            ArgumentException arg =>
                ResponseEnvelopeHelper.CreateError(requestId, "INVALID_ARGUMENT", arg.Message, retryable: false),

            _ =>
                ResponseEnvelopeHelper.CreateError(requestId, "INTERNAL_ERROR", $"Lỗi xử lý tài nguyên: {ex.Message}", retryable: false)
        };
    }
}
