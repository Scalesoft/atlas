namespace Atlas.Server.Features.Mcp;

/// <summary>Refusals of the draft tools worded for a model: what happened and what to call next, never stored content.</summary>
public static class McpWriteErrors
{
    public static ApiException VersionConflict(int currentVersion) => new(409,
        $"Stránka se mezitím změnila (aktuální verze {currentVersion}). Načtěte pracovní verzi znovu (atlas_get_page s version: \"working\") " +
        "a úpravu zopakujte s novým expectedVersion.", "version_conflict", new Dictionary<string, object?> { ["currentVersion"] = currentVersion });

    public static ApiException HiddenReferences() => new(403,
        "Stránka obsahuje odkazy na hesla, které nevidíte; přes MCP ji upravit nelze.", "page_has_hidden_references");

    /// <summary>A page the token's owner may read but not edit, worded as REST's 403 (the shared handlers' own refusals say the same).</summary>
    public static ApiException NoWriteRight() => new(403, "K této operaci potřebujete oprávnění k úpravám stránky.", "forbidden");

    public static ApiException MissingSpace() => new(404, "Prostor nebyl nalezen nebo k němu nemáte přístup.");
}
