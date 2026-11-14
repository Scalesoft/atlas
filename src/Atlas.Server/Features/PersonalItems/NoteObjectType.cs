using System.Text.Json;
using System.Text.Json.Nodes;
using Atlas.Server.Features.Contacts.Domain;
using Atlas.Server.Platform.Objects;
using Atlas.Server.Platform.Resources;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.PersonalItems;

/// <summary>
/// The private note (<c>atlas.note</c>, spec 2026-10-29 §1): its title (one line of inline Markdown) and text (Markdown) in the
/// envelope's data, changed by <c>{ "fields": { "title"?, "text"? } }</c>, the last writer winning per field. A note is always
/// private: its owner's and, by a grant, its grantees' (ObjectScopeAccess), never an administrator's. Its projection is the plain
/// text of both, one chunk; 3.0 indexes no private object (§4).
/// </summary>
public sealed class NoteObjectType : IObjectType, IObjectChangeHandler
{
    public const string Id = "atlas.note";
    public const int MaxTitle = 200;
    public const int MaxText = 100_000;

    private readonly ISession m_session;
    private readonly ObjectScopeAccess m_scopes;

    public NoteObjectType(ISession session, ObjectScopeAccess scopes)
    {
        m_session = session;
        m_scopes = scopes;
    }

    public string TypeId => Id;

    public ObjectEditingModel EditingModel => ObjectEditingModel.Fields;

    public Task<ObjectAccess> AuthorizeAsync(ObjectActor actor, Guid objectId, CancellationToken ct) => m_scopes.AuthorizeAsync(actor, objectId, ct);

    public async Task<ObjectProjection?> ProjectAsync(Guid objectId, CancellationToken ct)
    {
        if (await m_session.GetAsync<ContentResource>(objectId, ct) is not { TrashedAt: null, Data: { } json } envelope || envelope.TypeId != Id)
        {
            return null;
        }
        var data = JsonNode.Parse(json)!.AsObject();
        var title = ContactFields.InlinePlainText(Field(data, "title"));
        var markdown = Field(data, "text");
        var text = Plain(markdown);
        return new ObjectProjection(title, markdown, text, [new ObjectChunk(null, title, text)]);
    }

    public Task<ObjectWrite> CreateAsync(ObjectChangeContext context, JsonObject data, CancellationToken ct)
    {
        if (context.Scope != ObjectScopes.Private)
        {
            throw new ObjectChangeRejected("Poznámka je vždy soukromá: založte ji s rozsahem private.");
        }
        var note = Apply(new JsonObject { ["title"] = "", ["text"] = "" }, data);
        return Task.FromResult(new ObjectWrite(note, ContactFields.InlinePlainText(Field(note, "title")), note.DeepClone()));
    }

    public Task<ObjectWrite> ApplyAsync(ObjectChangeContext context, JsonObject payload, CancellationToken ct)
    {
        if (payload.Count != 1 || payload["fields"] is not JsonObject fields)
        {
            throw new ObjectChangeRejected("Změna poznámky nastavuje pole: { \"fields\": { \"title\"?, \"text\"? } }.");
        }
        var note = Apply(context.Data!.DeepClone().AsObject(), fields);
        return Task.FromResult(new ObjectWrite(note, ContactFields.InlinePlainText(Field(note, "title")), new JsonObject { ["fields"] = fields.DeepClone() }));
    }

    /// <summary>The fields merged in, each validated: a required title on one line, a text of lines; nothing else.</summary>
    private static JsonObject Apply(JsonObject note, JsonObject fields)
    {
        foreach (var (key, value) in fields)
        {
            if (value is null || value.GetValueKind() != JsonValueKind.String)
            {
                throw new ObjectChangeRejected($"Pole {key} poznámky je text.");
            }
            var text = value.GetValue<string>();
            note[key] = key switch
            {
                "title" when text.Trim() is { Length: > 0 and <= MaxTitle } line && Rules.IsLine(line) => line,
                "title" => throw new ObjectChangeRejected($"Název poznámky je povinný, na jednom řádku a nejvýše {MaxTitle} znaků."),
                "text" when text.Length <= MaxText && Rules.IsMultiLineText(text) => text.TrimEnd(),
                "text" => throw new ObjectChangeRejected($"Text poznámky smí mít nejvýše {MaxText} znaků a žádné řídicí znaky."),
                _ => throw new ObjectChangeRejected($"Poznámka nemá pole {key}.")
            };
        }
        if (Field(note, "title").Length == 0)
        {
            throw new ObjectChangeRejected("Název poznámky je povinný.");
        }
        return note;
    }

    private static string Field(JsonObject data, string key) => data[key]?.GetValue<string>() ?? "";

    private static string Plain(string markdown) => Markdig.Markdown.ToPlainText(markdown).Trim();
}
