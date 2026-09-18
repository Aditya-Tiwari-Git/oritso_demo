using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ItSupport.Api.Domain;
using ItSupport.Api.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.SignalR;

namespace ItSupport.Api.Application;

public record AppUser(string UserName, string Password, string DisplayName, string Role, bool IsActive = true);

public class CredentialStore(IWebHostEnvironment env)
{
    private readonly string _path = Path.Combine(env.ContentRootPath, "credentials.json");
    private readonly object _gate = new();
    public IReadOnlyList<AppUser> Load() => JsonSerializer.Deserialize<List<AppUser>>(File.ReadAllText(_path), new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? [];
    public AppUser? Validate(string user, string pass) => Load().FirstOrDefault(x => x.IsActive && string.Equals(x.UserName, user, StringComparison.OrdinalIgnoreCase) && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(x.Password), Encoding.UTF8.GetBytes(pass)));
    public AppUser Upsert(UserAdminRequest request)
    {
        if (request.Role is not ("Admin" or "ITSupport" or "User")) throw new ArgumentException("Role must be Admin, ITSupport, or User.");
        lock (_gate)
        {
            var users = Load().ToList(); var existing = users.FindIndex(x => x.UserName.Equals(request.UserName, StringComparison.OrdinalIgnoreCase));
            if (existing >= 0 && string.IsNullOrWhiteSpace(request.Password)) { var current = users[existing]; users[existing] = current with { DisplayName = request.DisplayName, Role = request.Role, IsActive = request.IsActive }; }
            else { if (string.IsNullOrWhiteSpace(request.Password) || request.Password.Length < 8) throw new ArgumentException("A new user's password must be at least 8 characters."); var user = new AppUser(request.UserName.Trim(), request.Password, request.DisplayName.Trim(), request.Role, request.IsActive); if (existing >= 0) users[existing] = user; else users.Add(user); }
            var temp = _path + ".tmp"; File.WriteAllText(temp, JsonSerializer.Serialize(users, new JsonSerializerOptions { WriteIndented = true })); File.Move(temp, _path, true); return users.Single(x => x.UserName.Equals(request.UserName, StringComparison.OrdinalIgnoreCase));
        }
    }
}

public class TokenService(IConfiguration config)
{
    private readonly byte[] _key = Encoding.UTF8.GetBytes(config["Auth:SigningKey"] ?? "DEMO-ONLY-CHANGE-THIS-32-CHAR-KEY!");
    public string Create(AppUser user, DateTimeOffset expires)
    {
        var payload = Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { sub = user.UserName, role = user.Role, name = user.DisplayName, exp = expires.ToUnixTimeSeconds() })));
        return payload + "." + Convert.ToHexString(HMACSHA256.HashData(_key, Encoding.UTF8.GetBytes(payload)));
    }
    public (string User, string Role, string Name)? Validate(string token)
    {
        var parts = token.Split('.'); if (parts.Length != 2) return null;
        var expected = Convert.ToHexString(HMACSHA256.HashData(_key, Encoding.UTF8.GetBytes(parts[0])));
        if (!CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(expected), Encoding.ASCII.GetBytes(parts[1]))) return null;
        try { using var doc = JsonDocument.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(parts[0]))); var r = doc.RootElement; if (r.GetProperty("exp").GetInt64() < DateTimeOffset.UtcNow.ToUnixTimeSeconds()) return null; return (r.GetProperty("sub").GetString()!, r.GetProperty("role").GetString()!, r.GetProperty("name").GetString()!); } catch { return null; }
    }
}

