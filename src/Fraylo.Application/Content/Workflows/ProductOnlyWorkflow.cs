using Fraylo.Contracts.Content;
using Fraylo.Contracts.Jobs;

namespace Fraylo.Application.Content.Workflows;

internal sealed class ProductOnlyWorkflow : IVideoWorkflow
{
    public void ValidateInputs(ProjectWorkflowConfigDto config)
    {
        throw new ProjectWorkflowValidationException("Chế độ Chỉ sản phẩm cũ đã được thay thế. Hãy chọn một chế độ hiện hành và lưu trước khi chạy.");
    }

    public WorkflowImagePlan PlanImages(ProjectWorkflowConfigDto config) => new(
        CommonImageAssetId: config.ProductAssetId,
        InitialSceneImageAssetId: config.VideoStrategy == VideoStrategies.SingleVideo ? config.ProductAssetId : null,
        GenerationPromptPurpose: config.VideoStrategy == VideoStrategies.MultiVideo ? JobTaskType.Image : null);

    public Task<JobEnqueueRequest?> CreateNextSceneJobAsync(
        WorkflowSceneContext context, WorkflowSceneJobs jobs, CancellationToken cancellationToken) =>
        context.Config.VideoStrategy == VideoStrategies.MultiVideo && context.NeedsImage
            ? jobs.CreateImageJobAsync(context, JobTaskType.Image,
                $"Tạo một ảnh sản phẩm cho cảnh: {context.Scene.Name}. Ảnh đính kèm là sản phẩm cần giữ đúng màu sắc, hình dáng và chi tiết nhìn thấy. {context.ImagePrompt}", cancellationToken)
            : jobs.CreateVideoPreparationJobAsync(context, cancellationToken);
}
