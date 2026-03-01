using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Cmux.Core.Config;
using Cmux.Core.Models;
using Cmux.Core.Services;

namespace Cmux.Services;

public sealed class AgentRuntimeService : IDisposable
{
    private readonly HttpClient _httpClient = new() { Timeout = TimeSpan.FromMinutes(3) };
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _activeRuns = [];
    private readonly ConcurrentDictionary<string, string> _activeThreadByPane = [];
    private readonly ConcurrentDictionary<string, ConcurrentQueue<string>> _steeringPromptsByRunKey = [];

    public event Action<AgentRuntimeUpdate>? RuntimeUpdated;

    public bool TryHandlePaneCommand(string rawCommand, AgentPaneContext context)
    {
        var settings = SettingsService.Current.Agent;

        if (!TryParseHandlerCommand(rawCommand, settings, out var prompt, out var handlerToken))
            return false;

        if (!settings.Enabled)
        {
            WriteAgentMessage(context, settings.AgentName, "Agent is disabled in Settings -> Agent.");
            return true;
        }

        if (string.IsNullOrWhiteSpace(prompt))
        {
            WriteAgentMessage(context, settings.AgentName, $"Usage: {handlerToken} <prompt>");
            return true;
        }

        return TryQueuePrompt(prompt, context, explicitThreadId: null, echoToPane: true);
    }

    public bool TrySendChatPrompt(string prompt, AgentPaneContext context, string? threadId = null)
    {
        return TryQueuePrompt(prompt, context, explicitThreadId: threadId, echoToPane: false);
    }

    public string? GetActiveThreadId(string workspaceId, string surfaceId, string paneId)
    {
        var runKey = BuildRunKey(workspaceId, surfaceId, paneId);
        if (_activeThreadByPane.TryGetValue(runKey, out var threadId))
            return threadId;
        return null;
    }

    public void SetActiveThreadId(string workspaceId, string surfaceId, string paneId, string threadId)
    {
        var runKey = BuildRunKey(workspaceId, surfaceId, paneId);
        if (string.IsNullOrWhiteSpace(threadId))
        {
            _activeThreadByPane.TryRemove(runKey, out _);
            return;
        }

        _activeThreadByPane[runKey] = threadId.Trim();
    }

    private bool TryQueuePrompt(string prompt, AgentPaneContext context, string? explicitThreadId, bool echoToPane)
    {
        var settings = SettingsService.Current.Agent;
        if (!settings.Enabled)
        {
            WriteAgentMessage(context, settings.AgentName, "Agent is disabled in Settings -> Agent.");
            EmitUpdate(context, new AgentRuntimeUpdate
            {
                Type = AgentRuntimeUpdateType.Status,
                Message = "Agent is disabled in Settings -> Agent.",
            });
            return false;
        }

        var trimmedPrompt = (prompt ?? "").Trim();
        if (string.IsNullOrWhiteSpace(trimmedPrompt))
            return true;

        var runKey = BuildRunKey(context.WorkspaceId, context.SurfaceId, context.PaneId);
        var threadId = ResolveThreadId(context, settings, runKey, explicitThreadId);

        var hasActiveRun = _activeRuns.TryGetValue(runKey, out var existingRun);
        var activeRunThreadId = _activeThreadByPane.TryGetValue(runKey, out var existingThreadId) ? existingThreadId : null;
        var canSteer = hasActiveRun
            && existingRun != null
            && !existingRun.IsCancellationRequested
            && !string.IsNullOrWhiteSpace(activeRunThreadId)
            && string.Equals(activeRunThreadId, threadId, StringComparison.Ordinal);

        App.AgentConversationStore.AppendMessage(new AgentConversationMessage
        {
            ThreadId = threadId,
            Role = "user",
            Content = trimmedPrompt,
            Provider = (settings.ActiveProvider ?? "openai").Trim().ToLowerInvariant(),
            Model = ResolveModel(settings),
            CreatedAtUtc = DateTime.UtcNow,
            TotalTokens = EstimateTokens(trimmedPrompt),
        });

        EmitUpdate(context, new AgentRuntimeUpdate
        {
            Type = AgentRuntimeUpdateType.ThreadChanged,
            ThreadId = threadId,
            AgentName = settings.AgentName,
        });

        EmitUpdate(context, new AgentRuntimeUpdate
        {
            Type = AgentRuntimeUpdateType.UserMessage,
            ThreadId = threadId,
            Message = trimmedPrompt,
            CreatedAtUtc = DateTime.UtcNow,
        });

        if (canSteer)
        {
            EnqueueSteeringPrompt(runKey, trimmedPrompt);
            EmitUpdate(context, new AgentRuntimeUpdate
            {
                Type = AgentRuntimeUpdateType.Status,
                ThreadId = threadId,
                Message = "Steering message received",
            });
            return true;
        }

        _activeThreadByPane[runKey] = threadId;

        if (_activeRuns.TryRemove(runKey, out var existing))
        {
            try { existing.Cancel(); } catch { }
            existing.Dispose();
        }

        var cts = new CancellationTokenSource();
        _activeRuns[runKey] = cts;

        _ = Task.Run(async () =>
        {
            var assistantBuffer = new StringBuilder();
            try
            {
                EmitUpdate(context, new AgentRuntimeUpdate
                {
                    Type = AgentRuntimeUpdateType.Status,
                    ThreadId = threadId,
                    Message = "Thinking...",
                });

                if (echoToPane)
                    WriteAgentMessage(context, settings.AgentName, "Thinking...");

                var result = await RunAgentAsync(
                    trimmedPrompt,
                    context,
                    runKey,
                    threadId,
                    delta =>
                    {
                        if (string.IsNullOrEmpty(delta))
                            return;

                        assistantBuffer.Append(delta);
                        EmitUpdate(context, new AgentRuntimeUpdate
                        {
                            Type = AgentRuntimeUpdateType.AssistantDelta,
                            ThreadId = threadId,
                            Message = delta,
                        });
                    },
                    cts.Token);

                var finalText = assistantBuffer.Length > 0 ? assistantBuffer.ToString() : result.Text;
                if (assistantBuffer.Length == 0 && !string.IsNullOrWhiteSpace(result.Text))
                {
                    EmitUpdate(context, new AgentRuntimeUpdate
                    {
                        Type = AgentRuntimeUpdateType.AssistantDelta,
                        ThreadId = threadId,
                        Message = result.Text,
                    });
                    finalText = result.Text;
                }

                finalText = (finalText ?? "").Trim();
                if (string.IsNullOrWhiteSpace(finalText))
                    finalText = "No text response received.";

                var assistantMessage = App.AgentConversationStore.AppendMessage(new AgentConversationMessage
                {
                    ThreadId = threadId,
                    Role = "assistant",
                    Content = finalText,
                    Provider = result.Provider,
                    Model = result.Model,
                    CreatedAtUtc = DateTime.UtcNow,
                    InputTokens = result.InputTokens,
                    OutputTokens = result.OutputTokens,
                    TotalTokens = result.TotalTokens > 0 ? result.TotalTokens : result.InputTokens + result.OutputTokens,
                });

                EmitUpdate(context, new AgentRuntimeUpdate
                {
                    Type = AgentRuntimeUpdateType.AssistantCompleted,
                    ThreadId = threadId,
                    Message = assistantMessage.Content,
                    InputTokens = assistantMessage.InputTokens,
                    OutputTokens = assistantMessage.OutputTokens,
                    TotalTokens = assistantMessage.TotalTokens,
                    EstimatedContextTokens = result.EstimatedContextTokens,
                    ContextBudgetTokens = result.ContextBudgetTokens,
                    ContextNeedsCompaction = result.ContextNeedsCompaction,
                    CompactionApplied = result.CompactionApplied,
                    Provider = result.Provider,
                    Model = result.Model,
                    CreatedAtUtc = assistantMessage.CreatedAtUtc,
                });

                if (echoToPane)
                    WriteAgentMessage(context, settings.AgentName, finalText);
            }
            catch (OperationCanceledException)
            {
                EmitUpdate(context, new AgentRuntimeUpdate
                {
                    Type = AgentRuntimeUpdateType.Status,
                    ThreadId = threadId,
                    Message = "canceled.",
                });

                if (echoToPane)
                    WriteAgentMessage(context, settings.AgentName, "canceled.");
            }
            catch (Exception ex)
            {
                EmitUpdate(context, new AgentRuntimeUpdate
                {
                    Type = AgentRuntimeUpdateType.Error,
                    ThreadId = threadId,
                    Message = ex.Message,
                });

                if (echoToPane)
                    WriteAgentMessage(context, settings.AgentName, $"error: {ex.Message}");
            }
            finally
            {
                if (_activeRuns.TryGetValue(runKey, out var current) && ReferenceEquals(current, cts))
                {
                    _activeRuns.TryRemove(runKey, out _);
                }

                cts.Dispose();

                if (!_activeRuns.ContainsKey(runKey) &&
                    _steeringPromptsByRunKey.TryGetValue(runKey, out var queue) &&
                    queue.IsEmpty)
                {
                    _steeringPromptsByRunKey.TryRemove(runKey, out _);
                }
            }
        });

        return true;
    }

    private async Task<AgentRunResult> RunAgentAsync(
        string userPrompt,
        AgentPaneContext context,
        string runKey,
        string threadId,
        Action<string> onAssistantDelta,
        CancellationToken ct)
    {
        var settings = SettingsService.Current.Agent;
        var tools = await BuildToolCatalogAsync(settings, context, ct);
        var systemPrompt = BuildSystemPrompt(settings, context, tools);
        var historyContext = PrepareConversationContext(settings, threadId, userPrompt, systemPrompt);
        var provider = (settings.ActiveProvider ?? "openai").Trim().ToLowerInvariant();

        EmitUpdate(context, new AgentRuntimeUpdate
        {
            Type = AgentRuntimeUpdateType.ContextMetrics,
            ThreadId = threadId,
            EstimatedContextTokens = historyContext.EstimatedTokens,
            ContextBudgetTokens = historyContext.BudgetTokens,
            ContextNeedsCompaction = historyContext.NeedsCompaction,
            CompactionApplied = historyContext.CompactionApplied,
        });

        var result = provider switch
        {
            "anthropic" => await RunAnthropicConversationAsync(settings, userPrompt, systemPrompt, context, runKey, threadId, tools, historyContext.HistoryMessages, onAssistantDelta, ct),
            _ => await RunOpenAiConversationAsync(settings, userPrompt, systemPrompt, context, runKey, threadId, tools, historyContext.HistoryMessages, onAssistantDelta, ct),
        };

        return result with
        {
            EstimatedContextTokens = historyContext.EstimatedTokens,
            ContextBudgetTokens = historyContext.BudgetTokens,
            ContextNeedsCompaction = historyContext.NeedsCompaction,
            CompactionApplied = historyContext.CompactionApplied,
        };
    }

