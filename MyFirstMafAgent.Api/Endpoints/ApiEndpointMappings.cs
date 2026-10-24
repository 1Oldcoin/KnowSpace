namespace MyFirstMafAgent.Api.Endpoints;

public static class ApiEndpointMappings
{
    public static void MapApiEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapHealthEndpoints();
        endpoints.MapKnowledgeEndpoints();
        endpoints.MapIndexEndpoints();
        endpoints.MapCompanionEndpoints();
        endpoints.MapTopicEndpoints();
        endpoints.MapNoteEndpoints();
        endpoints.MapKnowledgePackageEndpoints();
        endpoints.MapSettingsEndpoints();
        endpoints.MapStudyEndpoints();
        endpoints.MapDocumentContentEndpoints();
        endpoints.MapMediaEndpoints();
        endpoints.MapAgentEndpoints();
        endpoints.MapAvatarEndpoints();
        endpoints.MapPluginEndpoints();
        endpoints.MapAgentClientEndpoints();
        endpoints.MapJobEndpoints();
    }
}
