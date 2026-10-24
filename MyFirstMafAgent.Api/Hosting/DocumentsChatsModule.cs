using MyFirstMafAgent.Api.Application.Notes;
using MyFirstMafAgent.Api.Application.Settings;
using MyFirstMafAgent.Api.Application.Study;
using MyFirstMafAgent.Api.Application.Topics;
using MyFirstMafAgent.Api.Chats;
using MyFirstMafAgent.Api.Companions;
using MyFirstMafAgent.Api.Documents;

namespace MyFirstMafAgent.Api.Hosting;

/// <summary>
/// 资料、聊天、伴侣以及笔记/主题/学习模块。
/// 这些能力仍处于旧目录结构（Documents/Chats/Companions），后续按模块逐步迁移到 DDD 分层，
/// 迁移期间保持现有 API 与表结构兼容。
/// </summary>
public static class DocumentsChatsModule
{
    public static IServiceCollection AddDocumentsChatsModule(this IServiceCollection services)
    {
        services.AddSingleton<ChatHistoryParser>();
        services.AddSingleton<LocalDocumentStore>();
        services.AddSingleton<ResumableUploadService>();
        services.AddSingleton<WhisperModelManager>();
        services.AddSingleton<WhisperTranscriptionService>();

        services.AddScoped<KnowledgeChatService>();
        services.AddScoped<CompanionService>();
        services.AddScoped<NoteService>();
        services.AddScoped<TopicService>();
        services.AddScoped<StudyService>();
        services.AddScoped<SettingsService>();

        return services;
    }
}