    private async Task<AgentRunResult> RunOpenAiConversationAsync(
        AgentSettings settings,
        string userPrompt,
        string systemPrompt,
        AgentPaneContext context,
        string runKey,
        string threadId,
        List<AgentTool> tools,
        IReadOnlyList<AgentConversationMessage> historyMessages,
        Action<string> onAssistantDelta,
        CancellationToken ct)
    {
        var baseUrl = EnsureAbsoluteBase(settings.OpenAi.BaseUrl, "https://api.openai.com/v1");
        var apiKey = SecretStoreService.GetSecret(settings.OpenAi.ApiKeySecretName);
        if (string.IsNullOrWhiteSpace(apiKey))
            return AgentRunResult.Error("OpenAI-compatible API key is not set in Settings -> Agent.");

        var model = string.IsNullOrWhiteSpace(settings.OpenAi.Model) ? "gpt-4o-mini" : settings.OpenAi.Model.Trim();
        var messages = new List<Dictionary<string, object?>>
        {
            new()
            {
                ["role"] = "system",
                ["content"] = systemPrompt,
            },
        };

        foreach (var message in historyMessages)
        {
            var role = NormalizeHistoryRole(message.Role);
            if (role is not ("user" or "assistant" or "system"))
                continue;

            messages.Add(new Dictionary<string, object?>
            {
                ["role"] = role,
                ["content"] = message.Content ?? "",
            });
        }

        messages.Add(new Dictionary<string, object?>
        {
            ["role"] = "user",
            ["content"] = userPrompt,
        });

        int inputTokens = 0;
        int outputTokens = 0;
        int totalTokens = 0;

        for (int i = 0; i < 12; i++)
        {
            ct.ThrowIfCancellationRequested();
            ApplySteeringPromptsToOpenAiMessages(messages, runKey, context, threadId);

            bool useServerStream = settings.EnableStreaming;
            var requestBody = new Dictionary<string, object?>
            {
                ["model"] = model,
                ["messages"] = messages,
                ["temperature"] = 0.2,
                ["tool_choice"] = "auto",
            };

            if (tools.Count > 0)
            {
                requestBody["tools"] = tools.Select(t => new Dictionary<string, object?>
                {
                    ["type"] = "function",
                    ["function"] = new Dictionary<string, object?>
                    {
                        ["name"] = t.Name,
                        ["description"] = t.Description,
                        ["parameters"] = t.InputSchema,
                    },
                }).ToList();
            }

            if (useServerStream)
            {
                requestBody["stream"] = true;
                requestBody["stream_options"] = new Dictionary<string, object?>
                {
                    ["include_usage"] = true,
                };
            }

            using var req = new HttpRequestMessage(HttpMethod.Post, CombineUrl(baseUrl, "/chat/completions"))
            {
                Content = JsonContent(requestBody),
            };
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

            string assistantText;
            List<AgentToolCall> toolCalls;

            if (useServerStream)
            {
                var streamed = await ReadOpenAiStreamAsync(req, onAssistantDelta, ct);
                if (!streamed.Success)
                    return AgentRunResult.Error(streamed.Error ?? "Streaming request failed.", "openai", model);

                inputTokens += streamed.InputTokens;
                outputTokens += streamed.OutputTokens;
                totalTokens += streamed.TotalTokens;

                assistantText = streamed.AssistantText;
                toolCalls = streamed.ToolCalls;
            }
            else
            {
                using var res = await _httpClient.SendAsync(req, ct);
                var body = await res.Content.ReadAsStringAsync(ct);
                if (!res.IsSuccessStatusCode)
                    return AgentRunResult.Error($"Model request failed ({(int)res.StatusCode}): {Truncate(body, 800)}", "openai", model);

                using var doc = JsonDocument.Parse(body);
                var root = doc.RootElement;
                if (TryExtractUsage(root, out var inTok, out var outTok, out var totalTok))
                {
                    inputTokens += inTok;
                    outputTokens += outTok;
                    totalTokens += totalTok;
                }

                var message = root.GetProperty("choices")[0].GetProperty("message");
                assistantText = ExtractOpenAiText(message);
                toolCalls = ExtractOpenAiToolCalls(message);
            }

            if (toolCalls.Count == 0)
            {
                if (!useServerStream && !string.IsNullOrWhiteSpace(assistantText))
                    onAssistantDelta(assistantText);

                var pendingSteering = DrainSteeringPrompts(runKey);
                if (pendingSteering.Count > 0)
                {
                    if (!string.IsNullOrWhiteSpace(assistantText))
                    {
                        messages.Add(new Dictionary<string, object?>
                        {
                            ["role"] = "assistant",
                            ["content"] = assistantText,
                        });
                    }

                    AppendOpenAiUserMessages(messages, pendingSteering, context, threadId);
                    continue;
                }

                if (!string.IsNullOrWhiteSpace(assistantText))
                {
                    return new AgentRunResult(assistantText, "openai", model, inputTokens, outputTokens, totalTokens);
                }

                return new AgentRunResult("No text response received.", "openai", model, inputTokens, outputTokens, totalTokens);
            }

            var assistantMessage = new Dictionary<string, object?>
            {
                ["role"] = "assistant",
                ["content"] = string.IsNullOrWhiteSpace(assistantText) ? null : assistantText,
                ["tool_calls"] = toolCalls.Select(tc => new Dictionary<string, object?>
                {
                    ["id"] = tc.Id,
                    ["type"] = "function",
                    ["function"] = new Dictionary<string, object?>
                    {
                        ["name"] = tc.Name,
                        ["arguments"] = tc.ArgumentsJson,
                    },
                }).ToList(),
            };
            messages.Add(assistantMessage);

            foreach (var call in toolCalls)
            {
                var result = await ExecuteToolCallAsync(tools, call, context, ct);
                messages.Add(new Dictionary<string, object?>
                {
                    ["role"] = "tool",
                    ["tool_call_id"] = call.Id,
                    ["content"] = result,
                });
            }
        }

        return new AgentRunResult("Stopped after too many tool iterations.", "openai", model, inputTokens, outputTokens, totalTokens);
    }

    private async Task<AgentRunResult> RunAnthropicConversationAsync(
        AgentSettings settings,
        string userPrompt,
        string systemPrompt,
        AgentPaneContext context,
        string runKey,
        string threadId,
        List<AgentTool> tools,
        IReadOnlyList<AgentConversationMessage> historyMessages,
        Action<string> onAssistantDelta,
        CancellationToken ct)
    {
        var baseUrl = EnsureAbsoluteBase(settings.Anthropic.BaseUrl, "https://api.anthropic.com");
        var apiKey = SecretStoreService.GetSecret(settings.Anthropic.ApiKeySecretName);
        if (string.IsNullOrWhiteSpace(apiKey))
            return AgentRunResult.Error("Anthropic API key is not set in Settings -> Agent.");

        var model = string.IsNullOrWhiteSpace(settings.Anthropic.Model) ? "claude-3-5-sonnet-latest" : settings.Anthropic.Model.Trim();
        var messages = new List<Dictionary<string, object?>>();

        foreach (var message in historyMessages)
        {
            if (string.IsNullOrWhiteSpace(message.Content))
                continue;

            var role = message.Role?.Trim().ToLowerInvariant();
            if (role is not ("assistant" or "user"))
                continue;

            messages.Add(new Dictionary<string, object?>
            {
                ["role"] = role,
                ["content"] = message.Content,
            });
        }

        messages.Add(new Dictionary<string, object?>
        {
            ["role"] = "user",
            ["content"] = userPrompt,
        });

        int inputTokens = 0;
        int outputTokens = 0;
        int totalTokens = 0;

        for (int i = 0; i < 12; i++)
        {
            ct.ThrowIfCancellationRequested();
            ApplySteeringPromptsToAnthropicMessages(messages, runKey, context, threadId);

            var requestBody = new Dictionary<string, object?>
            {
                ["model"] = model,
                ["max_tokens"] = 2048,
                ["temperature"] = 0.2,
                ["system"] = systemPrompt,
                ["messages"] = messages,
            };

            if (tools.Count > 0)
            {
                requestBody["tools"] = tools.Select(t => new Dictionary<string, object?>
                {
                    ["name"] = t.Name,
                    ["description"] = t.Description,
                    ["input_schema"] = t.InputSchema,
                }).ToList();
            }

            using var req = new HttpRequestMessage(HttpMethod.Post, CombineUrl(baseUrl, "/v1/messages"))
            {
                Content = JsonContent(requestBody),
            };
            req.Headers.Add("x-api-key", apiKey);
            req.Headers.Add("anthropic-version", "2023-06-01");

            using var res = await _httpClient.SendAsync(req, ct);
            var body = await res.Content.ReadAsStringAsync(ct);
            if (!res.IsSuccessStatusCode)
                return AgentRunResult.Error($"Model request failed ({(int)res.StatusCode}): {Truncate(body, 800)}", "anthropic", model);

            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            var contentBlocks = root.GetProperty("content");
            if (TryExtractUsage(root, out var inTok, out var outTok, out var totalTok))
            {
                inputTokens += inTok;
                outputTokens += outTok;
                totalTokens += totalTok;
            }

            var assistantBlocks = JsonSerializer.Deserialize<List<object?>>(contentBlocks.GetRawText()) ?? [];
            messages.Add(new Dictionary<string, object?>
            {
                ["role"] = "assistant",
                ["content"] = assistantBlocks,
            });

            var textBuilder = new StringBuilder();
            var toolCalls = new List<AgentToolCall>();
            foreach (var block in contentBlocks.EnumerateArray())
            {
                if (!block.TryGetProperty("type", out var typeElement))
                    continue;

                var type = typeElement.GetString() ?? "";
                if (string.Equals(type, "text", StringComparison.OrdinalIgnoreCase))
                {
                    if (block.TryGetProperty("text", out var textElement))
                        textBuilder.Append(textElement.GetString());
                    continue;
                }

                if (string.Equals(type, "tool_use", StringComparison.OrdinalIgnoreCase))
                {
                    var id = block.GetProperty("id").GetString() ?? Guid.NewGuid().ToString("N");
                    var name = block.GetProperty("name").GetString() ?? "";
                    var inputJson = block.TryGetProperty("input", out var inputElement)
                        ? inputElement.GetRawText()
                        : "{}";
                    toolCalls.Add(new AgentToolCall(id, name, inputJson));
                }
            }

            if (toolCalls.Count == 0)
            {
                var text = textBuilder.ToString().Trim();
                var finalText = string.IsNullOrWhiteSpace(text) ? "No text response received." : text;
                await EmitStreamingTextAsync(finalText, settings.EnableStreaming, onAssistantDelta, ct);

                var pendingSteering = DrainSteeringPrompts(runKey);
                if (pendingSteering.Count > 0)
                {
                    if (!string.IsNullOrWhiteSpace(finalText))
                    {
                        messages.Add(new Dictionary<string, object?>
                        {
                            ["role"] = "assistant",
                            ["content"] = finalText,
                        });
                    }

                    AppendAnthropicUserMessages(messages, pendingSteering, context, threadId);
                    continue;
                }

                return new AgentRunResult(finalText, "anthropic", model, inputTokens, outputTokens, totalTokens);
            }

            var toolResultBlocks = new List<object?>();
            foreach (var call in toolCalls)
            {
                var result = await ExecuteToolCallAsync(tools, call, context, ct);
                toolResultBlocks.Add(new Dictionary<string, object?>
                {
                    ["type"] = "tool_result",
                    ["tool_use_id"] = call.Id,
                    ["content"] = result,
                });
            }

            messages.Add(new Dictionary<string, object?>
            {
                ["role"] = "user",
                ["content"] = toolResultBlocks,
            });
        }

        return new AgentRunResult("Stopped after too many tool iterations.", "anthropic", model, inputTokens, outputTokens, totalTokens);
    }

