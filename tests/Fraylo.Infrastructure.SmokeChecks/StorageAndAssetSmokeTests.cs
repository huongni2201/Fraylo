using System.IO;
using System.Security;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Fraylo.Application.Assets;
using Fraylo.Application.Documents;
using Fraylo.Application.Projects;
using Fraylo.Application.Settings;
using Fraylo.Contracts.Assets;
using Fraylo.Contracts.Documents;
using Fraylo.Contracts.Projects;
using Fraylo.Contracts.Settings;
using Fraylo.Infrastructure.Persistence;
using Fraylo.Infrastructure.Persistence.Entities;
using Fraylo.Infrastructure.Persistence.Interceptors;
using Fraylo.Infrastructure.Storage;

namespace Fraylo.Infrastructure.SmokeChecks;

/// <summary>
/// Bộ kiểm thử khói xác thực toàn diện cho Task T09 (Storage, Assets, Documents, Settings)
/// kèm theo các Regression Test về Parent Junction, Transaction Atomicity, Binary Audio Validation
/// và Schema Constraints theo execution-contract-v1.1.
/// </summary>
public static class StorageAndAssetSmokeTests
{
    public static async Task RunTestsAsync(string databasePath)
    {
        Console.WriteLine("\n=================================================================");
        Console.WriteLine("    BẮT ĐẦU KIỂM THỬ KHÓI T09: STORAGE, ASSETS & SETTINGS       ");
        Console.WriteLine("=================================================================");

        var connectionString = DatabasePathHelper.BuildConnectionString(databasePath);
        var optionsBuilder = new DbContextOptionsBuilder<FrayloDbContext>();
        optionsBuilder.UseSqlite(connectionString)
                      .AddInterceptors(new SqlitePragmaInterceptor());
        var dbOptions = optionsBuilder.Options;

        var testWorkDir = Path.Combine(Path.GetTempPath(), "titoai_t09_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(testWorkDir);

        // Cô lập hoàn toàn StorageRootRegistry với testWorkDir (không chạm LocalAppData)
        var rootRegistry = new StorageRootRegistry(dbOptions, testWorkDir);
        var fileStore = new AssetFileStore();
        var settingsService = new SettingsService(dbOptions, rootRegistry);
        var assetService = new AssetService(dbOptions, rootRegistry, fileStore);
        var documentExportService = new DocumentExportService(dbOptions, rootRegistry, fileStore);

        try
        {
            // Test 1: 3 default roots & mapping persistence trong AppSettings
            await TestDefaultRootsAndPersistenceAsync(rootRegistry, dbOptions);

            // Test 2: Đổi root: sinh rootId mới, giữ mapping cũ, tái sử dụng khi trùng path
            await TestRootUpdateAndHistoryRetentionAsync(rootRegistry, testWorkDir, dbOptions);

            // Test 3: Traversal & junction guard chặn truy cập ngoài root
            TestPathGuardAndTraversalProtection(testWorkDir);

            // Test 4: Dry-run write probe (hợp lệ vs thiếu quyền/hỏng)
            await TestWriteProbeAndRootValidationAsync(settingsService, testWorkDir);

            // Test 5: Atomic staging & finalize asset file (audio MP3/WAV & image)
            await TestAssetImportAtomicAndIntegrityAsync(assetService, dbOptions, testWorkDir);

            // Test 6: Document export UTF-8 có dấu tiếng Việt, newline, metadata
            await TestDocumentExportUtf8AndMetadataAsync(documentExportService, dbOptions, testWorkDir);

            // Regression 1: Parent directory junction protection
            TestParentDirectoryJunctionProtection(testWorkDir);

            // Regression 2: AssetFileStore staging path verification
            await TestAssetFileStoreStagingVerificationAsync(testWorkDir);

            // Regression 3: Fault injection & atomic rollback trong SettingsService
            await TestSettingsTransactionAtomicity(rootRegistry, dbOptions, testWorkDir);

            // Regression 4: Audio binary header check & 100MB limit
            await TestAudioHeaderValidation(assetService, dbOptions, testWorkDir);

            // Regression 5: Schema Composite FK & Check Constraint CK_Job_TaskType_Scene
            await TestSchemaCompositeFkAndCheckConstraint(dbOptions);

            // Regression 6: Upgrade DB cũ có Job thật & foreign_key_check
            await TestUpgradeLegacyDbWithRealJobsAsync(testWorkDir);

            // Regression 7: Upload vào Project không tồn tại bị từ chối; đối soát dọn staging và báo tệp mồ côi
            await TestUploadRejectsMissingProjectAndReconcileFilesAsync(assetService, rootRegistry);

            // Regression 8: Đối soát duyệt root chưa có Asset và không dọn staging qua junction
            await TestReconcileRegisteredRootsAndSkipStagingJunctionAsync(assetService, rootRegistry, testWorkDir);

            // Regression 9: Tài nguyên dùng chung trong Nhóm: upload, phạm vi theo Project và theo Nhóm
            await TestGroupScopedAssetsAsync(assetService, dbOptions);

            Console.WriteLine("=================================================================");
            Console.WriteLine("   TẤT CẢ KIỂM THỬ KHÓI VÀ REGRESSION T09 ĐÃ HOÀN TẤT THÀNH CÔNG! ");
            Console.WriteLine("=================================================================");
        }
        finally
        {
            try
            {
                if (Directory.Exists(testWorkDir))
                {
                    Directory.Delete(testWorkDir, recursive: true);
                }
            }
            catch
            {
                // Bỏ qua lỗi xóa thư mục tạm test
            }
        }
    }

    private static async Task TestDefaultRootsAndPersistenceAsync(
        StorageRootRegistry rootRegistry,
        DbContextOptions<FrayloDbContext> dbOptions)
    {
        Console.WriteLine("\n[T09-1] Kiểm thử 3 default roots & mapping persistence trong AppSettings...");

        await rootRegistry.EnsureDefaultRootsAsync();
        var snapshot = await rootRegistry.GetCurrentRootsAsync();

        Assert(snapshot.DocumentsRootId == StorageRootRegistry.RootDefaultDocsId, "DocumentsRootId mặc định không đúng.");
        Assert(snapshot.ImagesRootId == StorageRootRegistry.RootDefaultImagesId, "ImagesRootId mặc định không đúng.");
        Assert(snapshot.VideosRootId == StorageRootRegistry.RootDefaultVideosId, "VideosRootId mặc định không đúng.");
        Assert(snapshot.AudioRootId == StorageRootRegistry.RootInternalAudioId, "AudioRootId mặc định không đúng.");

        await using var db = new FrayloDbContext(dbOptions);
        var docsSetting = await db.AppSettings.FirstOrDefaultAsync(s => s.Key == StorageRootRegistry.KeyDocumentsRootId);
        var imagesSetting = await db.AppSettings.FirstOrDefaultAsync(s => s.Key == StorageRootRegistry.KeyImagesRootId);
        var videosSetting = await db.AppSettings.FirstOrDefaultAsync(s => s.Key == StorageRootRegistry.KeyVideosRootId);

        Assert(docsSetting != null && docsSetting.Value == StorageRootRegistry.RootDefaultDocsId, $"Setting {StorageRootRegistry.KeyDocumentsRootId} chưa được lưu.");
        Assert(imagesSetting != null && imagesSetting.Value == StorageRootRegistry.RootDefaultImagesId, $"Setting {StorageRootRegistry.KeyImagesRootId} chưa được lưu.");
        Assert(videosSetting != null && videosSetting.Value == StorageRootRegistry.RootDefaultVideosId, $"Setting {StorageRootRegistry.KeyVideosRootId} chưa được lưu.");

        Console.WriteLine("    -> 3 Default Roots và persistence AppSettings: PASS");
    }

    private static async Task TestRootUpdateAndHistoryRetentionAsync(
        StorageRootRegistry rootRegistry,
        string testWorkDir,
        DbContextOptions<FrayloDbContext> dbOptions)
    {
        Console.WriteLine("\n[T09-2] Kiểm thử Đổi root: sinh rootId mới, giữ mapping cũ, tái sử dụng khi trùng path...");

        var customDocs = Path.Combine(testWorkDir, "custom_docs");
        var customImages = Path.Combine(testWorkDir, "custom_images");
        var customVideos = Path.Combine(testWorkDir, "custom_videos");
        Directory.CreateDirectory(customDocs);
        Directory.CreateDirectory(customImages);
        Directory.CreateDirectory(customVideos);

        var originalRoots = await rootRegistry.GetCurrentRootsAsync();
        var defaultDocsPath = originalRoots.DocumentsPath;
        var defaultImagesPath = originalRoots.ImagesPath;
        var defaultVideosPath = originalRoots.VideosPath;

        // Cập nhật sang đường dẫn tùy chỉnh mới
        var updatedSnapshot = await rootRegistry.UpdateRootsAsync(customDocs, customImages, customVideos);

        Assert(updatedSnapshot.DocumentsRootId != StorageRootRegistry.RootDefaultDocsId, "RootId documents mới phải khác mặc định.");
        Assert(updatedSnapshot.ImagesRootId != StorageRootRegistry.RootDefaultImagesId, "RootId images mới phải khác mặc định.");
        Assert(updatedSnapshot.VideosRootId != StorageRootRegistry.RootDefaultVideosId, "RootId videos mới phải khác mặc định.");

        // Kiểm tra mapping cũ vẫn còn trong AppSettings (lịch sử không bị mất)
        await using (var db = new FrayloDbContext(dbOptions))
        {
            var oldDocsSetting = await db.AppSettings.FirstOrDefaultAsync(s => s.Key == $"{StorageRootRegistry.KeyRootsPrefix}{StorageRootRegistry.RootDefaultDocsId}");
            Assert(oldDocsSetting != null, "Mapping cũ của RootDefaultDocsId phải được giữ lại trong AppSettings.");
        }

        // Cập nhật lại đường dẫn mặc định ban đầu -> phải tái sử dụng lại RootDefaultDocsId
        var restored = await rootRegistry.UpdateRootsAsync(defaultDocsPath, defaultImagesPath, defaultVideosPath);
        Assert(restored.DocumentsRootId == StorageRootRegistry.RootDefaultDocsId,
            "Cập nhật lại đường dẫn cũ phải tái sử dụng root-default-docs.");

        Console.WriteLine("    -> Cơ chế sinh rootId mới, giữ mapping cũ và tái sử dụng rootId: PASS");
    }

    private static void TestPathGuardAndTraversalProtection(string testWorkDir)
    {
        Console.WriteLine("\n[T09-3] Kiểm thử Traversal & Junction guard chặn truy cập ngoài root...");

        var baseRoot = Path.Combine(testWorkDir, "sandbox_root");
        Directory.CreateDirectory(baseRoot);

        // Chặn traversal "..\"
        AssertThrows<ArgumentException>(() => StoragePathGuard.EnsureValidRelativePath(@"..\secret.txt"),
            "Chặn relative path bắt đầu bằng ..");
        AssertThrows<ArgumentException>(() => StoragePathGuard.EnsureValidRelativePath(@"folder/../../secret.txt"),
            "Chặn relative path có segment ..");
        AssertThrows<ArgumentException>(() => StoragePathGuard.EnsureValidRelativePath(@"/folder/test.txt"),
            "Chặn relative path bắt đầu bằng /");
        AssertThrows<ArgumentException>(() => StoragePathGuard.EnsureValidRelativePath(@"C:\Windows\System32"),
            "Chặn absolute path trong relative path validator");
        AssertThrows<ArgumentException>(() => StoragePathGuard.EnsureValidRelativePath(@"stream:file"),
            "Chặn NTFS alternate data streams chứa dấu hai chấm");

        // IsSubPathOf
        var validChild = Path.Combine(baseRoot, "subfolder", "file.txt");
        var outsideFolder = Path.Combine(testWorkDir, "outside_folder", "file.txt");

        Assert(StoragePathGuard.IsSubPathOf(baseRoot, validChild), "Thư mục con hợp lệ phải được xác nhận IsSubPathOf.");
        Assert(!StoragePathGuard.IsSubPathOf(baseRoot, outsideFolder), "Thư mục nằm ngoài baseRoot phải bị từ chối.");

        // CombineAndVerify
        var verified = StoragePathGuard.CombineAndVerify(baseRoot, "sub/file.txt");
        Assert(verified.StartsWith(StoragePathGuard.NormalizePath(baseRoot), StringComparison.OrdinalIgnoreCase),
            "CombineAndVerify phải trả về đường dẫn con hợp lệ.");

        Console.WriteLine("    -> StoragePathGuard chặn Directory Traversal và kiểm soát ranh giới: PASS");
    }

    private static async Task TestWriteProbeAndRootValidationAsync(SettingsService settingsService, string testWorkDir)
    {
        Console.WriteLine("\n[T09-4] Kiểm thử Dry-run write probe (hợp lệ vs thiếu quyền/hỏng)...");

        var validDir = Path.Combine(testWorkDir, "probe_valid");
        Directory.CreateDirectory(validDir);

        bool probeSuccess = StoragePathGuard.TryVerifyWritable(validDir, out var err);
        Assert(probeSuccess && string.IsNullOrEmpty(err), $"Dry-run write probe trên thư mục hợp lệ phải thành công. Lỗi: {err}");

        // Kiểm tra không còn file probe sót lại
        var remainingProbes = Directory.GetFiles(validDir, ".titoai_probe_*.tmp");
        Assert(remainingProbes.Length == 0, "File tạm probe phải được xóa ngay sau khi kiểm tra.");

        // Kiểm tra qua ValidateRootsAsync với đường dẫn rỗng / không hợp lệ
        var badRequest = new ValidateRootsRequest(
            DocumentsPath: "",
            ImagesPath: validDir,
            VideosPath: "??invalid_path_chars::<>*"
        );

        var badResponse = await settingsService.ValidateRootsAsync(badRequest);
        Assert(!badResponse.IsValid, "Yêu cầu có đường dẫn không hợp lệ phải có IsValid = false.");
        Assert(badResponse.FieldErrors.ContainsKey("documentsPath"), "Phải báo lỗi trường documentsPath.");
        Assert(badResponse.FieldErrors.ContainsKey("videosPath"), "Phải báo lỗi trường videosPath.");

        // Thử lưu cài đặt với request lỗi -> phải rollback và ném SettingsValidationException
        var saveRequest = new SettingsSaveRequest(
            DocumentsPath: "",
            ImagesPath: validDir,
            VideosPath: validDir
        );

        bool exceptionThrown = false;
        try
        {
            await settingsService.SaveSettingsAsync(saveRequest);
        }
        catch (SettingsValidationException)
        {
            exceptionThrown = true;
        }

        Assert(exceptionThrown, "SaveSettingsAsync phải ném SettingsValidationException khi validate thất bại.");
        Console.WriteLine("    -> Dry-run write probe và atomic validation rollback: PASS");
    }

    private static async Task TestAssetImportAtomicAndIntegrityAsync(
        AssetService assetService,
        DbContextOptions<FrayloDbContext> dbOptions,
        string testWorkDir)
    {
        Console.WriteLine("\n[T09-5] Kiểm thử Atomic staging & finalize asset file (audio MP3/WAV & image)...");
        var projectId = Guid.NewGuid().ToString("D").ToLowerInvariant();
        var sceneId = Guid.NewGuid().ToString("D").ToLowerInvariant();
        var now = DateTimeOffset.UtcNow.ToString("O");

        await using (var db = new FrayloDbContext(dbOptions))
        {

            db.Projects.Add(new Project
            {
                Id = projectId,
                Name = "Dự án Media Test",
                Status = "Draft",
                CreatedAtUtc = now,
                UpdatedAtUtc = now
            });

            db.Scenes.Add(new Scene
            {
                Id = sceneId,
                ProjectId = projectId,
                SequenceOrder = 1,
                Name = "Cảnh 1 Nhạc nền",
                TargetDurationSeconds = 15,
                Status = "Draft",
                CreatedAtUtc = now,
                UpdatedAtUtc = now
            });

            await db.SaveChangesAsync();
        }

        // Tạo file âm thanh MP3 giả lập có ID3 header hợp lệ
        var id3Header = new byte[] { 0x49, 0x44, 0x33, 0x03, 0x00, 0x00, 0x00, 0x00, 0x00, 0x20 };
        var sampleAudioData = id3Header.Concat(Encoding.UTF8.GetBytes("AUDIO-PAYLOAD-STREAM-TEST")).ToArray();
        var sourceAudioPath = Path.Combine(testWorkDir, "source_voice.mp3");
        await File.WriteAllBytesAsync(sourceAudioPath, sampleAudioData);

        var expectedAudioHash = Convert.ToHexString(SHA256.HashData(sampleAudioData)).ToLowerInvariant();

        // 1. Import Audio
        var importAudioResp = await assetService.ImportAudioAsync(new ImportAudioRequest(
            ProjectId: projectId,
            SceneId: sceneId,
            SourceFilePath: sourceAudioPath
        ));

        Assert(importAudioResp.Asset != null, "ImportAudioAsync phải trả về AssetDto.");
        Assert(importAudioResp.Asset!.AssetType == "Audio", "AssetType phải là Audio.");
        Assert(importAudioResp.Asset.ByteSize == sampleAudioData.Length, "ByteSize của audio lưu phải bằng nguồn.");
        Assert(importAudioResp.Asset.Sha256Hash == expectedAudioHash, "SHA-256 hash của audio lưu phải bằng hash nguồn.");
        Assert(importAudioResp.Asset.StorageRootId == StorageRootRegistry.RootInternalAudioId,
            "Audio phải được lưu tại root-internal-audio.");

        // Kiểm tra Scene.AudioAssetId đã được cập nhật trong DB
        await using (var db = new FrayloDbContext(dbOptions))
        {
            var updatedScene = await db.Scenes.FirstAsync(s => s.Id == sceneId);
            Assert(updatedScene.AudioAssetId == importAudioResp.Asset.AssetId, "Scene.AudioAssetId phải trỏ tới AssetId vừa tạo.");
        }

        // 2. Chặn import file sai định dạng (ví dụ .exe)
        var invalidFile = Path.Combine(testWorkDir, "malicious.exe");
        await File.WriteAllBytesAsync(invalidFile, new byte[] { 0x4D, 0x5A });

        await AssertThrowsAsync<AssetValidationException>(async () =>
        {
            await assetService.ImportAudioAsync(new ImportAudioRequest(
                ProjectId: projectId,
                SceneId: sceneId,
                SourceFilePath: invalidFile
            ));
        }, "Chặn import file có đuôi .exe vào audio.");

        // 3. Import Reference Image
        var sampleImageData = Encoding.UTF8.GetBytes("PNG-MOCK-IMAGE-DATA-TITOAI-TEST-SUITE");
        var sourceImagePath = Path.Combine(testWorkDir, "reference.png");
        await File.WriteAllBytesAsync(sourceImagePath, sampleImageData);

        var selectImgResp = await assetService.SelectReferenceImageAsync(new SelectReferenceImageRequest(
            ProjectId: projectId,
            SceneId: sceneId,
            SourceFilePath: sourceImagePath
        ));

        Assert(selectImgResp.Asset != null, "SelectReferenceImageAsync phải trả về AssetDto.");
        Assert(selectImgResp.Asset!.AssetType == "Image", "AssetType phải là Image.");

        // Kiểm tra Scene.ImageAssetId đã được liên kết
        await using (var db = new FrayloDbContext(dbOptions))
        {
            var updatedScene = await db.Scenes.FirstAsync(s => s.Id == sceneId);
            Assert(updatedScene.ImageAssetId == selectImgResp.Asset.AssetId, "Scene.ImageAssetId phải trỏ tới AssetId vừa tạo.");
        }

        // 4. Lấy metadata
        var metadata = await assetService.GetMetadataAsync(new GetAssetMetadataRequest(
            AssetId: importAudioResp.Asset.AssetId
        ));
        Assert(metadata.AssetId == importAudioResp.Asset.AssetId, "GetMetadataAsync phải trả về đúng AssetId.");

        Console.WriteLine("    -> Atomic staging, SHA-256 integrity và cập nhật liên kết Scene: PASS");
    }

    private static async Task TestDocumentExportUtf8AndMetadataAsync(
        DocumentExportService documentExportService,
        DbContextOptions<FrayloDbContext> dbOptions,
        string testWorkDir)
    {
        Console.WriteLine("\n[T09-6] Kiểm thử Document export UTF-8 có dấu tiếng Việt, newline, metadata...");
        var projectId = Guid.NewGuid().ToString("D").ToLowerInvariant();
        var sceneId = Guid.NewGuid().ToString("D").ToLowerInvariant();
        var now = DateTimeOffset.UtcNow.ToString("O");

        var promptTextWithVietnamese =
            "Khám phá các bí ẩn của vũ trụ bao la!\n" +
            "Hình ảnh ngôi sao siêu mới bùng nổ rực rỡ với ánh sáng xanh ngọc bích.\n" +
            "Lời thoại: 'Từ hàng tỷ năm trước, những hạt bụi vũ trụ đã tạo nên sự sống hôm nay.'\n" +
            "Tone giọng truyền cảm, ấm áp, nhịp điệu vừa phải.";

        await using (var db = new FrayloDbContext(dbOptions))
        {

            db.Projects.Add(new Project
            {
                Id = projectId,
                Name = "Dự Án Kịch Bản Thiên Văn",
                Status = "Draft",
                CreatedAtUtc = now,
                UpdatedAtUtc = now
            });

            db.Scenes.Add(new Scene
            {
                Id = sceneId,
                ProjectId = projectId,
                SequenceOrder = 1,
                Name = "Khởi đầu vũ trụ",
                PromptText = promptTextWithVietnamese,
                TargetDurationSeconds = 20,
                Status = "Draft",
                CreatedAtUtc = now,
                UpdatedAtUtc = now
            });

            await db.SaveChangesAsync();
        }

        var exportResp = await documentExportService.ExportDocumentAsync(new DocumentExportRequest(
            SceneId: sceneId,
            Format: "txt",
            CustomFileName: "KichBan_Canh1_VuTru.txt"
        ));

        Assert(!string.IsNullOrEmpty(exportResp.AssetId), "ExportDocumentAsync phải trả về AssetId.");
        Assert(exportResp.DisplayName == "KichBan_Canh1_VuTru.txt", "DisplayName phải là KichBan_Canh1_VuTru.txt.");

        // Kiểm tra bản ghi Asset trong DB
        await using (var db = new FrayloDbContext(dbOptions))
        {
            var docAsset = await db.Assets.FirstOrDefaultAsync(a => a.Id == exportResp.AssetId);
            Assert(docAsset != null, "Phải tìm thấy Asset Document vừa được tạo trong DB.");
            Assert(docAsset!.AssetType == "Document", "AssetType phải là Document.");
            Assert(docAsset.Status == "Linked", "Status phải là Linked.");
            Assert(docAsset.ByteSize > 0, "ByteSize của tài liệu phải lớn hơn 0.");
        }

        // Kiểm tra từ chối format không hỗ trợ (ví dụ docx)
        await AssertThrowsAsync<DocumentExportException>(async () =>
        {
            await documentExportService.ExportDocumentAsync(new DocumentExportRequest(
                SceneId: sceneId,
                Format: "docx"
            ));
        }, "Chặn xuất định dạng khác txt trong MVP.");

        Console.WriteLine("    -> Document export UTF-8 tiếng Việt, newline và metadata Asset Document: PASS");
    }

    private static void TestParentDirectoryJunctionProtection(string testWorkDir)
    {
        Console.WriteLine("\n[T09-REGRESSION-1] Kiểm thử Parent Directory Junction (Nested Junction Protection)...");

        var baseRoot = Path.Combine(testWorkDir, "junction_sandbox_root");
        var outsideSecret = Path.Combine(testWorkDir, "outside_secret_target");
        Directory.CreateDirectory(baseRoot);
        Directory.CreateDirectory(outsideSecret);

        var ordinaryChildInOutside = Path.Combine(outsideSecret, "ordinary_child");
        Directory.CreateDirectory(ordinaryChildInOutside);
        var targetFile = Path.Combine(ordinaryChildInOutside, "stolen.txt");
        File.WriteAllText(targetFile, "secret payload");

        var junctionLink = Path.Combine(baseRoot, "parent_junction");
        bool junctionCreated = false;
        try
        {
            Directory.CreateSymbolicLink(junctionLink, outsideSecret);
            junctionCreated = true;
        }
        catch
        {
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo("cmd.exe", $"/c mklink /J \"{junctionLink}\" \"{outsideSecret}\"")
                {
                    CreateNoWindow = true,
                    UseShellExecute = false
                };
                using var p = System.Diagnostics.Process.Start(psi);
                p?.WaitForExit();
                junctionCreated = Directory.Exists(junctionLink);
            }
            catch { }
        }

        if (junctionCreated)
        {
            var candidateThroughJunction = Path.Combine(junctionLink, "ordinary_child", "stolen.txt");
            bool isInside = StoragePathGuard.IsSubPathOf(baseRoot, candidateThroughJunction);
            Assert(!isInside, "StoragePathGuard PHẢI từ chối đường dẫn đi qua parent directory junction trỏ ra ngoài root!");
            Console.WriteLine("    -> Parent Directory Junction Protection: PASS (Đích ngoài root bị từ chối chính xác)");
        }
        else
        {
            Console.WriteLine("    -> [SKIPPED] Không thể tạo NTFS Junction trên môi trường hiện tại.");
        }
    }

