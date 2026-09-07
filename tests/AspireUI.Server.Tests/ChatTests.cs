using System.Security.Claims;
using System.Text.Json.Nodes;
using AspireUI.Server.Models;
using AspireUI.Server.Services;
using Microsoft.AspNetCore.Http;

// The chat: sessions that belong to somebody, and a turn that may call tools as that somebody.
public class ChatTests
{
    private sealed record World(ChatStore Chats, SettingsStore Settings, UserStore Users, User User, McpTools Agent,
        string Db);

    private static World Fresh(params string[] permissions)
    {
        var root = Path.Combine(Path.GetTempPath(), "aspireui-chat-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(root);
        var db = Path.Combine(root, "aspireui.db");
        var users = new UserStore(db);
        var created = users.Create("kim", "hash", isAdmin: false);
        users.SetPermissions(created.Id, permissions.ToList());

        var settings = new SettingsStore(db);
        settings.Save(new AppSettings("https://ai.example.com", null, "a-model", "Test", "http", null));

        var ctx = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, created.Id)], "test")),
        };
        return new World(new ChatStore(db), settings, users, users.Get(created.Id)!,
            new McpTools(new CatalogService(), new RunService(graph: new ResourceGraphService()), users,
                new HttpContextAccessor { HttpContext = ctx },
                new InstancePaths(db, Path.Combine(root, "workspace"))),
            db);
    }

    // A model that answers from a script: first the tool calls it was told to make, then a sentence.
    private sealed class ScriptedModel(params JsonObject[] replies) : IToolChatClient
    {
        private int _at;
        public List<JsonArray> Seen { get; } = [];
        public List<JsonArray?> Offered { get; } = [];

        public Task<JsonObject> ReplyAsync(JsonArray messages, JsonArray? tools, AppSettings s, CancellationToken ct = default)
        {
            Seen.Add(messages.DeepClone().AsArray());
            Offered.Add(tools?.DeepClone().AsArray());
            return Task.FromResult(replies[Math.Min(_at++, replies.Length - 1)].DeepClone().AsObject());
        }
    }

    private sealed class NoChat : IChatClient
    {
        public Task<string> CompleteAsync(string system, string user, AppSettings s) => Task.FromResult("plain: " + user);
    }

    private static JsonObject Says(string text) => new() { ["role"] = "assistant", ["content"] = text };

    private static JsonObject Calls(string tool, string argumentsJson) => new()
    {
        ["role"] = "assistant",
        ["content"] = null,
        ["tool_calls"] = new JsonArray
        {
            new JsonObject
            {
                ["id"] = "call_1",
                ["type"] = "function",
                ["function"] = new JsonObject { ["name"] = tool, ["arguments"] = argumentsJson },
            },
        },
    };

    [Fact]
    public void A_session_belongs_to_one_account()
    {
        var w = Fresh();
        var mine = w.Chats.Create(w.User.Id, "Mine");
        var other = w.Chats.Create("somebody-else", "Theirs");

        Assert.Equal("Mine", Assert.Single(w.Chats.List(w.User.Id)).Title);
        Assert.NotNull(w.Chats.Get(mine.Id, w.User.Id));
        // Knowing the id of somebody else's chat is not permission to read it.
        Assert.Null(w.Chats.Get(other.Id, w.User.Id));
        Assert.False(w.Chats.Delete(other.Id, w.User.Id));
        Assert.True(w.Chats.Delete(mine.Id, w.User.Id));
    }

    [Fact]
    public void Messages_come_back_in_order_and_the_session_carries_their_count()
    {
        var w = Fresh();
        var s = w.Chats.Create(w.User.Id);
        w.Chats.Add(s.Id, "user", "one");
        w.Chats.Add(s.Id, "assistant", "two");

        Assert.Equal(["one", "two"], w.Chats.Messages(s.Id).Select(m => m.Content));
        Assert.Equal(2, w.Chats.Get(s.Id, w.User.Id)!.Messages);
    }

    [Fact]
    public async Task An_answer_without_tools_is_stored_and_names_the_session()
    {
        var w = Fresh(Perm.OpenEditor);
        var model = new ScriptedModel(Says("Two stacks, both stopped."));
        var service = new AgentChatService(w.Chats, w.Settings, model, new NoChat());
        var session = w.Chats.Create(w.User.Id);

        var answer = await service.AskAsync(w.User, session.Id, "What is deployed?", w.Agent);

        Assert.Equal("Two stacks, both stopped.", answer.Reply);
        Assert.Empty(answer.Tools);
        Assert.Equal(["user", "assistant"], w.Chats.Messages(session.Id).Select(m => m.Role));
        // The first question becomes the title.
        Assert.Equal("What is deployed?", w.Chats.Get(session.Id, w.User.Id)!.Title);
    }

    [Fact]
    public async Task A_tool_the_user_may_use_is_called_and_its_result_goes_back_to_the_model()
    {
        var w = Fresh(Perm.OpenEditor);
        var model = new ScriptedModel(
            Calls("create_stack", """{"name":"From the chat"}"""),
            Says("Done — I created it."));
        var service = new AgentChatService(w.Chats, w.Settings, model, new NoChat());
        var session = w.Chats.Create(w.User.Id);

        var answer = await service.AskAsync(w.User, session.Id, "Make me a stack called From the chat", w.Agent);

        Assert.Equal("Done — I created it.", answer.Reply);
        var call = Assert.Single(answer.Tools);
        Assert.Equal("create_stack", call.Name);
        Assert.True(call.Ok);
        Assert.Contains("From the chat", call.Result);
        // It really happened.
        Assert.Contains("From the chat", new StackStore(w.Db).List().Select(s => s.Name));

        // The second round saw the tool result as a tool message.
        var second = model.Seen[1];
        Assert.Contains("tool", second.Select(m => m!["role"]!.GetValue<string>()));
        Assert.Contains("From the chat", second.Last()!["content"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_tool_the_user_may_not_use_is_not_offered_and_a_refusal_reaches_the_model()
    {
        var w = Fresh(Perm.OpenEditor);   // no deploy
        var model = new ScriptedModel(
            Calls("deploy_to_hosting", """{"stackId":"whatever"}"""),
            Says("You are not allowed to deploy."));
        var service = new AgentChatService(w.Chats, w.Settings, model, new NoChat());
        var session = w.Chats.Create(w.User.Id);

        var answer = await service.AskAsync(w.User, session.Id, "Deploy it", w.Agent);

        // Not among the offered tools…
        var offered = model.Offered[0]!.Select(t => t!["function"]!["name"]!.GetValue<string>()).ToList();
        Assert.DoesNotContain("deploy_to_hosting", offered);
        Assert.Contains("create_stack", offered);

        // …and calling it anyway comes back as a refusal rather than an exception.
        var call = Assert.Single(answer.Tools);
        Assert.False(call.Ok);
        Assert.Contains("'deploy' permission", call.Result);
        Assert.Equal("You are not allowed to deploy.", answer.Reply);
    }

    [Fact]
    public async Task A_tool_that_does_not_exist_is_answered_rather_than_thrown()
    {
        var w = Fresh(Perm.OpenEditor);
        var model = new ScriptedModel(Calls("make_coffee", "{}"), Says("There is no such thing."));
        var service = new AgentChatService(w.Chats, w.Settings, model, new NoChat());
        var session = w.Chats.Create(w.User.Id);

        var answer = await service.AskAsync(w.User, session.Id, "Coffee please", w.Agent);
        Assert.Contains("no tool called 'make_coffee'", Assert.Single(answer.Tools).Result);
        Assert.Equal("There is no such thing.", answer.Reply);
    }

    [Fact]
    public async Task A_model_that_only_ever_calls_tools_is_stopped_and_says_so()
    {
        var w = Fresh(Perm.OpenEditor);
        var model = new ScriptedModel(Calls("list_stacks", "{}"));   // the same reply for ever
        var service = new AgentChatService(w.Chats, w.Settings, model, new NoChat());
        var session = w.Chats.Create(w.User.Id);

        var answer = await service.AskAsync(w.User, session.Id, "Go", w.Agent);
        Assert.Contains("stopped after a lot of steps", answer.Reply);
        Assert.Equal(8, answer.Tools.Count);
    }

    [Fact]
    public async Task History_is_replayed_so_the_second_question_knows_about_the_first()
    {
        var w = Fresh(Perm.OpenEditor);
        var model = new ScriptedModel(Says("first"), Says("second"));
        var service = new AgentChatService(w.Chats, w.Settings, model, new NoChat());
        var session = w.Chats.Create(w.User.Id);

        await service.AskAsync(w.User, session.Id, "one", w.Agent);
        await service.AskAsync(w.User, session.Id, "two", w.Agent);

        var contents = model.Seen[1].Select(m => m!["content"]?.GetValue<string>()).ToList();
        Assert.Contains("one", contents);
        Assert.Contains("first", contents);
        Assert.Contains("two", contents);
    }

    [Fact]
    public async Task A_cli_backend_answers_without_tools_and_says_it_has_none()
    {
        var w = Fresh(Perm.OpenEditor);
        w.Settings.Save(new AppSettings(null, null, null, "Local", "cli", "claude"));
        var service = new AgentChatService(w.Chats, w.Settings, new ScriptedModel(Says("never asked")), new NoChat());
        var session = w.Chats.Create(w.User.Id);

        var answer = await service.AskAsync(w.User, session.Id, "hello", w.Agent);
        Assert.False(answer.ToolsAvailable);
        Assert.StartsWith("plain:", answer.Reply);
        Assert.Empty(answer.Tools);
    }

    [Fact]
    public void Configured_and_tool_capable_are_two_different_questions()
    {
        Assert.False(AgentChatService.Configured(new AppSettings(null, null, null, null, null, null)));
        Assert.False(AgentChatService.Configured(new AppSettings("https://ai.example.com", null, null, null, "http", null)));
        Assert.True(AgentChatService.Configured(new AppSettings("https://ai.example.com", null, "m", null, "http", null)));
        Assert.True(AgentChatService.Configured(new AppSettings(null, null, null, null, "cli", "claude")));

        Assert.True(AgentChatService.CanUseTools(new AppSettings("https://ai.example.com", null, "m", null, "http", null)));
        Assert.False(AgentChatService.CanUseTools(new AppSettings(null, null, null, null, "cli", "claude")));
    }
}
