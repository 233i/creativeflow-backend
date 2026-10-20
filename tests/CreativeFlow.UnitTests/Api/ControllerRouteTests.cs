using Microsoft.AspNetCore.Mvc.ActionConstraints;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using CreativeFlow.Api.Controllers;

namespace CreativeFlow.UnitTests.Api;

/// <summary>锁定公开 HTTP 边界及经过确认的后台管理路由。</summary>
public class ControllerRouteTests
{
    /// <summary>回归场景：Controllers Preserve The Public Http Surface。</summary>
    [Fact]
    public void ControllersPreserveThePublicHttpSurface()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddControllers()
            .AddApplicationPart(typeof(DeviceController).Assembly);

        using var provider = services.BuildServiceProvider();
        var descriptors = provider
            .GetRequiredService<IActionDescriptorCollectionProvider>()
            .ActionDescriptors.Items
            .OfType<ControllerActionDescriptor>()
            .ToArray();

        Assert.All(descriptors, descriptor =>
        {
            Assert.NotNull(descriptor.AttributeRouteInfo);
            Assert.False(string.IsNullOrWhiteSpace(descriptor.AttributeRouteInfo.Name));
            Assert.NotEmpty(descriptor.ActionConstraints!
                .OfType<HttpMethodActionConstraint>()
                .SelectMany(constraint => constraint.HttpMethods));
        });

