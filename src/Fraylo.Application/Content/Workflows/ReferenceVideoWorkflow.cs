using Fraylo.Application.Assets;
using Fraylo.Contracts.Content;
using Fraylo.Contracts.Jobs;

namespace Fraylo.Application.Content.Workflows;

internal sealed class ReferenceVideoWorkflow : IVideoWorkflow
{
    public void ValidateInputs(ProjectWorkflowConfigDto config)
    {
        if (string.IsNullOrWhiteSpace(config.ReadyImageAssetId) || string.IsNullOrWhiteSpace(config.ReferenceVideoAssetId))
            throw new ProjectWorkflowValidationException("Làm theo video mẫu yêu cầu cả ảnh người mẫu mặc sản phẩm và video mẫu.");
        if (config.VideoStrategy != VideoStrategies.SingleVideo || config.ReferenceImageMode != ReferenceImageModes.Direct ||
            config.TargetSceneCount != 1 || config.AudioOption != AudioOptions.None)
            throw new ProjectWorkflowValidationException("Video theo mẫu chỉ tạo một video dùng ảnh trực tiếp, không gửi tệp audio riêng.");
        if (!config.ReferenceVideoAvailable)
            throw new ProjectWorkflowValidationException(config.ReferenceVideoUnavailableReason ?? "MuseAI chưa xác minh giữ đoạn âm thanh gốc của video mẫu.");
    }

    public WorkflowImagePlan PlanImages(ProjectWorkflowConfigDto config) => new(
        CommonImageAssetId: config.ReadyImageAssetId,
        InitialSceneImageAssetId: config.ReadyImageAssetId,
        GenerationPromptPurpose: null,
        CreateSceneBeforePlanning: true,
        RequiresPromptTemplates: false);

    public Task<JobEnqueueRequest?> CreateNextSceneJobAsync(
        WorkflowSceneContext context, WorkflowSceneJobs jobs, CancellationToken cancellationToken)
        => Task.FromResult<JobEnqueueRequest?>(CreateRenderRequest(context));

    internal static JobEnqueueRequest CreateRenderRequest(WorkflowSceneContext context)
    {
        var durationSeconds = context.Config.TargetDurationPerSceneSeconds;
        var prompt = $"""
            Create ONE approximate motion-reference video. Direct motion transfer is NOT required.
            Input 1 is the PHOTO of the person wearing the product. Preserve that person's face, hair and visible outfit details, colors and shape.
            Input 2 is the VIDEO movement example. Inspect its actual walking, posing and turning actions and their order, then derive a concise English motion prompt for your still-image + text video tool.
            Use the PHOTO and that derived prompt to create one complete playable video, targeting about {durationSeconds} seconds. A different duration from the reference is explicitly accepted. Approximate the main motion sequence in its observed order, compressing it if necessary. Do not extend, loop or freeze the imagery to force matching duration; never use the reference person's actual frames as output.
            Extract the reference VIDEO's original audio and merge it into the GENERATED video yourself. Automatically fit that original audio to the actual generated video duration: time-stretch/compress it as needed, preserve the tune/voice and natural pitch as much as possible, with sensible trimming/fades when needed. Re-encoding is permitted. Do not compose or substitute a different track. Do not change the generated visual speed just to match audio. If the reference has no audio track, return a silent video without added audio. Do not copy the reference person's face, clothing or captions. Do not return a slideshow or a placeholder.
            Proceed with generation. In the final reply include the playable video and the actual derived prompt between MOTION_PROMPT_BEGIN and MOTION_PROMPT_END, plus a short motion summary. Also suggest a Vietnamese TikTok caption between TIKTOK_CONTENT_BEGIN and TIKTOK_CONTENT_END, without inventing product claims, prices or promotions.
            If a tool is unavailable or generation fails, state the reason honestly. Do not use paid external services, make purchases, publish or send messages elsewhere.
            """;
        if (!string.IsNullOrWhiteSpace(context.Config.GoalPrompt)) prompt += "\nUser goal: " + context.Config.GoalPrompt;
        if (!string.IsNullOrWhiteSpace(context.Config.VideoGuidance)) prompt += "\nAdditional user guidance: " + context.Config.VideoGuidance;
        var imageId = context.Config.ReadyImageAssetId!;
        var media = context.MediaSnapshot with
        {
            ModelAssetId = null,
            ProductAssetId = null,
            SourceImageAssetId = imageId,
            ImageAssetIds = new[] { imageId },
            ReferenceVideoAssetId = context.Config.ReferenceVideoAssetId,
            ReferenceVideoMode = ReferenceVideoMedia.ApproximationMode,
            ReferenceAudioPolicy = ReferenceVideoAudio.FitByAi,
            DurationSeconds = durationSeconds,
            AudioAssetId = null,
            AudioOption = AudioOptions.None,
            MediaRoles = new Dictionary<string, string> { ["SourceImage"] = imageId, ["MotionReferenceVideo"] = context.Config.ReferenceVideoAssetId! }
        };
        return new JobEnqueueRequest(
            context.ProjectId, context.Scene.Id, JobTaskType.VideoRender, CustomPromptText: prompt, MediaSnapshot: media,
            WorkflowRunId: context.Config.CurrentRunId, WorkflowStepKey: $"render.{context.Scene.Id}.{context.Scene.UpdatedAtUtc}");
    }
}
