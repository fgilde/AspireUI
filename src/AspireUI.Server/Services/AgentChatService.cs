using System.Text.Json;
using System.Text.Json.Nodes;
using AspireUI.Server.Models;

namespace AspireUI.Server.Services;

/// <summary>What one turn did: the answer, and every tool it called on the way there.</summary>
public record ChatAnswer(string Reply, List<ChatToolCall> Tools, bool ToolsAvailable);

public record ChatToolCall(string Name, string Arguments, string Result, bool Ok);

/// <summary>
/// The chat that can operate the instance. It runs the same tools the MCP server exposes, as the
/// person who is typing — nothing more than their permissions allow, and the tools they may not use
/// are not even offered to the model.
/// </summary>
public class AgentChatService(ChatStore chats, SettingsStore settings, IToolChatClient tools, IChatClient plain)
{
    /// <summary>How many tool rounds one question may take before we stop and answer with what we have.</summary>
    private const int MaxRounds = 8;

    public static bool Configured(AppSettings s) =>
        string.Equals(s.AiKind, "cli", StringComparison.OrdinalIgnoreCase)
            ? !string.IsNullOrWhiteSpace(s.AiCliTool)
            : !string.IsNullOrWhiteSpace(s.AiBaseUrl) && !string.IsNullOrWhiteSpace(s.AiModel);

    /// <summary>Tools need function calling, and only the http backend has it.</summary>
    public static bool CanUseTools(AppSettings s) =>
        !string.Equals(s.AiKind, "cli", StringComparison.OrdinalIgnoreCase);

    public string SystemPrompt(User? user, bool withTools) =>
        $"""
        You are the assistant inside AspireUI, a tool for building, deploying and hosting apps
        ({(user is null ? "an unknown user" : user.Username)} is asking). You know about .NET Aspire
        stacks, docker compose deployments, the built-in app catalog and the machines apps are
        deployed to.

        {(withTools
            ? """
              You can act on this instance through the tools you have been given. Use them rather than
              guessing: list before you change, and name what you did in your answer. You only have
              the tools this user is allowed to use — if something they ask for is not among them, say
              that they do not have the permission for it instead of trying a different tool.

              Anything that changes or removes something (deleting a stack, deploying, stopping an
              app) needs the user to have asked for it in this conversation. Do not do it on your own
              initiative, and do not do more than was asked.
              """
            : """
              You have no tools in this configuration (the AI backend is a local CLI, which cannot
              call functions), so answer with instructions the user can follow in the UI instead of
              claiming to have done something.
              """)}

        Answer in the user's language. Be brief: a couple of sentences and, where it helps, a short
        list. No markdown headings.
        """;

    public async Task<ChatAnswer> AskAsync(User? user, string sessionId, string prompt, McpTools agent,
        CancellationToken ct = default)
    {
        var s = settings.Get();
        if (!Configured(s)) throw new InvalidOperationException("no AI backend is configured");

        var history = chats.Messages(sessionId);
        chats.Add(sessionId, "user", prompt);
        // The first question becomes the title; a list of "New chat" is a list of nothing.
        if (history.Count == 0) chats.Rename(sessionId, user?.Id ?? "", prompt);

        if (!CanUseTools(s))
        {
            var text = await plain.CompleteAsync(SystemPrompt(user, withTools: false), Transcript(history, prompt), s);
            chats.Add(sessionId, "assistant", text);
            return new ChatAnswer(text, [], false);
        }

        var specs = AgentTools.SpecsFor(user);
        var messages = new JsonArray
        {
            new JsonObject { ["role"] = "system", ["content"] = SystemPrompt(user, withTools: specs.Count > 0) },
        };
        foreach (var m in history.Where(m => m.Role is "user" or "assistant"))
            messages.Add(new JsonObject { ["role"] = m.Role, ["content"] = m.Content });
        messages.Add(new JsonObject { ["role"] = "user", ["content"] = prompt });

        var called = new List<ChatToolCall>();
        for (var round = 0; round < MaxRounds; round++)
        {
            var reply = await tools.ReplyAsync(messages, specs, s, ct);
            var calls = reply["tool_calls"]?.AsArray();

            if (calls is null || calls.Count == 0)
            {
                var text = reply["content"]?.GetValue<string>() ?? "";
                chats.Add(sessionId, "assistant", text, called.Count > 0 ? called : null);
                return new ChatAnswer(text, called, true);
            }

            messages.Add(reply.DeepClone());
            foreach (var call in calls)
            {
                var (name, arguments, id) = Parse(call);
                var result = Run(agent, name, arguments, out var ok);
                called.Add(new ChatToolCall(name, arguments?.ToJsonString() ?? "{}", result, ok));
                messages.Add(new JsonObject
                {
                    ["role"] = "tool",
                    ["tool_call_id"] = id,
                    ["name"] = name,
                    ["content"] = result,
                });
            }
        }

        // Out of rounds: say so rather than pretending the last tool result was an answer.
        const string gaveUp = "I stopped after a lot of steps without reaching an answer. " +
                              "Ask me for one thing at a time and I will get further.";
        chats.Add(sessionId, "assistant", gaveUp, called);
        return new ChatAnswer(gaveUp, called, true);
    }

    private static (string Name, JsonObject? Arguments, string Id) Parse(JsonNode? call)
    {
        var name = call?["function"]?["name"]?.GetValue<string>() ?? "";
        var id = call?["id"]?.GetValue<string>() ?? Guid.NewGuid().ToString("n");
        JsonObject? arguments = null;
        // The arguments come as a json *string*, and a model sometimes sends an empty one.
        var raw = call?["function"]?["arguments"]?.GetValue<string>();
        if (!string.IsNullOrWhiteSpace(raw))
            try { arguments = JsonNode.Parse(raw!)?.AsObject(); } catch { }
        return (name, arguments, id);
    }

    /// <summary>
    /// Runs one tool. A refusal or a failure goes back to the model as text rather than ending the
    /// turn: told that it may not stop an app, it can say so; told nothing, it hangs.
    /// </summary>
    private static string Run(McpTools agent, string name, JsonObject? arguments, out bool ok)
    {
        ok = false;
        if (AgentTools.Find(name) is not { } tool) return $"there is no tool called '{name}'";
        try
        {
            var result = AgentTools.Result(AgentTools.Invoke(tool, agent, arguments));
            ok = true;
            return result.Length <= 8000 ? result : result[..8000] + "… (truncated)";
        }
        catch (Exception ex)
        {
            var real = ex is System.Reflection.TargetInvocationException { InnerException: { } inner } ? inner : ex;
            return "failed: " + real.Message;
        }
    }

    // A CLI backend gets the conversation as text, because that is all it can take.
    private static string Transcript(List<ChatMessage> history, string prompt) =>
        string.Join("\n\n", history.Where(m => m.Role is "user" or "assistant")
            .Select(m => $"{(m.Role == "user" ? "User" : "You")}: {m.Content}")
            .Append("User: " + prompt));

    public static string Describe(ChatToolCall call) =>
        JsonSerializer.Serialize(call, new JsonSerializerOptions(JsonSerializerDefaults.Web));
}
