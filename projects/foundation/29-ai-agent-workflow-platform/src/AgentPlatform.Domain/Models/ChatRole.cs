namespace AgentPlatform.Domain.Models;

/// <summary>Role of a message in a chat completion conversation.</summary>
public enum ChatRole
{
    System,
    User,
    Assistant,
    Tool,
}

/// <summary>Why a model stopped generating.</summary>
public enum FinishReason
{
    Stop,
    ToolCalls,
    Length,
    ContentFilter,
    Timeout,
    Refusal,
    Error,
}