    private async Task<List<AgentTool>> BuildToolCatalogAsync(AgentSettings settings, AgentPaneContext context, CancellationToken ct)
    {
        const string TargetSelectorsSchemaFragment = """
"workspaceId":{"type":"string"},
"workspaceName":{"type":"string"},
"workspaceIndex":{"type":"integer"},
"surfaceId":{"type":"string"},
"surfaceName":{"type":"string"},
"surfaceIndex":{"type":"integer"},
"paneId":{"type":"string"},
"paneName":{"type":"string"},
"paneIndex":{"type":"integer"}
""";

        var tools = new List<AgentTool>
        {
            new(
                "cmux_status",
                "Get cmux runtime status (version, workspace counts, selected workspace).",
                ParseSchema("""{"type":"object","properties":{},"additionalProperties":false}"""),
                async (_, _, token) => await ExecutePipeCommandAsync("STATUS", [], token)),

            new(
                "cmux_scaffold_agents_files",
                "Create AGENTS.md and skills/ scaffold in project root (or provided rootPath).",
                ParseSchema("""{"type":"object","properties":{"rootPath":{"type":"string"},"overwrite":{"type":"boolean"}},"additionalProperties":false}"""),
                async (args, paneContext, _) =>
                {
                    var rootPath = TryGetString(args, "rootPath", out var requestedRoot) && !string.IsNullOrWhiteSpace(requestedRoot)
                        ? requestedRoot.Trim()
                        : ResolveProjectRootDirectory(paneContext.WorkingDirectory);
                    if (string.IsNullOrWhiteSpace(rootPath))
                        return "Unable to resolve project root path.";

                    var overwrite = TryGetBool(args, "overwrite", out var requestedOverwrite) && requestedOverwrite;

                    Directory.CreateDirectory(rootPath);
                    var agentsPath = Path.Combine(rootPath, "agents.md");
                    var skillsRoot = Path.Combine(rootPath, "skills");
                    var sampleSkillDir = Path.Combine(skillsRoot, "sample");
                    var sampleSkillPath = Path.Combine(sampleSkillDir, "SKILL.md");

                    if (overwrite || !File.Exists(agentsPath))
                    {
                        File.WriteAllText(agentsPath, """
# AGENTS.md

## Team Instructions
- Keep responses concise and action-oriented.
- Prefer deterministic commands and verify outcomes.

## Skills
- Place skills under `skills/<skill-name>/SKILL.md`.
""");
                    }

                    Directory.CreateDirectory(sampleSkillDir);
                    if (overwrite || !File.Exists(sampleSkillPath))
                    {
                        File.WriteAllText(sampleSkillPath, """
# Sample Skill

Use this template to define a project skill.
Describe:
1. When to use it.
2. Required inputs.
3. Steps and expected output.
""");
                    }

                    return JsonSerializer.Serialize(new
                    {
                        ok = true,
                        rootPath,
                        agentsPath,
                        skillsRoot,
                        sampleSkillPath,
                    });
                }),

            new(
                "cmux_workspace_list",
                "List all workspaces.",
                ParseSchema("""{"type":"object","properties":{},"additionalProperties":false}"""),
                async (_, _, token) => await ExecutePipeCommandAsync("WORKSPACE.LIST", [], token)),

            new(
                "cmux_workspace_create",
                "Create a workspace. Optional name.",
                ParseSchema("""{"type":"object","properties":{"name":{"type":"string"}},"additionalProperties":false}"""),
                async (args, _, token) =>
                {
                    var payload = new Dictionary<string, string>();
                    if (TryGetString(args, "name", out var name))
                        payload["name"] = name;
                    return await ExecutePipeCommandAsync("WORKSPACE.CREATE", payload, token);
                }),

            new(
                "cmux_workspace_select",
                "Select a workspace by index, id, or name.",
                ParseSchema("""{"type":"object","properties":{"index":{"type":"integer"},"id":{"type":"string"},"name":{"type":"string"}},"additionalProperties":false}"""),
                async (args, _, token) =>
                {
                    var payload = new Dictionary<string, string>();
                    if (TryGetInt(args, "index", out var index))
                        payload["index"] = index.ToString();
                    if (TryGetString(args, "id", out var id))
                        payload["id"] = id;
                    if (TryGetString(args, "name", out var name))
                        payload["name"] = name;
                    return await ExecutePipeCommandAsync("WORKSPACE.SELECT", payload, token);
                }),

            new(
                "cmux_surface_create",
                "Create a new surface/tab in selected workspace.",
                ParseSchema("""{"type":"object","properties":{},"additionalProperties":false}"""),
                async (_, _, token) => await ExecutePipeCommandAsync("SURFACE.CREATE", [], token)),

            new(
                "cmux_surface_select",
                "Select a surface/tab by index, id, or name. Optional workspace selectors supported.",
                ParseSchema($$"""{"type":"object","properties":{ {{TargetSelectorsSchemaFragment}} },"additionalProperties":false}"""),
                async (args, _, token) =>
                {
                    var payload = BuildTargetSelectorPayload(args);
                    return await ExecutePipeCommandAsync("SURFACE.SELECT", payload, token);
                }),

            new(
                "cmux_split_right",
                "Split focused pane vertically (left/right).",
                ParseSchema("""{"type":"object","properties":{},"additionalProperties":false}"""),
                async (_, _, token) => await ExecutePipeCommandAsync("SPLIT.RIGHT", [], token)),

            new(
                "cmux_split_down",
                "Split focused pane horizontally (top/down).",
                ParseSchema("""{"type":"object","properties":{},"additionalProperties":false}"""),
                async (_, _, token) => await ExecutePipeCommandAsync("SPLIT.DOWN", [], token)),

            new(
                "cmux_notify",
                "Send a cmux notification.",
                ParseSchema("""{"type":"object","properties":{"title":{"type":"string"},"subtitle":{"type":"string"},"body":{"type":"string"}},"required":["body"],"additionalProperties":false}"""),
                async (args, _, token) =>
                {
                    var payload = new Dictionary<string, string>();
                    if (TryGetString(args, "title", out var title)) payload["title"] = title;
                    if (TryGetString(args, "subtitle", out var subtitle)) payload["subtitle"] = subtitle;
                    if (TryGetString(args, "body", out var body)) payload["body"] = body;
                    return await ExecutePipeCommandAsync("NOTIFY", payload, token);
                }),

            new(
                "cmux_pane_list",
                "List panes for a surface/workspace. Useful before targeting paneIndex/paneName.",
                ParseSchema($$"""{"type":"object","properties":{ {{TargetSelectorsSchemaFragment}} },"additionalProperties":false}"""),
                async (args, _, token) =>
                {
                    var payload = BuildTargetSelectorPayload(args);
                    return await ExecutePipeCommandAsync("PANE.LIST", payload, token);
                }),

            new(
                "cmux_pane_focus",
                "Focus/select a target pane by paneIndex/paneId/paneName (with optional workspace/surface selectors).",
                ParseSchema($$"""{"type":"object","properties":{ {{TargetSelectorsSchemaFragment}} },"additionalProperties":false}"""),
                async (args, _, token) =>
                {
                    var payload = BuildTargetSelectorPayload(args);
                    return await ExecutePipeCommandAsync("PANE.FOCUS", payload, token);
                }),

            new(
                "cmux_pane_read_tail",
                "Read recent output from a pane (tail view). Use this to verify what happened in another pane.",
                ParseSchema($$"""{"type":"object","properties":{"lines":{"type":"integer"},"maxChars":{"type":"integer"},{{TargetSelectorsSchemaFragment}}},"additionalProperties":false}"""),
                async (args, _, token) =>
                {
                    var payload = BuildTargetSelectorPayload(args);
                    if (TryGetInt(args, "lines", out var lines))
                        payload["lines"] = Math.Clamp(lines, 1, 5000).ToString();
                    if (TryGetInt(args, "maxChars", out var maxChars))
                        payload["maxChars"] = Math.Clamp(maxChars, 512, 200000).ToString();
                    return await ExecutePipeCommandAsync("PANE.READ", payload, token);
                }),

            new(
                "cmux_pane_write",
                "Write text into a pane input stream. Use submit=true to press Enter and run the command.",
                ParseSchema($$"""{"type":"object","properties":{"text":{"type":"string"},"submit":{"type":"boolean"},"submitKey":{"type":"string"},{{TargetSelectorsSchemaFragment}}},"required":["text"],"additionalProperties":false}"""),
                async (args, paneContext, token) =>
                {
                    if (!TryGetString(args, "text", out var text))
                        return "Missing required argument: text";

                    var submit = TryGetBool(args, "submit", out var parsedSubmit) && parsedSubmit;
                    var submitKey = TryGetString(args, "submitKey", out var requestedSubmitKey) && !string.IsNullOrWhiteSpace(requestedSubmitKey)
                        ? requestedSubmitKey.Trim().ToLowerInvariant()
                        : (settings.DefaultSubmitKey ?? "auto").Trim().ToLowerInvariant();

                    if (!HasAnyTargetSelector(args))
                    {
                        paneContext.WriteToPane(text);
                        if (submit)
                            paneContext.WriteToPane(SubmitKeyToSequence(submitKey));
                        return "ok";
                    }

                    var payload = BuildTargetSelectorPayload(args);
                    payload["text"] = text;
                    payload["submit"] = submit ? "true" : "false";
                    payload["submitKey"] = submitKey;
                    return await ExecutePipeCommandAsync("PANE.WRITE", payload, token);
                }),

            new(
                "cmux_pane_run_command",
                "Run a shell command in a target pane and optionally read tail output for confirmation.",
                ParseSchema($$"""{"type":"object","properties":{"command":{"type":"string"},"submit":{"type":"boolean"},"submitKey":{"type":"string"},"readTail":{"type":"boolean"},"waitMs":{"type":"integer"},"tailLines":{"type":"integer"},"maxChars":{"type":"integer"},{{TargetSelectorsSchemaFragment}}},"required":["command"],"additionalProperties":false}"""),
                async (args, _, token) =>
                {
                    if (!TryGetString(args, "command", out var command))
                        return "Missing required argument: command";

                    var normalizedCommand = command.TrimEnd('\r', '\n');
                    if (string.IsNullOrWhiteSpace(normalizedCommand))
                        return "Missing required argument: command";

                    var submit = !TryGetBool(args, "submit", out var parsedSubmit) || parsedSubmit;
                    var submitKey = TryGetString(args, "submitKey", out var requestedSubmitKey) && !string.IsNullOrWhiteSpace(requestedSubmitKey)
                        ? requestedSubmitKey.Trim().ToLowerInvariant()
                        : (settings.DefaultSubmitKey ?? "auto").Trim().ToLowerInvariant();
                    var selectorPayload = BuildTargetSelectorPayload(args);
                    string writeResult;
                    var submitAttemptOrder = submit
                        ? GetSubmitAttemptOrder(submitKey, settings)
                        : Array.Empty<string>();

                    if (!submit)
                    {
                        var writePayload = new Dictionary<string, string>(selectorPayload, StringComparer.Ordinal)
                        {
                            ["text"] = normalizedCommand,
                            ["submit"] = "false",
                            ["submitKey"] = submitKey,
                        };
                        writeResult = await ExecutePipeCommandAsync("PANE.WRITE", writePayload, token);
                    }
                    else
                    {
                        writeResult = "submit pending";
                    }

                    var readTail = !TryGetBool(args, "readTail", out var parsedReadTail) || parsedReadTail;
                    if (!submit && !readTail)
                        return writeResult;

                    var waitMs = Math.Clamp(settings.SubmitFallbackWaitMs, 0, 5000);
                    var hasWaitOverride = TryGetInt(args, "waitMs", out var requestedWait);
                    if (hasWaitOverride)
                        waitMs = Math.Clamp(requestedWait, 0, 5000);
                    var tailLines = 80;
                    if (TryGetInt(args, "tailLines", out var requestedLines))
                        tailLines = Math.Clamp(requestedLines, 1, 5000);
                    var maxChars = 20000;
                    if (TryGetInt(args, "maxChars", out var requestedMaxChars))
                        maxChars = Math.Clamp(requestedMaxChars, 512, 200000);
                    var readPayload = new Dictionary<string, string>(selectorPayload, StringComparer.Ordinal)
                    {
                        ["lines"] = tailLines.ToString(),
                        ["maxChars"] = maxChars.ToString(),
                    };

                    var submitTrace = new List<string>();
                    if (submit)
                    {
                        var beforeSubmit = await ExecutePipeCommandAsync("PANE.READ", readPayload, token);
                        var beforeText = TryExtractPaneReadText(beforeSubmit, out var extractedBefore) ? extractedBefore : "";

                        var matchedSubmitProfile = ResolveSubmitProfile(settings, selectorPayload, normalizedCommand, beforeText, submitKey);
                        if (matchedSubmitProfile != null)
                        {
                            var profileOrder = ParseSubmitKeySequence(matchedSubmitProfile.SubmitOrder, keepDuplicates: true);
                            if (profileOrder.Count > 0)
                                submitAttemptOrder = profileOrder;

                            if (!hasWaitOverride && matchedSubmitProfile.WaitMs >= 0)
                                waitMs = Math.Clamp(matchedSubmitProfile.WaitMs, 0, 5000);

                            submitTrace.Add($"profile: {matchedSubmitProfile.Name}");
                            submitTrace.Add($"profileOrder: {string.Join(",", submitAttemptOrder)}");
                        }

                        var repeatCount = matchedSubmitProfile == null
                            ? 1
                            : Math.Clamp(matchedSubmitProfile.RepeatCount, 1, 8);
                        var repeatDelayMs = matchedSubmitProfile == null
                            ? 0
                            : Math.Clamp(matchedSubmitProfile.DelayMs, 0, 3000);

                        bool accepted = false;
                        for (int i = 0; i < submitAttemptOrder.Count && !accepted; i++)
                        {
                            var candidate = submitAttemptOrder[i];
                            for (int repeat = 0; repeat < repeatCount && !accepted; repeat++)
                            {
                                var submitPayload = new Dictionary<string, string>(selectorPayload, StringComparer.Ordinal)
                                {
                                    ["text"] = i == 0 && repeat == 0 ? normalizedCommand : "",
                                    ["submit"] = "true",
                                    ["submitKey"] = candidate,
                                };

                                var submitResult = await ExecutePipeCommandAsync("PANE.WRITE", submitPayload, token);
                                if (i == 0 && repeat == 0)
                                    writeResult = submitResult;
                                submitTrace.Add($"{candidate}[{repeat + 1}/{repeatCount}]: {submitResult}");

                                if (waitMs > 0)
                                    await Task.Delay(waitMs, token);

                                var afterSubmit = await ExecutePipeCommandAsync("PANE.READ", readPayload, token);
                                if (TryExtractPaneReadText(afterSubmit, out var afterText))
                                {
                                    if (HasMeaningfulPaneTailChange(beforeText, afterText))
                                    {
                                        submitTrace.Add($"acceptedKey: {candidate} (try {repeat + 1})");
                                        beforeText = afterText;
                                        accepted = true;
                                        break;
                                    }

                                    submitTrace.Add($"noMeaningfulChange: {candidate} (try {repeat + 1})");
                                    beforeText = afterText;
                                }

                                if (!accepted && repeat + 1 < repeatCount && repeatDelayMs > 0)
                                    await Task.Delay(repeatDelayMs, token);
                            }
                        }
                    }

                    if (!readTail)
                        return submitTrace.Count == 0
                            ? writeResult
                            : $"writeResult:\n{writeResult}\n\nsubmitTrace:\n{string.Join("\n", submitTrace)}";

                    var readResult = await ExecutePipeCommandAsync("PANE.READ", readPayload, token);
                    return submitTrace.Count == 0
                        ? $"writeResult:\n{writeResult}\n\npaneTail:\n{readResult}"
                        : $"writeResult:\n{writeResult}\n\nsubmitTrace:\n{string.Join("\n", submitTrace)}\n\npaneTail:\n{readResult}";
                }),
        };

        if (settings.EnableBashTool)
        {
            tools.Add(new AgentTool(
                "bash_run",
                "Execute a shell command and return stdout/stderr.",
                ParseSchema("""{"type":"object","properties":{"command":{"type":"string"},"timeoutSeconds":{"type":"integer"}},"required":["command"],"additionalProperties":false}"""),
                async (args, paneContext, token) =>
                {
                    if (!TryGetString(args, "command", out var command))
                        return "Missing required argument: command";

                    var timeout = settings.BashTimeoutSeconds <= 0 ? 120 : settings.BashTimeoutSeconds;
                    if (TryGetInt(args, "timeoutSeconds", out var requested))
                        timeout = Math.Clamp(requested, 1, 1800);

                    var result = await RunShellCommandAsync(command, paneContext.WorkingDirectory, timeout, token);
                    var sb = new StringBuilder();
                    sb.AppendLine($"exitCode: {result.ExitCode}");
                    if (!string.IsNullOrWhiteSpace(result.StdOut))
                    {
                        sb.AppendLine("stdout:");
                        sb.AppendLine(Truncate(result.StdOut, 8000));
                    }
                    if (!string.IsNullOrWhiteSpace(result.StdErr))
                    {
                        sb.AppendLine("stderr:");
                        sb.AppendLine(Truncate(result.StdErr, 8000));
                    }
                    if (result.TimedOut)
                        sb.AppendLine("timedOut: true");
                    return sb.ToString().Trim();
                }));
        }

        if (settings.EnableWebSearchTool)
        {
            tools.Add(new AgentTool(
                "web_search",
                "Search the web via Exa and return top results.",
                ParseSchema("""{"type":"object","properties":{"query":{"type":"string"},"numResults":{"type":"integer"}},"required":["query"],"additionalProperties":false}"""),
                async (args, _, token) =>
                {
                    if (!TryGetString(args, "query", out var query))
                        return "Missing required argument: query";

                    var numResults = 5;
                    if (TryGetInt(args, "numResults", out var n))
                        numResults = Math.Clamp(n, 1, 20);

                    return await SearchExaAsync(settings.Exa, query, numResults, token);
                }));
        }

        foreach (var custom in settings.CustomTools.Where(t => t.Enabled && !string.IsNullOrWhiteSpace(t.Name) && !string.IsNullOrWhiteSpace(t.CommandTemplate)))
        {
            var customName = "custom_" + Slug(custom.Name);
            tools.Add(new AgentTool(
                customName,
                string.IsNullOrWhiteSpace(custom.Description) ? $"Run custom tool '{custom.Name}'." : custom.Description,
                ParseSchema("""{"type":"object","properties":{},"additionalProperties":{"type":"string"}}"""),
                async (args, paneContext, token) =>
                {
                    var rendered = RenderCommandTemplate(custom.CommandTemplate, args, paneContext.WorkingDirectory);
                    var timeout = settings.BashTimeoutSeconds <= 0 ? 120 : settings.BashTimeoutSeconds;
                    var result = await RunShellCommandAsync(rendered, paneContext.WorkingDirectory, timeout, token);
                    var sb = new StringBuilder();
                    sb.AppendLine($"exitCode: {result.ExitCode}");
                    if (!string.IsNullOrWhiteSpace(result.StdOut))
                        sb.AppendLine(Truncate(result.StdOut, 8000));
                    if (!string.IsNullOrWhiteSpace(result.StdErr))
                    {
                        sb.AppendLine("stderr:");
                        sb.AppendLine(Truncate(result.StdErr, 4000));
                    }
                    return sb.ToString().Trim();
                }));
        }

        foreach (var server in settings.McpServers.Where(s => s.Enabled && !string.IsNullOrWhiteSpace(s.Name) && !string.IsNullOrWhiteSpace(s.Command)))
        {
            try
            {
                var mcpClient = new McpServerClient(server);
                var mcpTools = await mcpClient.ListToolsAsync(ct);
                foreach (var mcpTool in mcpTools)
                {
                    var toolName = $"mcp_{Slug(server.Name)}_{Slug(mcpTool.Name)}";
                    tools.Add(new AgentTool(
                        toolName,
                        string.IsNullOrWhiteSpace(mcpTool.Description)
                            ? $"MCP tool '{mcpTool.Name}' from server '{server.Name}'."
                            : mcpTool.Description,
                        mcpTool.InputSchema,
                        async (args, _, token) =>
                        {
                            using var callClient = new McpServerClient(server);
                            return await callClient.CallToolAsync(mcpTool.Name, args, token);
                        }));
                }
            }
            catch (Exception ex)
            {
                tools.Add(new AgentTool(
                    $"mcp_{Slug(server.Name)}_error",
                    $"MCP server '{server.Name}' failed to initialize.",
                    ParseSchema("""{"type":"object","properties":{},"additionalProperties":false}"""),
                    (_, _, _) => Task.FromResult($"MCP init error for '{server.Name}': {ex.Message}")));
            }
        }

        return tools;
    }

