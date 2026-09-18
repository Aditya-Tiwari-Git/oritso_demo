using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ItSupport.Api.Domain;
using ItSupport.Api.Infrastructure;
using Microsoft.EntityFrameworkCore;
namespace ItSupport.Api.Application;

public record AppUser(string UserName, string Password, string DisplayName, string Role);
public class CredentialStore(IWebHostEnvironment env)
{
 private readonly string _path = Path.Combine(env.ContentRootPath, "credentials.json");
 public IReadOnlyList<AppUser> Load() => JsonSerializer.Deserialize<List<AppUser>>(File.ReadAllText(_path), new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? [];
 public AppUser? Validate(string user, string pass) => Load().FirstOrDefault(x => string.Equals(x.UserName, user, StringComparison.OrdinalIgnoreCase) && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(x.Password), Encoding.UTF8.GetBytes(pass)));
}
public class TokenService(IConfiguration config)
{
 private readonly byte[] _key = Encoding.UTF8.GetBytes(config["Auth:SigningKey"] ?? "DEMO-ONLY-CHANGE-THIS-32-CHAR-KEY!");
 public string Create(AppUser user, DateTimeOffset expires) { var payload = Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { sub = user.UserName, role = user.Role, name = user.DisplayName, exp = expires.ToUnixTimeSeconds() }))); return payload + "." + Convert.ToHexString(HMACSHA256.HashData(_key, Encoding.UTF8.GetBytes(payload))); }
 public (string User, string Role, string Name)? Validate(string token) { var p = token.Split('.'); if (p.Length != 2) return null; var expected = Convert.ToHexString(HMACSHA256.HashData(_key, Encoding.UTF8.GetBytes(p[0]))); if (!CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(expected), Encoding.ASCII.GetBytes(p[1]))) return null; try { using var d = JsonDocument.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(p[0]))); var r = d.RootElement; if (r.GetProperty("exp").GetInt64() < DateTimeOffset.UtcNow.ToUnixTimeSeconds()) return null; return (r.GetProperty("sub").GetString()!, r.GetProperty("role").GetString()!, r.GetProperty("name").GetString()!); } catch { return null; } }
}
public class RoutingService(AppDbContext db)
{
 public async Task<int?> RouteAsync(string category, string service, string priority, string type, string text) { var rules = await db.RoutingRules.Where(x => x.IsActive).OrderBy(x => x.Order).ToListAsync(); foreach (var r in rules) { bool Match(string? rule, string value) => string.IsNullOrWhiteSpace(rule) || string.Equals(rule, value, StringComparison.OrdinalIgnoreCase); var keywords = (r.Keywords ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries); if (Match(r.Category, category) && Match(r.Service, service) && Match(r.Priority, priority) && Match(r.TicketType, type) && (keywords.Length == 0 || keywords.Any(k => text.Contains(k, StringComparison.OrdinalIgnoreCase)))) return r.AssignmentGroupId; } return (await db.AssignmentGroups.FirstOrDefaultAsync(x => x.IsActive))?.Id; }
}
public class TicketService(AppDbContext db, RoutingService routing)
{
 public async Task<Ticket> CreateAsync(CreateTicketRequest request, string user, string source = "Portal") { var group = await routing.RouteAsync(request.Category, request.Service, request.Priority, request.Type, request.Title + " " + request.Description); var ticket = new Ticket { Number = $"INC-{DateTime.UtcNow:yyyyMMdd}-{Guid.NewGuid().ToString("N")[..6].ToUpperInvariant()}", Type = request.Type, Title = request.Title, Description = request.Description, Category = request.Category, Service = request.Service, Priority = request.Priority, CreatedBy = user, AssignmentGroupId = group }; ticket.History.Add(new() { Actor = user, Action = "Created", Detail = $"Created via {source}; routed to group {group?.ToString() ?? "unassigned"}" }); db.Tickets.Add(ticket); await db.SaveChangesAsync(); return ticket; }
}
public class ChatService(AppDbContext db, TicketService tickets, IConfiguration config, IHttpClientFactory clients)
{
 public async Task<ChatReply> ReplyAsync(ChatRequest request, string user)
 {
  var session = request.SessionId is null ? null : await db.ChatSessions.Include(x => x.Messages).FirstOrDefaultAsync(x => x.PublicId == request.SessionId && x.UserName == user);
  session ??= new ChatSession { UserName = user }; if (session.Id == 0) db.ChatSessions.Add(session);
  session.Messages.Add(new() { Role = "user", Content = request.Message });
  var words = request.Message.Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(x => x.Length > 3).Select(x => x.Trim('?', '.', ',', '!')).Distinct(StringComparer.OrdinalIgnoreCase).Take(12).ToArray();
  var all = await db.KnowledgeArticles.Where(x => x.IsPublished).ToListAsync(); var matches = all.Select(a => new { Article = a, Score = words.Count(w => (a.Title + " " + a.Keywords + " " + a.Content).Contains(w, StringComparison.OrdinalIgnoreCase)) }).Where(x => x.Score > 0).OrderByDescending(x => x.Score).Take(3).Select(x => x.Article).ToList();
  var statusIntent = request.Message.Contains("status", StringComparison.OrdinalIgnoreCase) || request.Message.Contains("ticket", StringComparison.OrdinalIgnoreCase); var existing = statusIntent ? await db.Tickets.Where(x => x.CreatedBy == user).OrderByDescending(x => x.UpdatedAt).FirstOrDefaultAsync() : null;
  var response = existing is not null ? $"Your latest ticket {existing.Number} is {existing.Status} with priority {existing.Priority}." : matches.Count > 0 ? $"I found a likely fix: {matches[0].Title}\n\n{matches[0].Content}" : "I couldn't find a confident knowledge-base solution. Tell me the affected service, what you expected, what happened, and any error message. I can also create and route a ticket for you.";
  var apiKey = config["OPENAI_API_KEY"] ?? Environment.GetEnvironmentVariable("OPENAI_API_KEY"); if (!string.IsNullOrWhiteSpace(apiKey)) response = await AskLlm(apiKey, request.Message, matches, response);
  Ticket? created = null; if (request.CreateTicket) { var category = Has(request.Message, "password", "login", "access") ? "Access" : Has(request.Message, "laptop", "screen", "keyboard") ? "Hardware" : "Software"; created = await tickets.CreateAsync(new("Standard", request.Message.Length > 100 ? request.Message[..100] : request.Message, request.Message, category, "Medium", category == "Hardware" ? "End User Computing" : "Business Applications"), user, "Chatbot"); session.TicketId = created.Id; response += $"\n\nI've created {created.Number} and routed it to the appropriate support team."; }
  session.Messages.Add(new() { Role = "assistant", Content = response }); session.UpdatedAt = DateTimeOffset.UtcNow; await db.SaveChangesAsync(); return new(session.PublicId, response, matches.Select(x => (object)new { x.Id, x.Title, x.Category }).ToArray(), created?.Id ?? existing?.Id, created?.Number ?? existing?.Number, matches.Count == 0 && created is null);
 }
 private static bool Has(string text, params string[] words) => words.Any(word => text.Contains(word, StringComparison.OrdinalIgnoreCase));
 private async Task<string> AskLlm(string key, string question, List<KnowledgeArticle> articles, string fallback) { try { var client = clients.CreateClient(); client.DefaultRequestHeaders.Authorization = new("Bearer", key); var context = string.Join("\n", articles.Select(x => $"{x.Title}: {x.Content}")); var body = new { model = config["OPENAI_MODEL"] ?? "gpt-5-mini", input = $"You are an IT support agent. Use only this KB when giving fixes; otherwise ask concise diagnostic questions and offer escalation. KB:\n{context}\nUser: {question}" }; var res = await client.PostAsJsonAsync(config["OPENAI_BASE_URL"] ?? "https://api.openai.com/v1/responses", body); if (!res.IsSuccessStatusCode) return fallback; using var json = JsonDocument.Parse(await res.Content.ReadAsStringAsync()); return json.RootElement.TryGetProperty("output_text", out var output) ? output.GetString() ?? fallback : fallback; } catch { return fallback; } }
}