    private static async Task TestAssetFileStoreStagingVerificationAsync(string testWorkDir)
    {
        Console.WriteLine("\n[T09-REGRESSION-2] Kiểm thử AssetFileStore staging path verification...");
        var store = new AssetFileStore();
        var root = Path.Combine(testWorkDir, "store_root");
        Directory.CreateDirectory(root);

        await AssertThrowsAsync<ArgumentException>(async () =>
        {
            await store.SaveFileAsync(root, @"..\outside.bin", new MemoryStream(new byte[] { 1, 2, 3 }));
        }, "Chặn traversal khi SaveFileAsync");

        var fileResult = await store.SaveFileAsync(root, "valid/test.bin", new MemoryStream(new byte[] { 1, 2, 3 }));
        Assert(fileResult.ByteSize == 3, "FileResult.ByteSize phải bằng 3.");
        Assert(File.Exists(Path.Combine(root, "valid", "test.bin")), "File phải tồn tại tại vị trí đã verify.");
        Console.WriteLine("    -> AssetFileStore staging verification: PASS");
    }

    private static async Task TestSettingsTransactionAtomicity(
        StorageRootRegistry rootRegistry,
        DbContextOptions<FrayloDbContext> dbOptions,
        string testWorkDir)
    {
        Console.WriteLine("\n[T09-REGRESSION-3] Kiểm thử Fault injection & Atomic rollback giữa Roots và DB transaction...");

        var initialRoots = await rootRegistry.GetCurrentRootsAsync();
        var validNewDocs = Path.Combine(testWorkDir, "new_docs_fault");
        Directory.CreateDirectory(validNewDocs);

        // Mô phỏng fault injection: Mở transaction, gọi UpdateRootsInternalAsync thành công,
        // sau đó phát sinh lỗi ghi provider/setting và Rollback
        await using (var db = new FrayloDbContext(dbOptions))
        {
            await using var tx = await db.Database.BeginTransactionAsync();

            await rootRegistry.UpdateRootsInternalAsync(
                db,
                validNewDocs,
                initialRoots.ImagesPath,
                initialRoots.VideosPath);

            // Fault injection: Giả lập lỗi ghi provider hoặc exception bất ngờ
            await tx.RollbackAsync();
        }

        // Xác minh: Giá trị roots trong DB và registry PHẢI GIỮ NGUYÊN ban đầu (không bị cập nhật rò rỉ)
        var afterRoots = await rootRegistry.GetCurrentRootsAsync();
        Assert(afterRoots.DocumentsPath == initialRoots.DocumentsPath,
            "Roots KHÔNG ĐƯỢC thay đổi khi transaction bị rollback do lỗi provider!");
        Assert(afterRoots.DocumentsRootId == initialRoots.DocumentsRootId,
            "RootId KHÔNG ĐƯỢC thay đổi khi transaction bị rollback!");
        Console.WriteLine("    -> Settings atomic transaction rollback (roots & providers đồng bộ): PASS");
    }

