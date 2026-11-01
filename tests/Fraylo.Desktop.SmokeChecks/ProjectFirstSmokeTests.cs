using System.IO;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Fraylo.Application.Projects;
using Fraylo.Application.Scenes;
using Fraylo.Contracts.Bridge;
using Fraylo.Contracts.Projects;
using Fraylo.Desktop.Bridge;
using Fraylo.Desktop.Bridge.Handlers;
using Fraylo.Infrastructure.Persistence;

namespace Fraylo.Desktop.SmokeChecks;

public static class ProjectFirstSmokeTests
{
    public static async Task<int> RunAllAsync()
    {
        var tempDirectory = Path.Combine(Path.GetTempPath(), "titoai-project-first-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        var databasePath = Path.Combine(tempDirectory, "project-first.db");
        var options = new DbContextOptionsBuilder<FrayloDbContext>()
            .UseSqlite(DatabasePathHelper.BuildConnectionString(databasePath))
            .Options;

        try
        {
            await using (var db = new FrayloDbContext(options))
            {
                await db.Database.EnsureCreatedAsync();
            }

            var service = new ProjectService(options);
            var firstId = (await service.CreateAsync(new ProjectCreateRequest("Video đầu tiên", null))).ProjectId;

            var secondId = (await service.CreateAsync(new ProjectCreateRequest("Video thứ hai", null))).ProjectId;
            var allProjects = await service.ListAllAsync();

            Assert(allProjects.Count == 2, "project.list phải liệt kê toàn bộ Project.");
            Assert(allProjects.Select(p => p.ProjectId).ToHashSet().SetEquals(new[] { firstId, secondId }),
                "Danh sách toàn cục phải chứa đúng hai Project đã tạo.");
            Assert(allProjects.All(p => !p.HasVideo), "Project mới chưa có video đầu ra.");

            var dispatcher = new BridgeDispatcher();
            new ProjectHandlers(service).Register(dispatcher);
            new SceneHandlers(new SceneService(options)).Register(dispatcher);
            Assert(dispatcher.IsCommandAllowed("project.list"), "Project-first command phải được đăng ký.");

            using var payloadDocument = JsonDocument.Parse("{}");
            var request = new RequestEnvelope(
                Guid.NewGuid(),
                "request",
                "project.list",
                payloadDocument.RootElement.Clone(),
                DateTimeOffset.UtcNow,
                BridgeContract.Version);
            var response = await dispatcher.DispatchAsync(request, CancellationToken.None);
            Assert(response.Success, "project.list bridge command phải thành công.");
            Assert(response.Data.HasValue, "project.list response phải có dữ liệu.");
            var responseData = JsonSerializer.Deserialize<ProjectListResponseData>(response.Data?.GetRawText() ?? throw new InvalidOperationException("project.list response data missing."), new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });
            Assert(responseData?.Projects.Count == 2, "project.list phải trả đủ hai Project.");

            async Task<ResponseEnvelope> DispatchProjectAsync(string command, string json)
            {
                using var document = JsonDocument.Parse(json);
                return await dispatcher.DispatchAsync(new RequestEnvelope(
                    Guid.NewGuid(), "request", command, document.RootElement.Clone(),
                    DateTimeOffset.UtcNow, BridgeContract.Version), CancellationToken.None);
            }

            var createScene = await DispatchProjectAsync("scene.create", $$"""{"projectId":"{{firstId}}","name":"Cảnh thử","targetDurationSeconds":5}""");
            Assert(createScene.Success, "scene.create phải hoạt động.");
            var scenes = await DispatchProjectAsync("scene.list_by_project", $$"""{"projectId":"{{firstId}}"}""");
            Assert(scenes.Success, "scene.list_by_project phải hoạt động.");
            var invalidScenes = await DispatchProjectAsync("scene.list_by_project", "{}");
            Assert(!invalidScenes.Success, "scene.list_by_project phải từ chối request thiếu projectId.");

            var get = await DispatchProjectAsync("project.get", $$"""{"projectId":"{{firstId}}"}""");
            Assert(get.Success, "project.get phải hoạt động.");
            var update = await DispatchProjectAsync("project.update", $$"""{"projectId":"{{firstId}}","name":"Đã sửa","description":""}""");
            Assert(update.Success, "project.update phải hoạt động.");
            var delete = await DispatchProjectAsync("project.delete", $$"""{"projectId":"{{secondId}}"}""");
            Assert(delete.Success, "project.delete phải hoạt động.");
            var missingId = await DispatchProjectAsync("project.get", "{}");
            Assert(!missingId.Success, "project.get phải từ chối request thiếu projectId.");
            Console.WriteLine("[PASS] Project create/list and Project-scoped scene operations work.");
            await ProjectGroupSmokeTests.RunAsync(options);
            return 0;
        }
        finally
        {
            try
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
            catch
            {
                // SQLite may still hold a Windows file handle until disposal completes.
            }
        }
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
