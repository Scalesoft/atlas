namespace Atlas.Server.Features.Contacts.LinkRun.Commands;

// Every account's contact (owner decision R3) is maintenance in the background, not a user's command: no MCP tool.

/// <summary>Matches the next accounts without a contact (AccountContacts.SweepAsync); the accounts it matched, 0 when none is left.</summary>
public sealed record SweepAccountContacts;

public sealed class SweepAccountContactsHandler(AccountContacts accounts)
{
    public Task<int> Handle(SweepAccountContacts command, CancellationToken ct) => accounts.SweepAsync(ct);
}
