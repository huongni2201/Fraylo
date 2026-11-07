using Fraylo.Contracts.Content;

namespace Fraylo.Application.Content.Workflows;

internal static class VideoWorkflows
{
    private static readonly IVideoWorkflow ModelAndProduct = new ModelAndProductWorkflow();
    private static readonly IVideoWorkflow ReadyImage = new ReadyImageWorkflow();
    private static readonly IVideoWorkflow ProductOnly = new ProductOnlyWorkflow();
    private static readonly IVideoWorkflow ReferenceVideo = new ReferenceVideoWorkflow();

    public static IVideoWorkflow For(string workflowMode) => workflowMode switch
    {
        WorkflowModes.ModelAndProduct => ModelAndProduct,
        WorkflowModes.ReadyImage => ReadyImage,
        WorkflowModes.ProductOnly => ProductOnly,
        WorkflowModes.ReferenceVideo => ReferenceVideo,
        _ => throw new ProjectWorkflowValidationException($"Chế độ '{workflowMode}' không được hỗ trợ.")
    };
}
