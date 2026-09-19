using System.Security.Cryptography;
using System.Net.Mail;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ItSupport.Api.Domain;
using ItSupport.Api.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.SignalR;

namespace ItSupport.Api.Application;

public record AppUser(string UserName, string Password, string DisplayName, string Role, string Email = "", bool IsActive = true);

public class CredentialStore(IWebHostEnvironment env)
{
    private readonly string _path = Path.Combine(env.ContentRootPath, "credentials.json");
    private readonly object _gate = new();
    public IReadOnlyList<AppUser> Load() => JsonSerializer.Deserialize<List<AppUser>>(File.ReadAllText(_path), new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? [];
    public AppUser? Validate(string user, string pass) => Load().FirstOrDefault(x => x.IsActive && string.Equals(x.UserName, user, StringComparison.OrdinalIgnoreCase) && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(x.Password), Encoding.UTF8.GetBytes(pass)));
    public AppUser Upsert(UserAdminRequest request)
    {
        if (request.Role is not ("Admin" or "ITSupport" or "User")) throw new ArgumentException("Role must be Admin, ITSupport, or User.");
        try { _ = new MailAddress(request.Email); } catch { throw new ArgumentException("Enter a valid email address."); }
        lock (_gate)
        {
            var users = Load().ToList(); var existing = users.FindIndex(x => x.UserName.Equals(request.UserName, StringComparison.OrdinalIgnoreCase));
            if (existing >= 0 && string.IsNullOrWhiteSpace(request.Password)) { var current = users[existing]; users[existing] = current with { DisplayName = request.DisplayName.Trim(), Email = request.Email.Trim(), Role = request.Role, IsActive = request.IsActive }; }
            else { if (string.IsNullOrWhiteSpace(request.Password) || request.Password.Length < 8) throw new ArgumentException("A new user's password must be at least 8 characters."); var user = new AppUser(request.UserName.Trim(), request.Password, request.DisplayName.Trim(), request.Role, request.Email.Trim(), request.IsActive); if (existing >= 0) users[existing] = user; else users.Add(user); }
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
public static partial class RuleMatcher
{
    [GeneratedRegex(@"https?://|www\.", RegexOptions.IgnoreCase)] private static partial Regex ProtocolPattern();
    [GeneratedRegex(@"[^a-z0-9.\-]+", RegexOptions.IgnoreCase)] private static partial Regex SeparatorPattern();
    public static string Normalize(string value)
    {
        string decoded; try { decoded = Uri.UnescapeDataString(value ?? ""); } catch (UriFormatException) { decoded = value ?? ""; }
        return SeparatorPattern().Replace(ProtocolPattern().Replace(decoded.ToLowerInvariant(), ""), " ").Trim();
    }
    public static bool ContainsAny(string actual, string? patterns)
    {
        var terms = (patterns ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(Normalize).Where(x => x.Length > 0).ToArray();
        if (terms.Length == 0) return true;
        var normalized = Normalize(actual); return terms.Any(normalized.Contains);
    }
}

public class RoutingService(AppDbContext db)
{
    public async Task<RoutingResult> RouteAsync(string category, string subcategory, string service, string priority, string type, string text)
    {
        var rules = await db.RoutingRules.Where(x => x.IsActive).OrderBy(x => x.Order).ToListAsync();
        foreach (var rule in rules)
        {
            bool Match(string? value, string actual) => string.IsNullOrWhiteSpace(value) || string.Equals(value, actual, StringComparison.OrdinalIgnoreCase);
            if (Match(rule.Category, category) && Match(rule.Subcategory, subcategory) && Match(rule.Service, service) && Match(rule.Priority, priority) && Match(rule.TicketType, type) && RuleMatcher.ContainsAny(text, rule.Keywords))
            {
                var group = await db.AssignmentGroups.FindAsync(rule.AssignmentGroupId);
                return new(rule.AssignmentGroupId, group?.Name ?? "Unknown group", rule.Name);
            }
        }
        var fallback = await db.AssignmentGroups.FirstOrDefaultAsync(x => x.Name == "Service Desk") ?? await db.AssignmentGroups.FirstOrDefaultAsync(x => x.IsActive);
        return new(fallback?.Id, fallback?.Name ?? "Unassigned", "Default Service Desk fallback");
    }
}

public record PriorityResult(string Priority, string RuleName);
public class PriorityService(AppDbContext db)
{
    public async Task<PriorityResult> CalculateAsync(string category, string service, string text)
    {
        var rules = await db.PriorityRules.Where(x => x.IsActive).OrderBy(x => x.Order).ThenBy(x => x.Id).ToListAsync();
        foreach (var rule in rules)
        {
            bool Match(string? expected, string actual) => string.IsNullOrWhiteSpace(expected) || string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase);
            if (Match(rule.Category, category) && Match(rule.Service, service) && RuleMatcher.ContainsAny(text, rule.Keywords)) return new(rule.Priority, rule.Name);
        }
        return new("Low", "Built-in safe fallback");
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

public class TicketService(AppDbContext db, RoutingService routing, PriorityService priorities)
{
    public async Task<(Ticket Ticket, RoutingResult Routing)> CreateAsync(CreateTicketRequest request, string user, string source = "Portal")
    {
        if (request.Type is not ("Incident" or "Major Incident" or "Service Request")) throw new ArgumentException("Select a valid issue type.");
        if (string.IsNullOrWhiteSpace(request.Title) || request.Title.Length > 140 || string.IsNullOrWhiteSpace(request.Description) || request.Description.Length > 4000) throw new ArgumentException("Title and description are required and must fit the allowed lengths.");
        var category = await db.Categories.Include(x => x.Subcategories).FirstOrDefaultAsync(x => x.IsActive && x.Name == request.Category);
        if (category is null || !category.Subcategories.Any(x => x.IsActive && x.Name == request.Subcategory)) throw new ArgumentException("Select a configured category and subcategory.");
        if (!await db.Services.AnyAsync(x => x.IsActive && x.Name == request.Service)) throw new ArgumentException("Select a configured affected service.");
        var priority = await priorities.CalculateAsync(request.Category, request.Service, request.Title + " " + request.Description);
        var route = await routing.RouteAsync(request.Category, request.Subcategory, request.Service, priority.Priority, request.Type, request.Title + " " + request.Description);
        var ticket = new Ticket { Number = "PENDING-" + Guid.NewGuid().ToString("N"), Type = request.Type, Title = request.Title.Trim(), Description = request.Description.Trim(), Category = request.Category, Subcategory = request.Subcategory, Service = request.Service, Priority = priority.Priority, Impact = request.Impact, Urgency = request.Urgency, CreatedBy = user, AssignmentGroupId = route.AssignmentGroupId };
        ticket.History.Add(new() { Actor = user, Action = "Created", Detail = $"Ticket created via {source}." });
        ticket.History.Add(new() { Actor = "Routing Engine", Action = "Automatically Routed", Detail = $"Routed to {route.GroupName} based on rule: {route.RuleName}." });
        ticket.History.Add(new() { Actor = "Priority Engine", Action = "Priority Calculated", Detail = $"Calculated {priority.Priority} based on rule: {priority.RuleName}." });
        db.Tickets.Add(ticket); await db.SaveChangesAsync();
        ticket.Number = $"INC{ticket.Id:000000}"; await db.SaveChangesAsync();
        return (ticket, route);
    }
}

public record BotState(string Stage = "Identify", string Category = "", string Subcategory = "", string Service = "", string Title = "", string Details = "", string Impact = "Single User", string Urgency = "Medium", string Error = "", string Started = "", string Troubleshooting = "", string PendingField = "");

public class ChatService(AppDbContext db, TicketService tickets, IConfiguration config, IHttpClientFactory clients, ILogger<ChatService> logger, IHubContext<LiveSupportHub> liveHub)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private sealed record TroubleshootingScenario(string Title, string[] Applications, string[] Symptoms);
    private static readonly TroubleshootingScenario[] GeneralScenarios =
    [
        new("Clear Outlook cache", ["outlook"], ["cache", "cached", "clear data", "temporary files"]),
        new("Outlook Synchronization and Connectivity", ["outlook"], ["sync", "synchroniz", "not receiving", "not sending", "send receive", "offline", "connectivity", "connection"]),
        new("Outlook not responding", ["outlook"], ["stuck", "frozen", "freeze", "not responding", "hang", "won't open", "will not open", "stuck on loading"]),
        new("Microsoft Teams troubleshooting", ["teams", "microsoft teams", "ms teams"], ["cache", "stuck", "frozen", "freeze", "not responding", "hang", "loading", "won't open", "will not open", "connectivity", "connection"]),
        new("Clear browser cache and cookies", ["browser", "chrome", "edge", "firefox", "safari", "website", "web page"], ["cache", "cookie", "strange", "strangely", "not loading", "loading issue", "slow", "outdated", "old version", "behaving"])
    ];

    public async Task<ChatReply> ReplyAsync(ChatRequest request, string user)
    {
        var session = request.SessionId is null ? null : await db.ChatSessions.Include(x => x.Messages).FirstOrDefaultAsync(x => x.PublicId == request.SessionId && x.UserName == user);
        session ??= new ChatSession { UserName = user }; if (session.Id == 0) db.ChatSessions.Add(session);
        var state = JsonSerializer.Deserialize<BotState>(session.StateJson, JsonOptions) ?? new();
        session.Messages.Add(new() { Role = "user", Content = request.Message });
        var input = request.Message.Trim(); var lower = input.ToLowerInvariant(); var matches = new List<KnowledgeArticle>();

        if (request.Action == "request-live" || lower.Contains("live agent") || lower.Contains("talk to") && lower.Contains("agent"))
        {
            var live = new LiveSupportSession { RequestedBy = user, Subject = state.Title.Length > 0 ? state.Title : "Chatbot escalation", TicketId = session.TicketId };
            live.Messages.Add(new() { Sender = "System", Body = "Conversation transferred from the Oritso IT assistant. Previous context follows." });
            foreach (var message in session.Messages.OrderBy(x => x.CreatedAt).TakeLast(12)) live.Messages.Add(new() { Sender = message.Role == "user" ? user : "Oritso Assistant", Body = message.Content, CreatedAt = message.CreatedAt });
            live.Messages.Add(new() { Sender = "System", Body = "Waiting for an available support agent." }); db.LiveSupportSessions.Add(live); await db.SaveChangesAsync(); await liveHub.Clients.All.SendAsync("QueueChanged");
            return await Finish(session, state with { Stage = "LiveSupport" }, $"Connecting to support… You are waiting for an agent. Reference {live.PublicId.ToString()[..8].ToUpperInvariant()}.", matches, liveSessionId: live.PublicId, liveStatus: live.Status);
        }

        if (lower.Contains("status") || lower.Contains("my ticket"))
        {
            var mine = (await db.Tickets.Where(x => x.CreatedBy == user).ToListAsync()).OrderByDescending(x => x.UpdatedAt).FirstOrDefault();
            var message = mine is null ? "You do not have any tickets yet." : $"Your latest ticket {mine.Number} is {mine.Status}, assigned to {await GroupName(mine.AssignmentGroupId)}{(mine.AssignedAgent is null ? "" : $" and agent {mine.AssignedAgent}")}.";
            return await Finish(session, state, message, matches, ticket: mine);
        }

        if (state.Stage is "ResolvedWithoutTicket" or "Created" or "LiveSupport" or "LiveSupportEnded") state = new();
        if (IsGreeting(lower) && state.Stage == "Identify") return await Finish(session, state, "Hello! I can help with browser, Outlook, Teams, scanner and CBS issues, approved troubleshooting, ticket status, support tickets, or a live support agent. What can I help you with?", matches);
        if (IsThanks(lower) && state.Stage == "Identify") return await Finish(session, state, "You’re welcome. Describe another IT issue whenever you’re ready and I’ll start a fresh support flow.", matches);
        if (lower.Contains("what can you help") || lower is "help" or "i have another issue") return await Finish(session, new(), "I can troubleshoot configured IT services, search approved knowledge, check ticket status, prepare a reviewed support ticket, or connect you to a live agent. What is happening?", matches);
        if (lower.Contains("what is cbs")) return await Finish(session, state, "CBS means Core Banking Solution—the platform used for core banking operations and connected services. If you have an access or URL error, share the address and error text and I’ll apply the configured CBS rules.", matches);
        if (lower.Contains("reset") && lower.Contains("password")) return await Finish(session, state, "Use your organization’s approved self-service password reset process or contact the Service Desk. Never share a password or one-time code here. If the approved reset fails, you can review a prefilled ticket.", matches, canCreateTicket: true, draft: Draft(new() { Category = "General", Subcategory = "Software", Service = "Branch IT", Title = "Password reset assistance", Details = input }));

        if (request.Action == "create-ticket" || lower.Contains("create a ticket") || lower.Contains("raise a ticket"))
        {
            if (state.Category.Length == 0) state = await IdentifyAsync(input, state);
            if (state.Category.Length == 0) state = state with { Category = "General", Subcategory = "Software", Service = "Branch IT", Title = "General IT support request", Details = input };
            state = state with { Stage = "TicketForm" };
            return await Finish(session, state, "Review the prefilled details. Priority is calculated securely when you submit and cannot be overridden.", matches, canCreateTicket: true, draft: Draft(state));
        }

        string response;
        if (state.Stage == "Identify")
        {
            state = await IdentifyAsync(input, state);
            if (state.Category.Length == 0) return await Finish(session, state, "Is this an IT problem, a service request, or a question about a specific application or device? A little more detail will help me find the right guidance.", matches);
            matches = await SearchKnowledge(state.Category, state.Subcategory, input, state.Title);
            var guidance = matches.FirstOrDefault()?.Content ?? "I don’t have an approved knowledge article for this issue. Check whether the affected application can be restarted safely, record the exact error, and avoid repeated retries that could duplicate a transaction.";
            response = await EnhanceWithOpenAi(input, matches, $"This looks like {state.Category} / {state.Subcategory}.\n\n{guidance}\n\nDid these steps resolve the issue?");
            state = state with { Stage = "Troubleshooting", Troubleshooting = "Approved guidance provided" };
        }
        else if (state.Stage == "Troubleshooting")
        {
            if (IsPositive(lower)) { response = "Great—the current issue is marked resolved. No ticket was created. You can describe another unrelated problem whenever you’re ready."; state = state with { Stage = "ResolvedWithoutTicket" }; }
            else { response = "The troubleshooting did not resolve this issue. Choose the next step: connect to a live support agent, or review a prefilled support-ticket form."; state = state with { Stage = "OfferActions", Error = input }; }
        }
        else if (state.Stage == "OfferActions") response = "Choose Talk to Live Agent for real-time help, or Create Support Ticket to review and edit the prefilled details.";
        else if (state.Stage == "TicketForm") response = "Your ticket form is ready below. Review the details and submit when ready.";
        else response = "Tell me about a scanner issue, CBS access problem, another configured IT service, or a general support question.";

        var offers = state.Stage == "OfferActions";
        return await Finish(session, state, response, matches, offers, offers || state.Stage == "TicketForm", offers || state.Stage == "TicketForm" ? Draft(state) : null);
    }

    public async Task<ChatReply?> CreateTicketAsync(BotTicketRequest request, string user)
    {
        var session = await db.ChatSessions.Include(x => x.Messages).FirstOrDefaultAsync(x => x.PublicId == request.SessionId && x.UserName == user); if (session is null) return null;
        var state = JsonSerializer.Deserialize<BotState>(session.StateJson, JsonOptions) ?? new();
        var context = string.Join("\n", session.Messages.OrderBy(x => x.CreatedAt).TakeLast(10).Select(x => $"{x.Role}: {x.Content}"));
        var description = $"{request.Description.Trim()}\n\nConversation context:\n{context}"; if (description.Length > 4000) description = description[..4000];
        var result = await tickets.CreateAsync(new(request.Type, request.Title, description, request.Category, request.Subcategory, null, request.Service, request.Impact, request.Urgency), user, "Agentic IT Assistant form");
        session.TicketId = result.Ticket.Id; state = state with { Stage = "Created" };
        return await Finish(session, state, $"Ticket {result.Ticket.Number} was created with {result.Ticket.Priority} priority and routed to {result.Routing.GroupName}. You can track it from My Tickets.", [], ticket: result.Ticket, route: result.Routing);
    }

    private async Task<ChatReply> Finish(ChatSession session, BotState state, string response, List<KnowledgeArticle> articles, bool canEscalate = false, bool canCreateTicket = false, object? draft = null, Guid? liveSessionId = null, string? liveStatus = null, Ticket? ticket = null, RoutingResult? route = null)
    {
        session.StateJson = JsonSerializer.Serialize(state, JsonOptions); session.UpdatedAt = DateTimeOffset.UtcNow; session.Messages.Add(new() { Role = "assistant", Content = response }); await db.SaveChangesAsync();
        return new(session.PublicId, response, articles.Select(x => (object)new { x.Id, x.Title, x.Category, x.Subcategory }).ToArray(), ticket?.Id ?? session.TicketId, ticket?.Number, route?.GroupName, ticket?.Priority, canEscalate, canCreateTicket, draft, liveSessionId, liveStatus, state.Stage);
    }

    private static object Draft(BotState state) => new { type = "Incident", title = state.Title.Length > 0 ? state.Title : "IT support request", description = state.Details.Length > 0 ? state.Details : state.Error, category = state.Category.Length > 0 ? state.Category : "General", subcategory = state.Subcategory.Length > 0 ? state.Subcategory : "Software", service = state.Service.Length > 0 ? state.Service : "Branch IT", impact = state.Impact, urgency = state.Urgency };

    private async Task<BotState> IdentifyAsync(string input, BotState state)
    {
        var lower = input.ToLowerInvariant();
        if (lower.Contains("cookie")) return state with { Category = "General", Subcategory = "Software", Service = "Branch IT", Title = "Clear browser cache and cookies", Details = input };
        var generalScenario = GeneralScenarios.FirstOrDefault(s => s.Applications.Any(lower.Contains) && s.Symptoms.Any(lower.Contains));
        if (generalScenario is not null) return state with { Category = "General", Subcategory = "Software", Service = "Branch IT", Title = generalScenario.Title, Details = input };
        if (lower.Contains("cbs") || lower.Contains("core banking") || lower.Contains("catch") || lower.Contains("dispatch")) return state with { Category = "CBS Integration", Subcategory = "Catch & Dispatch", Service = "CTS / CBS Integration", Title = "CBS access or integration issue", Details = input };
        var domainRules = await db.RoutingRules.Where(x => x.IsActive && x.Keywords != null).OrderBy(x => x.Order).ToListAsync();
        foreach (var rule in domainRules.Where(x => !string.IsNullOrWhiteSpace(x.Keywords) && RuleMatcher.ContainsAny(input, x.Keywords))) { var group = await db.AssignmentGroups.FindAsync(rule.AssignmentGroupId); if (group?.Name.Contains("CBS", StringComparison.OrdinalIgnoreCase) == true) return state with { Category = "CBS Integration", Subcategory = "Catch & Dispatch", Service = "CTS / CBS Integration", Title = "CBS URL or host access issue", Details = input }; }
        if (lower.Contains("jam")) return state with { Category = "CTS Hardware", Subcategory = "Scanner Jam", Service = "CTS Scanner", Title = "CTS scanner jam", Details = input };
        if (lower.Contains("scanner") || lower.Contains("scanning") || lower.Contains("disconnect") || lower.Contains("device not detected") || lower.Contains("connectivity")) return state with { Category = "CTS Hardware", Subcategory = "Connectivity", Service = "CTS Scanner", Title = "CTS scanner issue", Details = input };
        if (lower.Contains("issue") || lower.Contains("error") || lower.Contains("not working") || lower.Contains("cannot access") || lower.Contains("can't access")) return state with { Category = "General", Subcategory = "Software", Service = "Branch IT", Title = "General software issue", Details = input };
        return state;
    }

    private async Task<List<KnowledgeArticle>> SearchKnowledge(string category, string subcategory, string input, string preferredTitle)
    {
        var words = input.Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(x => x.Length > 3).ToArray();
        var candidates = await db.KnowledgeArticles.Where(x => x.IsPublished && (x.Category == category || x.Subcategory == subcategory)).ToListAsync();
        return candidates.OrderByDescending(a => (a.Title.Equals(preferredTitle, StringComparison.OrdinalIgnoreCase) ? 100 : 0) + (a.Subcategory == subcategory ? 10 : 0) + words.Count(w => (a.Title + a.Keywords + a.Content).Contains(w, StringComparison.OrdinalIgnoreCase))).Take(3).ToList();
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
    private static bool IsGreeting(string text) => new[] { "hello", "hi", "hey", "good morning", "good afternoon" }.Any(x => text == x || text.StartsWith(x + " "));
    private static bool IsThanks(string text) => text.Contains("thank") || text is "thanks" or "cheers";
    private async Task<string> GroupName(int? id) => id is null ? "Unassigned" : (await db.AssignmentGroups.FindAsync(id))?.Name ?? "Unknown group";
}