    private static async Task<string> ExecuteToolCallAsync(List<AgentTool> tools, AgentToolCall call, AgentPaneContext context, CancellationToken ct)
    {
        var tool = tools.FirstOrDefault(t => string.Equals(t.Name, call.Name, StringComparison.OrdinalIgnoreCase));
        if (tool == null)
            return $"Unknown tool: {call.Name}";

        JsonElement args;
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(call.ArgumentsJson) ? "{}" : call.ArgumentsJson);
            args = doc.RootElement.Clone();
        }
        catch (Exception ex)
        {
            return $"Tool arguments JSON parse error: {ex.Message}";
        }

        try
        {
            return await tool.ExecuteAsync(args, context, ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return $"Tool execution failed: {ex.Message}";
        }
    }

    private async Task<string> SearchExaAsync(ExaSearchSettings exa, string query, int numResults, CancellationToken ct)
    {
        var apiKey = SecretStoreService.GetSecret(exa.ApiKeySecretName);
        if (string.IsNullOrWhiteSpace(apiKey))
            return "Exa API key is not configured in Settings -> Agent.";

        var baseUrl = EnsureAbsoluteBase(exa.BaseUrl, "https://api.exa.ai");
        using var req = new HttpRequestMessage(HttpMethod.Post, CombineUrl(baseUrl, "/search"))
        {
            Content = JsonContent(new
            {
                query,
                numResults,
            }),
        };
        req.Headers.Add("x-api-key", apiKey);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

        using var res = await _httpClient.SendAsync(req, ct);
        var body = await res.Content.ReadAsStringAsync(ct);
        if (!res.IsSuccessStatusCode)
            return $"Exa search failed ({(int)res.StatusCode}): {Truncate(body, 500)}";

        using var doc = JsonDocument.Parse(body);
        if (!doc.RootElement.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array)
            return Truncate(body, 2000);

        var sb = new StringBuilder();
        int i = 1;
        foreach (var item in results.EnumerateArray())
        {
            var title = item.TryGetProperty("title", out var t) ? t.GetString() : "(no title)";
            var url = item.TryGetProperty("url", out var u) ? u.GetString() : "";
            var text = item.TryGetProperty("text", out var x) ? x.GetString() : "";

            sb.AppendLine($"{i}. {title}");
            if (!string.IsNullOrWhiteSpace(url))
                sb.AppendLine($"   {url}");
            if (!string.IsNullOrWhiteSpace(text))
                sb.AppendLine($"   {Truncate(text, 240)}");
            i++;
        }

        return sb.ToString().Trim();
    }

    private static async Task<string> ExecutePipeCommandAsync(string command, Dictionary<string, string> args, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var handler = App.PipeServer?.OnCommand;
        if (handler == null)
            return JsonSerializer.Serialize(new { error = "cmux command handler unavailable" });

        return await handler(command, args);
    }

    private static StringContent JsonContent(object payload)
    {
        var json = JsonSerializer.Serialize(payload);
        return new StringContent(json, Encoding.UTF8, "application/json");
    }

    private static string BuildSystemPrompt(AgentSettings settings, AgentPaneContext context, IReadOnlyList<AgentTool> tools)
    {
        var userSystemPrompt = string.IsNullOrWhiteSpace(settings.SystemPrompt)
            ? "You are a pragmatic engineering assistant running inside cmux. Keep responses concise and action-oriented."
            : settings.SystemPrompt.Trim();

        var toolSummary = tools.Count == 0
            ? "No tools are currently available."
            : string.Join(", ", tools.Select(t => t.Name).OrderBy(n => n));

        var workspaceAgentContext = BuildWorkspaceAgentContext(settings, context.WorkingDirectory);

        return $"""
{userSystemPrompt}
Current context:
- workspaceId: {context.WorkspaceId}
- surfaceId: {context.SurfaceId}
- paneId: {context.PaneId}
- workingDirectory: {context.WorkingDirectory ?? "(unknown)"}
Use tools whenever they can produce reliable results.
When using shell commands, prefer short and safe commands.
Use cmux_pane_list before targeting non-focused panes.
For pane targeting selectors, paneIndex/surfaceIndex/workspaceIndex are 1-based.
For pane write/run, you can set submitKey: auto|enter|linefeed|crlf.
Available tools:
{toolSummary}
{workspaceAgentContext}
""";
    }

    private static string BuildWorkspaceAgentContext(AgentSettings settings, string? workingDirectory)
    {
        try
        {
            var projectRoot = ResolveProjectRootDirectory(workingDirectory);
            string? agentsPath = ResolveConfiguredFilePath(settings.AgentInstructionsPath, projectRoot);
            if (string.IsNullOrWhiteSpace(agentsPath) && settings.AutoDiscoverAgentFiles && !string.IsNullOrWhiteSpace(projectRoot))
                agentsPath = FindClosestFile(projectRoot, ["agents.md", "AGENTS.md"]);

            var sb = new StringBuilder();
            if (!string.IsNullOrWhiteSpace(agentsPath) && File.Exists(agentsPath))
            {
                var agentsContent = Truncate(File.ReadAllText(agentsPath), 12000);
                sb.AppendLine($"Project instructions file detected: {agentsPath}");
                if (!string.IsNullOrWhiteSpace(agentsContent))
                {
                    sb.AppendLine("AGENTS.md content:");
                    sb.AppendLine(agentsContent);
                }
            }

            var skillsRoot = ResolveConfiguredDirectoryPath(settings.SkillsRootPath, projectRoot);
            if (string.IsNullOrWhiteSpace(skillsRoot) && settings.AutoDiscoverAgentFiles && !string.IsNullOrWhiteSpace(projectRoot))
                skillsRoot = Path.Combine(projectRoot, "skills");

            if (!string.IsNullOrWhiteSpace(skillsRoot) && Directory.Exists(skillsRoot))
            {
                var skillFiles = Directory.EnumerateFiles(skillsRoot, "SKILL.md", SearchOption.AllDirectories)
                    .Take(64)
                    .ToList();

                if (skillFiles.Count > 0)
                {
                    sb.AppendLine("Local skills discovered:");
                    foreach (var file in skillFiles)
                    {
                        var relativeBase = projectRoot ?? Path.GetDirectoryName(skillsRoot) ?? skillsRoot;
                        var relative = Path.GetRelativePath(relativeBase, file);
                        sb.AppendLine($"- {relative}");
                    }
                }
            }

            return sb.ToString().Trim();
        }
        catch
        {
            return "";
        }
    }

    private static string? ResolveConfiguredFilePath(string? configuredPath, string? projectRoot)
    {
        if (string.IsNullOrWhiteSpace(configuredPath))
            return null;

        var trimmed = configuredPath.Trim();
        if (Path.IsPathRooted(trimmed))
            return trimmed;

        if (string.IsNullOrWhiteSpace(projectRoot))
            return trimmed;

        return Path.Combine(projectRoot, trimmed);
    }

    private static string? ResolveConfiguredDirectoryPath(string? configuredPath, string? projectRoot)
    {
        if (string.IsNullOrWhiteSpace(configuredPath))
            return null;

        var trimmed = configuredPath.Trim();
        if (Path.IsPathRooted(trimmed))
            return trimmed;

        if (string.IsNullOrWhiteSpace(projectRoot))
            return trimmed;

        return Path.Combine(projectRoot, trimmed);
    }

    private static string? FindClosestFile(string startDirectory, IReadOnlyList<string> candidates)
    {
        var current = new DirectoryInfo(startDirectory);
        while (current != null)
        {
            foreach (var candidate in candidates)
            {
                var fullPath = Path.Combine(current.FullName, candidate);
                if (File.Exists(fullPath))
                    return fullPath;
            }

            current = current.Parent;
        }

        return null;
    }

    private static string ResolveProjectRootDirectory(string? workingDirectory)
    {
        if (string.IsNullOrWhiteSpace(workingDirectory))
            return Environment.CurrentDirectory;

        if (!Directory.Exists(workingDirectory))
            return workingDirectory;

        var current = new DirectoryInfo(workingDirectory);
        while (current != null)
        {
            if (Directory.Exists(Path.Combine(current.FullName, ".git")))
                return current.FullName;

            current = current.Parent;
        }

        return workingDirectory;
    }

    private static void WriteAgentMessage(AgentPaneContext context, string? agentName, string? message)
    {
        if (context.WriteToPane == null || string.IsNullOrWhiteSpace(message))
            return;

        var prefix = GetShellCommentPrefix();
        var tag = string.IsNullOrWhiteSpace(agentName) ? "agent" : agentName.Trim();
        var lines = message.Replace("\r", "", StringComparison.Ordinal).Split('\n', StringSplitOptions.None);

        foreach (var raw in lines)
        {
            var line = raw.TrimEnd();
            var text = string.IsNullOrWhiteSpace(line)
                ? $"{prefix} [{tag}]"
                : $"{prefix} [{tag}] {line}";
            context.WriteToPane("\r\n" + text + "\r\n");
        }
    }

    private static string GetShellCommentPrefix()
    {
        var shell = (SettingsService.Current.DefaultShell ?? "").Trim();
        var fileName = string.IsNullOrWhiteSpace(shell)
            ? ""
            : Path.GetFileName(shell).ToLowerInvariant();

        return fileName switch
        {
            "cmd.exe" => "rem",
            _ => "#",
        };
    }

    private static string ExtractOpenAiText(JsonElement message)
    {
        if (!message.TryGetProperty("content", out var contentElement))
            return "";

        return contentElement.ValueKind switch
        {
            JsonValueKind.String => contentElement.GetString() ?? "",
            JsonValueKind.Array => string.Join(
                "",
                contentElement.EnumerateArray()
                    .Where(x => x.TryGetProperty("type", out var t) && t.GetString() == "text")
                    .Select(x => x.TryGetProperty("text", out var text) ? text.GetString() ?? "" : "")),
            _ => "",
        };
    }

    private static List<AgentToolCall> ExtractOpenAiToolCalls(JsonElement message)
    {
        var result = new List<AgentToolCall>();
        if (!message.TryGetProperty("tool_calls", out var toolCalls) || toolCalls.ValueKind != JsonValueKind.Array)
            return result;

        foreach (var tc in toolCalls.EnumerateArray())
        {
            var id = tc.TryGetProperty("id", out var idEl) ? idEl.GetString() ?? Guid.NewGuid().ToString("N") : Guid.NewGuid().ToString("N");
            var function = tc.GetProperty("function");
            var name = function.GetProperty("name").GetString() ?? "";
            var args = function.TryGetProperty("arguments", out var argEl) ? argEl.GetString() ?? "{}" : "{}";
            result.Add(new AgentToolCall(id, name, args));
        }

        return result;
    }

    private static bool TryParseHandlerCommand(string rawCommand, AgentSettings settings, out string prompt, out string handlerToken)
    {
        prompt = "";
        handlerToken = settings.Handler;
        if (string.IsNullOrWhiteSpace(rawCommand))
            return false;

        var trimmed = rawCommand.Trim();

        string token;
        string remainder;
        int space = trimmed.IndexOf(' ');
        if (space < 0)
        {
            token = trimmed;
            remainder = "";
        }
        else
        {
            token = trimmed[..space];
            remainder = trimmed[(space + 1)..];
        }

        var handlers = GetHandlers(settings);

        bool matched = handlers.Any(h => string.Equals(h, token, StringComparison.OrdinalIgnoreCase));
        if (!matched)
        {
            var normalizedToken = token.TrimEnd(':', ',');
            var agentName = (settings.AgentName ?? "").Trim();
            if (!string.IsNullOrWhiteSpace(agentName))
            {
                matched = string.Equals(normalizedToken, agentName, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(normalizedToken, "/" + agentName, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(normalizedToken, "@" + agentName, StringComparison.OrdinalIgnoreCase);
            }
        }

        if (!matched)
            return false;

        prompt = remainder.Trim();
        handlerToken = token;
        return true;
    }

    private static List<string> GetHandlers(AgentSettings settings)
    {
        var handlers = new List<string>();

        void Add(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return;

            var v = value.Trim();
            if (!v.StartsWith('/'))
                v = "/" + v;
            if (!handlers.Any(h => string.Equals(h, v, StringComparison.OrdinalIgnoreCase)))
                handlers.Add(v);
        }

        Add(settings.Handler);
        Add(settings.AgentName);

        if (!string.IsNullOrWhiteSpace(settings.AdditionalHandlers))
        {
            foreach (var part in settings.AdditionalHandlers.Split([',', ';', '\n', '\r', '\t', ' '], StringSplitOptions.RemoveEmptyEntries))
                Add(part);
        }

        if (handlers.Count == 0)
            handlers.Add("/agent");

        return handlers;
    }

    private static string EnsureAbsoluteBase(string? value, string fallback)
    {
        var candidate = string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
        if (!candidate.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !candidate.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            candidate = "https://" + candidate;
        }
        return candidate.TrimEnd('/');
    }

    private static Uri CombineUrl(string baseUrl, string path)
    {
        return new Uri(baseUrl.TrimEnd('/') + "/" + path.TrimStart('/'));
    }

    private static bool TryGetString(JsonElement args, string name, out string value)
    {
        value = "";
        if (args.ValueKind != JsonValueKind.Object || !args.TryGetProperty(name, out var prop))
            return false;

        if (prop.ValueKind == JsonValueKind.String)
        {
            value = prop.GetString() ?? "";
            return true;
        }

        value = prop.GetRawText();
        return true;
    }

    private static bool TryGetInt(JsonElement args, string name, out int value)
    {
        value = 0;
        if (args.ValueKind != JsonValueKind.Object || !args.TryGetProperty(name, out var prop))
            return false;

        if (prop.ValueKind == JsonValueKind.Number && prop.TryGetInt32(out var n))
        {
            value = n;
            return true;
        }

        if (prop.ValueKind == JsonValueKind.String && int.TryParse(prop.GetString(), out n))
        {
            value = n;
            return true;
        }

        return false;
    }

    private static bool TryGetBool(JsonElement args, string name, out bool value)
    {
        value = false;
        if (args.ValueKind != JsonValueKind.Object || !args.TryGetProperty(name, out var prop))
            return false;

        if (prop.ValueKind == JsonValueKind.True)
        {
            value = true;
            return true;
        }

        if (prop.ValueKind == JsonValueKind.False)
        {
            value = false;
            return true;
        }

        if (prop.ValueKind == JsonValueKind.String && bool.TryParse(prop.GetString(), out var parsed))
        {
            value = parsed;
            return true;
        }

        return false;
    }

    private void EnqueueSteeringPrompt(string runKey, string prompt)
    {
        if (string.IsNullOrWhiteSpace(runKey) || string.IsNullOrWhiteSpace(prompt))
            return;

        var queue = _steeringPromptsByRunKey.GetOrAdd(runKey, _ => new ConcurrentQueue<string>());
        queue.Enqueue(prompt.Trim());
    }

    private List<string> DrainSteeringPrompts(string runKey)
    {
        var prompts = new List<string>();
        if (!_steeringPromptsByRunKey.TryGetValue(runKey, out var queue))
            return prompts;

        while (queue.TryDequeue(out var prompt))
        {
            var normalized = (prompt ?? "").Trim();
            if (!string.IsNullOrWhiteSpace(normalized))
                prompts.Add(normalized);
        }

        return prompts;
    }

    private void ApplySteeringPromptsToOpenAiMessages(
        List<Dictionary<string, object?>> messages,
        string runKey,
        AgentPaneContext context,
        string threadId)
    {
        var prompts = DrainSteeringPrompts(runKey);
        if (prompts.Count == 0)
            return;

        AppendOpenAiUserMessages(messages, prompts, context, threadId);
    }

    private void AppendOpenAiUserMessages(
        List<Dictionary<string, object?>> messages,
        IReadOnlyList<string> prompts,
        AgentPaneContext context,
        string threadId)
    {
        foreach (var prompt in prompts)
        {
            messages.Add(new Dictionary<string, object?>
            {
                ["role"] = "user",
                ["content"] = prompt,
            });
        }

        EmitUpdate(context, new AgentRuntimeUpdate
        {
            Type = AgentRuntimeUpdateType.Status,
            ThreadId = threadId,
            Message = prompts.Count == 1 ? "Applied steering message" : $"Applied {prompts.Count} steering messages",
        });
    }

    private void ApplySteeringPromptsToAnthropicMessages(
        List<Dictionary<string, object?>> messages,
        string runKey,
        AgentPaneContext context,
        string threadId)
    {
        var prompts = DrainSteeringPrompts(runKey);
        if (prompts.Count == 0)
            return;

        AppendAnthropicUserMessages(messages, prompts, context, threadId);
    }

    private void AppendAnthropicUserMessages(
        List<Dictionary<string, object?>> messages,
        IReadOnlyList<string> prompts,
        AgentPaneContext context,
        string threadId)
    {
        foreach (var prompt in prompts)
        {
            messages.Add(new Dictionary<string, object?>
            {
                ["role"] = "user",
                ["content"] = prompt,
            });
        }

        EmitUpdate(context, new AgentRuntimeUpdate
        {
            Type = AgentRuntimeUpdateType.Status,
            ThreadId = threadId,
            Message = prompts.Count == 1 ? "Applied steering message" : $"Applied {prompts.Count} steering messages",
        });
    }

    private static bool HasAnyTargetSelector(JsonElement args)
    {
        if (args.ValueKind != JsonValueKind.Object)
            return false;

        string[] keys =
        [
            "workspaceId", "workspaceName", "workspaceIndex",
            "surfaceId", "surfaceName", "surfaceIndex",
            "paneId", "paneName", "paneIndex",
        ];

        foreach (var key in keys)
        {
            if (args.TryGetProperty(key, out _))
                return true;
        }

        return false;
    }

    private static Dictionary<string, string> BuildTargetSelectorPayload(JsonElement args)
    {
        var payload = new Dictionary<string, string>(StringComparer.Ordinal);

        void CopyString(string key)
        {
            if (TryGetString(args, key, out var value) && !string.IsNullOrWhiteSpace(value))
                payload[key] = value;
        }

        void CopyInt(string key)
        {
            if (TryGetInt(args, key, out var value))
                payload[key] = value.ToString();
        }

        CopyString("workspaceId");
        CopyString("workspaceName");
        CopyInt("workspaceIndex");
        CopyString("surfaceId");
        CopyString("surfaceName");
        CopyInt("surfaceIndex");
        CopyString("paneId");
        CopyString("paneName");
        CopyInt("paneIndex");

        return payload;
    }

    private static string SubmitKeyToSequence(string? submitKey)
    {
        var normalized = (submitKey ?? "auto").Trim().ToLowerInvariant();
        return normalized switch
        {
            "linefeed" or "lf" or "ctrl+j" => "\n",
            "crlf" => "\r\n",
            "none" => "",
            _ => "\r",
        };
    }

    private static IReadOnlyList<string> GetSubmitAttemptOrder(string submitKey, AgentSettings settings)
    {
        var normalized = (submitKey ?? "auto").Trim().ToLowerInvariant();
        if (normalized is not ("auto" or ""))
            return [normalized];

        if (!settings.EnableSubmitFallback)
            return ["enter"];

        var parsed = ParseSubmitKeySequence(settings.SubmitFallbackOrder, keepDuplicates: false);
        return parsed.Count == 0 ? ["enter", "linefeed", "crlf"] : parsed;
    }

    private static List<string> ParseSubmitKeySequence(string? value, bool keepDuplicates)
    {
        var list = new List<string>();
        if (string.IsNullOrWhiteSpace(value))
            return list;

        foreach (var part in value.Split([',', ';', ' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var normalized = part.Trim().ToLowerInvariant();
            if (normalized is not ("enter" or "linefeed" or "crlf" or "lf" or "cr" or "ctrl+j" or "ctrl+m"))
                continue;

            normalized = normalized switch
            {
                "lf" or "ctrl+j" => "linefeed",
                "cr" or "ctrl+m" => "enter",
                _ => normalized,
            };

            if (keepDuplicates || !list.Any(item => string.Equals(item, normalized, StringComparison.Ordinal)))
                list.Add(normalized);
        }

        return list;
    }

    private static AgentSubmitProfileConfig? ResolveSubmitProfile(
        AgentSettings settings,
        Dictionary<string, string> selectorPayload,
        string command,
        string paneTail,
        string submitKey)
    {
        if (!settings.EnableTargetSubmitProfiles || settings.SubmitProfiles.Count == 0)
            return null;

        var normalizedSubmitKey = (submitKey ?? "auto").Trim().ToLowerInvariant();
        bool autoSubmit = normalizedSubmitKey is "auto" or "";

        foreach (var profile in settings.SubmitProfiles)
        {
            if (profile == null || !profile.Enabled)
                continue;

            if (profile.AutoOnly && !autoSubmit)
                continue;

            var workspaceValue = SelectFirst(selectorPayload, ["workspaceName", "workspaceId"]);
            var surfaceValue = SelectFirst(selectorPayload, ["surfaceName", "surfaceId"]);
            var paneValue = SelectFirst(selectorPayload, ["paneName", "paneId"]);

            if (!MatchesSubmitProfilePattern(profile.WorkspacePattern, workspaceValue))
                continue;
            if (!MatchesSubmitProfilePattern(profile.SurfacePattern, surfaceValue))
                continue;
            if (!MatchesSubmitProfilePattern(profile.PanePattern, paneValue))
                continue;
            if (!MatchesSubmitProfilePattern(profile.CommandPattern, command))
                continue;
            if (!MatchesSubmitProfilePattern(profile.TailPattern, paneTail))
                continue;

            return profile;
        }

        return null;
    }

    private static string SelectFirst(Dictionary<string, string> payload, IReadOnlyList<string> keys)
    {
        foreach (var key in keys)
        {
            if (payload.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value))
                return value.Trim();
        }

        return "";
    }

    private static bool MatchesSubmitProfilePattern(string? pattern, string? value)
    {
        var normalizedPattern = (pattern ?? "").Trim();
        if (string.IsNullOrWhiteSpace(normalizedPattern))
            return true;

        var normalizedValue = value ?? "";
        if (string.IsNullOrWhiteSpace(normalizedValue))
            return false;

        if (normalizedPattern.Contains('*') || normalizedPattern.Contains('?'))
        {
            var wildcardRegex = "^" + Regex.Escape(normalizedPattern)
                .Replace("\\*", ".*", StringComparison.Ordinal)
                .Replace("\\?", ".", StringComparison.Ordinal) + "$";
            return Regex.IsMatch(normalizedValue, wildcardRegex, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }

        return normalizedValue.Contains(normalizedPattern, StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryExtractPaneReadText(string response, out string text)
    {
        text = "";
        if (string.IsNullOrWhiteSpace(response))
            return false;

        try
        {
            using var doc = JsonDocument.Parse(response);
            var root = doc.RootElement;
            if (!root.TryGetProperty("ok", out var okElement) || okElement.ValueKind != JsonValueKind.True)
                return false;

            if (!root.TryGetProperty("text", out var textElement))
                return false;

            text = textElement.GetString() ?? "";
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool HasMeaningfulPaneTailChange(string beforeText, string afterText)
    {
        var before = NormalizePaneTailText(beforeText);
        var after = NormalizePaneTailText(afterText);
        return !string.Equals(before, after, StringComparison.Ordinal);
    }

    private static string NormalizePaneTailText(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return "";

        return text.Replace("\r", "", StringComparison.Ordinal).TrimEnd();
    }

    private static JsonElement ParseSchema(string schemaJson)
    {
        using var doc = JsonDocument.Parse(schemaJson);
        return doc.RootElement.Clone();
    }

    private static string Slug(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "tool";

        var chars = value.Trim().ToLowerInvariant()
            .Select(ch => char.IsLetterOrDigit(ch) ? ch : '_')
            .ToArray();

        var slug = new string(chars);
        while (slug.Contains("__", StringComparison.Ordinal))
            slug = slug.Replace("__", "_", StringComparison.Ordinal);
        return slug.Trim('_');
    }

    private static string RenderCommandTemplate(string template, JsonElement args, string? cwd)
    {
        var rendered = template.Replace("{{cwd}}", cwd ?? "", StringComparison.OrdinalIgnoreCase);
        if (args.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in args.EnumerateObject())
            {
                var token = "{{" + property.Name + "}}";
                var value = property.Value.ValueKind == JsonValueKind.String
                    ? property.Value.GetString() ?? ""
                    : property.Value.GetRawText();
                rendered = rendered.Replace(token, value, StringComparison.OrdinalIgnoreCase);
            }
        }
        return rendered;
    }

    private static async Task<ShellCommandResult> RunShellCommandAsync(string command, string? workingDirectory, int timeoutSeconds, CancellationToken ct)
    {
        var settings = SettingsService.Current;
        var shell = settings.DefaultShell;
        var shellArgsPrefix = settings.DefaultShellArgs;

        if (string.IsNullOrWhiteSpace(shell) || !File.Exists(shell))
        {
            var system32 = Environment.GetFolderPath(Environment.SpecialFolder.System);
            shell = Path.Combine(system32, "cmd.exe");
            shellArgsPrefix = "";
        }

        var shellName = Path.GetFileName(shell).ToLowerInvariant();
        string args = shellName switch
        {
            "pwsh.exe" or "powershell.exe" => $"{shellArgsPrefix} -NoLogo -NoProfile -Command \"{EscapeForDoubleQuotes(command)}\"".Trim(),
            "wsl.exe" => $"{shellArgsPrefix} sh -lc \"{EscapeForDoubleQuotes(command)}\"".Trim(),
            "cmd.exe" => $"{shellArgsPrefix} /d /c \"{command}\"".Trim(),
            _ => $"{shellArgsPrefix} -c \"{EscapeForDoubleQuotes(command)}\"".Trim(),
        };

        var psi = new ProcessStartInfo
        {
            FileName = shell,
            Arguments = args,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = false,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        if (!string.IsNullOrWhiteSpace(workingDirectory) && Directory.Exists(workingDirectory))
            psi.WorkingDirectory = workingDirectory;

        using var process = new Process { StartInfo = psi };
        var stdOut = new StringBuilder();
        var stdErr = new StringBuilder();

        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data != null) stdOut.AppendLine(e.Data);
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data != null) stdErr.AppendLine(e.Data);
        };

        if (!process.Start())
            return new ShellCommandResult(-1, "", "Failed to start process.", false);

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(timeoutSeconds, 1, 1800)));

        bool timedOut = false;
        try
        {
            await process.WaitForExitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            timedOut = true;
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
        }

        return new ShellCommandResult(
            timedOut ? -1 : process.ExitCode,
            stdOut.ToString(),
            stdErr.ToString(),
            timedOut);
    }

    private static string EscapeForDoubleQuotes(string text)
    {
        return text.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal);
    }

    private static string Truncate(string? text, int maxLength)
    {
        if (string.IsNullOrEmpty(text) || text.Length <= maxLength)
            return text ?? "";
        return text[..maxLength] + "...";
    }

    private static string BuildRunKey(string workspaceId, string surfaceId, string paneId)
    {
        return $"{workspaceId}:{surfaceId}:{paneId}";
    }

    private string ResolveThreadId(AgentPaneContext context, AgentSettings settings, string runKey, string? explicitThreadId)
    {
        if (!string.IsNullOrWhiteSpace(explicitThreadId))
        {
            var existing = App.AgentConversationStore.GetThread(explicitThreadId.Trim());
            if (existing != null)
                return existing.Id;
        }

        if (settings.EnableConversationMemory &&
            _activeThreadByPane.TryGetValue(runKey, out var activeThreadId) &&
            !string.IsNullOrWhiteSpace(activeThreadId))
        {
            var existing = App.AgentConversationStore.GetThread(activeThreadId);
            if (existing != null)
                return existing.Id;
        }

        var created = App.AgentConversationStore.CreateThread(
            context.WorkspaceId,
            context.SurfaceId,
            context.PaneId,
            settings.AgentName);

        return created.Id;
    }

    private static string ResolveModel(AgentSettings settings)
    {
        var provider = (settings.ActiveProvider ?? "openai").Trim().ToLowerInvariant();
        return provider switch
        {
            "anthropic" => string.IsNullOrWhiteSpace(settings.Anthropic.Model) ? "claude-3-5-sonnet-latest" : settings.Anthropic.Model.Trim(),
            _ => string.IsNullOrWhiteSpace(settings.OpenAi.Model) ? "gpt-4o-mini" : settings.OpenAi.Model.Trim(),
        };
    }

    private AgentConversationContext PrepareConversationContext(AgentSettings settings, string threadId, string currentPrompt, string systemPrompt)
    {
        var maxMessages = Math.Clamp(settings.MaxContextMessages, 8, 500);
        var keepRecentOnCompaction = Math.Clamp(settings.KeepRecentMessagesOnCompaction, 4, maxMessages);
        var budgetTokens = Math.Clamp(settings.ContextBudgetTokens, 2048, 1_000_000);
        var compactThresholdPercent = Math.Clamp(settings.CompactThresholdPercent, 50, 95);
        var thresholdTokens = budgetTokens * compactThresholdPercent / 100;

        if (!settings.EnableConversationMemory || string.IsNullOrWhiteSpace(threadId))
        {
            int estimatedNoHistory = EstimateContextTokens(
                systemPrompt,
                [],
                currentPrompt);

            return new AgentConversationContext([], estimatedNoHistory, budgetTokens, estimatedNoHistory >= thresholdTokens, false);
        }

        var history = App.AgentConversationStore.GetMessages(threadId, maxMessages * 3)
            .Where(m => !string.IsNullOrWhiteSpace(m.Content))
            .Where(m =>
            {
                var role = NormalizeHistoryRole(m.Role);
                return role is "user" or "assistant" or "system";
            })
            .ToList();

        if (history.Count > maxMessages)
            history = history.TakeLast(maxMessages).ToList();

        int estimatedTokens = EstimateContextTokens(systemPrompt, history, currentPrompt);
        bool needsCompaction = estimatedTokens >= thresholdTokens;
        bool compactionApplied = false;

        if (settings.AutoCompactContext && needsCompaction && history.Count > keepRecentOnCompaction + 2)
        {
            var older = history.Take(history.Count - keepRecentOnCompaction).ToList();
            var summary = BuildCompactionSummary(older);
            if (!string.IsNullOrWhiteSpace(summary))
            {
                var summaryMessage = App.AgentConversationStore.AppendMessage(new AgentConversationMessage
                {
                    ThreadId = threadId,
                    Role = "system",
                    Content = summary,
                    IsCompactionSummary = true,
                    CreatedAtUtc = DateTime.UtcNow,
                    TotalTokens = EstimateTokens(summary),
                });

                history = history.TakeLast(keepRecentOnCompaction).ToList();
                history.Insert(0, summaryMessage);
                estimatedTokens = EstimateContextTokens(systemPrompt, history, currentPrompt);
                needsCompaction = estimatedTokens >= thresholdTokens;
                compactionApplied = true;
            }
        }

        return new AgentConversationContext(history.AsReadOnly(), estimatedTokens, budgetTokens, needsCompaction, compactionApplied);
    }

    private static string NormalizeHistoryRole(string? role)
    {
        var normalized = (role ?? "").Trim().ToLowerInvariant();
        return normalized switch
        {
            "system" => "system",
            "assistant" => "assistant",
            "tool" => "tool",
            _ => "user",
        };
    }

    private static int EstimateContextTokens(string? systemPrompt, IReadOnlyList<AgentConversationMessage> history, string? userPrompt)
    {
        int total = EstimateTokens(systemPrompt) + EstimateTokens(userPrompt);
        foreach (var msg in history)
            total += EstimateTokens(msg.Content) + 8;
        return total;
    }

    private static int EstimateTokens(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return 0;

        // Rough estimate: most LLM tokenizers average around ~4 chars/token for English text.
        return Math.Max(1, (int)Math.Ceiling(text.Length / 4.0));
    }

    private static string BuildCompactionSummary(IReadOnlyList<AgentConversationMessage> messages)
    {
        if (messages.Count == 0)
            return "";

        var sb = new StringBuilder();
        sb.AppendLine($"Context summary (auto-compacted at {DateTime.Now:yyyy-MM-dd HH:mm:ss}):");

        int count = 0;
        foreach (var message in messages.TakeLast(24))
        {
            if (string.IsNullOrWhiteSpace(message.Content))
                continue;

            var role = NormalizeHistoryRole(message.Role);
            var line = message.Content.Replace("\r", "", StringComparison.Ordinal)
                .Replace("\n", " ", StringComparison.Ordinal)
                .Trim();

            if (line.Length > 220)
                line = line[..220] + "...";

            sb.Append("- ");
            sb.Append(role);
            sb.Append(": ");
            sb.AppendLine(line);
            count++;
        }

        if (count == 0)
            return "";

        return sb.ToString().Trim();
    }

    private async Task<OpenAiStreamReadResult> ReadOpenAiStreamAsync(
        HttpRequestMessage request,
        Action<string> onAssistantDelta,
        CancellationToken ct)
    {
        using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            return OpenAiStreamReadResult.Fail($"Model request failed ({(int)response.StatusCode}): {Truncate(body, 800)}");
        }

        var mediaType = response.Content.Headers.ContentType?.MediaType ?? "";
        if (!mediaType.Contains("event-stream", StringComparison.OrdinalIgnoreCase))
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            try
            {
                using var doc = JsonDocument.Parse(body);
                var root = doc.RootElement;

                int nsInputTokens = 0;
                int nsOutputTokens = 0;
                int nsTotalTokens = 0;
                if (TryExtractUsage(root, out var inTok, out var outTok, out var totalTok))
                {
                    nsInputTokens += inTok;
                    nsOutputTokens += outTok;
                    nsTotalTokens += totalTok;
                }

                if (!root.TryGetProperty("choices", out var choices) ||
                    choices.ValueKind != JsonValueKind.Array ||
                    choices.GetArrayLength() == 0)
                {
                    return OpenAiStreamReadResult.Ok("", [], nsInputTokens, nsOutputTokens, nsTotalTokens);
                }

                var message = choices[0].GetProperty("message");
                var assistantText = ExtractOpenAiText(message);
                var nsToolCalls = ExtractOpenAiToolCalls(message);

                return OpenAiStreamReadResult.Ok(assistantText, nsToolCalls, nsInputTokens, nsOutputTokens, nsTotalTokens);
            }
            catch
            {
                return OpenAiStreamReadResult.Fail($"Unexpected non-stream response: {Truncate(body, 800)}");
            }
        }

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream, Encoding.UTF8);

        var textBuilder = new StringBuilder();
        var toolCalls = new Dictionary<int, OpenAiToolCallAccumulator>();

        int inputTokens = 0;
        int outputTokens = 0;
        int totalTokens = 0;

        while (true)
        {
            var line = await reader.ReadLineAsync(ct);
            if (line == null)
                break;

            if (string.IsNullOrWhiteSpace(line) || !line.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                continue;

            var data = line[5..].Trim();
            if (string.Equals(data, "[DONE]", StringComparison.Ordinal))
                break;

            JsonDocument? doc = null;
            try
            {
                doc = JsonDocument.Parse(data);
                var root = doc.RootElement;

                if (TryExtractUsage(root, out var inTok, out var outTok, out var totalTok))
                {
                    inputTokens += inTok;
                    outputTokens += outTok;
                    totalTokens += totalTok;
                }

                if (!root.TryGetProperty("choices", out var choices) ||
                    choices.ValueKind != JsonValueKind.Array ||
                    choices.GetArrayLength() == 0)
                {
                    continue;
                }

                var choice = choices[0];
                if (!choice.TryGetProperty("delta", out var delta) || delta.ValueKind != JsonValueKind.Object)
                    continue;

                if (delta.TryGetProperty("content", out var contentEl) && contentEl.ValueKind == JsonValueKind.String)
                {
                    var content = contentEl.GetString() ?? "";
                    if (!string.IsNullOrEmpty(content))
                    {
                        textBuilder.Append(content);
                        onAssistantDelta(content);
                    }
                }

                if (delta.TryGetProperty("tool_calls", out var toolCallsEl) && toolCallsEl.ValueKind == JsonValueKind.Array)
                {
                    foreach (var tc in toolCallsEl.EnumerateArray())
                    {
                        int index = 0;
                        if (tc.TryGetProperty("index", out var indexEl) && indexEl.TryGetInt32(out var idx))
                            index = idx;

                        if (!toolCalls.TryGetValue(index, out var acc))
                        {
                            acc = new OpenAiToolCallAccumulator();
                            toolCalls[index] = acc;
                        }

                        if (tc.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.String)
                            acc.Id = idEl.GetString() ?? acc.Id;

                        if (tc.TryGetProperty("function", out var functionEl) && functionEl.ValueKind == JsonValueKind.Object)
                        {
                            if (functionEl.TryGetProperty("name", out var nameEl) && nameEl.ValueKind == JsonValueKind.String)
                                acc.Name = nameEl.GetString() ?? acc.Name;

                            if (functionEl.TryGetProperty("arguments", out var argsEl) && argsEl.ValueKind == JsonValueKind.String)
                                acc.Arguments.Append(argsEl.GetString() ?? "");
                        }
                    }
                }
            }
            catch
            {
                // Ignore malformed stream chunk lines.
            }
            finally
            {
                doc?.Dispose();
            }
        }

        var finalizedToolCalls = toolCalls
            .OrderBy(kvp => kvp.Key)
            .Select(kvp =>
            {
                var tc = kvp.Value;
                var id = string.IsNullOrWhiteSpace(tc.Id) ? Guid.NewGuid().ToString("N") : tc.Id!;
                var name = tc.Name ?? "";
                var args = tc.Arguments.Length == 0 ? "{}" : tc.Arguments.ToString();
                return new AgentToolCall(id, name, args);
            })
            .Where(tc => !string.IsNullOrWhiteSpace(tc.Name))
            .ToList();

        return OpenAiStreamReadResult.Ok(
            textBuilder.ToString(),
            finalizedToolCalls,
            inputTokens,
            outputTokens,
            totalTokens);
    }

    private static bool TryExtractUsage(JsonElement root, out int inputTokens, out int outputTokens, out int totalTokens)
    {
        inputTokens = 0;
        outputTokens = 0;
        totalTokens = 0;

        if (!root.TryGetProperty("usage", out var usage) || usage.ValueKind != JsonValueKind.Object)
            return false;

        if (usage.TryGetProperty("prompt_tokens", out var promptTokens) && promptTokens.TryGetInt32(out var p))
            inputTokens = p;
        if (usage.TryGetProperty("completion_tokens", out var completionTokens) && completionTokens.TryGetInt32(out var c))
            outputTokens = c;
        if (usage.TryGetProperty("total_tokens", out var total) && total.TryGetInt32(out var t))
            totalTokens = t;

        if (usage.TryGetProperty("input_tokens", out var input) && input.TryGetInt32(out var inputValue))
            inputTokens = inputValue;
        if (usage.TryGetProperty("output_tokens", out var output) && output.TryGetInt32(out var outputValue))
            outputTokens = outputValue;

        if (totalTokens <= 0)
            totalTokens = Math.Max(0, inputTokens) + Math.Max(0, outputTokens);

        return inputTokens > 0 || outputTokens > 0 || totalTokens > 0;
    }

    private static async Task EmitStreamingTextAsync(string text, bool enabled, Action<string> onAssistantDelta, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(text))
            return;

        if (!enabled)
        {
            onAssistantDelta(text);
            return;
        }

        int chunkSize = text.Length <= 220 ? 18 : 36;
        int delayMs = text.Length <= 220 ? 12 : 8;

        for (int i = 0; i < text.Length; i += chunkSize)
        {
            ct.ThrowIfCancellationRequested();
            var take = Math.Min(chunkSize, text.Length - i);
            onAssistantDelta(text.Substring(i, take));
            await Task.Delay(delayMs, ct);
        }
    }

    private void EmitUpdate(AgentPaneContext context, AgentRuntimeUpdate update)
    {
        update.WorkspaceId = context.WorkspaceId;
        update.SurfaceId = context.SurfaceId;
        update.PaneId = context.PaneId;
        if (update.CreatedAtUtc == default)
            update.CreatedAtUtc = DateTime.UtcNow;

        try { RuntimeUpdated?.Invoke(update); } catch { }
    }

    public void Dispose()
    {
        foreach (var cts in _activeRuns.Values)
        {
            try { cts.Cancel(); } catch { }
            cts.Dispose();
        }
        _activeRuns.Clear();
        _activeThreadByPane.Clear();
        _steeringPromptsByRunKey.Clear();
        _httpClient.Dispose();
    }

    private sealed record AgentTool(string Name, string Description, JsonElement InputSchema, Func<JsonElement, AgentPaneContext, CancellationToken, Task<string>> ExecuteAsync);
    private sealed record AgentToolCall(string Id, string Name, string ArgumentsJson);
    private sealed record ShellCommandResult(int ExitCode, string StdOut, string StdErr, bool TimedOut);
    private sealed class OpenAiToolCallAccumulator
    {
        public string? Id { get; set; }
        public string? Name { get; set; }
        public StringBuilder Arguments { get; } = new();
    }

    private sealed record OpenAiStreamReadResult(
        bool Success,
        string AssistantText,
        List<AgentToolCall> ToolCalls,
        int InputTokens,
        int OutputTokens,
        int TotalTokens,
        string? Error)
    {
        public static OpenAiStreamReadResult Ok(string assistantText, List<AgentToolCall> toolCalls, int inputTokens, int outputTokens, int totalTokens)
            => new(true, assistantText, toolCalls, inputTokens, outputTokens, totalTokens, null);

        public static OpenAiStreamReadResult Fail(string error)
            => new(false, "", [], 0, 0, 0, error);
    }

    private sealed record AgentConversationContext(
        IReadOnlyList<AgentConversationMessage> HistoryMessages,
        int EstimatedTokens,
        int BudgetTokens,
        bool NeedsCompaction,
        bool CompactionApplied);

    private sealed record AgentRunResult(
        string Text,
        string Provider,
        string Model,
        int InputTokens,
        int OutputTokens,
        int TotalTokens,
        int EstimatedContextTokens = 0,
        int ContextBudgetTokens = 0,
        bool ContextNeedsCompaction = false,
        bool CompactionApplied = false)
    {
        public static AgentRunResult Error(string text, string provider = "", string model = "")
            => new(text, provider, model, 0, 0, 0);
    }
}