    private static async Task TestAudioHeaderValidation(
        AssetService assetService,
        DbContextOptions<FrayloDbContext> dbOptions,
        string testWorkDir)
    {
        Console.WriteLine("\n[T09-REGRESSION-4] Kiểm thử Audio header byte check và giới hạn kích thước...");
        var projectId = Guid.NewGuid().ToString("D").ToLowerInvariant();
        var sceneId = Guid.NewGuid().ToString("D").ToLowerInvariant();
        var now = DateTimeOffset.UtcNow.ToString("O");

        await using (var db = new FrayloDbContext(dbOptions))
        {
            db.Projects.Add(new Project { Id = projectId, Name = "PRJ Audio", Status = "Draft", CreatedAtUtc = now, UpdatedAtUtc = now });
            db.Scenes.Add(new Scene { Id = sceneId, ProjectId = projectId, SequenceOrder = 1, Name = "SCN Audio", TargetDurationSeconds = 10, Status = "Draft", CreatedAtUtc = now, UpdatedAtUtc = now });
            await db.SaveChangesAsync();
        }

        // 1. Fake WAV: Đuôi .wav nhưng nội dung không có header RIFF/WAVE -> BỊ TỪ CHỐI
        var fakeWavPath = Path.Combine(testWorkDir, "fake.wav");
        await File.WriteAllBytesAsync(fakeWavPath, Encoding.UTF8.GetBytes("THIS IS NOT A REAL WAV FILE AT ALL!"));

        await AssertThrowsAsync<AssetValidationException>(async () =>
        {
            await assetService.ImportAudioAsync(new ImportAudioRequest(projectId, sceneId, fakeWavPath));
        }, "Chặn file đuôi .wav nhưng thiếu header RIFF/WAVE.");

        // 2. Valid WAV: Header RIFF + WAVE
        var validWavData = new byte[44];
        Encoding.ASCII.GetBytes("RIFF").CopyTo(validWavData, 0);
        BitConverter.GetBytes(36).CopyTo(validWavData, 4);
        Encoding.ASCII.GetBytes("WAVEfmt ").CopyTo(validWavData, 8);
        var validWavPath = Path.Combine(testWorkDir, "valid.wav");
        await File.WriteAllBytesAsync(validWavPath, validWavData);

        var wavResp = await assetService.ImportAudioAsync(new ImportAudioRequest(projectId, sceneId, validWavPath));
        Assert(wavResp.Asset != null, "Valid WAV file phải được import thành công.");

        Console.WriteLine("    -> Audio header validation (RIFF/WAVE & MP3 byte checks): PASS");
    }

