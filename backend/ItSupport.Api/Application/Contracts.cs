using System.ComponentModel.DataAnnotations;

namespace ItSupport.Api.Application;

public record LoginRequest([Required] string UserName, [Required] string Password);
public record LoginResponse(string Token, string UserName, string DisplayName, string Role, DateTimeOffset ExpiresAt);
public record CreateTicketRequest(
    [Required, MaxLength(30)] string Type,
    [Required, MaxLength(140)] string Title,
    [Required, MaxLength(4000)] string Description,
    [Required] string Category,
    string Subcategory,
    [Required] string Priority,
    [Required] string Service,
    string Impact = "Single User",
    string Urgency = "Medium");
public record UpdateTicketRequest(string? Status, int? AssignmentGroupId, string? AssignedAgent, string? Priority, string? Impact, string? Urgency, string? ResolutionCode, string? ResolutionNotes, int? ParentMajorIncidentId, bool? Escalate);
public record AddCommentRequest([Required, MaxLength(2000)] string Body);
public record AddWorkNoteRequest([Required, MaxLength(4000)] string Body);
public record ChatRequest([Required, MaxLength(4000)] string Message, Guid? SessionId, string? Action = null);
public record ChatReply(Guid SessionId, string Message, object[] Articles, int? TicketId, string? TicketNumber, string? AssignmentGroup, bool CanEscalate, bool AwaitingConfirmation, string Stage);
public record NamedOptionRequest([Required, MaxLength(80)] string Name, bool IsActive = true, int? CategoryId = null);
public record ArticleRequest([Required] string Title, [Required] string Content, string Keywords, string Category, string Subcategory, bool IsPublished = true);
public record RuleRequest([Required] string Name, int Order, string? Category, string? Subcategory, string? Service, string? Priority, string? TicketType, string? Keywords, int AssignmentGroupId, bool IsActive = true);
public record MembershipRequest([Required] string UserName, int[] AssignmentGroupIds);
public record UserAdminRequest([Required] string UserName, [Required] string DisplayName, [Required] string Role, string? Password, bool IsActive = true);
public record LiveSupportRequest(string? Subject, int? TicketId = null);
public record LiveMessageRequest([Required, MaxLength(2000)] string Body);
public record LiveLinkTicketRequest(int? TicketId, bool CreateTicket = false, string? Title = null, string? Description = null);