public sealed class AgentPaneContext
{
    public required string WorkspaceId { get; init; }
    public required string SurfaceId { get; init; }
    public required string PaneId { get; init; }
    public string? WorkingDirectory { get; init; }
    public required Action<string> WriteToPane { get; init; }
}

public enum AgentRuntimeUpdateType
{
    ThreadChanged,
    UserMessage,
    AssistantDelta,
    AssistantCompleted,
    Status,
    Error,
    ContextMetrics,
}

public sealed class AgentRuntimeUpdate
{
    public AgentRuntimeUpdateType Type { get; set; }
    public string WorkspaceId { get; set; } = "";
    public string SurfaceId { get; set; } = "";
    public string PaneId { get; set; } = "";
    public string ThreadId { get; set; } = "";
    public string AgentName { get; set; } = "";
    public string Message { get; set; } = "";
    public string Provider { get; set; } = "";
    public string Model { get; set; } = "";
    public int InputTokens { get; set; }
    public int OutputTokens { get; set; }
    public int TotalTokens { get; set; }
    public int EstimatedContextTokens { get; set; }
    public int ContextBudgetTokens { get; set; }
    public bool ContextNeedsCompaction { get; set; }
    public bool CompactionApplied { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}

internal sealed class McpToolDescriptor
{
    public string Name { get; init; } = "";
    public string Description { get; init; } = "";
    public JsonElement InputSchema { get; init; }
}

internal sealed class McpServerClient : IDisposable
{
    private readonly AgentMcpServerConfig _config;

