namespace MyFirstMafAgent.Api.Options;

public sealed class AgentOptions
{
    public const string SectionName = "Agent";

    /// <summary>是否允许聊天模型通过 function calling 调用本地工具。</summary>
    public bool EnableToolCalling { get; set; } = true;

    /// <summary>单轮对话中最多执行多少次工具调用往返，避免模型无限循环。</summary>
    public int MaxToolIterations { get; set; } = 6;

    /// <summary>智能体可读写的本地沙箱目录（相对内容根目录或绝对路径）。</summary>
    public string WorkspaceRootPath { get; set; } = "App_Data/agent-workspace";

    /// <summary>是否允许模型写入/删除沙箱内的文件。</summary>
    public bool AllowWorkspaceWrite { get; set; } = true;

    /// <summary>单次读取的最大字符数。</summary>
    public int MaxReadChars { get; set; } = 8_000;

    /// <summary>单次写入内容的最大字符数。</summary>
    public int MaxWriteChars { get; set; } = 200_000;

    /// <summary>工具返回给模型的最大字符数，超出部分会截断。</summary>
    public int MaxToolResultChars { get; set; } = 12_000;

    /// <summary>是否把外部 MCP 服务器的工具桥接给聊天模型。</summary>
    public bool EnableMcpToolBridge { get; set; } = true;

    /// <summary>
    /// 额外授权给模型访问的本机目录（例如 D:\学习资料）。
    /// 列表为空时，模型只能访问智能体工作目录。默认只读，需显式开启写/删。
    /// </summary>
    public List<AgentFileRootOptions> FileRoots { get; set; } = [];

    /// <summary>单次目录列举最多返回多少条。</summary>
    public int MaxDirectoryEntries { get; set; } = 300;

    /// <summary>单次内容检索最多返回多少条匹配。</summary>
    public int MaxSearchMatches { get; set; } = 60;
}

/// <summary>一个被授权访问的本机目录。</summary>
public sealed class AgentFileRootOptions
{
    /// <summary>展示名，同时可作为路径前缀（例如“学习资料/docs/a.md”）。留空时使用目录名。</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>绝对路径，或相对于内容根目录的路径。</summary>
    public string Path { get; set; } = string.Empty;

    /// <summary>是否允许模型写入该目录内的文件。</summary>
    public bool AllowWrite { get; set; }

    /// <summary>是否允许模型删除该目录内的文件。</summary>
    public bool AllowDelete { get; set; }

    public bool Enabled { get; set; } = true;
}