public record RoutingResult(int? AssignmentGroupId, string GroupName, string RuleName);
public class RoutingService(AppDbContext db)
{
    public async Task<RoutingResult> RouteAsync(string category, string subcategory, string service, string priority, string type, string text)
    {
        var rules = await db.RoutingRules.Where(x => x.IsActive).OrderBy(x => x.Order).ToListAsync();
        foreach (var rule in rules)
        {
            bool Match(string? value, string actual) => string.IsNullOrWhiteSpace(value) || string.Equals(value, actual, StringComparison.OrdinalIgnoreCase);
            var keywords = (rule.Keywords ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (Match(rule.Category, category) && Match(rule.Subcategory, subcategory) && Match(rule.Service, service) && Match(rule.Priority, priority) && Match(rule.TicketType, type) && (keywords.Length == 0 || keywords.Any(k => text.Contains(k, StringComparison.OrdinalIgnoreCase))))
            {
                var group = await db.AssignmentGroups.FindAsync(rule.AssignmentGroupId);
                return new(rule.AssignmentGroupId, group?.Name ?? "Unknown group", rule.Name);
            }
        }
        var fallback = await db.AssignmentGroups.FirstOrDefaultAsync(x => x.Name == "Service Desk") ?? await db.AssignmentGroups.FirstOrDefaultAsync(x => x.IsActive);
        return new(fallback?.Id, fallback?.Name ?? "Unassigned", "Default Service Desk fallback");
    }
}

public class TicketAccessService(AppDbContext db)
{
    public async Task<int[]> GroupIdsAsync(string user) => await db.AgentGroupMemberships.Where(x => x.UserName == user).Select(x => x.AssignmentGroupId).ToArrayAsync();
    public async Task<bool> CanViewAsync(Ticket ticket, string user, string role)
    {
        if (role == "Admin" || ticket.CreatedBy == user || ticket.AssignedAgent == user) return true;
        return role == "ITSupport" && ticket.AssignmentGroupId.HasValue && (await GroupIdsAsync(user)).Contains(ticket.AssignmentGroupId.Value);
    }
}

public class TicketService(AppDbContext db, RoutingService routing)
{
    public async Task<(Ticket Ticket, RoutingResult Routing)> CreateAsync(CreateTicketRequest request, string user, string source = "Portal")
    {
        var route = await routing.RouteAsync(request.Category, request.Subcategory, request.Service, request.Priority, request.Type, request.Title + " " + request.Description);
        var ticket = new Ticket { Number = "PENDING-" + Guid.NewGuid().ToString("N"), Type = request.Type, Title = request.Title.Trim(), Description = request.Description.Trim(), Category = request.Category, Subcategory = request.Subcategory, Service = request.Service, Priority = request.Priority, Impact = request.Impact, Urgency = request.Urgency, CreatedBy = user, AssignmentGroupId = route.AssignmentGroupId };
        ticket.History.Add(new() { Actor = user, Action = "Created", Detail = $"Ticket created via {source}." });
        ticket.History.Add(new() { Actor = "Routing Engine", Action = "Automatically Routed", Detail = $"Routed to {route.GroupName} based on rule: {route.RuleName}." });
        db.Tickets.Add(ticket); await db.SaveChangesAsync();
        ticket.Number = $"INC{ticket.Id:000000}"; await db.SaveChangesAsync();
        return (ticket, route);
    }
}

public record BotState(string Stage = "Identify", string Category = "", string Subcategory = "", string Service = "", string Title = "", string Details = "", string Impact = "", string Urgency = "", string Error = "", string Started = "", string Troubleshooting = "", string PendingField = "");

public class ChatService(AppDbContext db, TicketService tickets, IConfiguration config, IHttpClientFactory clients, ILogger<ChatService> logger, IHubContext<LiveSupportHub> liveHub)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<ChatReply> ReplyAsync(ChatRequest request, string user)
    {
        var session = request.SessionId is null ? null : await db.ChatSessions.Include(x => x.Messages).FirstOrDefaultAsync(x => x.PublicId == request.SessionId && x.UserName == user);
        session ??= new ChatSession { UserName = user }; if (session.Id == 0) db.ChatSessions.Add(session);
        var state = JsonSerializer.Deserialize<BotState>(session.StateJson, JsonOptions) ?? new();
        session.Messages.Add(new() { Role = "user", Content = request.Message });
        var input = request.Message.Trim(); var lower = input.ToLowerInvariant();
        string response; List<KnowledgeArticle> matches = [];
        Ticket? created = null; RoutingResult? route = null;

        if (request.Action == "request-live" || lower.Contains("live agent") || lower.Contains("talk to") && lower.Contains("agent"))
        {
            var live = new LiveSupportSession { RequestedBy = user, Subject = state.Title.Length > 0 ? state.Title : "Chatbot escalation", TicketId = session.TicketId };
            live.Messages.Add(new() { Sender = "System", Body = "Live support requested from the IT assistant." }); db.LiveSupportSessions.Add(live); await db.SaveChangesAsync(); await liveHub.Clients.All.SendAsync("QueueChanged");
            response = $"You’re in the live support queue. Your request reference is {live.PublicId.ToString()[..8].ToUpperInvariant()}. An available agent can now accept the conversation.";
            return await Finish(session, state with { Stage = "LiveSupport" }, response, matches, null, null, false);
        }

        if (lower.Contains("status") || (lower.Contains("my ticket") && state.Stage == "Identify"))
        {
            var mine = (await db.Tickets.Where(x => x.CreatedBy == user).ToListAsync()).OrderByDescending(x => x.UpdatedAt).FirstOrDefault();
            response = mine is null ? "You do not have any tickets yet." : $"Your latest ticket {mine.Number} is {mine.Status}, assigned to {await GroupName(mine.AssignmentGroupId)}{(mine.AssignedAgent is null ? "" : $" and agent {mine.AssignedAgent}")}.";
            return await Finish(session, state, response, matches, mine, null, false);
        }

