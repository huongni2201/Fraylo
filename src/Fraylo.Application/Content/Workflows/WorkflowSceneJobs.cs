using Microsoft.EntityFrameworkCore;
using Fraylo.Application.Prompts;
using Fraylo.Contracts.Content;
using Fraylo.Contracts.Jobs;
using Fraylo.Contracts.Prompts;
using Fraylo.Infrastructure.Persistence;
using Fraylo.Infrastructure.Persistence.Entities;

namespace Fraylo.Application.Content.Workflows;

internal sealed class WorkflowSceneJobs(DbContextOptions<FrayloDbContext> dbOptions)
{
    public const string GridStepPrefix = "grid.";

    public static string GridMarkerKey(string sceneId) => $"scene.grid.{sceneId}";

    public async Task<JobEnqueueRequest?> CreateImageJobAsync(
        WorkflowSceneContext context, string templatePurpose, string instruction, CancellationToken cancellationToken)
    {
        var template = await VideoWorkflowPromptComposer.RequireActiveTemplateAsync(
            context.DbContext, templatePurpose, cancellationToken).ConfigureAwait(false);
        var rendered = (await new PromptTemplateService(context.DbContext).PreviewAsync(
            new PromptPreviewRequest(template.Id, null, null, null), cancellationToken).ConfigureAwait(false)).RenderedText;
        return new JobEnqueueRequest(
            ProjectId: context.ProjectId,
            SceneId: context.Scene.Id,
            TaskType: JobTaskType.Image,
            CustomPromptText: rendered + "\n\n" + instruction,
            WorkflowRunId: context.Config.CurrentRunId,
            WorkflowStepKey: $"image.{context.Scene.Id}.{context.Scene.UpdatedAtUtc}",
            MediaSnapshot: context.MediaSnapshot);
    }

    public async Task<JobEnqueueRequest?> CreateVideoPreparationJobAsync(
        WorkflowSceneContext context, CancellationToken cancellationToken)
    {
        if (await NeedsGridImageAsync(context.DbContext, context.Config, context.Scene, cancellationToken).ConfigureAwait(false))
        {
            return await BuildGridImageRequestAsync(context.DbContext, context.Config, context.Scene, cancellationToken).ConfigureAwait(false);
        }

        if (string.IsNullOrWhiteSpace(context.Scene.ImageAssetId) ||
            (!context.StaleTasks.Contains(JobTaskType.VideoPrompt) && !string.IsNullOrWhiteSpace(context.Scene.PromptText)))
        {
            return null;
        }

        var composition = await new VideoWorkflowPromptComposer(dbOptions).ComposeVideoPromptAiRequestAsync(
            context.Config, context.Scene, cancellationToken, context.ScenePurpose).ConfigureAwait(false);
        return new JobEnqueueRequest(
            ProjectId: context.ProjectId,
            SceneId: context.Scene.Id,
            TaskType: JobTaskType.VideoPrompt,
            CustomPromptText: composition.ComposedPrompt,
            WorkflowRunId: context.Config.CurrentRunId,
            WorkflowStepKey: $"prompt.{context.Scene.Id}.{context.Scene.UpdatedAtUtc}",
            MediaSnapshot: context.MediaSnapshot);
    }

    public static async Task<bool> NeedsGridImageAsync(
        FrayloDbContext dbContext, ProjectWorkflowConfigDto config, Scene scene, CancellationToken cancellationToken)
    {
        if (!VideoWorkflowPromptComposer.IsGridMode(config) || string.IsNullOrWhiteSpace(scene.ImageAssetId)) return false;
        var key = GridMarkerKey(scene.Id);
        var marker = dbContext.AppSettings.Local.FirstOrDefault(setting => setting.Key == key)?.Value
            ?? await dbContext.AppSettings.AsNoTracking().Where(setting => setting.Key == key)
                .Select(setting => setting.Value).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        return !string.Equals(marker, scene.ImageAssetId, StringComparison.Ordinal);
    }

    public async Task<JobEnqueueRequest> BuildGridImageRequestAsync(
        FrayloDbContext dbContext, ProjectWorkflowConfigDto config, Scene scene, CancellationToken cancellationToken)
    {
        var storyboard = await new StoryboardContentService(dbOptions).GetAsync(scene.ProjectId, cancellationToken).ConfigureAwait(false);
        if (storyboard == null || storyboard.Shots.Count != ReferenceImageModes.GridShotCount)
        {
            throw new ProjectWorkflowValidationException("Chưa có storyboard đủ 9 shot nên chưa thể tạo ảnh 3×3.");
        }
        var template = await VideoWorkflowPromptComposer.RequireActiveTemplateAsync(dbContext, JobTaskType.Image, cancellationToken).ConfigureAwait(false);
        var rendered = (await new PromptTemplateService(dbContext).PreviewAsync(
            new PromptPreviewRequest(template.Id, null, null, null), cancellationToken).ConfigureAwait(false)).RenderedText;
        var source = scene.ImageAssetId!;
        return new JobEnqueueRequest(
            ProjectId: scene.ProjectId,
            SceneId: scene.Id,
            TaskType: JobTaskType.Image,
            CustomPromptText: VideoWorkflowPromptComposer.ComposeGridImagePrompt(rendered, storyboard),
            WorkflowRunId: config.CurrentRunId,
            WorkflowStepKey: $"{GridStepPrefix}{scene.Id}.{scene.UpdatedAtUtc}",
            MediaSnapshot: new JobMediaSnapshotDto(
                SourceImageAssetId: source,
                DurationSeconds: config.TargetDurationPerSceneSeconds,
                MediaRoles: new Dictionary<string, string> { ["SourceImage"] = source }));
    }
}
