using Fraylo.Contracts.Content;
using Fraylo.Contracts.Jobs;
using Fraylo.Infrastructure.Persistence;
using Fraylo.Infrastructure.Persistence.Entities;

namespace Fraylo.Application.Content.Workflows;

internal interface IVideoWorkflow
{
    void ValidateInputs(ProjectWorkflowConfigDto config);
    WorkflowImagePlan PlanImages(ProjectWorkflowConfigDto config);
    Task<JobEnqueueRequest?> CreateNextSceneJobAsync(
        WorkflowSceneContext context, WorkflowSceneJobs jobs, CancellationToken cancellationToken);
}

internal sealed record WorkflowImagePlan(
    string? CommonImageAssetId,
    string? InitialSceneImageAssetId,
    string? GenerationPromptPurpose,
    bool ComposeBeforePlanning = false,
    bool CreateSceneBeforePlanning = false,
    bool RequiresPromptTemplates = true);

internal sealed record WorkflowSceneContext(
    FrayloDbContext DbContext,
    string ProjectId,
    ProjectWorkflowConfigDto Config,
    Scene Scene,
    string? ScenePurpose,
    IReadOnlySet<string> StaleTasks,
    JobMediaSnapshotDto MediaSnapshot)
{
    public bool NeedsImage => StaleTasks.Contains(JobTaskType.Image) || string.IsNullOrWhiteSpace(Scene.ImageAssetId);

    public string ImagePrompt => VideoWorkflowPromptComposer.IsGridMode(Config)
        ? Config.GoalPrompt
        : ScenePurpose ?? Scene.PromptText ?? Config.GoalPrompt;
}