        if (state.Stage == "Identify")
        {
            state = Identify(input, state);
            if (state.Category.Length == 0)
            {
                response = "Please describe the issue before I create or escalate anything. Include the affected application or device and what happens when you use it.";
                return await Finish(session, state, response, matches, null, null, false);
            }
            matches = await SearchKnowledge(state.Category, state.Subcategory, input);
            var guidance = matches.FirstOrDefault()?.Content ?? "I don’t have an approved knowledge article with enough detail for this issue, so additional investigation may be required.";
            response = $"This looks like {state.Category} / {state.Subcategory}.\n\n{guidance}\n\nDid these steps resolve the issue?";
            response = await EnhanceWithOpenAi(input, matches, response);
            state = state with { Stage = "Troubleshooting", Troubleshooting = "Approved knowledge guidance provided" };
        }
        else if (state.Stage == "Troubleshooting")
        {
            if (IsPositive(lower)) { response = "Great — I’ve recorded that the approved troubleshooting resolved the issue. No ticket is needed."; state = state with { Stage = "ResolvedWithoutTicket" }; }
            else { state = state with { Stage = "Collecting", PendingField = "Error" }; response = state.Subcategory == "Catch & Dispatch" ? "I’ll collect the details needed for CBS support. What is the exact error message or behavior shown?" : "I can prepare a routed incident. What exact error message or device behavior do you see?"; }
        }
        else if (state.Stage == "Collecting")
        {
            (state, response) = Collect(state, input);
        }
        else if (state.Stage == "Confirm" && (IsPositive(lower) || request.Action == "confirm-ticket"))
        {
            var result = await tickets.CreateAsync(new("Incident", state.Title, BuildDescription(state), state.Category, state.Subcategory, Priority(state.Impact, state.Urgency), state.Service, state.Impact, state.Urgency), user, "Agentic IT Assistant");
            created = result.Ticket; route = result.Routing; session.TicketId = created.Id; state = state with { Stage = "Created" };
            response = $"I’ve created {created.Number} and routed it to {route.GroupName}. The ticket includes the error, impact, timing, and troubleshooting context you provided.";
        }
        else if (state.Stage == "Confirm") response = "I have not created a ticket. Say “yes, create it” when you are ready, or tell me what you want to change in the summary.";
        else if (state.Stage == "Created") response = $"This conversation is linked to {await TicketNumber(session.TicketId)}. You can ask for its status, add more information from the ticket page, or request a live agent.";
        else response = "Tell me about a scanner jam, scanner connectivity issue, or CBS catch-and-dispatch problem and I’ll use the approved support workflow.";

        return await Finish(session, state, response, matches, created, route, state.Stage is "Collecting" or "Confirm");
    }

    private async Task<ChatReply> Finish(ChatSession session, BotState state, string response, List<KnowledgeArticle> articles, Ticket? ticket, RoutingResult? route, bool canEscalate)
    {
        session.StateJson = JsonSerializer.Serialize(state, JsonOptions); session.UpdatedAt = DateTimeOffset.UtcNow; session.Messages.Add(new() { Role = "assistant", Content = response }); await db.SaveChangesAsync();
        return new(session.PublicId, response, articles.Select(x => (object)new { x.Id, x.Title, x.Category, x.Subcategory }).ToArray(), ticket?.Id ?? session.TicketId, ticket?.Number, route?.GroupName, canEscalate, state.Stage == "Confirm", state.Stage);
    }

    private static BotState Identify(string input, BotState state)
    {
        var lower = input.ToLowerInvariant();
        if (lower.Contains("cbs") || lower.Contains("catch") || lower.Contains("dispatch")) return state with { Category = "CBS Integration", Subcategory = "Catch & Dispatch", Service = "CTS / CBS Integration", Title = "CBS catch and dispatch issue", Details = input };
        if (lower.Contains("jam")) return state with { Category = "CTS Hardware", Subcategory = "Scanner Jam", Service = "CTS Scanner", Title = "CTS scanner jam", Details = input };
        if (lower.Contains("scanner") || lower.Contains("disconnect") || lower.Contains("device not detected") || lower.Contains("connectivity")) return state with { Category = "CTS Hardware", Subcategory = "Connectivity", Service = "CTS Scanner", Title = "CTS scanner connectivity issue", Details = input };
        return state;
    }

