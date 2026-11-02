using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Fraylo.Application.Prompts;
using Fraylo.Application.Prompts.Models;
using Fraylo.Contracts.Bridge;
using Fraylo.Contracts.Prompts;
using Fraylo.Desktop.Bridge;
using Fraylo.Desktop.Bridge.Handlers;
using Fraylo.Infrastructure.Persistence;

namespace Fraylo.Desktop.SmokeChecks;

/// <summary>Smoke checks cho prompt template toàn ứng dụng, preview, snapshot và bridge contract.</summary>
public static class PromptEngineSmokeTests
{
    private sealed class SqliteTestDb : IAsyncDisposable, IDisposable
    {
        public SqliteConnection Connection { get; }
        public DbContextOptions<FrayloDbContext> Options { get; }

        private SqliteTestDb(SqliteConnection connection, DbContextOptions<FrayloDbContext> options)
        {
            Connection = connection;
            Options = options;
        }

        public static async Task<SqliteTestDb> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var options = new DbContextOptionsBuilder<FrayloDbContext>().UseSqlite(connection).Options;
            await using var db = new FrayloDbContext(options);
            await db.Database.EnsureCreatedAsync();
            return new SqliteTestDb(connection, options);
        }

        public void Dispose() => Connection.Dispose();
        public ValueTask DisposeAsync() => Connection.DisposeAsync();
    }

    private static void Assert([DoesNotReturnIf(false)] bool condition, string message)
    {
        if (!condition) throw new Exception($"[ASSERTION FAILED] {message}");
    }

    public static void Test_RendererHandlesVariablesAndEscaping()
    {
        const string template = "Xin chào {channel_name}! Chủ đề: {topic}; cú pháp: {{literal}}.";
        var (variables, malformed) = PromptRenderer.ParseTemplate(template);
        Assert(malformed.Count == 0, "Template phải hợp lệ.");
        Assert(variables.SequenceEqual(new[] { "channel_name", "topic" }), "Danh sách biến phải đúng và ổn định.");
        var rendered = PromptRenderer.Render(template, new Dictionary<string, string>
        {
            ["channel_name"] = "Fraylo",
            ["topic"] = "Công nghệ",
        });
        Assert(rendered == "Xin chào Fraylo! Chủ đề: Công nghệ; cú pháp: {literal}.", "Render và escape phải chính xác.");
    }

    public static async Task Test_MultipleTemplatesAndActiveSelection()
    {
        await using var fixture = await SqliteTestDb.CreateAsync();
        await using var db = new FrayloDbContext(fixture.Options);
        var service = new PromptTemplateService(db);
        var first = await service.SaveTemplateAsync(new PromptSaveTemplateRequest("Script", "Bản 1: {topic}", "Mẫu 1", null));
        var second = await service.SaveTemplateAsync(new PromptSaveTemplateRequest("Script", "Bản 2: {topic}", "Mẫu 2", null));
        var image = await service.SaveTemplateAsync(new PromptSaveTemplateRequest("Image", "Tạo ảnh", "Ảnh", null));
        Assert(first.Template.IsActive && !second.Template.IsActive && image.Template.IsActive, "Mẫu đầu tiên của mỗi loại phải tự được chọn.");
        var snapshot = await service.CreateSnapshotAsync(null, "Script", new Dictionary<string, string> { ["topic"] = "AI" });
        await service.ActivateTemplateAsync(second.Template.TemplateId);
        var list = await service.ListTemplatesAsync();
        var loaded = await service.GetTemplateAsync(null, "Script");

        Assert(list.Templates.Count == 3 && list.Templates.Count(t => t.Purpose == "Script" && t.IsActive) == 1, "Mỗi loại chỉ có một mẫu đang dùng.");
        Assert(loaded.Template.TemplateId == second.Template.TemplateId && loaded.Content.TemplateText == "Bản 2: {topic}", "Get theo purpose phải trả mẫu đã chọn.");
        Assert(snapshot.RenderedText == "Bản 1: AI", "Đổi mẫu không được thay đổi snapshot trước đó.");
        Assert((await service.GetTemplateAsync(null, "Image")).Template.TemplateId == image.Template.TemplateId, "Chọn Script không được đổi mẫu Image.");
    }

    public static async Task Test_PreviewAndSnapshotAreIndependentOfLaterEdits()
    {
        await using var fixture = await SqliteTestDb.CreateAsync();
        await using var db = new FrayloDbContext(fixture.Options);
        var service = new PromptTemplateService(db);
        var saved = await service.SaveTemplateAsync(new PromptSaveTemplateRequest(
            "Script", "Viết về {topic}, độ dài {duration}", "Mẫu", null));
        const string definitions = "[{\"name\":\"topic\"},{\"name\":\"duration\",\"defaultValue\":\"30s\"}]";
        var stored = await db.PromptTemplates.SingleAsync();
        stored.VariableDefinitionsJson = definitions;
        await db.SaveChangesAsync();
        var snapshot = await service.CreateSnapshotAsync(saved.Template.TemplateId, null, new Dictionary<string, string> { ["topic"] = "AI" });
        var preview = await service.PreviewAsync(new PromptPreviewRequest(
            saved.Template.TemplateId, null, null, new Dictionary<string, string> { ["topic"] = "AI" }));
        await service.SaveTemplateAsync(new PromptSaveTemplateRequest("Script", "Đã sửa: {topic}", "Mẫu", saved.Template.TemplateId));

        Assert(preview.RenderedText == "Viết về AI, độ dài 30s", "Preview phải dùng default value.");
        Assert(snapshot.RenderedText == "Viết về AI, độ dài 30s", "Snapshot phải giữ rendered text đã chụp.");
        Assert((await service.GetTemplateAsync(null, "Script")).Content.VariableDefinitionsJson == definitions,
            "Lưu nội dung phải giữ định nghĩa biến do backend quản lý trong database.");
    }

    public static async Task Test_BridgeExposesOnlyCurrentPromptCommands()
    {
        await using var fixture = await SqliteTestDb.CreateAsync();
        var dispatcher = new BridgeDispatcher();
        var handlers = new PromptHandlers(fixture.Options);
        handlers.Register(dispatcher);

        foreach (var command in new[] { "prompt.list_templates", "prompt.get_template", "prompt.save_template", "prompt.activate_template", "prompt.preview" })
            Assert(dispatcher.IsCommandAllowed(command), $"{command} phải nằm trong allow-list.");
        var request = new RequestEnvelope(Guid.NewGuid(), "request", "prompt.save_template",
            JsonSerializer.SerializeToElement(new { purpose = "Image", name = "Ảnh", templateText = "{prompt}", variableDefinitionsJson = "invalid" }),
            DateTimeOffset.UtcNow, "1.1.0");
        var response = await dispatcher.DispatchAsync(request, CancellationToken.None);
        Assert(response.Success, "Lưu template qua bridge phải thành công.");
        await using var db = new FrayloDbContext(fixture.Options);
        Assert((await db.PromptTemplates.SingleAsync()).VariableDefinitionsJson == "[]",
            "Bridge không được cho client ghi đè định nghĩa biến.");
        var second = await new PromptTemplateService(db).SaveTemplateAsync(new PromptSaveTemplateRequest("Image", "Ảnh thứ hai", "Mẫu 2", null));
        var activate = new RequestEnvelope(Guid.NewGuid(), "request", "prompt.activate_template",
            JsonSerializer.SerializeToElement(new PromptActivateTemplateRequest(second.Template.TemplateId)), DateTimeOffset.UtcNow, "1.1.0");
        var activated = await dispatcher.DispatchAsync(activate, CancellationToken.None);
        Assert(activated.Success, "Chọn mẫu qua bridge phải thành công.");
        await using var verify = new FrayloDbContext(fixture.Options);
        Assert((await verify.PromptTemplates.SingleAsync(t => t.Purpose == "Image" && t.IsActive)).Id == second.Template.TemplateId,
            "Mẫu đã chọn phải được lưu bền vững.");
        handlers.Unregister(dispatcher);
    }

    public static async Task<int> RunAllTestsAsync()
    {
        var tests = new (string Name, Func<Task> Run)[]
        {
            ("Prompt renderer", () => { Test_RendererHandlesVariablesAndEscaping(); return Task.CompletedTask; }),
            ("Multiple templates and active selection", Test_MultipleTemplatesAndActiveSelection),
            ("Preview and immutable snapshot", Test_PreviewAndSnapshotAreIndependentOfLaterEdits),
            ("Current prompt bridge contract", Test_BridgeExposesOnlyCurrentPromptCommands),
        };
        var failed = 0;
        foreach (var test in tests)
        {
            try { await test.Run(); }
            catch (Exception ex) { failed++; Console.WriteLine($"FAIL {test.Name}: {ex.Message}"); }
        }
        return failed;
    }
}
