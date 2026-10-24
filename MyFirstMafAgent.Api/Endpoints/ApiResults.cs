using MyFirstMafAgent.Api.Application.Common;
using MyFirstMafAgent.Api.Application.KnowledgePackages;
using MyFirstMafAgent.Api.Application.Settings;
using MyFirstMafAgent.Api.Domain.Avatar;
using MyFirstMafAgent.Api.Domain.Notes;
using MyFirstMafAgent.Api.Domain.Study;
using MyFirstMafAgent.Api.Domain.Topics;

namespace MyFirstMafAgent.Api.Endpoints;

/// <summary>把应用/领域异常统一映射为 HTTP 响应，避免端点重复写 try/catch。</summary>
internal static class ApiResults
{
    public static async Task<IResult> ExecuteAsync(Func<Task<IResult>> action)
    {
        try
        {
            return await action();
        }
        catch (NotFoundException exception)
        {
            return Results.NotFound(new { error = exception.Message });
        }
        catch (ConflictException exception)
        {
            return Results.Conflict(new { error = exception.Message });
        }
        catch (Exception exception) when (exception is InvalidOperationException
                                                  or NotSupportedException
                                                  or InvalidDataException)
        {
            // 模型端点、索引状态或配置冲突属于可修正错误，返回 409 而不是 500。
            return Results.Conflict(new { error = exception.Message });
        }
        catch (Exception exception) when (exception is NoteValidationException
                                                  or StudyValidationException
                                                  or TopicValidationException
                                                  or SettingsValidationException
                                                  or AvatarValidationException
                                                  or KnowledgePackageException
                                                  or ArgumentException)
        {
            return Results.BadRequest(new { error = exception.Message });
        }
    }
}
