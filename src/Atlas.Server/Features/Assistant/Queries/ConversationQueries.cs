namespace Atlas.Server.Features.Assistant.Queries;

/// <summary><c>GET /api/ai/conversations?before=</c>: the actor's listed conversations, at most
/// <see cref="ConversationReader.PageSize"/>, with last activity strictly before <see cref="Before"/> when given.</summary>
public sealed record ListAiConversations(DateTimeOffset? Before = null);

/// <summary><c>GET /api/ai/conversations/{id}</c>: one of the actor's conversations, listed or not (404 otherwise).</summary>
public sealed record GetAiConversation(Guid ConversationId);
