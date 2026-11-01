using System;
using System.Collections.Generic;

namespace Fraylo.Contracts.Workflows;

public sealed record WorkflowStepDefinitionDto(
    string StepKey,
    string TaskType,
    int StepOrder,
    bool RequiresReview,
    string? PromptTemplateId = null
);

public sealed record WorkflowDefinitionDto(
    string WorkflowId,
    int Version,
    string Name,
    string Description,
    IReadOnlyList<WorkflowStepDefinitionDto> Steps,
    long Revision,
    string CreatedAtUtc
);

public sealed record WorkflowGetActiveResponseData(
    WorkflowDefinitionDto Definition,
    long Revision
);

public sealed record WorkflowSaveVersionRequest(
    string? WorkflowId,
    long ExpectedRevision,
    string ClientRequestId,
    string Name,
    string Description,
    IReadOnlyList<WorkflowStepDefinitionDto> Steps
);

public sealed record WorkflowSaveVersionResponseData(
    WorkflowDefinitionDto Definition,
    long Revision
);

public sealed record WorkflowActivateRequest(
    string WorkflowId,
    int Version,
    long ExpectedRevision,
    string ClientRequestId
);

public sealed record WorkflowActivateResponseData(
    string WorkflowId,
    int Version,
    long Revision
);
