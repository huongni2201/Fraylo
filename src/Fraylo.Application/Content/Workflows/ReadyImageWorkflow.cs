using Fraylo.Contracts.Content;
using Fraylo.Contracts.Jobs;

namespace Fraylo.Application.Content.Workflows;

internal sealed class ReadyImageWorkflow : IVideoWorkflow
{
    public void ValidateInputs(ProjectWorkflowConfigDto config)
    {
        if (string.IsNullOrWhiteSpace(config.ReadyImageAssetId))
        {
            throw new ProjectWorkflowValidationException(
                "Chế độ 'Ảnh đã sẵn sàng' yêu cầu cung cấp Ảnh hoàn thiện (đã mặc/cầm sản phẩm).");
        }
    }

    public WorkflowImagePlan PlanImages(ProjectWorkflowConfigDto config) => new(
        CommonImageAssetId: config.ReadyImageAssetId,
        InitialSceneImageAssetId: config.ReadyImageAssetId,
        GenerationPromptPurpose: null);

    public Task<JobEnqueueRequest?> CreateNextSceneJobAsync(
        WorkflowSceneContext context, WorkflowSceneJobs jobs, CancellationToken cancellationToken) =>
        jobs.CreateVideoPreparationJobAsync(context, cancellationToken);
}
