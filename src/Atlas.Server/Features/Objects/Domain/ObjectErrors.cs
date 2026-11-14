namespace Atlas.Server.Features.Objects.Domain;

/// <summary>The answers of the object API. None of them repeats the input.</summary>
public static class ObjectErrors
{
    public static ApiException NotFound() => new(404, "Objekt nebyl nalezen.");

    public static ApiException NotInTrash() => new(404, "Objekt v koši nebyl nalezen.");

    public static ApiException ReadOnly() => new(403, "K úpravě objektu nemáte oprávnění.");

    public static ApiException UnknownType() => new(400, "Tento typ objektu není k dispozici.", "unknown_type");

    public static ApiException InvalidObject() => new(400, "Rozsah objektu není platný.", "invalid_object");

    public static ApiException UnsupportedChange() => new(400, "Tento typ objektu se mění vlastní cestou.", "unsupported_change");

    public static ApiException InvalidChange(string? message = null) => new(400, message ?? "Změna objektu není platná.", "invalid_object_change");

    public static ApiException VersionConflict() => new(409, "Objekt mezitím změnil někdo jiný; načtěte ho znovu.", "version_conflict");

    public static ApiException OperationConflict() => new(409, "Změna s tímto identifikátorem už proběhla s jiným obsahem.", "operation_conflict");
}