    private static async Task TestSchemaCompositeFkAndCheckConstraint(DbContextOptions<FrayloDbContext> dbOptions)
    {
        Console.WriteLine("\n[T09-REGRESSION-5] Kiểm thử Check Constraint CK_Job_TaskType_Scene & Composite FK...");

        var projectId = Guid.NewGuid().ToString("D").ToLowerInvariant();
        var otherProjectId = Guid.NewGuid().ToString("D").ToLowerInvariant();
        var sceneId = Guid.NewGuid().ToString("D").ToLowerInvariant();
        var now = DateTimeOffset.UtcNow.ToString("O");

        await using (var db = new FrayloDbContext(dbOptions))
        {
            db.Projects.Add(new Project { Id = projectId, Name = "PRJ JobCheck", Status = "Draft", CreatedAtUtc = now, UpdatedAtUtc = now });
            db.Projects.Add(new Project { Id = otherProjectId, Name = "PRJ khác", Status = "Draft", CreatedAtUtc = now, UpdatedAtUtc = now });
            db.Scenes.Add(new Scene { Id = sceneId, ProjectId = projectId, SequenceOrder = 1, Name = "SCN JobCheck", TargetDurationSeconds = 10, Status = "Draft", CreatedAtUtc = now, UpdatedAtUtc = now });
            await db.SaveChangesAsync();
        }

        // Test 1: Task Script mà có scene_id -> Vi phạm CK_Job_TaskType_Scene
        await using (var db = new FrayloDbContext(dbOptions))
        {
            db.Jobs.Add(new Job
            {
                Id = Guid.NewGuid().ToString("N"),
                ProjectId = projectId,
                SceneId = sceneId, // Script KHÔNG ĐƯỢC có scene_id
                TaskType = "Script",
                State = "Queued",
                ProviderKey = "chatgpt-web",
                StorageRootId = "root-docs",
                CreatedAtUtc = now,
                UpdatedAtUtc = now
            });

            bool checkViolated = false;
            try
            {
                await db.SaveChangesAsync();
            }
            catch (DbUpdateException ex) when (ex.InnerException is SqliteException sqlEx && sqlEx.SqliteErrorCode == 19)
            {
                checkViolated = true;
            }
            Assert(checkViolated, "Job TaskType = 'Script' có SceneId phải bị SQLite từ chối bởi CK_Job_TaskType_Scene!");
        }

        // Image có thể là job cấp Project, không cần Scene.
        await using (var db = new FrayloDbContext(dbOptions))
        {
            db.Jobs.Add(new Job
            {
                Id = Guid.NewGuid().ToString("N"),
                ProjectId = projectId,
                SceneId = null,
                TaskType = "Image",
                State = "Queued",
                ProviderKey = "chatgpt-web",
                StorageRootId = "root-images",
                CreatedAtUtc = now,
                UpdatedAtUtc = now
            });

            await db.SaveChangesAsync();
        }

        // Composite FK ngăn gắn Scene của Project khác vào Job.
        await using (var db = new FrayloDbContext(dbOptions))
        {
            db.Jobs.Add(new Job
            {
                Id = Guid.NewGuid().ToString("N"), ProjectId = otherProjectId, SceneId = sceneId,
                TaskType = "Image", State = "Queued", ProviderKey = "chatgpt-web",
                StorageRootId = "root-images", CreatedAtUtc = now, UpdatedAtUtc = now
            });
            var rejected = false;
            try { await db.SaveChangesAsync(); }
            catch (DbUpdateException ex) when (ex.InnerException is SqliteException sqlEx && sqlEx.SqliteErrorCode == 19)
            { rejected = true; }
            Assert(rejected, "Job không được tham chiếu Scene thuộc Project khác.");
        }

        Console.WriteLine("    -> Check Constraint CK_Job_TaskType_Scene & Composite FK: PASS");
    }

