using ItSupport.Api.Domain;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace ItSupport.Api.Infrastructure;

public class LiveSupportHub(AppDbContext db) : Hub
{
    public async Task JoinSession(Guid sessionId)
    {
        var session = await db.LiveSupportSessions.FirstOrDefaultAsync(x => x.PublicId == sessionId) ?? throw new HubException("Conversation not found.");
        var user = Context.User?.Identity?.Name ?? throw new HubException("Authentication required.");
        var staff = Context.User!.IsInRole("Admin") || Context.User.IsInRole("ITSupport");
        if (!staff && session.RequestedBy != user) throw new HubException("You cannot access this conversation.");
        await Groups.AddToGroupAsync(Context.ConnectionId, sessionId.ToString());
    }

    public async Task SendMessage(Guid sessionId, string body)
    {
        if (string.IsNullOrWhiteSpace(body) || body.Length > 2000) throw new HubException("Message must be between 1 and 2000 characters.");
        var session = await db.LiveSupportSessions.FirstOrDefaultAsync(x => x.PublicId == sessionId) ?? throw new HubException("Conversation not found.");
        var user = Context.User?.Identity?.Name ?? throw new HubException("Authentication required.");
        var staff = Context.User!.IsInRole("Admin") || Context.User.IsInRole("ITSupport");
        if ((!staff && session.RequestedBy != user) || session.Status == "Ended") throw new HubException("Message not permitted.");
        var message = new LiveSupportMessage { LiveSupportSessionId = session.Id, Sender = user, Body = body.Trim() }; db.LiveSupportMessages.Add(message); session.UpdatedAt = DateTimeOffset.UtcNow; await db.SaveChangesAsync();
        await Clients.Group(sessionId.ToString()).SendAsync("MessageReceived", new { message.Id, message.Sender, message.Body, message.CreatedAt });
    }
}
