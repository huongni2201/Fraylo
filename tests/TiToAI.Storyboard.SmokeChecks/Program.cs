using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using TiToAI.Application.Content;
using TiToAI.Application.Providers;
using TiToAI.Infrastructure.Persistence;
using TiToAI.Infrastructure.Persistence.Entities;

const string valid = """
{"durationSeconds":15,"shots":[
  {"startSeconds":0,"endSeconds":5,"description":"Giới thiệu","imagePrompt":"Toàn thân"},
  {"startSeconds":5,"endSeconds":15,"description":"Chi tiết","imagePrompt":"Cận áo"}
]}
""";

var storyboard = StoryboardContentService.Parse(valid, 15);
if (storyboard.Shots.Count != 2) throw new Exception("Thiếu phân cảnh");
foreach (var invalid in new[] { valid.Replace("\"startSeconds\":5", "\"startSeconds\":6"), valid.Replace("\"endSeconds\":15", "\"endSeconds\":14"), valid.Replace("\"description\":\"Chi tiết\"", "\"description\":\"\""), "{ không phải JSON" })
{
    try { StoryboardContentService.Parse(invalid, 15); throw new Exception("Đã chấp nhận storyboard sai"); }
    catch (ProjectWorkflowValidationException) { }
}
// Luồng một video: imagePrompt không bắt buộc; thời lượng AI đặt lệch trong ±2 giây được chấp nhận, quá dung sai bị từ chối.
if (StoryboardContentService.Parse(valid.Replace(",\"imagePrompt\":\"Toàn thân\"", ""), 15).Shots.Count != 2) throw new Exception("Bắt buộc imagePrompt của đoạn");
if (StoryboardContentService.Parse(valid.Replace("\"durationSeconds\":15", "\"durationSeconds\":17").Replace("\"endSeconds\":15", "\"endSeconds\":17"), 15).Shots.Count != 2) throw new Exception("Từ chối thời lượng trong dung sai");
try { StoryboardContentService.Parse(valid, 18); throw new Exception("Chấp nhận thời lượng ngoài dung sai"); }
catch (ProjectWorkflowValidationException) { }

await using var connection = new SqliteConnection("Data Source=:memory:");
await connection.OpenAsync();
var options = new DbContextOptionsBuilder<TiToAIDbContext>().UseSqlite(connection).Options;
await using (var db = new TiToAIDbContext(options))
{
    await db.Database.EnsureCreatedAsync();
    db.Projects.Add(new Project { Id = "26fd4c1a-e393-4e52-bc3c-7339a2ef9abd", Name = "Test", CreatedAtUtc = "2026-01-01", UpdatedAtUtc = "2026-01-01" });
    await db.SaveChangesAsync();
}
var service = new StoryboardContentService(options);
await service.SaveAsync("26fd4c1a-e393-4e52-bc3c-7339a2ef9abd", valid, 15);
if ((await service.GetAsync("26fd4c1a-e393-4e52-bc3c-7339a2ef9abd"))?.Shots.Count != 2)
    throw new Exception("Không tải được storyboard");
try { await service.SaveAsync("26fd4c1a-e393-4e52-bc3c-7339a2ef9abd", valid.Replace("\"description\":\"Chi tiết\"", "\"description\":\"\""), 15); throw new Exception("Đã lưu nội dung sai"); }
catch (ProjectWorkflowValidationException) { }
var missingImage = valid.Replace("\"imagePrompt\":\"Cận áo\"", "\"imagePrompt\":\"Cận áo\",\"imageAssetId\":\"missing\"");
try { await service.SaveAsync("26fd4c1a-e393-4e52-bc3c-7339a2ef9abd", missingImage, 15); throw new Exception("Đã lưu ảnh không tồn tại"); }
catch (ProjectWorkflowValidationException) { }
await using (var db = new TiToAIDbContext(options))
{
    var saved = await db.AppSettings.SingleAsync(x => x.Key == "project.storyboard.26fd4c1a-e393-4e52-bc3c-7339a2ef9abd");
    if (StoryboardContentService.Parse(saved.Value, 15).Shots.Count != 2) throw new Exception("Không đọc lại được storyboard");
}
// MuseAI recovery may reopen only a muse.ai side chat saved by the render job.
const string sideChat = "https://muse.ai/thread/b23f917c-44ad-4421-a80d-3824902a034a";
if (!MuseAiPlaywrightAutomation.IsSideChatUrl(sideChat)) throw new Exception("Từ chối side chat MuseAI hợp lệ");
foreach (var invalid in new string?[] { null, "", "https://muse.ai/thread/new", "http://muse.ai/thread/b23f917c-44ad-4421-a80d-3824902a034a", "https://evil.example/thread/b23f917c-44ad-4421-a80d-3824902a034a" })
{
    if (MuseAiPlaywrightAutomation.IsSideChatUrl(invalid)) throw new Exception($"Chấp nhận URL không hợp lệ: {invalid}");
}
Console.WriteLine("Storyboard content checks passed");