    private static (BotState, string) Collect(BotState state, string input)
    {
        state = state.PendingField switch { "Error" => state with { Error = input, PendingField = "Impact" }, "Impact" => state with { Impact = input, PendingField = "Urgency" }, "Urgency" => state with { Urgency = input, PendingField = "Started" }, "Started" => state with { Started = input, PendingField = "Troubleshooting" }, "Troubleshooting" => state with { Troubleshooting = input, PendingField = "" }, _ => state with { PendingField = "Error" } };
        if (state.PendingField == "Impact") return (state, "Is this affecting one user/device, multiple users, or the whole branch?");
        if (state.PendingField == "Urgency") return (state, "How urgent is this: low, medium, high, or critical to clearing operations?");
        if (state.PendingField == "Started") return (state, "When did the issue start?");
        if (state.PendingField == "Troubleshooting") return (state, "What troubleshooting have you already attempted, and are logs or screenshots available?");
        state = state with { Stage = "Confirm" };
        return (state, $"I have enough information to raise this incident.\n\n• Issue: {state.Title}\n• Error: {state.Error}\n• Impact: {state.Impact}\n• Urgency: {state.Urgency}\n• Started: {state.Started}\n\nWould you like me to create and route the ticket?");
    }

    private async Task<List<KnowledgeArticle>> SearchKnowledge(string category, string subcategory, string input)
    {
        var words = input.Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(x => x.Length > 3).ToArray();
        var candidates = await db.KnowledgeArticles.Where(x => x.IsPublished && (x.Category == category || x.Subcategory == subcategory)).ToListAsync();
        return candidates.OrderByDescending(a => (a.Subcategory == subcategory ? 10 : 0) + words.Count(w => (a.Title + a.Keywords + a.Content).Contains(w, StringComparison.OrdinalIgnoreCase))).Take(3).ToList();
    }

    private async Task<string> EnhanceWithOpenAi(string question, List<KnowledgeArticle> articles, string fallback)
    {
        var key = config["OPENAI_API_KEY"]; var enabled = !string.Equals(config["OPENAI_ENABLED"], "false", StringComparison.OrdinalIgnoreCase); if (!enabled || string.IsNullOrWhiteSpace(key)) { logger.LogInformation("OpenAI disabled or OPENAI_API_KEY is not configured. Using local KB response."); return fallback; }
        var model = config["OPENAI_MODEL"] ?? "gpt-5-mini"; logger.LogInformation("OpenAI enabled. Sending grounded request using model {Model}.", model);
        try
        {
            var client = clients.CreateClient(); client.Timeout = TimeSpan.FromSeconds(30); client.DefaultRequestHeaders.Authorization = new("Bearer", key);
            var context = string.Join("\n\n", articles.Select(x => $"ARTICLE: {x.Title}\n{x.Content}"));
            var body = new { model, instructions = "You are the Oritso IT Support CRM assistant for banking CTS support. Use only the supplied approved knowledge. Give concise numbered steps, do not invent procedures, and end by asking whether the issue was resolved.", input = $"Approved knowledge:\n{context}\n\nUser issue: {question}" };
            var result = await client.PostAsJsonAsync(config["OPENAI_BASE_URL"] ?? "https://api.openai.com/v1/responses", body);
            if (!result.IsSuccessStatusCode) { logger.LogWarning("OpenAI request failed with status {Status}; using local fallback.", (int)result.StatusCode); return fallback; }
            using var json = JsonDocument.Parse(await result.Content.ReadAsStringAsync());
            var texts = new List<string>(); if (json.RootElement.TryGetProperty("output", out var output)) foreach (var item in output.EnumerateArray()) if (item.TryGetProperty("content", out var content)) foreach (var block in content.EnumerateArray()) if (block.TryGetProperty("text", out var text)) texts.Add(text.GetString() ?? "");
            var answer = string.Join("\n", texts.Where(x => x.Length > 0)); logger.LogInformation("OpenAI response succeeded."); return answer.Length > 0 ? answer : fallback;
        }
        catch (Exception ex) { logger.LogWarning(ex, "OpenAI request failed; using local fallback."); return fallback; }
    }

    private static bool IsPositive(string text) => new[] { "yes", "fixed", "resolved", "working now", "create it", "confirm" }.Any(text.Contains);
    private static string Priority(string impact, string urgency) => impact.Contains("branch", StringComparison.OrdinalIgnoreCase) || impact.Contains("multiple", StringComparison.OrdinalIgnoreCase) || urgency.Contains("critical", StringComparison.OrdinalIgnoreCase) ? "High" : "Medium";
    private static string BuildDescription(BotState s) => $"{s.Details}\n\nError/behavior: {s.Error}\nImpact: {s.Impact}\nUrgency: {s.Urgency}\nStarted: {s.Started}\nTroubleshooting/logs: {s.Troubleshooting}";
    private async Task<string> GroupName(int? id) => id is null ? "Unassigned" : (await db.AssignmentGroups.FindAsync(id))?.Name ?? "Unknown group";
    private async Task<string> TicketNumber(int? id) => id is null ? "the ticket" : (await db.Tickets.FindAsync(id))?.Number ?? "the ticket";
}
