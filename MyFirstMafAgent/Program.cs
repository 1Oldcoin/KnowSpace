using Microsoft.Agents.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.AI;
using OpenAI;
using OpenAI.Chat;
using System.ClientModel;
using MyFirstMafAgent;

var config = new ConfigurationBuilder()
    .SetBasePath(AppContext.BaseDirectory)
    .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
    .AddEnvironmentVariables()
    .Build();

var llmSettings = config.GetSection("Llm").Get<LlmSettings>()!;

// 1. 创建底层 ChatClient（DeepSeek 兼容 OpenAI 接口）
var chatClient = new OpenAIClient(
        new ApiKeyCredential(llmSettings.ApiKey),
        new OpenAIClientOptions { Endpoint = new Uri(llmSettings.Endpoint) })
    .GetChatClient(llmSettings.Model);

// 2. 创建 Agent
AIAgent agent = chatClient.AsAIAgent(
    instructions: "你是一位专业的英语口语教练。帮助学生提升英语口语能力，始终保持鼓励和友好的态度。",
    name: "SpokenEnglishCoach"
);

Console.WriteLine("输入问题，回车发送；输入 exit 退出。");
Console.WriteLine();

// 3. 循环问答 + 流式输出
while (true)
{
    Console.Write("你：");
    var input = Console.ReadLine();

    if (string.IsNullOrWhiteSpace(input))
        continue;

    if (input.Equals("exit", StringComparison.OrdinalIgnoreCase))
        break;

    Console.Write("AI：");

    await foreach (var update in agent.RunStreamingAsync(input))
    {
        // 关键：流式返回的是 update 对象，文本在 update.Text 里
        if (!string.IsNullOrEmpty(update.Text))
        {
            Console.Write(update.Text);
        }
    }

    Console.WriteLine();
    Console.WriteLine();
}