namespace MyFirstMafAgent.Api.Contracts;

/// <summary>
/// 显式检索请求。
/// <para>
/// <c>Mode</c> 可选：<c>hybrid</c>（默认，向量 + 词法 RRF）、<c>vector</c>、<c>lexical</c>。
/// 保留它主要是为了让检索评估脚本可以分别跑三条基线做对比。
/// </para>
/// </summary>
public sealed record SearchRequest(string Query, int Limit = 5, Guid? KnowledgeBaseId = null, string? Mode = null);
