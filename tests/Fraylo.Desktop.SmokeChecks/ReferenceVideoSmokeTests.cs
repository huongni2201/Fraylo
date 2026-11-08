using System.Buffers.Binary;
using System.IO;
using System.Text;
using System.Text.Json;
using Fraylo.Application.Assets;
using Fraylo.Application.Content;
using Fraylo.Application.Jobs;
using Fraylo.Application.Settings;
using Fraylo.Contracts.Jobs;
using Fraylo.Contracts.Assets;
using Fraylo.Contracts.Content;
using Fraylo.Infrastructure.Persistence;
using Fraylo.Infrastructure.Persistence.Entities;
using Fraylo.Infrastructure.Storage;
using Microsoft.EntityFrameworkCore;

namespace Fraylo.Desktop.SmokeChecks;

internal static class ReferenceVideoSmokeTests
{
    public static async Task<int> RunAllAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "Fraylo-reference-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var reference = Path.Combine(root, "source.mp4");
            var output = Path.Combine(root, "output.mp4");
            await File.WriteAllBytesAsync(reference, Mp4(16.56, "abcde"));
            await File.WriteAllBytesAsync(output, Mp4(16.56, "abcde"));
            var metadata = ReferenceVideoMedia.ReadInfo(reference);
            Check(Math.Abs(metadata.DurationSeconds - 16.56) < .001 && metadata.Width == 320 && metadata.Height == 480, "MP4 metadata");
            ReferenceVideoAudio.VerifyOutput(reference, output);
            Check(ReferenceVideoAudio.HasAudio(reference), "audio stream detected");
            await File.WriteAllBytesAsync(output, Mp4(16.56, null));
            Reject(() => ReferenceVideoAudio.VerifyOutput(reference, output), "source audio was omitted");
            await File.WriteAllBytesAsync(output, Mp4(10, "abcde"));
            ReferenceVideoAudio.VerifyOutput(reference, output);
            await File.WriteAllBytesAsync(output, Mp4(3, "abc", bitrateHint: 256000));
            ReferenceVideoAudio.VerifyOutput(reference, output);
            await File.WriteAllBytesAsync(output, Mp4(3, "ab", audioSeconds: 1));
            Reject(() => ReferenceVideoAudio.VerifyOutput(reference, output), "audio not fitted to visual duration");
            await File.WriteAllBytesAsync(output, Mp4(3, "abX"));
            ReferenceVideoAudio.VerifyOutput(reference, output); // AI fitting may re-encode; content identity needs user review.
            await File.WriteAllBytesAsync(reference, Mp4(16.56, null));
            await File.WriteAllBytesAsync(output, Mp4(16.56, null));
            ReferenceVideoAudio.VerifyOutput(reference, output);
            Check(!ReferenceVideoAudio.HasAudio(reference), "silent source stays silent");
            await File.WriteAllBytesAsync(output, Mp4(16.56, "abcde"));
            Reject(() => ReferenceVideoAudio.VerifyOutput(reference, output), "added music on silent source");
            await File.WriteAllBytesAsync(output, Mp4(31, null));
            Reject(() => ReferenceVideoMedia.ReadInfo(output), "over duration limit");
            await File.WriteAllTextAsync(output, "not a video");
            Reject(() => ReferenceVideoMedia.ReadInfo(output), "wrong file content");

