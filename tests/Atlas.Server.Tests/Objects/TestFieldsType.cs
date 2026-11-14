using System.Text.Json.Nodes;
using Atlas.Server.Platform.Objects;
using Atlas.Server.Platform.Resources;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Tests.Objects;

/// <summary>
/// A test-only <see cref="ObjectEditingModel.Fields"/> type that proves the one object path: a required <c>name</c> and an
/// optional <c>email</c>, changed field by field (last write wins per field) by <c>{ "fields": { … } }</c>; access by scope.
/// </summary>
internal sealed class TestFieldsType(ISession session, ObjectScopeAccess scopes) : IObjectType, IObjectChangeHandler
{
    public const string Id = "test.fields";

    public string TypeId => Id;

    public ObjectEditingModel EditingModel => ObjectEditingModel.Fields;

    public Task<ObjectAccess> AuthorizeAsync(ObjectActor actor, Guid objectId, CancellationToken ct) => scopes.AuthorizeAsync(actor, objectId, ct);

    public async Task<ObjectProjection?> ProjectAsync(Guid objectId, CancellationToken ct)
    {
        return await session.GetAsync<ContentResource>(objectId, ct) is { Data: { } data }
            ? new ObjectProjection(Name(JsonNode.Parse(data)!.AsObject()), "", "", [])
            : null;
    }

    public Task<ObjectWrite> CreateAsync(ObjectChangeContext context, JsonObject data, CancellationToken ct)
    {
        return Task.FromResult(Write(Merge(new JsonObject(), data), data));
    }

    public Task<ObjectWrite> ApplyAsync(ObjectChangeContext context, JsonObject payload, CancellationToken ct)
    {
        var fields = payload["fields"] as JsonObject ?? throw new ObjectChangeRejected("Změna potřebuje pole fields.");
        return Task.FromResult(Write(Merge(context.Data!.DeepClone().AsObject(), fields), fields));
    }

    private static JsonObject Merge(JsonObject data, JsonObject fields)
    {
        foreach (var (key, value) in fields)
        {
            if (key is not ("name" or "email") || value is not null && value.GetValueKind() != System.Text.Json.JsonValueKind.String)
            {
                throw new ObjectChangeRejected("Neznámé nebo neplatné pole.");
            }
            data[key] = value?.DeepClone();
        }
        if (Name(data).Length == 0)
        {
            throw new ObjectChangeRejected("Jméno je povinné.");
        }
        return data;
    }

    private static ObjectWrite Write(JsonObject data, JsonObject change) => new(data, Name(data), change.DeepClone());

    private static string Name(JsonObject data) => data["name"]?.GetValue<string>().Trim() ?? "";
}