        var actual = descriptors
            .Select(ToSignature)
            .Order(StringComparer.Ordinal)
            .ToArray();
        var expected = ExpectedRoutes
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(expected, actual);
    }

    /// <summary>将 MVC 路由元数据归一为可比较的方法、路径和端点名称。</summary>
    private static string ToSignature(ControllerActionDescriptor descriptor)
    {
        var methods = descriptor.ActionConstraints!
            .OfType<HttpMethodActionConstraint>()
            .SelectMany(constraint => constraint.HttpMethods)
            .Order(StringComparer.Ordinal)
            .ToArray();
        var template = descriptor.AttributeRouteInfo!.Template!.Trim('/');
        return $"{string.Join('|', methods)} /{template} #{descriptor.AttributeRouteInfo.Name}";
    }

    /// <summary>既有 HTTP 边界及后续明确新增的管理员请求读取路由。</summary>
    private static readonly string[] ExpectedRoutes =
    [
        "GET /api/v1/admin/assets #ListManagedAssets",
        "GET /api/v1/admin/assets/facets #GetAssetFacets",
        "GET /api/v1/admin/assets/{id:guid} #GetManagedAsset",
        "GET /api/v1/admin/assets/{id:guid}/content #GetManagedAssetContent",
        "GET /api/v1/admin/assets/{id:guid}/thumbnail #GetManagedAssetThumbnail",
        "GET /api/v1/admin/assets/{id:guid}/deletion-preview #PreviewManagedAssetDeletion",
        "PATCH /api/v1/admin/assets/{id:guid} #UpdateManagedAsset",
        "POST /api/v1/admin/assets/{id:guid}/{operation} #ManageAssetLifecycle",
        "POST /api/v1/admin/assets/batch #BatchManageAssets",
        "GET /api/v1/admin/assets/storage #GetAssetStorage",
        "PUT /api/v1/admin/assets/storage-policy #SetAssetStoragePolicy",
        "POST /api/v1/admin/assets/upload #UploadManagedAsset",
        "POST /api/v1/admin/assets/{id:guid}/last-frame #ExtractManagedAssetLastFrame",
        "POST /api/v1/admin/assets/download #DownloadManagedAssets",
        "GET /api/v1/canvas/asset-library #PageCanvasAssetLibrary",
        "GET /api/v1/canvas/asset-library/{id:guid} #GetLibraryAsset",
        "GET /api/v1/canvas/asset-library/{id:guid}/deletion-preview #PreviewLibraryAssetDeletion",
        "POST /api/v1/canvas/asset-library/{id:guid}/{operation} #ManageLibraryAsset",
        "POST /api/v1/canvas/asset-library/public/{id:guid}/reuse #ReusePublicLibraryAsset",
        "GET /api/v1/canvas/asset-library/public/{id:guid}/content #ReadPublicLibraryAsset",
        "GET /api/v1/canvas/asset-library/public/{id:guid}/thumbnail #ReadPublicLibraryThumbnail",
        "POST /api/v1/canvas/asset-library/download #DownloadLibraryAssets",
        "GET /api/v1/canvas/assets/{id:guid}/thumbnail #GetCanvasAssetThumbnail",
        "GET /api/v1/admin/workflow-apis/{id}/assets/library #PageAdminWorkflowAssets",
        "POST /api/v1/admin/workflow-apis/{id}/assets/public/{assetId:guid}/reuse #ReusePublicAdminWorkflowAsset",
        "GET /api/v1/admin/workflow-apis/{id}/assets/{assetId:guid}/thumbnail #GetAdminWorkflowAssetThumbnail",
        "GET /api/v1/admin/workflow-apis/{id}/assets/public/{assetId:guid}/thumbnail #GetPublicAdminWorkflowAssetThumbnail",
        "GET /api/external/v1/runs/{runId}/events #StreamExternalWorkflowRun",
        "POST /api/agui/v1/workflows/{id}/runs #RunPublishedWorkflowWithAgUi",
        "GET /api/agui/v1/runs/{runId}/events #ReadWorkflowAgUiEvents",
        "GET /api/v1/http-credentials/catalog #HttpCredentialCatalog",
        "GET /api/v1/admin/http-credentials/catalog #AdminHttpCredentialCatalog",
        "GET /api/v1/admin/http-credentials #ListHttpCredentials",
        "GET /api/v1/admin/http-credentials/{id:guid} #GetHttpCredential",
        "POST /api/v1/admin/http-credentials #CreateHttpCredential",
        "PUT /api/v1/admin/http-credentials/{id:guid} #SaveHttpCredentialRevision",
        "PUT /api/v1/admin/http-credentials/{id:guid}/state #SetHttpCredentialState",
        "GET /api/v1/admin/http-credentials/{id:guid}/grants #GetHttpCredentialGrants",
        "PUT /api/v1/admin/http-credentials/{id:guid}/grants/{kind}/{subject:guid} #SetHttpCredentialGrant",
        "GET /api/v1/knowledge-bases/catalog #KnowledgeCatalog",
        "GET /api/v1/admin/knowledge-bases/catalog #AdminKnowledgeCatalog",
        "GET /api/v1/admin/knowledge-bases/{id:guid}/grants #GetKnowledgeGrants",
        "PUT /api/v1/admin/knowledge-bases/{id:guid}/grants/{account:guid} #SetKnowledgeGrant",
        "PUT /api/v1/admin/knowledge-bases/{id:guid}/api-key-grants/{apiKey:guid} #SetKnowledgeApiKeyGrant",
        "GET /api/v1/workflows/capabilities #WorkflowCapabilities",
        "POST /api/v1/workflows/{id}/nodes/{nodeId}/debug-runs #StartWorkflowNodeDebug",
        "GET /api/v1/workflows/{id}/nodes/{nodeId}/debug-inputs #GetWorkflowNodeDebugInputs",
        "POST /api/v1/admin/workflow-apis/{id}/nodes/{nodeId}/debug-runs #AdminStartWorkflowNodeDebug",
        "GET /api/v1/admin/workflow-apis/{id}/nodes/{nodeId}/debug-inputs #AdminGetWorkflowNodeDebugInputs",
        "GET /api/v1/admin/workflow-apis/capabilities #AdminWorkflowCapabilities",
        "GET /api/v1/admin/ai/models/{modelId}/embedding/settings #GetEmbeddingSettings",
        "PUT /api/v1/admin/ai/models/{modelId}/embedding/settings #SaveEmbeddingSettings",
        "POST /api/v1/admin/ai/models/{modelId}/embedding/test #TestEmbeddingModel",
        "GET /api/v1/admin/ai/models/{modelId}/rerank/settings #GetRerankSettings",
        "PUT /api/v1/admin/ai/models/{modelId}/rerank/settings #SaveRerankSettings",
        "POST /api/v1/admin/ai/models/{modelId}/rerank/test #TestRerankModel",
        "POST /api/v1/admin/knowledge-bases/{id:guid}/versions/{versionId:guid}/rebuild #RebuildAdminKnowledgeHybridVersion",
        "POST /api/v1/admin/knowledge-bases/{id:guid}/evaluate #EvaluateAdminKnowledgeRetrieval",
        "GET /api/v1/admin/workflow-apis #AdminListWorkflows",
        "GET /api/v1/admin/workflow-center #AdminWorkflowCenter",
        "GET /api/v1/admin/workflow-center/{source}/{id:guid}/deletion-preview #AdminWorkflowDeletionPreview",
        "DELETE /api/v1/admin/workflow-center/{source}/{id:guid} #AdminDeleteManagedWorkflow",
        "GET /api/v1/admin/workflow-center/{source}/{id}/runs #AdminWorkflowCenterRuns",
        "GET /api/v1/admin/workflow-center/{source}/{id:guid}/runs/{runId:guid}/{view} #AdminWorkflowCenterRunSnapshot",
        "GET /api/v1/admin/workflow-apis/{id}/runs/{runId}/snapshot #AdminGetWorkflowRunSnapshot",
        "GET /api/v1/admin/workflow-apis/{id}/external-runs/{runId}/snapshot #GetAdminExternalRunSnapshot",
        "GET /api/v1/workflows/{id}/runs/{runId}/snapshot #GetWorkflowRunSnapshot",
        "POST /api/v1/admin/workflow-apis #AdminCreateWorkflow",
        "GET /api/v1/admin/workflow-apis/{id} #AdminGetWorkflow",
        "PUT /api/v1/admin/workflow-apis/{id} #AdminUpdateWorkflow",
        "POST /api/v1/admin/workflow-apis/{id}/runs #AdminStartWorkflowRun",
        "POST /api/v1/admin/workflow-apis/{id}/runs/{runId}/resume #AdminResumeWorkflowRun",
        "GET /api/v1/admin/workflow-apis/{id}/runs/{runId}/partial-rerun/preview #AdminPreviewPartialWorkflowRerun",
        "POST /api/v1/admin/workflow-apis/{id}/runs/{runId}/partial-rerun #AdminPartialWorkflowRerun",
        "POST /api/v1/admin/workflow-apis/{id}/runs/{runId}/nodes/{nodeId}/retry #AdminRetryWorkflowNode",
        "POST /api/v1/admin/workflow-apis/{id}/runs/{runId}/nodes/{nodeId}/skip #AdminSkipWorkflowNode",
        "POST /api/v1/admin/workflow-apis/{id}/runs/{runId}/rollback #AdminRollbackWorkflowRun",
        "GET /api/v1/admin/workflow-apis/{id}/runs/latest #AdminGetLatestWorkflowRun",
        "GET /api/v1/admin/workflow-apis/{id}/runs/{runId} #AdminGetWorkflowRun",
        "DELETE /api/v1/admin/workflow-apis/{id}/runs/{runId} #AdminCancelWorkflowRun",
        "GET /api/v1/admin/workflow-apis/{id}/assets #ListAdminWorkflowApiAssets",
        "GET /api/v1/admin/workflow-apis/{id}/assets/groups #ListAdminWorkflowApiAssetGroups",
        "GET /api/v1/admin/workflow-apis/{id}/assets/{assetId} #GetAdminWorkflowApiAsset",
        "POST /api/v1/admin/workflow-apis/{id}/assets #UploadAdminWorkflowApiAsset",
        "GET /api/v1/admin/requests #ListManagedRequests",
        "GET /api/v1/admin/requests/{source}/{id:guid} #GetManagedRequest",
        "GET /api/v1/admin/requests/{source}/{id:guid}/assets/{assetId:guid} #GetManagedRequestArtifact",
        "DELETE /api/v1/admin/ai/models/{modelId} #DeleteModel",
        "DELETE /api/v1/admin/ai/providers/{id} #DeleteProvider",
        "DELETE /api/v1/admin/workflow-templates/{id} #DeleteWorkflowTemplate",
        "DELETE /api/v1/agent/runs/{id} #CancelAgentRun",
        "DELETE /api/v1/agent/sessions/{id}/messages #ClearAgentMessages",
        "DELETE /api/v1/ai/videos/{id} #CancelVideoJob",
        "DELETE /api/v1/canvas/assets/groups/{id} #DeleteCanvasAssetGroup",
        "DELETE /api/v1/canvas/assets/{id} #DeleteCanvasAsset",
        "DELETE /api/v1/canvas/{id} #DeleteCanvas",
        "DELETE /api/v1/mcp/servers/{id} #DeleteMcpServer",
        "DELETE /api/v1/skills/{id} #DeleteSkill",
        "DELETE /api/v1/workflows/{id} #DeleteWorkflow",
        "DELETE /api/v1/admin/workflow-apis/{id}/api-keys/{keyId} #RevokeWorkflowApiKey",
        "DELETE /api/v1/admin/workflow-apis/{id}/deployment #DisableWorkflowApi",
        "DELETE /api/v1/workflows/{id}/runs/{runId} #CancelWorkflowRun",
        "DELETE /api/external/v1/runs/{runId} #CancelExternalWorkflowRun",
        "GET /api/v1/admin/ai/image-generation-settings #GetAdminImageGenerationSettings",
        "GET /api/v1/admin/ai/model-slots #ListAdminModelSlots",
        "GET /api/v1/admin/ai/providers #ListAdminProviders",
        "GET /api/v1/admin/ai/providers/{id}/upstream-models #ListUpstreamModels",
        "GET /api/v1/admin/auth/me #CurrentAdmin",
        "GET /api/v1/admin/users #ListManagedUsers",
        "GET /api/v1/admin/workflow-templates #ListAdminWorkflowTemplates",
        "GET /api/v1/admin/workflow-templates/{id} #GetAdminWorkflowTemplate",
        "GET /api/v1/agent/runs/active #GetActiveAgentRun",
        "GET /api/v1/agent/runs/{id} #GetAgentRun",
        "GET /api/v1/agent/runs/{id}/events #ListAgentEvents",
        "GET /api/v1/agent/sessions #GetAgentSessionByCanvas",
        "GET /api/v1/agent/sessions/{id}/messages #ListAgentMessages",
        "GET /api/v1/agent/tools #ListAgentTools",
        "GET /api/v1/ai/model-slots #ListModelSlots",
        "GET /api/v1/ai/providers #ListProviders",
        "GET /api/v1/ai/videos/{id} #GetVideoJob",
        "GET /api/v1/auth/callback/{provider} #ExternalLoginCallback",
        "GET /api/v1/auth/link/{provider} #LinkExternalLogin",
        "GET /api/v1/auth/login/{provider} #StartExternalLogin",
        "GET /api/v1/auth/me #CurrentAccount",
        "GET /api/v1/auth/providers #AuthProviders",
        "GET /api/v1/canvas #ListCanvases",
        "GET /api/v1/canvas/assets #ListCanvasAssets",
        "GET /api/v1/canvas/assets/groups #ListCanvasAssetGroups",
        "GET /api/v1/canvas/assets/{id} #GetCanvasAsset",
        "GET /api/v1/canvas/{id} #GetCanvas",
        "GET /api/v1/device/me #CurrentDevice",
        "GET /api/v1/mcp/servers #ListMcpServers",
        "GET /api/v1/mcp/servers/{id}/tools #ListMcpServerTools",
        "GET /api/v1/skills #ListSkills",
        "GET /api/v1/skills/{id} #GetSkill",
        "GET /api/v1/system/info #SystemInfo",
        "GET /api/v1/workflow-templates #ListWorkflowTemplates",
        "GET /api/v1/workflow-templates/{id} #GetWorkflowTemplate",
        "GET /api/v1/workflows #ListWorkflows",
        "GET /api/v1/admin/workflow-apis/{id}/api-keys #ListWorkflowApiKeys",
        "GET /api/v1/admin/workflow-apis/{id}/deployments #ListWorkflowDeployments",
        "GET /api/v1/admin/workflow-apis/{id}/external-runs #ListWorkflowExternalRuns",
        "GET /api/v1/admin/workflow-apis/{id}/external-runs/{runId} #GetAdminWorkflowExternalRun",
        "GET /api/v1/admin/workflow-apis/{id}/runs #AdminListWorkflowRuns",
        "GET /api/v1/workflows/{id} #GetWorkflow",
        "GET /api/v1/workflows/{id}/runs/latest #GetLatestWorkflowRun",
        "GET /api/v1/workflows/{id}/runs #ListWorkflowRuns",
        "GET /api/v1/workflows/{id}/runs/{runId} #GetWorkflowRun",
        "GET /api/v1/workflows/{id}/runs/{runId}/partial-rerun/preview #PreviewPartialWorkflowRerun",
        "GET /health/live #HealthLive",
        "GET /health/ready #HealthReady",
        "GET /api/external/v1/runs/{runId} #GetExternalWorkflowRun",
        "GET /api/external/v1/workflows #ListPublishedExternalWorkflows",
        "GET /api/external/v1/workflows/{id} #GetPublishedExternalWorkflow",
        "GET|HEAD /api/v1/media/assets/{id} #GetSignedMediaAsset",
        "PATCH /api/v1/admin/ai/models/{modelId} #UpdateModel",
        "PATCH /api/v1/admin/ai/providers/{id} #UpdateProvider",
        "PATCH /api/v1/admin/auth/password #ChangeAdminPassword",
        "PATCH /api/v1/admin/users/{id}/enabled #SetManagedUserEnabled",
        "PATCH /api/v1/canvas/assets/group #AssignCanvasAssetsToGroup",
        "PATCH /api/v1/canvas/assets/groups/{id} #RenameCanvasAssetGroup",
        "PATCH /api/v1/mcp/servers/{id} #UpdateMcpServer",
        "POST /api/v1/admin/ai/config/import #ImportAiConfig",
        "POST /api/v1/admin/ai/providers #CreateProvider",
        "POST /api/v1/admin/ai/providers/{id}/check #CheckProvider",
        "POST /api/v1/admin/ai/providers/{id}/models #AddModel",
        "POST /api/v1/admin/ai/providers/{id}/models/import #ImportModels",
        "POST /api/v1/admin/auth/login #AdminLogin",
        "POST /api/v1/admin/auth/logout #AdminLogout",
        "POST /api/v1/admin/workflow-templates #CreateWorkflowTemplate",
        "POST /api/v1/admin/workflow-templates/{id}/archive #ArchiveWorkflowTemplate",
        "POST /api/v1/admin/workflow-templates/{id}/debug-workflow #PrepareWorkflowTemplateDebugWorkflow",
        "POST /api/v1/admin/workflow-templates/{id}/duplicate #DuplicateWorkflowTemplate",
        "POST /api/v1/admin/workflow-templates/{id}/publish #PublishWorkflowTemplate",
        "POST /api/v1/agent/sessions #CreateAgentSession",
        "POST /api/v1/agent/sessions/{id}/runs #CreateAgentRun",
        "POST /api/v1/ai/images #GenerateImages",
        "POST /api/v1/ai/image-jobs #SubmitImageJob",
        "GET /api/v1/ai/image-jobs #ListImageJobs",
        "GET /api/v1/ai/image-jobs/{jobId:guid} #GetImageJob",
        "GET /api/v1/ai/image-jobs/by-submission/{key:guid} #FindImageJobSubmission",
        "POST /api/v1/ai/image-jobs/{jobId:guid}/retry-download #RetryImageJobDownload",
        "POST /api/v1/ai/image-jobs/{jobId:guid}/regenerate #RegenerateImageJob",
        "POST /api/v1/ai/text #GenerateText",
        "POST /api/v1/ai/videos #StartVideoJob",
        "POST /api/v1/auth/logout #Logout",
        "POST /api/v1/auth/password/login #LoginWithPassword",
        "POST /api/v1/auth/password/register #RegisterWithPassword",
        "POST /api/v1/canvas #CreateCanvas",
        "POST /api/v1/canvas/assets #UploadCanvasAsset",
        "POST /api/v1/canvas/assets/groups #CreateCanvasAssetGroup",
        "POST /api/v1/canvas/assets/{id}/last-frame #ExtractCanvasVideoLastFrame",
        "POST /api/v1/device/handshake #DeviceHandshake",
        "POST /api/v1/mcp/servers #CreateMcpServer",
        "POST /api/v1/mcp/servers/import #ImportMcpServers",
        "POST /api/v1/mcp/servers/{id}/check #CheckMcpServer",
        "POST /api/v1/skills #InstallSkill",
        "POST /api/v1/skills/scan #ScanSkill",
        "POST /api/v1/workflow-templates/{id}/instantiate #InstantiateWorkflowTemplate",
        "POST /api/v1/workflows #CreateWorkflow",
        "POST /api/v1/admin/workflow-apis/{id}/api-keys #CreateWorkflowApiKey",
        "POST /api/v1/admin/workflow-apis/{id}/publish #PublishWorkflowApi",
        "POST /api/v1/workflows/{id}/runs #StartWorkflowRun",
        "POST /api/v1/workflows/{id}/runs/{runId}/partial-rerun #PartialWorkflowRerun",
        "POST /api/v1/workflows/{id}/runs/{runId}/nodes/{nodeId}/retry #RetryWorkflowNode",
        "POST /api/v1/workflows/{id}/runs/{runId}/nodes/{nodeId}/skip #SkipWorkflowNode",
        "POST /api/v1/workflows/{id}/runs/{runId}/rollback #RollbackWorkflowRun",
        "POST /api/v1/workflows/{id}/runs/{runId}/resume #ResumeWorkflowRun",
        "POST /api/external/v1/assets/images #UploadExternalWorkflowImage",
        "POST /api/external/v1/workflows/{id}/runs #CreateExternalWorkflowRun",
        "PUT /api/v1/admin/ai/image-generation-settings #UpdateAdminImageGenerationSettings",
        "PUT /api/v1/admin/ai/model-slots/{kind} #UpdateAdminModelSlot",
        "PUT /api/v1/admin/workflow-templates/{id} #UpdateWorkflowTemplate",
        "PUT /api/v1/canvas/{id} #UpdateCanvas",
        "PUT /api/v1/workflows/{id} #UpdateWorkflow",
        "GET /api/v1/admin/knowledge-bases/capabilities #AdminKnowledgeCapabilities",
        "GET /api/v1/admin/knowledge-bases #ListAdminKnowledgeBases",
        "POST /api/v1/admin/knowledge-bases #CreateAdminKnowledgeBase",
        "GET /api/v1/admin/knowledge-bases/{id:guid} #GetAdminKnowledgeBase",
        "PUT /api/v1/admin/knowledge-bases/{id:guid}/enabled #SetAdminKnowledgeEnabled",
        "POST /api/v1/admin/knowledge-bases/preview #PreviewAdminKnowledgeDocuments",
        "POST /api/v1/admin/knowledge-bases/{id:guid}/versions #ImportAdminKnowledgeVersion",
        "GET /api/v1/admin/knowledge-bases/{id:guid}/versions/{versionId:guid}/chunks #GetAdminKnowledgeChunks",
        "GET /api/v1/admin/knowledge-bases/{id:guid}/versions/{versionId:guid}/documents #ListAdminKnowledgeDocuments",
        "GET /api/v1/admin/knowledge-bases/{id:guid}/versions/{versionId:guid}/documents/{documentId:guid} #GetAdminKnowledgeDocument",
        "POST /api/v1/admin/knowledge-bases/{id:guid}/document-changes/preview #PreviewAdminKnowledgeDocumentChanges",
        "POST /api/v1/admin/knowledge-bases/{id:guid}/document-changes #ApplyAdminKnowledgeDocumentChanges",
        "GET /api/v1/admin/knowledge-bases/{id:guid}/versions/{versionId:guid}/cleanup #PreviewAdminKnowledgeCleanup",
        "POST /api/v1/admin/knowledge-bases/{id:guid}/versions/{versionId:guid}/cleanup #RequestAdminKnowledgeCleanup",
        "POST /api/v1/admin/knowledge-bases/{id:guid}/versions/{versionId:guid}/activate #ActivateAdminKnowledgeVersion",
        "POST /api/v1/admin/knowledge-bases/{id:guid}/versions/{versionId:guid}/recover #RecoverAdminKnowledgeVersion",
        "POST /api/v1/admin/knowledge-bases/{id:guid}/search #SearchAdminKnowledgeBase",
    ];
}