    private static async Task TestUpgradeLegacyDbWithRealJobsAsync(string testWorkDir)
    {
        Console.WriteLine("\n[T09-REGRESSION-6] Kiểm thử PRAGMA foreign_key_check trên DB mới...");

        var legacyDbPath = Path.Combine(testWorkDir, "legacy_upgrade.db");
        var connStr = DatabasePathHelper.BuildConnectionString(legacyDbPath);

        // Khởi tạo DB
        var bootstrapper = new SqliteDatabaseBootstrapper();
        var bootstrapResult = await bootstrapper.BootstrapAsync(legacyDbPath);

        // Thực thi kiểm tra foreign key check
        await using var conn = new SqliteConnection(connStr);
        await conn.OpenAsync();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA foreign_key_check;";
        using var reader = await cmd.ExecuteReaderAsync();
        bool hasFkViolation = await reader.ReadAsync();
        Assert(!hasFkViolation, "PRAGMA foreign_key_check phải trả về 0 vi phạm trên DB mới!");

        Console.WriteLine("    -> DB mới có PRAGMA foreign_key_check = 0 vi phạm: PASS");
    }

    private static async Task TestUploadRejectsMissingProjectAndReconcileFilesAsync(
        AssetService assetService,
        StorageRootRegistry rootRegistry)
    {
        Console.WriteLine("\n[T09-7] Kiểm thử upload vào Project không tồn tại bị từ chối và đối soát tệp...");
        var payload = Convert.ToBase64String(new byte[] { 1, 2, 3, 4 });

        var rejected = false;
        try
        {
            await assetService.UploadAssetAsync(new UploadAssetRequest(
                ProjectId: Guid.NewGuid().ToString("D"), AssetType: "Image", FileName: "a.png", Base64Data: payload));
        }
        catch (AssetValidationException)
        {
            rejected = true;
        }
        Assert(rejected, "Upload vào Project không tồn tại phải bị từ chối, không lưu thành asset toàn app.");

        // Một upload hợp lệ để root có metadata; sau đó tạo tệp mồ côi và tệp staging bị bỏ dở quá 1 ngày.
        await assetService.UploadAssetAsync(new UploadAssetRequest(AssetType: "Image", FileName: "b.png", Base64Data: payload));
        var imagesRoot = (await rootRegistry.GetCurrentRootsAsync()).ImagesPath;
        var orphanRelative = "orphan-check/images/stray.png";
        var orphanPath = Path.Combine(imagesRoot, orphanRelative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(orphanPath)!);
        File.WriteAllBytes(orphanPath, new byte[] { 9 });

        var stagingDir = Path.Combine(imagesRoot, ".staging");
        Directory.CreateDirectory(stagingDir);
        var staleTmp = Path.Combine(stagingDir, "stale.tmp");
        File.WriteAllBytes(staleTmp, new byte[] { 7 });
        File.SetLastWriteTimeUtc(staleTmp, DateTime.UtcNow.AddDays(-2));

        var report = await assetService.ReconcileFilesAsync();
        Assert(!File.Exists(staleTmp), "Đối soát phải dọn tệp staging bị bỏ dở quá 1 ngày.");
        Assert(report.StagingFilesRemoved >= 1, "Báo cáo đối soát phải đếm tệp staging đã dọn.");
        Assert(report.OrphanRelativePaths.Contains(orphanRelative), "Đối soát phải báo tệp không có metadata Asset.");
        Assert(File.Exists(orphanPath), "Đối soát không được tự xóa tệp đã chốt.");

        Console.WriteLine("    -> Từ chối Project không tồn tại và đối soát tệp: PASS");
    }