    public McpServerClient(AgentMcpServerConfig config)
    {
        _config = config;
    }

    public async Task<IReadOnlyList<McpToolDescriptor>> ListToolsAsync(CancellationToken ct)
    {
        using var session = new McpProcessSession(_config);
        await session.InitializeAsync(ct);

        var response = await session.SendRequestAsync("tools/list", new Dictionary<string, object?>(), ct);
        if (!response.TryGetProperty("result", out var result) ||
            !result.TryGetProperty("tools", out var toolsElement) ||
            toolsElement.ValueKind != JsonValueKind.Array)
            return [];

        var tools = new List<McpToolDescriptor>();
        foreach (var tool in toolsElement.EnumerateArray())
        {
            var name = tool.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
            if (string.IsNullOrWhiteSpace(name))
                continue;

            var description = tool.TryGetProperty("description", out var d) ? d.GetString() ?? "" : "";
            JsonElement schema = default;
            if (tool.TryGetProperty("inputSchema", out var s1))
                schema = s1.Clone();
            else if (tool.TryGetProperty("input_schema", out var s2))
                schema = s2.Clone();
            else
            {
                using var schemaDoc = JsonDocument.Parse("""{"type":"object","properties":{},"additionalProperties":true}""");
                schema = schemaDoc.RootElement.Clone();
            }

            tools.Add(new McpToolDescriptor
            {
                Name = name,
                Description = description,
                InputSchema = schema,
            });
        }

        return tools;
    }

