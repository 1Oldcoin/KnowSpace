using System.Text;

namespace MyFirstMafAgent.Api.Persistence;

public static class KnowledgeChatExportFormatter
{
    public static string Format(KnowledgeChatSession session, IReadOnlyList<KnowledgeChatMessage> messages)
    {
        var export = new StringBuilder();
        export.Append("# ").AppendLine(session.Title);
        export.AppendLine();
        export.Append("创建时间：").AppendLine(session.CreatedAt.ToString("yyyy-MM-dd HH:mm:ss zzz"));
        export.Append("最近更新：").AppendLine(session.UpdatedAt.ToString("yyyy-MM-dd HH:mm:ss zzz"));

        foreach (var message in messages)
        {
            export.AppendLine();
            export.Append("## ").Append(message.Role == "user" ? "我" : "知间 AI").Append(" · ")
                .AppendLine(message.CreatedAt.ToString("yyyy-MM-dd HH:mm:ss zzz"));
            export.AppendLine();
            export.AppendLine(message.Content);
        }

        return export.ToString();
    }
}