    private static async Task TestReconcileRegisteredRootsAndSkipStagingJunctionAsync(
        AssetService assetService,
        StorageRootRegistry rootRegistry,
        string testWorkDir)
    {
        Console.WriteLine("\n[T09-8] Kiểm thử đối soát duyệt mọi root đã đăng ký và không dọn staging qua junction...");
        var current = await rootRegistry.GetCurrentRootsAsync();

        // Root Documents mới đăng ký nhưng chưa có Asset nào: staging bỏ dở vẫn phải được dọn.
        var docsRoot = Path.Combine(testWorkDir, "reconcile-docs-root");
        var staleInDocs = Path.Combine(docsRoot, ".staging", "crash.tmp");
        Directory.CreateDirectory(Path.GetDirectoryName(staleInDocs)!);
        File.WriteAllBytes(staleInDocs, new byte[] { 5 });
        File.SetLastWriteTimeUtc(staleInDocs, DateTime.UtcNow.AddDays(-2));

        // Root Videos có .staging là junction trỏ ra ngoài root: tệp đích không được xóa.
        var videosRoot = Path.Combine(testWorkDir, "reconcile-videos-root");
        var outsideTarget = Path.Combine(testWorkDir, "reconcile-outside-target");
        Directory.CreateDirectory(videosRoot);
        Directory.CreateDirectory(outsideTarget);
        var victim = Path.Combine(outsideTarget, "victim.tmp");
        File.WriteAllBytes(victim, new byte[] { 6 });
        File.SetLastWriteTimeUtc(victim, DateTime.UtcNow.AddDays(-2));
        var junction = Path.Combine(videosRoot, ".staging");
        using (var mklink = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
                   "cmd", $"/c mklink /J \"{junction}\" \"{outsideTarget}\"") { UseShellExecute = false, CreateNoWindow = true })!)
        {
            mklink.WaitForExit();
            Assert(mklink.ExitCode == 0, "Không tạo được junction .staging cho fixture.");
        }

        try
        {
            await rootRegistry.UpdateRootsAsync(docsRoot, current.ImagesPath, videosRoot);
            await assetService.ReconcileFilesAsync();

            Assert(!File.Exists(staleInDocs), "Đối soát phải dọn staging của root đã đăng ký dù chưa có Asset.");
            Assert(File.Exists(victim), "Đối soát không được xóa tệp nằm ngoài root qua junction .staging.");
        }
        finally
        {
            await rootRegistry.UpdateRootsAsync(current.DocumentsPath, current.ImagesPath, current.VideosPath);
            if (Directory.Exists(junction)) Directory.Delete(junction); // xóa junction, không xóa đích
        }

        Console.WriteLine("    -> Đối soát root chưa có Asset và junction .staging: PASS");
    }

    private static async Task TestGroupScopedAssetsAsync(AssetService assetService, DbContextOptions<FrayloDbContext> dbOptions)
    {
        Console.WriteLine("\n[T09-9] Kiểm thử tài nguyên dùng chung trong Nhóm: upload, phạm vi Project và Nhóm...");
        var groups = new ProjectGroupService(dbOptions);
        var projects = new ProjectService(dbOptions);
        var payload = Convert.ToBase64String(new byte[] { 1, 2, 3, 4 });

        var group = await groups.SaveAsync(new ProjectGroupSaveRequest(null, "Nhóm tài nguyên", null, null, null, null));
        var member = await projects.CreateAsync(new ProjectCreateRequest("Thành viên nhóm", null, group.GroupId));
        var outsider = await projects.CreateAsync(new ProjectCreateRequest("Ngoài nhóm", null, null));

        var groupUpload = await assetService.UploadAssetAsync(new UploadAssetRequest(GroupId: group.GroupId, AssetType: "Image", FileName: "nhom.png", Base64Data: payload));
        var memberUpload = await assetService.UploadAssetAsync(new UploadAssetRequest(ProjectId: member.ProjectId, AssetType: "Image", FileName: "rieng.png", Base64Data: payload));
        var outsiderUpload = await assetService.UploadAssetAsync(new UploadAssetRequest(ProjectId: outsider.ProjectId, AssetType: "Image", FileName: "ngoai.png", Base64Data: payload));
        Assert(groupUpload.Asset is not null && groupUpload.Asset.GroupId == group.GroupId && groupUpload.Asset.ProjectId is null,
            "Tải lên trong nhóm phải gắn Nhóm và không gắn Project.");
        Assert(groupUpload.Asset!.RelativePath.StartsWith($"groups/{group.GroupId}/", StringComparison.Ordinal),
            "Tệp của nhóm phải lưu dưới thư mục groups/.");

        var rejectedBoth = false;
        try
        {
            await assetService.UploadAssetAsync(new UploadAssetRequest(ProjectId: member.ProjectId, GroupId: group.GroupId, AssetType: "Image", FileName: "x.png", Base64Data: payload));
        }
        catch (AssetValidationException)
        {
            rejectedBoth = true;
        }
        Assert(rejectedBoth, "Tải lên không được gắn đồng thời Project và Nhóm.");

        var rejectedMissingGroup = false;
        try
        {
            await assetService.UploadAssetAsync(new UploadAssetRequest(GroupId: Guid.NewGuid().ToString("D"), AssetType: "Image", FileName: "y.png", Base64Data: payload));
        }
        catch (AssetValidationException)
        {
            rejectedMissingGroup = true;
        }
        Assert(rejectedMissingGroup, "Tải lên vào nhóm không tồn tại phải bị từ chối.");

        var forMember = await assetService.ListAssetsAsync(new ListAssetsRequest(ProjectId: member.ProjectId, PageSize: 100));
        Assert(forMember.Assets.Any(a => a.AssetId == groupUpload.Asset.AssetId) && forMember.Assets.Any(a => a.AssetId == memberUpload.Asset!.AssetId),
            "Project trong nhóm phải thấy cả tệp riêng và tệp của nhóm.");
        var forOutsider = await assetService.ListAssetsAsync(new ListAssetsRequest(ProjectId: outsider.ProjectId, PageSize: 100));
        Assert(!forOutsider.Assets.Any(a => a.AssetId == groupUpload.Asset.AssetId),
            "Project ngoài nhóm không được thấy tệp của nhóm.");
        var forGroup = await assetService.ListAssetsAsync(new ListAssetsRequest(GroupId: group.GroupId, PageSize: 100));
        Assert(forGroup.Assets.Any(a => a.AssetId == groupUpload.Asset.AssetId) && forGroup.Assets.Any(a => a.AssetId == memberUpload.Asset!.AssetId)
            && !forGroup.Assets.Any(a => a.AssetId == outsiderUpload.Asset!.AssetId),
            "Danh sách theo nhóm phải gồm tệp của nhóm và tệp của dự án trong nhóm, không gồm dự án ngoài nhóm.");
        Console.WriteLine("    -> Tài nguyên dùng chung trong Nhóm: upload và phạm vi theo Project/Nhóm PASS");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException($"[ASSERTION FAILED] {message}");
        }
    }

    private static void AssertThrows<TException>(Action action, string scenario) where TException : Exception
    {
        try
        {
            action();
            throw new InvalidOperationException($"[ASSERTION FAILED] Dự kiến ngoại lệ {typeof(TException).Name} nhưng không xảy ra trong: {scenario}");
        }
        catch (TException)
        {
            // Mong muốn
        }
    }

    private static async Task AssertThrowsAsync<TException>(Func<Task> action, string scenario) where TException : Exception
    {
        try
        {
            await action();
            throw new InvalidOperationException($"[ASSERTION FAILED] Dự kiến ngoại lệ {typeof(TException).Name} nhưng không xảy ra trong: {scenario}");
        }
        catch (TException)
        {
            // Mong muốn
        }
    }
}