    public async Task<string> CallToolAsync(string toolName, JsonElement args, CancellationToken ct)
    {
        using var session = new McpProcessSession(_config);
        await session.InitializeAsync(ct);

        object? argObject = JsonSerializer.Deserialize<object>(args.GetRawText());
        var response = await session.SendRequestAsync("tools/call", new Dictionary<string, object?>
        {
            ["name"] = toolName,
            ["arguments"] = argObject ?? new Dictionary<string, object?>(),
        }, ct);

        if (response.TryGetProperty("error", out var err))
            return err.ToString();

        if (!response.TryGetProperty("result", out var result))
            return response.ToString();

        if (result.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
        {
            var sb = new StringBuilder();
            foreach (var block in content.EnumerateArray())
            {
                if (block.TryGetProperty("type", out var type) && string.Equals(type.GetString(), "text", StringComparison.OrdinalIgnoreCase))
                {
                    if (block.TryGetProperty("text", out var text))
                        sb.AppendLine(text.GetString());
                }
                else
                {
                    sb.AppendLine(block.ToString());
                }
            }
            return sb.ToString().Trim();
        }

        return result.ToString();
    }

    public void Dispose()
    {
    }
}

internal sealed class McpProcessSession : IDisposable
{
    private readonly Process _process;
    private readonly Stream _stdin;
    private readonly Stream _stdout;
    private int _nextId;
    private bool _initialized;

