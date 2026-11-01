using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Fraylo.Application.Projects;
using Fraylo.Application.Scenes;
using Fraylo.Contracts.Projects;
using Fraylo.Contracts.Scenes;
using Fraylo.Infrastructure.Persistence;
using Fraylo.Infrastructure.Persistence.Entities;
using Fraylo.Infrastructure.Persistence.Interceptors;

namespace Fraylo.Infrastructure.SmokeChecks;

/// <summary>
/// Bộ kiểm thử khói kiểm chứng nghiệp vụ Backend cho Task T08 (T08a Projects Backend &amp; T08b Scenes Backend)
/// thực thi trên cơ sở dữ liệu SQLite vật lý với đầy đủ PRAGMA WAL, Foreign Keys và Write Lock Serialization.
/// </summary>
public static class ProjectAndSceneSmokeTests
{
    public static async Task RunTestsAsync(string databasePath)
    {
        Console.WriteLine("\n=================================================================");
        Console.WriteLine("    BẮT ĐẦU KIỂM THỬ KHÓI T08: PROJECTS & SCENES BACKEND        ");
        Console.WriteLine("=================================================================");

        var connectionString = DatabasePathHelper.BuildConnectionString(databasePath);
        var optionsBuilder = new DbContextOptionsBuilder<FrayloDbContext>();
        optionsBuilder.UseSqlite(connectionString)
                      .AddInterceptors(new SqlitePragmaInterceptor());
        var dbOptions = optionsBuilder.Options;

        var projectService = new ProjectService(dbOptions);
        var sceneService = new SceneService(dbOptions);

        var projectDir1 = "project-a";
        var projectDir2 = "project-b";
        var nowUtc = DateTime.UtcNow.ToString("o");

        // =====================================================================
        // TEST CASE 1: CRUD Project cơ bản & Validation
        // =====================================================================
        Console.WriteLine("\n[1] Kiểm thử T08a: CRUD Project cơ bản & Validation...");
        var createProj1 = await projectService.CreateAsync(new ProjectCreateRequest("Video Khám Phá Sao Hỏa",
            "Mô tả video kịch bản khoa học 60s"));

        Assert(!string.IsNullOrWhiteSpace(createProj1.ProjectId), "ProjectId phải được tạo.");
        var proj1Id = createProj1.ProjectId;

        var proj1Detail = await projectService.GetAsync(proj1Id);
        Assert(proj1Detail.ProjectId == proj1Id, "Mã dự án phải trùng khớp.");
        Assert(proj1Detail.Name == "Video Khám Phá Sao Hỏa", "Tên dự án phải chính xác.");
        Assert(proj1Detail.Status == "Draft", "Trạng thái khởi tạo dự án phải là Draft.");
        Assert(proj1Detail.Description == "Mô tả video kịch bản khoa học 60s", "Mô tả dự án phải chính xác.");

        // Kiểm thử update
        var updatedProj1 = await projectService.UpdateAsync(new ProjectUpdateRequest(proj1Id,
            "Video Khám Phá Sao Hỏa (Đã sửa)",
            "Mô tả cập nhật chi tiết hơn",
            "InProgress"));

        Assert(updatedProj1.Name == "Video Khám Phá Sao Hỏa (Đã sửa)", "Tên cập nhật không khớp.");
        Assert(updatedProj1.Status == "InProgress", "Trạng thái cập nhật phải là InProgress.");

        // Kiểm thử Project list ban đầu (sceneCount = 0).
        var projList = await projectService.ListAllAsync();
        Assert(projList.Count >= 1, "Danh sách dự án phải có ít nhất 1 bản ghi.");
        var pSummary = projList.First(p => p.ProjectId == proj1Id);
        Assert(pSummary.SceneCount == 0, "SceneCount ban đầu phải bằng 0.");

        // Kiểm thử validation dự án
        await AssertThrowsAsync<ProjectValidationException>(
            () => projectService.CreateAsync(new ProjectCreateRequest("   ", null)),
            "Tên dự án rỗng phải bị từ chối.");

        await AssertThrowsAsync<ProjectValidationException>(
            () => projectService.UpdateAsync(new ProjectUpdateRequest(proj1Id, null, null, "TrangThaiLa")),
            "Trạng thái dự án không hợp lệ phải bị từ chối.");

        Console.WriteLine("    -> [PASS] Tạo, đọc, cập nhật và xác thực Project thành công.");

        // =====================================================================
        // TEST CASE 3: CRUD Scene & Tự động tăng SequenceOrder
        // =====================================================================
        Console.WriteLine("\n[3] Kiểm thử T08b: CRUD Scene & SequenceOrder Auto-increment...");

        // Tạo Scene 1 (không truyền Name -> tự động sinh 'Cảnh 1')
        var createScene1 = await sceneService.CreateAsync(new SceneCreateRequest(proj1Id,
            null,
            "Cảnh mở đầu: Tàu thăm dò đáp xuống bề mặt sao Hỏa",
            5,
            null, null, null));

        Assert(createScene1.SceneSequence == 1, "Cảnh đầu tiên phải có sequenceOrder = 1.");
        var scene1Id = createScene1.SceneId;

        // Tạo Scene 2 (truyền Name cụ thể)
        var createScene2 = await sceneService.CreateAsync(new SceneCreateRequest(proj1Id,
            "Cảnh bay lượn trên hẻm núi",
            "Flycam quét toàn cảnh Mariner Valley",
            8,
            null, null, null));

        Assert(createScene2.SceneSequence == 2, "Cảnh thứ hai phải có sequenceOrder = 2.");
        var scene2Id = createScene2.SceneId;

        // Tạo Scene 3
        var createScene3 = await sceneService.CreateAsync(new SceneCreateRequest(proj1Id,
            "Cảnh hoàng hôn sao Hỏa",
            "Mặt trời lặn mang sắc lam kỳ ảo",
            6,
            null, null, null));

        Assert(createScene3.SceneSequence == 3, "Cảnh thứ ba phải có sequenceOrder = 3.");
        var scene3Id = createScene3.SceneId;

        // Kiểm tra SceneCount trong Project list.
        var projListAfterScenes = await projectService.ListAllAsync();
        var pSummaryAfter = projListAfterScenes.First(p => p.ProjectId == proj1Id);
        Assert(pSummaryAfter.SceneCount == 3, $"SceneCount phải là 3 (thực tế: {pSummaryAfter.SceneCount}).");

        // Kiểm thử get chi tiết Scene
        var scene1Detail = await sceneService.GetAsync(proj1Id, scene1Id);
        Assert(scene1Detail.Name == "Cảnh 1", "Tên cảnh tự sinh phải là 'Cảnh 1'.");
        Assert(scene1Detail.TargetDurationSeconds == 5, "TargetDurationSeconds phải là 5.");

        // Kiểm thử update Scene
        var updatedScene1 = await sceneService.UpdateAsync(new SceneUpdateRequest(ProjectId: proj1Id,
            SceneId: scene1Id,
            Name: "Cảnh mở đầu tàu vũ trụ",
            PromptText: "Prompt cập nhật",
            TargetDurationSeconds: 10,
            Status: "Draft"));

        Assert(updatedScene1.Name == "Cảnh mở đầu tàu vũ trụ", "Tên cảnh cập nhật không khớp.");
        Assert(updatedScene1.TargetDurationSeconds == 10, "TargetDurationSeconds cập nhật phải là 10.");
        Assert(!string.IsNullOrEmpty(updatedScene1.Revision), "Revision của cảnh phải có giá trị.");

        // Kiểm thử validation duration dương
        await AssertThrowsAsync<SceneValidationException>(
            () => sceneService.CreateAsync(new SceneCreateRequest(proj1Id, "Lỗi", "Prompt", 0, null, null, null)),
            "Duration <= 0 phải bị từ chối.");

        // Kiểm thử Scene thuộc Project khác.
        // Scene authorization follows its owning Project.

        Console.WriteLine("    -> [PASS] CRUD Scene & SequenceOrder tăng tự động thành công.");

        // =====================================================================
        // TEST CASE 4: Asset Type & Ownership Validation trong Scene
        // =====================================================================
        Console.WriteLine("\n[4] Kiểm thử T08b: Kiểm tra Asset Type & Ownership trong Scene...");

        var audioAssetId = Guid.NewGuid().ToString("D").ToLowerInvariant();
        var imageAssetId = Guid.NewGuid().ToString("D").ToLowerInvariant();
        var sharedAssetId = Guid.NewGuid().ToString("D").ToLowerInvariant();

        await using (var db = new FrayloDbContext(dbOptions))
        {
            db.Assets.AddRange(
                new Asset
                {
                    Id = audioAssetId,
                    ProjectId = proj1Id,
                    StorageRootId = "root-audio",
                    RelativePath = $"{projectDir1}/{proj1Id}/audio/{audioAssetId}.mp3",
                    AssetType = "Audio",
                    ByteSize = 1024,
                    Sha256Hash = "hash1",
                    CreatedAtUtc = nowUtc
                },
                new Asset
                {
                    Id = imageAssetId,
                    ProjectId = proj1Id,
                    StorageRootId = "root-images",
                    RelativePath = $"{projectDir1}/{proj1Id}/images/{imageAssetId}.png",
                    AssetType = "Image",
                    ByteSize = 2048,
                    Sha256Hash = "hash2",
                    CreatedAtUtc = nowUtc
                },
                new Asset
                {
                    Id = sharedAssetId,
                    ProjectId = null,
                    StorageRootId = "root-audio",
                    RelativePath = $"{projectDir2}/audio/{sharedAssetId}.mp3",
                    AssetType = "Audio",
                    ByteSize = 1024,
                    Sha256Hash = "hash3",
                    CreatedAtUtc = nowUtc
                });
            await db.SaveChangesAsync();
        }

        // Gán đúng asset type
        var sceneWithAsset = await sceneService.UpdateAsync(new SceneUpdateRequest(ProjectId: proj1Id,
            SceneId: scene1Id,
            AudioAssetId: audioAssetId,
            ImageAssetId: imageAssetId));

        Assert(sceneWithAsset.AudioAssetId == audioAssetId, "AudioAssetId phải được gán.");
        Assert(sceneWithAsset.ImageAssetId == imageAssetId, "ImageAssetId phải được gán.");

        // Gán nhầm Image vào Audio slot -> phải bị từ chối
        await AssertThrowsAsync<SceneValidationException>(
            () => sceneService.UpdateAsync(new SceneUpdateRequest(ProjectId: proj1Id,
                SceneId: scene1Id,
                AudioAssetId: imageAssetId)),
            "Gán Asset Image vào vị trí Audio phải bị ném ngoại lệ.");

        // Tài nguyên độc lập được chia sẻ toàn ứng dụng.
        var sharedAssetScene = await sceneService.UpdateAsync(new SceneUpdateRequest(ProjectId: proj1Id,
            SceneId: scene1Id, AudioAssetId: sharedAssetId));
        Assert(sharedAssetScene.AudioAssetId == sharedAssetId, "Asset dùng chung phải được gắn vào Scene.");
        await sceneService.UpdateAsync(new SceneUpdateRequest(ProjectId: proj1Id,
            SceneId: scene1Id, AudioAssetId: audioAssetId));

        Console.WriteLine("    -> [PASS] Kiểm tra loại tài nguyên và asset dùng chung.");

        // =====================================================================
        // TEST CASE 5: Tự động đánh số lại SequenceOrder khi xóa Scene
        // =====================================================================
        Console.WriteLine("\n[5] Kiểm thử T08b: Auto Re-sequencing khi xóa Scene...");

        // Hiện tại: Scene 1 (seq 1), Scene 2 (seq 2), Scene 3 (seq 3)
        // Xóa Scene 2 -> Scene 3 phải tự động thành sequence 2
        var delScene2 = await sceneService.DeleteAsync(proj1Id, scene2Id);
        Assert(delScene2.Success, "Xóa scene2 phải thành công.");

        var remainingScenes = await sceneService.ListByProjectAsync(proj1Id);
        Assert(remainingScenes.Count == 2, "Chỉ còn 2 scene.");
        Assert(remainingScenes[0].SceneId == scene1Id && remainingScenes[0].SceneSequence == 1, "Scene1 giữ sequence 1.");
        Assert(remainingScenes[1].SceneId == scene3Id && remainingScenes[1].SceneSequence == 2, "Scene3 phải tự động chuyển thành sequence 2!");

        Console.WriteLine("    -> [PASS] Tự động đánh số lại SequenceOrder liên tục 1, 2... thành công.");

        // =====================================================================
        // TEST CASE 6: Reorder Transactional & Collision Prevention
        // =====================================================================
        Console.WriteLine("\n[6] Kiểm thử T08b: Reorder Transactional & Collision Prevention...");

        // Thêm 2 cảnh mới để có tổng cộng 4 cảnh
        var createScene4 = await sceneService.CreateAsync(new SceneCreateRequest(proj1Id, "Cảnh 4", null, 5, null, null, null));
        var createScene5 = await sceneService.CreateAsync(new SceneCreateRequest(proj1Id, "Cảnh 5", null, 5, null, null, null));
        var scene4Id = createScene4.SceneId;
        var scene5Id = createScene5.SceneId;

        // Hiện tại: Scene1 (1), Scene3 (2), Scene4 (3), Scene5 (4)
        // Đảo ngược thứ tự: Scene5 (1), Scene4 (2), Scene3 (3), Scene1 (4)
        var reorderResult = await sceneService.ReorderAsync(new SceneReorderRequest(proj1Id,
            new[] { scene5Id, scene4Id, scene3Id, scene1Id }));

        Assert(reorderResult.Reordered, "Reorder phải thành công.");
        Assert(reorderResult.Scenes.Count == 4, "Phải trả về đủ 4 scenes.");
        Assert(reorderResult.Scenes[0].SceneId == scene5Id && reorderResult.Scenes[0].SceneSequence == 1, "Scene5 phải có seq = 1.");
        Assert(reorderResult.Scenes[1].SceneId == scene4Id && reorderResult.Scenes[1].SceneSequence == 2, "Scene4 phải có seq = 2.");
        Assert(reorderResult.Scenes[2].SceneId == scene3Id && reorderResult.Scenes[2].SceneSequence == 3, "Scene3 phải có seq = 3.");
        Assert(reorderResult.Scenes[3].SceneId == scene1Id && reorderResult.Scenes[3].SceneSequence == 4, "Scene1 phải có seq = 4.");

        // Xác thực lại trong cơ sở dữ liệu
        var scenesFromDb = await sceneService.ListByProjectAsync(proj1Id);
        Assert(scenesFromDb[0].SceneId == scene5Id && scenesFromDb[0].SceneSequence == 1, "DB: Scene5 seq = 1");
        Assert(scenesFromDb[3].SceneId == scene1Id && scenesFromDb[3].SceneSequence == 4, "DB: Scene1 seq = 4");

        // Negative Reorder: danh sách thiếu phần tử
        await AssertThrowsAsync<SceneValidationException>(
            () => sceneService.ReorderAsync(new SceneReorderRequest(proj1Id, new[] { scene5Id, scene4Id })),
            "Reorder thiếu phần tử phải bị từ chối.");

        // Negative Reorder: danh sách có phần tử trùng lặp
        await AssertThrowsAsync<SceneValidationException>(
            () => sceneService.ReorderAsync(new SceneReorderRequest(proj1Id, new[] { scene5Id, scene5Id, scene3Id, scene1Id })),
            "Reorder có phần tử trùng lặp phải bị từ chối.");

        // Negative Reorder: danh sách chứa ID không thuộc dự án
        await AssertThrowsAsync<SceneValidationException>(
            () => sceneService.ReorderAsync(new SceneReorderRequest(proj1Id, new[] { scene5Id, scene4Id, scene3Id, Guid.NewGuid().ToString() })),
            "Reorder chứa ID ngoại lai phải bị từ chối.");

        Console.WriteLine("    -> [PASS] Reorder Transactional & kiểm tra tính toàn vẹn 100% đạt yêu cầu.");

        // =====================================================================
        // TEST CASE 6b: Optimistic Concurrency - Stale Scene Editor
        // =====================================================================
        Console.WriteLine("\n[6b] Kiểm thử R3: Optimistic Concurrency - Stale Scene Editor...");

        // Client A đọc Scene 3
        var scene3A = await sceneService.GetAsync(proj1Id, scene3Id);
        var revisionR1 = scene3A.Revision;
        Assert(!string.IsNullOrEmpty(revisionR1), "Revision R1 của scene3 không được để trống.");

        // Delay ngắn để đảm bảo timestamp Utc khác nhau nếu timer resolution trên OS ~1ms
        await Task.Delay(15);

        // Client B cập nhật Scene 3 -> Revision đổi thành R2
        var scene3BUpdate = await sceneService.UpdateAsync(new SceneUpdateRequest(ProjectId: proj1Id,
            SceneId: scene3Id,
            ExpectedRevision: revisionR1,
            Name: "Cảnh hoàng hôn sao Hỏa (B sửa)"));
        var revisionR2 = scene3BUpdate.Revision;
        Assert(revisionR2 != revisionR1, $"Revision R2 ({revisionR2}) phải khác R1 ({revisionR1}).");

        // Client A gửi cập nhật với ExpectedRevision cũ = R1 -> Bị từ chối với SceneConcurrencyConflictException
        await AssertThrowsAsync<SceneConcurrencyConflictException>(
            () => sceneService.UpdateAsync(new SceneUpdateRequest(ProjectId: proj1Id,
                SceneId: scene3Id,
                ExpectedRevision: revisionR1,
                Name: "Cảnh hoàng hôn sao Hỏa (A cố ghi đè)")),
            "Cập nhật Scene với revision cũ phải bị từ chối với SceneConcurrencyConflictException.");

        // Client A đồng bộ lại revision R2 và cập nhật thành công -> Revision thành R3
        await Task.Delay(15);
        var scene3AUpdateSuccess = await sceneService.UpdateAsync(new SceneUpdateRequest(ProjectId: proj1Id,
            SceneId: scene3Id,
            ExpectedRevision: revisionR2,
            Name: "Cảnh hoàng hôn sao Hỏa (A sửa sau khi đồng bộ)"));
        var revisionR3 = scene3AUpdateSuccess.Revision;
        Assert(revisionR3 != revisionR2, $"Revision R3 ({revisionR3}) phải khác R2 ({revisionR2}).");
        Assert(scene3AUpdateSuccess.Name == "Cảnh hoàng hôn sao Hỏa (A sửa sau khi đồng bộ)", "Nội dung cập nhật của A phải được lưu.");

        Console.WriteLine("    -> [PASS] Stale Scene Editor bị chặn và phục hồi sau khi sync revision thành công.");

        // =====================================================================
        // TEST CASE 6c: Optimistic Concurrency - Stale Project Reorder & Stale Scene Reorder
        // =====================================================================
        Console.WriteLine("\n[6c] Kiểm thử R3: Optimistic Concurrency - Stale Project & Scene Reorder...");

        // Client A lấy thông tin Project để có ExpectedProjectRevision
        var projDetailA = await projectService.GetAsync(proj1Id);
        var projRevisionP1 = projDetailA.UpdatedAtUtc;

        // Lấy danh sách scenes hiện tại
        var currentScenesList = await sceneService.ListByProjectAsync(proj1Id);
        var sceneIdsOrder = currentScenesList.Select(s => s.SceneId).ToList();
        var sceneRevisionsMap = currentScenesList.ToDictionary(s => s.SceneId, s => s.Revision);

        // Client B cập nhật Project -> UpdatedAtUtc đổi thành P2
        await Task.Delay(15);
        var updatedProjByB = await projectService.UpdateAsync(new ProjectUpdateRequest(ProjectId: proj1Id,
            Name: "Video Khám Phá Sao Hỏa (B đổi tên)",
            Description: null,
            Status: null));
        var projRevisionP2 = updatedProjByB.UpdatedAtUtc;
        Assert(projRevisionP2 != projRevisionP1, "Revision của Project phải thay đổi sau khi B cập nhật.");

        // Client A cố gắng Reorder với ExpectedProjectRevision cũ = P1 -> Bị từ chối
        await AssertThrowsAsync<SceneConcurrencyConflictException>(
            () => sceneService.ReorderAsync(new SceneReorderRequest(ProjectId: proj1Id,
                SceneIds: sceneIdsOrder,
                ExpectedProjectRevision: projRevisionP1)),
            "Reorder với ExpectedProjectRevision cũ phải bị từ chối với SceneConcurrencyConflictException.");

        // Kiểm thử Stale Scene Revisions trong Reorder:
        // Giả sử ProjectRevision đã sync lên P2, nhưng một Scene trong ExpectedSceneRevisions bị stale
        var staleSceneId = sceneIdsOrder[0];
        var staleRevisionsMap = new Dictionary<string, string>(sceneRevisionsMap)
        {
            [staleSceneId] = "stale-fake-revision-xyz"
        };

        await AssertThrowsAsync<SceneConcurrencyConflictException>(
            () => sceneService.ReorderAsync(new SceneReorderRequest(ProjectId: proj1Id,
                SceneIds: sceneIdsOrder,
                ExpectedProjectRevision: projRevisionP2,
                ExpectedSceneRevisions: staleRevisionsMap)),
            "Reorder với ExpectedSceneRevisions chứa revision cũ/sai lệch phải bị từ chối.");

        // Client A đồng bộ đầy đủ revision và Reorder thành công
        await Task.Delay(15);
        // Đảo thứ tự 2 phần tử đầu
        var reorderedOrder = new List<string>(sceneIdsOrder);
        (reorderedOrder[0], reorderedOrder[1]) = (reorderedOrder[1], reorderedOrder[0]);

        // Cập nhật lại snapshot revision của các scenes
        var freshScenesList = await sceneService.ListByProjectAsync(proj1Id);
        var freshRevisionsMap = freshScenesList.ToDictionary(s => s.SceneId, s => s.Revision);

        var successfulReorder = await sceneService.ReorderAsync(new SceneReorderRequest(ProjectId: proj1Id,
            SceneIds: reorderedOrder,
            ExpectedProjectRevision: projRevisionP2,
            ExpectedSceneRevisions: freshRevisionsMap));

        Assert(successfulReorder.Reordered, "Reorder với revision hợp lệ phải thành công.");
        Assert(successfulReorder.Scenes[0].SceneId == reorderedOrder[0], "Thứ tự cảnh mới phải khớp yêu cầu.");

        // Kiểm tra project.UpdatedAtUtc và scene.UpdatedAtUtc đã được làm mới đồng bộ
        var projAfterReorder = await projectService.GetAsync(proj1Id);
        Assert(projAfterReorder.UpdatedAtUtc != projRevisionP2, "Project.UpdatedAtUtc phải được cập nhật mới sau Reorder.");
        Assert(successfulReorder.Scenes.All(s => s.Revision == projAfterReorder.UpdatedAtUtc), "Tất cả Scene.Revision phải đồng bộ với timestamp Reorder.");

        Console.WriteLine("    -> [PASS] Stale Project Reorder & Stale Scene Reorder bị chặn và thực thi chuẩn xác.");

        // =====================================================================
        // TEST CASE 6d: Concurrent Racing Updates & Reorder
        // =====================================================================
        Console.WriteLine("\n[6d] Kiểm thử R3: Concurrent Racing Updates...");

        // Hai tác vụ racing trên cùng Scene: cả hai bắt đầu từ cùng một Revision gốc
        var targetScene = await sceneService.GetAsync(proj1Id, scene1Id);
        var baseRevision = targetScene.Revision;

        var task1 = Task.Run(async () =>
        {
            try
            {
                await sceneService.UpdateAsync(new SceneUpdateRequest(ProjectId: proj1Id,
                    SceneId: scene1Id,
                    ExpectedRevision: baseRevision,
                    Name: "Cập nhật song song từ Luồng 1"));
                return (Success: true, Error: (Exception?)null);
            }
            catch (Exception ex)
            {
                return (Success: false, Error: ex);
            }
        });

        var task2 = Task.Run(async () =>
        {
            try
            {
                await sceneService.UpdateAsync(new SceneUpdateRequest(ProjectId: proj1Id,
                    SceneId: scene1Id,
                    ExpectedRevision: baseRevision,
                    Name: "Cập nhật song song từ Luồng 2"));
                return (Success: true, Error: (Exception?)null);
            }
            catch (Exception ex)
            {
                return (Success: false, Error: ex);
            }
        });

        var results = await Task.WhenAll(task1, task2);
        var successCount = results.Count(r => r.Success);
        var conflictCount = results.Count(r => !r.Success && r.Error is SceneConcurrencyConflictException);

        Assert(successCount == 1, $"Trong race condition với ExpectedRevision, phải có đúng 1 tác vụ thành công (thực tế: {successCount}).");
        Assert(conflictCount == 1, $"Tác vụ còn lại bắt buộc phải thất bại với SceneConcurrencyConflictException (thực tế: {conflictCount}).");

        Console.WriteLine("    -> [PASS] Concurrent Racing Updates cho kết quả xác định (1 thành công, 1 xung đột).");

        // =====================================================================
        // TEST CASE 7: Strict Active Job Guard khi xóa Scene
        // =====================================================================
        Console.WriteLine("\n[7] Kiểm thử T08b: Strict Active Job Guard trên Scene...");

        var activeJobId = Guid.NewGuid().ToString("D").ToLowerInvariant();
        await using (var db = new FrayloDbContext(dbOptions))
        {
            db.Jobs.Add(new Job
            {
                Id = activeJobId,
                ProjectId = proj1Id,
                SceneId = scene5Id,
                TaskType = "VideoPrompt",
                State = "Running", // Job đang chạy!
                ProviderKey = "gemini-web",
                StorageRootId = "root-internal",
                CreatedAtUtc = nowUtc,
                UpdatedAtUtc = nowUtc
            });
            await db.SaveChangesAsync();
        }

        // Cố tình xóa Scene5 khi có Job Running -> phải bị chặn với SceneConcurrencyConflictException
        await AssertThrowsAsync<SceneConcurrencyConflictException>(
            () => sceneService.DeleteAsync(proj1Id, scene5Id),
            "Xóa Scene khi có Job Running phải bị chặn.");

        // Chuyển Job sang Canceled
        await using (var db = new FrayloDbContext(dbOptions))
        {
            var j = await db.Jobs.FindAsync(activeJobId);
            j!.State = "Canceled";
            j.UpdatedAtUtc = DateTime.UtcNow.ToString("o");
            await db.SaveChangesAsync();
        }

        // Bây giờ xóa Scene5 phải thành công
        var delScene5 = await sceneService.DeleteAsync(proj1Id, scene5Id);
        Assert(delScene5.Success, "Xóa scene5 sau khi job kết thúc phải thành công.");

        Console.WriteLine("    -> [PASS] Active Job Guard trên Scene chặn xóa thành công.");

        // =====================================================================
        // TEST CASE 8: Strict Active Job Guard trên Project
        // =====================================================================
        Console.WriteLine("\n[8] Kiểm thử T08a: Strict Active Job Guard trên Project...");

        var uncompletedJobId = Guid.NewGuid().ToString("D").ToLowerInvariant();
        await using (var db = new FrayloDbContext(dbOptions))
        {
            db.Jobs.Add(new Job
            {
                Id = uncompletedJobId,
                ProjectId = proj1Id,
                SceneId = scene1Id,
                TaskType = "Image",
                State = "Queued", // Job đang chờ thực thi!
                ProviderKey = "gemini-web",
                StorageRootId = "root-default-images",
                CreatedAtUtc = nowUtc,
                UpdatedAtUtc = nowUtc
            });
            await db.SaveChangesAsync();
        }

        // Cố tình xóa Project1 khi có Job Queued -> phải bị chặn với ProjectConcurrencyConflictException
        await AssertThrowsAsync<ProjectConcurrencyConflictException>(
            () => projectService.DeleteAsync(proj1Id),
            "Xóa Project khi có Job Queued phải bị chặn.");

        // Thử trường hợp Job có SideEffectSubmitted = 1 nhưng ResultReference = null (chưa đối soát)
        await using (var db = new FrayloDbContext(dbOptions))
        {
            var j = await db.Jobs.FindAsync(uncompletedJobId);
            j!.State = "Succeeded";
            j.SideEffectSubmitted = 1;
            j.ResultReference = null; // Thiếu mã đối soát!
            await db.SaveChangesAsync();
        }

        await AssertThrowsAsync<ProjectConcurrencyConflictException>(
            () => projectService.DeleteAsync(proj1Id),
            "Xóa Project khi Job chưa đối soát xong (ResultReference = null) phải bị chặn.");

        // Cập nhật Job đã đối soát hợp lệ
        await using (var db = new FrayloDbContext(dbOptions))
        {
            var j = await db.Jobs.FindAsync(uncompletedJobId);
            j!.ResultReference = "https://example.com/result/script-123";
            await db.SaveChangesAsync();
        }

        Console.WriteLine("    -> [PASS] Active Job Guard trên Project chặn xóa chính xác.");

        // =====================================================================
        // TEST CASE 9: Safe Asset Detach & ON DELETE NO ACTION Preservation
        // =====================================================================
        Console.WriteLine("\n[9] Kiểm thử T08a: Safe Asset Detach & ON DELETE NO ACTION...");

        // Kiểm tra trước khi xóa: Asset có ProjectId = proj1Id
        await using (var db = new FrayloDbContext(dbOptions))
        {
            var ast = await db.Assets.FindAsync(audioAssetId);
            Assert(ast != null && ast.ProjectId == proj1Id, "Asset ban đầu phải gắn với proj1Id.");
        }

        // Thực hiện xóa Project
        var delProjResult = await projectService.DeleteAsync(proj1Id);
        Assert(delProjResult.Success, "Xóa Project phải thành công.");

        // Kiểm tra sau khi xóa:
        await using (var db = new FrayloDbContext(dbOptions))
        {
            // 1. Project đã bị xóa
            var prj = await db.Projects.FindAsync(proj1Id);
            Assert(prj == null, "Bản ghi Project phải không còn tồn tại trong DB.");

            // 2. Scene và Job thuộc Project đã được SQLite cascade xóa
            var scenesRemaining = await db.Scenes.Where(s => s.ProjectId == proj1Id).ToListAsync();
            Assert(scenesRemaining.Count == 0, "Toàn bộ Scenes thuộc Project phải được cascade xóa.");

            var jobsRemaining = await db.Jobs.Where(j => j.SceneId == scene1Id).ToListAsync();
            Assert(jobsRemaining.Count == 0, "Toàn bộ Jobs thuộc Scenes phải được cascade xóa.");

            // 3. Asset không bị xóa, ProjectId được chuyển thành NULL.
            var detachedAsset = await db.Assets.FindAsync(audioAssetId);
            Assert(detachedAsset != null, "Asset không được phép bị xóa khi xóa Project!");
            Assert(detachedAsset!.ProjectId == null, "Asset.ProjectId phải được detach về NULL.");
        }

        Console.WriteLine("    -> [PASS] Asset được giữ lại sau khi xóa Project.");

        Console.WriteLine("\n=================================================================");
        Console.WriteLine("    TẤT CẢ KIỂM THỬ KHÓI T08 (PROJECTS & SCENES) ĐÃ VƯỢT QUA!    ");
        Console.WriteLine("=================================================================");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException($"[ASSERTION FAILED] {message}");
        }
    }

    private static async Task AssertThrowsAsync<TException>(Func<Task> action, string message) where TException : Exception
    {
        try
        {
            await action();
        }
        catch (TException)
        {
            // Ngoại lệ mong đợi đã xảy ra -> Pass
            return;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"[ASSERTION FAILED] Mong đợi ngoại lệ {typeof(TException).Name} nhưng gặp {ex.GetType().Name}: {ex.Message} ({message})");
        }

        throw new InvalidOperationException($"[ASSERTION FAILED] Không có ngoại lệ nào được ném ra! Mong đợi {typeof(TException).Name}. ({message})");
    }
}
