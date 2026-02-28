namespace Cmux.Core.Config;

public class AgentSettings
{
    public bool Enabled { get; set; } = false;
    public string AgentName { get; set; } = "assistant";
    public string Handler { get; set; } = "/agent";
    public string AdditionalHandlers { get; set; } = "";
    public string SystemPrompt { get; set; } = "You are a pragmatic engineering assistant running inside cmux. Keep responses concise and action-oriented.";
    public string ActiveProvider { get; set; } = "openai";
    public OpenAiCompatibleAgentSettings OpenAi { get; set; } = new();
    public AnthropicAgentSettings Anthropic { get; set; } = new();
    public bool EnableBashTool { get; set; } = true;
    public int BashTimeoutSeconds { get; set; } = 120;
    public bool EnableWebSearchTool { get; set; } = false;
    public ExaSearchSettings Exa { get; set; } = new();
    public bool UseJsonForCustomTools { get; set; } = false;
    public bool UseJsonForMcpServers { get; set; } = false;
    public List<AgentCustomToolConfig> CustomTools { get; set; } = [];
    public List<AgentMcpServerConfig> McpServers { get; set; } = [];
    public bool EnableConversationMemory { get; set; } = true;
    public bool EnableStreaming { get; set; } = true;
    public string ChatFontFamily { get; set; } = "Cascadia Code";
    public int ChatFontSize { get; set; } = 13;
    public string DefaultSubmitKey { get; set; } = "auto";
    public bool EnableSubmitFallback { get; set; } = true;
    public int SubmitFallbackWaitMs { get; set; } = 350;
    public string SubmitFallbackOrder { get; set; } = "enter,linefeed";
    public bool AutoDiscoverAgentFiles { get; set; } = true;
    public string AgentInstructionsPath { get; set; } = "";
    public string SkillsRootPath { get; set; } = "";
    public bool AutoCompactContext { get; set; } = true;
    public int MaxContextMessages { get; set; } = 60;
    public int ContextBudgetTokens { get; set; } = 24000;
    public int CompactThresholdPercent { get; set; } = 85;
    public int KeepRecentMessagesOnCompaction { get; set; } = 20;
}

public class OpenAiCompatibleAgentSettings
{
    public string BaseUrl { get; set; } = "https://api.openai.com/v1";
    public string Model { get; set; } = "gpt-4o-mini";
    public string ApiKeySecretName { get; set; } = "agent.openai.apiKey";
}

public class AnthropicAgentSettings
{
    public string BaseUrl { get; set; } = "https://api.anthropic.com";
    public string Model { get; set; } = "claude-3-5-sonnet-latest";
    public string ApiKeySecretName { get; set; } = "agent.anthropic.apiKey";
}

public class ExaSearchSettings
{
    public string BaseUrl { get; set; } = "https://api.exa.ai";
    public string ApiKeySecretName { get; set; } = "agent.exa.apiKey";
}

public class AgentCustomToolConfig
{
    public bool Enabled { get; set; } = true;
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public string CommandTemplate { get; set; } = "";
}

public class AgentMcpServerConfig
{
    public bool Enabled { get; set; } = true;
    public string Name { get; set; } = "";
    public string Command { get; set; } = "";
    public string Arguments { get; set; } = "";
    public string WorkingDirectory { get; set; } = "";
}