    public McpProcessSession(AgentMcpServerConfig config)
    {
        var psi = new ProcessStartInfo
        {
            FileName = config.Command,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        foreach (var arg in SplitArgs(config.Arguments))
            psi.ArgumentList.Add(arg);

        if (!string.IsNullOrWhiteSpace(config.WorkingDirectory) && Directory.Exists(config.WorkingDirectory))
            psi.WorkingDirectory = config.WorkingDirectory;

        _process = new Process { StartInfo = psi };
        if (!_process.Start())
            throw new InvalidOperationException($"Failed to start MCP server process: {config.Command}");

        _stdin = _process.StandardInput.BaseStream;
        _stdout = _process.StandardOutput.BaseStream;
    }

    public async Task InitializeAsync(CancellationToken ct)
    {
        if (_initialized)
            return;

        await SendRequestAsync("initialize", new Dictionary<string, object?>
        {
            ["protocolVersion"] = "2024-11-05",
            ["capabilities"] = new Dictionary<string, object?>(),
            ["clientInfo"] = new Dictionary<string, object?>
            {
                ["name"] = "cmux",
                ["version"] = "1.0.6",
            },
        }, ct);

        await SendNotificationAsync("notifications/initialized", new Dictionary<string, object?>(), ct);
        _initialized = true;
    }

    public async Task<JsonElement> SendRequestAsync(string method, object? parameters, CancellationToken ct)
    {
        int id = Interlocked.Increment(ref _nextId);
        await WriteFrameAsync(new Dictionary<string, object?>
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id,
            ["method"] = method,
            ["params"] = parameters,
        }, ct);

        while (true)
        {
            var frame = await ReadFrameAsync(ct);
            if (frame.TryGetProperty("id", out var idElement) && idElement.ValueKind == JsonValueKind.Number && idElement.TryGetInt32(out var frameId) && frameId == id)
                return frame;
        }
    }

    private async Task SendNotificationAsync(string method, object? parameters, CancellationToken ct)
    {
        await WriteFrameAsync(new Dictionary<string, object?>
        {
            ["jsonrpc"] = "2.0",
            ["method"] = method,
            ["params"] = parameters,
        }, ct);
    }

    private async Task WriteFrameAsync(object payload, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(payload);
        var body = Encoding.UTF8.GetBytes(json);
        var header = Encoding.ASCII.GetBytes($"Content-Length: {body.Length}\r\n\r\n");

        await _stdin.WriteAsync(header, ct);
        await _stdin.WriteAsync(body, ct);
        await _stdin.FlushAsync(ct);
    }

    private async Task<JsonElement> ReadFrameAsync(CancellationToken ct)
    {
        int contentLength = 0;

        while (true)
        {
            var line = await ReadAsciiLineAsync(_stdout, ct);
            if (line == null)
                throw new IOException("MCP stream closed.");

            if (line.Length == 0)
                break;

            if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
            {
                var value = line["Content-Length:".Length..].Trim();
                _ = int.TryParse(value, out contentLength);
            }
        }

        if (contentLength <= 0)
            throw new IOException("Invalid MCP frame: missing Content-Length.");

        var buffer = new byte[contentLength];
        int read = 0;
        while (read < contentLength)
        {
            int n = await _stdout.ReadAsync(buffer.AsMemory(read, contentLength - read), ct);
            if (n == 0)
                throw new IOException("MCP stream closed while reading frame.");
            read += n;
        }

        using var doc = JsonDocument.Parse(buffer);
        return doc.RootElement.Clone();
    }

    private static async Task<string?> ReadAsciiLineAsync(Stream stream, CancellationToken ct)
    {
        var bytes = new List<byte>(64);
        var one = new byte[1];

        while (true)
        {
            int n = await stream.ReadAsync(one.AsMemory(0, 1), ct);
            if (n == 0)
            {
                if (bytes.Count == 0)
                    return null;
                break;
            }

            var b = one[0];
            if (b == (byte)'\n')
                break;

            if (b != (byte)'\r')
                bytes.Add(b);
        }

        return Encoding.ASCII.GetString(bytes.ToArray());
    }

    private static IEnumerable<string> SplitArgs(string? args)
    {
        if (string.IsNullOrWhiteSpace(args))
            yield break;

        var current = new StringBuilder();
        bool inQuote = false;
        char quoteChar = '\0';

        foreach (var ch in args)
        {
            if (inQuote)
            {
                if (ch == quoteChar)
                {
                    inQuote = false;
                    continue;
                }
                current.Append(ch);
                continue;
            }

            if (ch is '"' or '\'')
            {
                inQuote = true;
                quoteChar = ch;
                continue;
            }

            if (char.IsWhiteSpace(ch))
            {
                if (current.Length > 0)
                {
                    yield return current.ToString();
                    current.Clear();
                }
                continue;
            }

            current.Append(ch);
        }

        if (current.Length > 0)
            yield return current.ToString();
    }

    public void Dispose()
    {
        try { _stdin.Dispose(); } catch { }
        try { _stdout.Dispose(); } catch { }
        try
        {
            if (!_process.HasExited)
                _process.Kill(entireProcessTree: true);
        }
        catch { }
        try { _process.Dispose(); } catch { }
    }
}
