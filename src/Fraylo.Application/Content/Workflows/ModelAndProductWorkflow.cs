using Fraylo.Contracts.Content;
using Fraylo.Contracts.Jobs;

namespace Fraylo.Application.Content.Workflows;

internal sealed class ModelAndProductWorkflow : IVideoWorkflow
{
    public void ValidateInputs(ProjectWorkflowConfigDto config)
    {
        if (string.IsNullOrWhiteSpace(config.ModelAssetId) || string.IsNullOrWhiteSpace(config.ProductAssetId))
        {
            throw new ProjectWorkflowValidationException(
                "Chế độ 'Mẫu + sản phẩm' yêu cầu cung cấp đầy đủ cả Ảnh mẫu và Ảnh sản phẩm có vai trò rõ ràng.");
        }
    }

    public WorkflowImagePlan PlanImages(ProjectWorkflowConfigDto config) => new(
        CommonImageAssetId: null,
        InitialSceneImageAssetId: null,
        GenerationPromptPurpose: VideoWorkflowPromptComposer.ComposeImagePurpose,
        ComposeBeforePlanning: config.VideoStrategy == VideoStrategies.SingleVideo && !VideoWorkflowPromptComposer.IsGridMode(config));

    public Task<JobEnqueueRequest?> CreateNextSceneJobAsync(
        WorkflowSceneContext context, WorkflowSceneJobs jobs, CancellationToken cancellationToken) =>
        context.NeedsImage
            ? jobs.CreateImageJobAsync(context, VideoWorkflowPromptComposer.ComposeImagePurpose,
                $"Ảnh đính kèm thứ nhất là người mẫu, ảnh thứ hai là sản phẩm. Cảnh: {context.Scene.Name}. {context.ImagePrompt}", cancellationToken)
            : jobs.CreateVideoPreparationJobAsync(context, cancellationToken);
}
