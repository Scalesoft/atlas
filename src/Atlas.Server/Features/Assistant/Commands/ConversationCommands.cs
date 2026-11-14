namespace Atlas.Server.Features.Assistant.Commands;

/// <summary><c>POST /api/ai/conversations/{id}/continue</c> (S6 "Pokračovat v asistentovi"): the answer from the omnisearch joins
/// "Moje konverzace". Idempotent, no model call; answers the revalidated conversation.</summary>
public sealed record ContinueAiConversation(Guid ConversationId);

/// <summary><c>DELETE /api/ai/conversations/{id}</c>: the actor's conversation with its messages and dependencies.</summary>
public sealed record DeleteAiConversation(Guid ConversationId);

/// <summary><c>DELETE /api/ai/conversations</c> (Profil → AI "Smazat všechny konverzace"): every conversation of the actor, listed
/// or not.</summary>
public sealed record DeleteAllAiConversations;

/// <summary>Retention: answers still preparing since before <see cref="PreparedBefore"/> become canceled at <see cref="At"/>.</summary>
public sealed record CancelStaleAiTurns(DateTime PreparedBefore, DateTime At);

public sealed record CanceledAiTurns(int Count);

/// <summary>Retention: one batch of at most <see cref="Batch"/> expired conversations (<see cref="Domain.AiConversationRetention"/>).</summary>
public sealed record PurgeAiConversations(DateTime UnlistedBefore, DateTime ListedBefore, int Batch);

public sealed record PurgedAiConversations(int Count);


