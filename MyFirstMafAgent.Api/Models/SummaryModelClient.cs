using System.Security.Cryptography;
using System.Text;
using MyFirstMafAgent.Api.Application.Settings;
using MyFirstMafAgent.Api.Domain.Knowledge;
using MyFirstMafAgent.Api.Options;

namespace MyFirstMafAgent.Api.Models;

/// <summary>
/// 摘要模型客户端：优先使用知识库绑定的摘要模型，未绑定时回退到全局聊天模型。
/// 与向量模型完全独立，切换摘要模型不会触发索引重建。
/// </summary>
public sealed class SummaryModelClient(
    IModelBindingStore bindings,
    ILlmSettingsAccessor llmSettings,
    OpenAiCompatibleModelClient modelClient) : ISummaryModelClient
{
    public async Task<string> GetModelFingerprintAsync(Guid knowledgeBaseId, CancellationToken cancellationToken)
    {
        var settings = await ResolveAsync(knowledgeBaseId, cancellationToken);
        var canonical = $"{settings.Endpoint.Trim()}|{settings.Model.Trim()}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }

    public async Task<string> CompleteAsync(Guid knowledgeBaseId, string systemPrompt, string userPrompt, CancellationToken cancellationToken)
    {
        var settings = await ResolveAsync(knowledgeBaseId, cancellationToken);
        return await modelClient.CompleteChatAsync(settings, systemPrompt, userPrompt, cancellationToken);
    }

    private async Task<ModelEndpointOptions> ResolveAsync(Guid knowledgeBaseId, CancellationToken cancellationToken)
    {
        var binding = await bindings.GetSummaryBindingAsync(knowledgeBaseId, cancellationToken);
        if (binding is null) return llmSettings.Selected;

        var selected = binding.UseCloudApi ? binding.Online : binding.Local;
        if (selected is null)
            // 绑定只配置了另一来源时回退到全局设置，避免摘要功能不可用。
            return llmSettings.Selected;

        return new ModelEndpointOptions
        {
            Endpoint = selected.Endpoint,
            Model = selected.Model,
            ApiKey = selected.ApiKey
        };
    }
}