            var options = new DbContextOptionsBuilder<FrayloDbContext>().UseSqlite($"Data Source={Path.Combine(root, "test.db")}").Options;
            var id = Guid.NewGuid().ToString();
            await using (var db = new FrayloDbContext(options))
            {
                await db.Database.EnsureCreatedAsync();
                db.Projects.Add(new Project { Id = id, Name = "Reference test", CreatedAtUtc = "2026-11-07T00:00:00Z", UpdatedAtUtc = "2026-11-07T00:00:00Z" });
                await db.SaveChangesAsync();
            }
            var roots = new StorageRootRegistry(options, root);
            await roots.EnsureDefaultRootsAsync();
            var assets = new AssetService(options, roots, new AssetFileStore());
            var imported = await assets.UploadAssetAsync(new UploadAssetRequest(ProjectId: id, AssetType: "Video", SourceFilePath: reference));
            Check(imported.Asset?.AssetType == "Video" && imported.Asset.MimeType == "video/mp4", "video imported with correct role");
            Check((await assets.ListAssetsAsync(new ListAssetsRequest(id, "Video"))).Assets.Count == 1, "video available in library");
            await assets.UploadAssetAsync(new UploadAssetRequest(AssetType: "Video", SourceFilePath: reference));
            Check((await assets.ListAssetsAsync(new ListAssetsRequest(id, "Video", IncludeShared: true))).Assets.Count == 2, "shared video available without mixing other projects");
            try { await assets.UploadAssetAsync(new UploadAssetRequest(ProjectId: id, AssetType: "Video", Base64Data: "AAAA")); throw new Exception("base64 video accepted"); }
            catch (AssetValidationException) { }
            var imagePath = Path.Combine(root, "image.jpg");
            await File.WriteAllBytesAsync(imagePath, new byte[] { 255, 216, 255, 224, 0, 1, 2, 3, 4, 5, 6, 7 });
            var image = await assets.UploadAssetAsync(new UploadAssetRequest(id, "Image", SourceFilePath: imagePath));
            var settings = new SettingsService(options, roots);
            var snapshotFactory = new JobSnapshotFactory(settings, dbOptions: options);
            var scheduler = new JobScheduler(options, new JobReservation(options), new NotExecutedExecutor());
            var workflow = new ProjectWorkflowService(options, new JobService(options, snapshotFactory, scheduler));
            var saved = await workflow.SaveWorkflowConfigAsync(new SaveProjectWorkflowConfigRequest(id, WorkflowModes.ReferenceVideo, "", null, null,
                image.Asset!.AssetId, AudioOptions.None, null, 1, 20, VideoStrategy: VideoStrategies.SingleVideo, ReferenceVideoAssetId: imported.Asset!.AssetId));
            Check(saved.ReferenceVideoAssetId == imported.Asset.AssetId, "video draft saved");
            var loaded = await workflow.GetWorkflowConfigAsync(id);
            Check(loaded.ReferenceVideoAvailable, "silent reference supports approximate generation with different duration");
            var started = await workflow.StartWorkflowAsync(new StartProjectWorkflowRequest(id, "reference-start"));
            Check(started.RunState == WorkflowRunState.Running, "reference workflow starts without Script/Image/VideoPrompt templates");
            var replay = await workflow.StartWorkflowAsync(new StartProjectWorkflowRequest(id, "reference-start"));
            Check(replay.RunId == started.RunId, "double click does not create another run");
            JobMediaSnapshotDto media;
            string jobId;
            await using (var db = new FrayloDbContext(options))
            {
                var job = await db.Jobs.SingleAsync(); jobId = job.Id;
                Check(job.TaskType == JobTaskType.VideoRender && await db.Scenes.CountAsync() == 1, "one render and one internal scene");
                using var snapshot = JsonDocument.Parse(job.ConfigSnapshotJson!);
                media = JsonSerializer.Deserialize<JobMediaSnapshotDto>(snapshot.RootElement.GetProperty("MediaSnapshot").GetRawText())!;
                Check(media.ReferenceVideoAssetId == imported.Asset.AssetId && media.ReferenceVideoAssetHash is not null && media.SourceImageAssetHash is not null && media.DurationSeconds == 20 && media.ReferenceVideoDurationSeconds == 16.56,
                    "immutable two-media snapshot preserves source metadata and independent output duration");
            }
            await File.WriteAllBytesAsync(output, Mp4(10, null));
            var candidate = await assets.UploadAssetAsync(new UploadAssetRequest(id, "Video", SourceFilePath: output));
            await using (var db = new FrayloDbContext(options))
            {
                var job = await db.Jobs.SingleAsync();
                job.State = JobState.Succeeded; job.ResultReference = candidate.Asset!.AssetId;
                job.ResultText = "{\"reference_mode\":\"PromptApproximation\",\"video_prompt\":\"Actual motion prompt\",\"tiktok_content\":\"Caption\"}";
                await db.SaveChangesAsync();
            }
            Check(await workflow.ApplyJobOutputAsync(jobId), "apply completed reference render");
            Check((await workflow.GetWorkflowConfigAsync(id)).RunState == WorkflowRunState.Completed, "different duration can complete workflow");
            await using (var db = new FrayloDbContext(options))
                Check((await db.VideoRevisions.SingleAsync()).ProvenanceJson!.Contains("FitOriginalAudioByAI"), "provenance records approximate motion and AI audio policy");
            await workflow.CreateSceneVideoAsync(new CreateSceneVideoRequest(id));
            await using (var db = new FrayloDbContext(options))
            {
                var rerender = await db.Jobs.SingleAsync(j => j.State == JobState.Queued);
                using var snapshot = JsonDocument.Parse(rerender.ConfigSnapshotJson!);
                var rerenderMedia = JsonSerializer.Deserialize<JobMediaSnapshotDto>(snapshot.RootElement.GetProperty("MediaSnapshot").GetRawText())!;
                Check(snapshot.RootElement.GetProperty("ConfigVersion").GetInt32() == 3 &&
                    rerenderMedia.ReferenceVideoAssetId == imported.Asset.AssetId && rerenderMedia.ReferenceVideoAssetHash == media.ReferenceVideoAssetHash &&
                    rerenderMedia.ReferenceVideoMode == ReferenceVideoMedia.ApproximationMode && rerenderMedia.ReferenceAudioPolicy == ReferenceVideoAudio.FitByAi &&
                    rerenderMedia.DurationSeconds == 20 && rerender.PromptSnapshotText!.Contains("original audio"),
                    "manual rerender preserves reference, audio policy, hashes and generation instructions");
                Check((await workflow.GetWorkflowConfigAsync(id)).RunState == WorkflowRunState.Running, "completed run becomes dispatchable for manual rerender");
                db.BrowserProfiles.Add(new() { Id = "reference-fixture", Kind = "MuseAI", ProviderKey = "museai", AccountIdentity = "Fixture", SessionStatus = "Connected" });
                db.ProviderAccounts.Add(new() { Id = "reference-account", ProfileId = "reference-fixture", ProviderKey = "museai", Enabled = true });
                await db.SaveChangesAsync();
                var reservation = new JobReservation(options);
                Check((await reservation.TryReserveJobAsync(rerender.Id, "fixture", TimeSpan.FromSeconds(45))).Acquired, "scheduler can reserve manual rerender after completed run");
                await db.Entry(rerender).ReloadAsync();
                await reservation.ReleaseOccupancyAsync(rerender.Id);
                rerender.State = JobState.Succeeded; rerender.ResultReference = candidate.Asset!.AssetId;
                rerender.ResultText = "{\"reference_mode\":\"PromptApproximation\",\"video_prompt\":\"Rerender motion\"}";
                await db.SaveChangesAsync();
                jobId = rerender.Id;
            }
            Check(await workflow.ApplyJobOutputAsync(jobId), "manual reference rerender applies to video history");
            var musicPath = Path.Combine(root, "music.mp4");
            await File.WriteAllBytesAsync(musicPath, Mp4(5.055, "music"));
            var music = await assets.UploadAssetAsync(new UploadAssetRequest(id, "Video", SourceFilePath: musicPath));
            await workflow.SaveWorkflowConfigAsync(new SaveProjectWorkflowConfigRequest(id, WorkflowModes.ReferenceVideo, "", null, null,
                image.Asset.AssetId, AudioOptions.None, null, 1, 10, VideoStrategy: VideoStrategies.SingleVideo, ReferenceVideoAssetId: music.Asset!.AssetId));
            var musicStart = await workflow.StartWorkflowAsync(new StartProjectWorkflowRequest(id, "music-start"));
            Check(musicStart.RunState == WorkflowRunState.Running, "source with audio starts with AI fitting capability");
            await using (var db = new FrayloDbContext(options))
            {
                var musicJob = await db.Jobs.SingleAsync(j => j.State == JobState.Queued);
                using var snapshot = JsonDocument.Parse(musicJob.ConfigSnapshotJson!);
                var musicMedia = JsonSerializer.Deserialize<JobMediaSnapshotDto>(snapshot.RootElement.GetProperty("MediaSnapshot").GetRawText())!;
                Check(musicMedia.ReferenceVideoHasAudio == true && musicMedia.ReferenceAudioPolicy == ReferenceVideoAudio.FitByAi, "audio source and AI fitting policy are frozen");
                Check((await db.Scenes.SingleAsync()).VideoAssetId == null && await db.VideoRevisions.CountAsync() == 2, "new run clears current scene output, preserves revision history");
            }
            var importedFile = StoragePathGuard.CombineAndVerify((await roots.GetCurrentRootsAsync()).VideosPath, imported.Asset.RelativePath);
            await File.AppendAllTextAsync(importedFile, "changed after snapshot");
            try
            {
                await snapshotFactory.CreateSnapshotAsync(new JobEnqueueRequest(id, null, JobTaskType.VideoRender, CustomPromptText: "test", MediaSnapshot: media));
                throw new Exception("modified reference was accepted");
            }
            catch (JobValidationException) { }
            Check(!WorkflowModes.IsValid(WorkflowModes.ProductOnly) && WorkflowModes.All.Count == 3, "three current workflows");
            Console.WriteLine("ReferenceVideo: metadata, AI-fitted audio duration, silence, independent video duration, import, shared Library, snapshots, duplicate Start, pipeline and provenance PASS.");
            return 0;
        }
        catch (Exception ex) { Console.WriteLine(ex); return 1; }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            var fullRoot = Path.GetFullPath(root);
            if (Path.GetDirectoryName(fullRoot) == Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) && Path.GetFileName(fullRoot).StartsWith("Fraylo-reference-", StringComparison.Ordinal))
                try { Directory.Delete(fullRoot, true); } catch { }
        }
    }

    private static void Check(bool ok, string scenario) { if (!ok) throw new Exception(scenario); }
    private static void Reject(Action action, string scenario)
    {
        try { action(); } catch (AssetValidationException) { return; }
        throw new Exception("Expected rejection: " + scenario);
    }

    // Structural fixtures test the header/sample verifier, not media decoding or visual quality.
    internal static byte[] Mp4(double seconds, string? audio, uint bitrateHint = 0, double? audioSeconds = null)
    {
        byte[] Join(params byte[][] parts) => parts.SelectMany(x => x).ToArray();
        byte[] Num(uint n) { var b = new byte[4]; BinaryPrimitives.WriteUInt32BigEndian(b, n); return b; }
        byte[] Box(string type, params byte[][] data) { var bytes = Join(data); return Join(Num((uint)bytes.Length + 8), Encoding.ASCII.GetBytes(type), bytes); }
        var ftyp = Box("ftyp", Encoding.ASCII.GetBytes("isom"), new byte[8]);
        var mdat = Box("mdat", Encoding.ASCII.GetBytes(audio ?? "video"));
        var mvhd = new byte[20]; Num(1000).CopyTo(mvhd, 12); Num((uint)Math.Round(seconds * 1000)).CopyTo(mvhd, 16);
        var tkhd = new byte[84]; Num(320 << 16).CopyTo(tkhd, 76); Num(480 << 16).CopyTo(tkhd, 80);
        byte[] Handler(string type) => Join(new byte[8], Encoding.ASCII.GetBytes(type));
        var video = Box("trak", Box("tkhd", tkhd), Box("mdia", Box("hdlr", Handler("vide"))));
        byte[] sound = [];
        if (audio is not null)
        {
            byte[] Descriptor(byte tag, byte[] data) => Join(new[] { tag, (byte)data.Length }, data);
            var config = Descriptor(4, Join(new byte[] { 64, 21, 0, 0, 0 }, Num(bitrateHint), Num(bitrateHint), Descriptor(5, new byte[] { 17, 144 })));
            var esds = Box("esds", new byte[4], Descriptor(3, Join(new byte[3], config, Descriptor(6, new byte[] { 2 }))));
            var sampleHeader = new byte[28]; sampleHeader[7] = 1; sampleHeader[17] = 2; sampleHeader[19] = 16; Num(48000u << 16).CopyTo(sampleHeader, 24);
            var mdhd = new byte[20]; Num(1000).CopyTo(mdhd, 12); Num((uint)Math.Round((audioSeconds ?? seconds) * 1000)).CopyTo(mdhd, 16);
            var stbl = Box("stbl", Box("stsz", new byte[4], Num(1), Num((uint)audio.Length)),
                Box("stco", new byte[4], Num(1), Num((uint)ftyp.Length + 8)),
                Box("stsc", new byte[4], Num(1), Num(1), Num((uint)audio.Length), Num(1)),
                Box("stts", new byte[4], Num(1), Num((uint)audio.Length), Num(1000)), Box("stsd", new byte[4], Num(1), Box("mp4a", sampleHeader, esds)));
            sound = Box("trak", Box("mdia", Box("hdlr", Handler("soun")), Box("mdhd", mdhd), Box("minf", stbl)));
        }
        return Join(ftyp, mdat, Box("moov", Box("mvhd", mvhd), video, sound));
    }

    private sealed class NotExecutedExecutor : IJobExecutor
    {
        public Task<JobExecutionResult> ExecuteAsync(JobExecutionContext context, Func<Task> beforeSubmit,
            Func<int, string, Task>? progress, CancellationToken cancellationToken) => throw new InvalidOperationException("Scheduler is not started in this test.");
    }
}
