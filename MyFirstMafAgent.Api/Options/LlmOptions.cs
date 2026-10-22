namespace MyFirstMafAgent.Api.Options;

public sealed class LlmOptions
{
    public const string SectionName = "Llm";

    public bool UseCloudApi { get; set; }
    public ModelEndpointOptions Local { get; set; } = new();
    public ModelEndpointOptions Online { get; set; } = new();
}

public sealed class ModelEndpointOptions
{
    public string Endpoint { get; set; } = string.Empty;
    public string Model { get; set; } = string.Empty;
    public string ApiKey { get; set; } = string.Empty;
}
