using System.Collections.Frozen;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using static Atlas.Markdown.Sync.JsValues;

namespace Atlas.Markdown.Sync;

/// <summary>
/// Server implementations of the structured validators the built-in plugins name in the schema manifest (image presentation,
/// table column widths, secret field lists and the <c>data</c> of the block modules). Each follows the named editor function
/// of @atlas/markdown, including what it tolerates (unknown keys in module data are dropped by the editor, so they pass).
/// The verdict corpus (MarkdownDocumentValidatorCorpusTests) holds them to the editor's verdicts.
/// </summary>
internal static class StructuredValidators
{
    public static FrozenDictionary<string, MarkdownValueValidator> Builtin { get; } = new Dictionary<string, MarkdownValueValidator>(StringComparer.Ordinal)
    {
        ["imagePresentation"] = ImagePresentation,
        ["columnWidths"] = ColumnWidths,
        ["secretFields"] = SecretFields,
        ["attachment"] = Attachment,
        ["contacts"] = (value, _) => ContactList(value, single: false),
        ["contact"] = (value, _) => ContactList(value, single: true),
        ["chart"] = Chart,
        ["diagram"] = Diagram,
        ["math"] = Math,
        ["tableOfContents"] = TableOfContents,
        ["openapi"] = OpenApi,
    }.ToFrozenDictionary(StringComparer.Ordinal);

    private static readonly string[] PresentationKeys =
        ["width", "align", "caption", "rotate", "flip", "brightness", "contrast", "saturation", "filter", "cropLeft", "cropRight", "cropTop",
         "cropBottom", "naturalWidth", "naturalHeight"];

    private static readonly string[] SecretFieldNames = ["title", "path", "username", "password", "url", "notes"];

    /// <summary>features/images/presentation.ts imagePresentation: absent keys take the defaults.</summary>
    private static string? ImagePresentation(JsonElement value, JsonElement _)
    {
        if (value.ValueKind == JsonValueKind.Null) return null;
        if (value.ValueKind != JsonValueKind.Object) return "must be an image presentation object or null";
        var data = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject())
        {
            if (!PresentationKeys.Contains(property.Name, StringComparer.Ordinal))
                return $"has the unsupported key {property.Name}; allowed are {string.Join(", ", PresentationKeys)}";
            data[property.Name] = property.Value;
        }
        string? Text(string key, Func<string, bool> valid) => !data.TryGetValue(key, out var item) || (TryString(item, out var text) && valid(text)) ? null : $"{key} is invalid";
        var error = Text("caption", _ => true)
            ?? Text("align", text => text is "left" or "center" or "right")
            ?? Text("filter", text => text is "none" or "grayscale" or "sepia");
        if (error is not null) return error;
        if (data.TryGetValue("flip", out var flip) && flip.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) return "flip must be true or false";
        if (data.TryGetValue("rotate", out var rotate) && !(IsNumber(rotate, out var turn) && turn is 0 or 90 or 180 or 270)) return "rotate must be 0, 90, 180 or 270";
        double Number(string key, double fallback) => data.TryGetValue(key, out var item) && IsNumber(item, out var number) ? number : fallback;
        foreach (var key in new[] { "width", "brightness", "contrast", "saturation", "cropLeft", "cropRight", "cropTop", "cropBottom" })
        {
            var (min, max) = key == "width" ? (10d, 100d) : key.StartsWith("crop", StringComparison.Ordinal) ? (0d, 90d) : (0d, 200d);
            if (data.TryGetValue(key, out var item) && !(IsNumber(item, out var number) && number >= min && number <= max)) return $"{key} must be a number from {min} to {max}";
        }
        if (Number("cropLeft", 0) + Number("cropRight", 0) >= 95 || Number("cropTop", 0) + Number("cropBottom", 0) >= 95)
            return "the crop must keep at least 5 % of the image on each axis";
        foreach (var key in new[] { "naturalWidth", "naturalHeight" })
        {
            if (data.TryGetValue(key, out var item) && !(IsSafeInteger(item, out var number) && number >= 0 && number <= 1_000_000))
                return $"{key} must be an integer from 0 to 1000000";
        }
        return (Number("naturalWidth", 0) == 0) != (Number("naturalHeight", 0) == 0) ? "naturalWidth and naturalHeight must both be zero or both be positive" : null;
    }

    /// <summary>
    /// core/documentConstraints.ts isValidColumnWidths: null, or one width from 0 to 10000 per spanned column. Without attrs (the
    /// colspan unknown) one to 1000 widths, the table span bound.
    /// </summary>
    private static string? ColumnWidths(JsonElement value, JsonElement attrs)
    {
        if (value.ValueKind == JsonValueKind.Null) return null;
        var colspan = attrs.ValueKind == JsonValueKind.Undefined ? (double?)null
            : attrs.ValueKind == JsonValueKind.Object && attrs.TryGetProperty("colspan", out var span) && IsNumber(span, out var number) ? number : 1;
        return value.ValueKind == JsonValueKind.Array && (colspan is { } count ? value.GetArrayLength() == count : value.GetArrayLength() is >= 1 and <= 1000)
            && value.EnumerateArray().All(width => IsNumber(width, out var pixels) && pixels >= 0 && pixels <= 10_000)
            ? null : "must be null or one width from 0 to 10000 per column the cell spans (colspan)";
    }

    /// <summary>plugins/secrets/model.ts validateFields: null, or 1–6 distinct secret fields.</summary>
    private static string? SecretFields(JsonElement value, JsonElement _)
    {
        if (value.ValueKind == JsonValueKind.Null) return null;
        if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() is < 1 or > 6) return "must be null or 1 to 6 secret fields";
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in value.EnumerateArray())
            if (!TryString(item, out var field) || !SecretFieldNames.Contains(field, StringComparer.Ordinal) || !seen.Add(field))
                return $"must hold distinct fields of {string.Join(", ", SecretFieldNames)}";
        return null;
    }

    /// <summary>plugins/attachments/data.ts normalizeAttachment.</summary>
    private static string? Attachment(JsonElement value, JsonElement _)
    {
        if (value.ValueKind != JsonValueKind.Object) return "must be an attachment object";
        if (!Version1(value)) return "version must be 1";
        if (!Field(value, "name", out var name) || !TryString(name, out var text) || IsBlank(text) || text.Length > 512 || HasControl(text))
            return "name must be a non-blank name of at most 512 characters without control characters";
        if (!Field(value, "url", out var url) || !TryString(url, out var address) || address.Length > 8192 || HasLineBreak(address)
            || ValueConstraint.ModuleUrlError(address, optional: true) is not null)
            return "url must be empty or an http(s) or relative URL";
        if (Field(value, "size", out var size) && !(IsSafeInteger(size, out var bytes) && bytes >= 0)) return "size must be a non-negative integer";
        if (Field(value, "mimeType", out var mime) && !(TryString(mime, out var type) && (type.Length == 0 || MimeType.IsMatch(type))))
            return "mimeType must be a media type such as type/subtype";
        if (Field(value, "description", out var description) && !(TryString(description, out var about) && about.Length <= 4096 && !HasControlExceptBreaks(about)))
            return "description must be text of at most 4096 characters";
        return null;
    }

    /// <summary>plugins/contacts/data.ts normalizeContacts (contactCard: 1–100 contacts; the inline contact exactly one).</summary>
    private static string? ContactList(JsonElement value, bool single)
    {
        if (value.ValueKind != JsonValueKind.Object || !Version1(value)
            || !Field(value, "contacts", out var contacts) || contacts.ValueKind != JsonValueKind.Array || contacts.GetArrayLength() is < 1 or > 100)
            return "must have version 1 and 1 to 100 contacts";
        if (single && contacts.GetArrayLength() != 1) return "an inline contact holds exactly one contact";
        var index = 0;
        foreach (var contact in contacts.EnumerateArray())
        {
            if (ContactError(contact) is { } error) return $"contacts[{index}]: {error}";
            index++;
        }
        return null;
    }

    private static string? ContactError(JsonElement contact)
    {
        if (contact.ValueKind != JsonValueKind.Object) return "a contact must be an object";
        if (ContactText(contact, "name", required: true) is { } name) return name;
        if (Field(contact, "kind", out var kind) && !(TryString(kind, out var display) && display is "auto" or "internal" or "external"))
            return "kind must be auto, internal or external";
        if (Field(contact, "internalUser", out var user) && InternalUser(user) is { } userError) return $"internalUser: {userError}";
        // `unresolved=true` of the inline Markdown form is text until normalized (data.ts reads it so).
        if (Field(contact, "unresolved", out var unresolved) && unresolved.ValueKind is not (JsonValueKind.True or JsonValueKind.False or JsonValueKind.Null)
            && !(TryString(unresolved, out var flag) && flag is "true" or "false"))
            return "unresolved must be true or false";
        foreach (var key in new[] { "organization", "department", "title", "address", "directoryId", "objectId" })
            if (ContactText(contact, key) is { } error) return error;
        if (ContactText(contact, "role") is { } roleError) return roleError;
        if (Field(contact, "role", out var roleValue) && TryString(roleValue, out var roleRaw) && JsTrim(roleRaw) is { Length: > 0 } role
            && (role.Length > 400 || HasLineBreak(role)))
            return "role must have at most 400 characters on one line";
        if (ContactText(contact, "username") is { } usernameError) return usernameError;
        if (Field(contact, "username", out var username) && TryString(username, out var login) && JsTrim(login) is { Length: > 0 } trimmed
            && (trimmed.Length > 256 || HasLineBreak(trimmed)))
            return "username must have at most 256 characters on one line";
        foreach (var key in new[] { "photo", "website" })
        {
            if (ContactText(contact, key) is { } error) return error;
            if (Field(contact, key, out var item) && TryString(item, out var raw) && JsTrim(raw) is { Length: > 0 } address
                && (HasLineBreak(address) || ValueConstraint.ModuleUrlError(address, optional: false) is not null))
                return $"{key} must be an http(s) or relative URL";
        }
        foreach (var key in new[] { "phones", "emails" })
        {
            if (!Field(contact, key, out var list) || list.ValueKind == JsonValueKind.Null) continue;
            if (list.ValueKind != JsonValueKind.Array || list.GetArrayLength() > 20) return $"{key} must be a list of at most 20 items";
            foreach (var item in list.EnumerateArray())
            {
                if (!TryString(item, out var raw) || raw.Length > 4096 || HasControlExceptBreaks(raw) || IsBlank(raw)) return $"{key} holds an invalid item";
                var entry = JsTrim(raw);
                if (key == "phones" ? !Phone.IsMatch(entry) || HasLineBreak(entry) : !Email.IsMatch(entry))
                    return key == "phones" ? $"the phone number {entry} is invalid" : $"the e-mail address {entry} is invalid";
            }
        }
        return null;
    }

    private static string? InternalUser(JsonElement user)
    {
        if (user.ValueKind != JsonValueKind.Object) return "must be an object";
        if (!Field(user, "id", out var id) || !TryString(id, out var text) || IsBlank(text) || text.Length > 256 || HasControl(text))
            return "id must be a non-blank single-line identifier of at most 256 characters";
        if (ContactText(user, "name") is { } nameError) return nameError;
        if (ContactText(user, "photo") is { } photoError) return photoError;
        if (Field(user, "photo", out var photo) && TryString(photo, out var raw) && JsTrim(raw) is { Length: > 0 } address
            && (HasLineBreak(address) || ValueConstraint.ModuleUrlError(address, optional: false) is not null))
            return "photo must be an http(s) or relative URL";
        return null;
    }

    /// <summary>The contacts plugin's text(): null/absent allowed unless required; at most 4096 characters without control characters.</summary>
    private static string? ContactText(JsonElement owner, string key, bool required = false)
    {
        if (!Field(owner, key, out var value) || value.ValueKind == JsonValueKind.Null) return required ? $"{key} is required" : null;
        if (!TryString(value, out var text) || text.Length > 4096 || HasControlExceptBreaks(text)) return $"{key} must be text of at most 4096 characters";
        return required && IsBlank(text) ? $"{key} is required" : null;
    }

    /// <summary>plugins/charts/data.ts normalizeChart and validateChartGrid.</summary>
    private static string? Chart(JsonElement value, JsonElement _)
    {
        if (value.ValueKind != JsonValueKind.Object) return "must be a chart object";
        if (!Version1(value) || !Field(value, "kind", out var kind) || !TryString(kind, out var type) || type is not ("bar" or "line" or "pie" or "doughnut"))
            return "must have version 1 and the kind bar, line, pie or doughnut";
        if (!Field(value, "title", out var title) || !TryString(title, out var name) || name.Length > 300) return "title must be text of at most 300 characters";
        if (!Field(value, "grid", out var grid) || grid.ValueKind != JsonValueKind.Array || grid.GetArrayLength() is < 2 or > 201)
            return "grid must have a header and 1 to 200 rows";
        var rows = grid.EnumerateArray().ToArray();
        var width = rows[0].ValueKind == JsonValueKind.Array ? rows[0].GetArrayLength() : 0;
        if (width is < 2 or > 21) return "grid must have labels and 1 to 20 series";
        for (var r = 0; r < rows.Length; r++)
        {
            if (rows[r].ValueKind != JsonValueKind.Array || rows[r].GetArrayLength() != width) return "every grid row must have the same width";
            var c = 0;
            foreach (var cell in rows[r].EnumerateArray())
            {
                if (!TryString(cell, out var text) || text.Length > 2000) return $"grid[{r}][{c}] must be text of at most 2000 characters";
                if (r > 0 && c > 0)
                {
                    if (!ChartNumber(text, out var number)) return $"grid[{r}][{c}] is not a number";
                    if (type is "pie" or "doughnut" && number < 0) return $"grid[{r}][{c}]: a pie chart cannot hold negative values";
                }
                c++;
            }
        }
        var hasSource = Field(value, "source", out var source) && source.ValueKind != JsonValueKind.Null;
        if (hasSource)
        {
            static bool ValidId(JsonElement id) => TryString(id, out var text) && text.Length is > 0 and <= 100;
            var cells = source.ValueKind == JsonValueKind.Object && source.TryGetProperty("cells", out var list) && list.ValueKind == JsonValueKind.Array ? list : default;
            if (source.ValueKind != JsonValueKind.Object || !source.TryGetProperty("tableId", out var table) || !ValidId(table)
                || cells.ValueKind != JsonValueKind.Array || cells.GetArrayLength() != rows.Length
                || cells.EnumerateArray().Any(row => row.ValueKind != JsonValueKind.Array || row.GetArrayLength() != width || row.EnumerateArray().Any(id => !ValidId(id))))
                return "source must name its table (tableId) and hold a cell id for every grid value";
            var ids = cells.EnumerateArray().SelectMany(row => row.EnumerateArray().Select(id => id.GetString()!)).ToArray();
            if (ids.Distinct(StringComparer.Ordinal).Count() != ids.Length) return "source cannot refer to merged cells";
        }
        var hasStatus = Field(value, "sourceStatus", out var status) && status.ValueKind != JsonValueKind.Null;
        if (hasSource ? !(TryString(status, out var state) && state is "linked" or "missing" or "invalid") : hasStatus)
            return "sourceStatus must be linked, missing or invalid for a linked chart and null otherwise";
        return null;
    }

    // Every pattern here runs on the non-backtracking engine: linear in the input, whatever a client stores.
    private static readonly Regex ChartNumberPattern = new(
        "^[+-]?(?:[0-9]+|[0-9]{1,3}(?:[ " + (char)0xa0 + (char)0x202f + "][0-9]{3})+)(?:[,.][0-9]+)?$",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    /// <summary>plugins/charts/data.ts parseChartNumber: empty is a gap, Czech decimal comma and grouped thousands.</summary>
    private static bool ChartNumber(string value, out double number)
    {
        number = 0;
        var trimmed = JsTrim(value);
        if (trimmed.Length == 0) return true;
        if (!ChartNumberPattern.IsMatch(trimmed)) return false;
        var normalized = new StringBuilder(trimmed.Length);
        foreach (var c in trimmed)
        {
            if (c is ' ' || c == (char)0xa0 || c == (char)0x202f) continue;
            normalized.Append(c == ',' ? '.' : c);
        }
        return double.TryParse(normalized.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out number) && double.IsFinite(number);
    }

    // The line start is a preceding line break rather than a lookbehind: the same language, which the non-backtracking engine
    // matches in linear time (a backtracking engine rescans a run of blank lines from every line of it: quadratic).
    private static readonly Regex DiagramUnsafe = new(
        "%%[" + JsWhitespace + "]*\\{|(?:^|[\\n\\r\\u2028\\u2029])[" + JsWhitespace + "]*---|</?[a-z!]",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    /// <summary>plugins/diagrams/index.ts normalizeDiagram: Mermaid source without init directives, front matter or HTML.</summary>
    private static string? Diagram(JsonElement value, JsonElement _)
    {
        if (value.ValueKind != JsonValueKind.Object || !Version1(value)
            || !Field(value, "source", out var source) || !TryString(source, out var text) || text.Length > 20_000
            || !Field(value, "caption", out var caption) || !TryString(caption, out var label) || label.Length > 500)
            return "must have version 1, a source of at most 20000 characters and a caption of at most 500 characters";
        return DiagramUnsafe.IsMatch(text) ? "the source cannot hold %%{…} directives, front matter (---) or HTML" : null;
    }

    /// <summary>plugins/math/index.ts normalizeMath.</summary>
    private static string? Math(JsonElement value, JsonElement _) => value.ValueKind == JsonValueKind.Object && Version1(value)
        && Field(value, "expression", out var expression) && TryString(expression, out var text) && !IsBlank(text) && text.Length <= 10_000
            ? null : "must have version 1 and a non-blank expression of at most 10000 characters";

    /// <summary>plugins/toc/index.ts normalizeToc.</summary>
    private static string? TableOfContents(JsonElement value, JsonElement _) => value.ValueKind == JsonValueKind.Object && Version1(value)
        && Field(value, "title", out var title) && TryString(title, out var text) && text.Length <= 200
        && Field(value, "minLevel", out var min) && IsInteger(min, out var from) && Field(value, "maxLevel", out var max) && IsInteger(max, out var to)
        && from >= 1 && to <= 6 && from <= to
            ? null : "must have version 1, a title of at most 200 characters and levels 1 ≤ minLevel ≤ maxLevel ≤ 6";

    /// <summary>
    /// plugins/openapi/shape.ts normalizeOpenApiShape: the block's fields and the size of its source. Neither the node schema nor
    /// the server parses the source as YAML (the editor's card, source dialog and upload do), so a page whose spec does not parse
    /// still opens.
    /// </summary>
    private static string? OpenApi(JsonElement value, JsonElement _)
    {
        if (value.ValueKind != JsonValueKind.Object || !Version1(value)) return "must have version 1";
        if (!Field(value, "filename", out var filename) || !TryString(filename, out var name) || IsBlank(name) || name.Length > 255
            || name.Any(c => c < ' ' || c is '/' or '\\'))
            return "filename must be a file name of at most 255 characters without slashes";
        if (!Field(value, "source", out var source) || !TryString(source, out var text) || IsBlank(text)) return "source must be a non-blank OpenAPI specification";
        if (Encoding.UTF8.GetByteCount(text) > 512 * 1024) return "source can have at most 512 KiB";
        if (Field(value, "mode", out var mode) && !(TryString(mode, out var display) && display is "download" or "reference" or "interactive"))
            return "mode must be download, reference or interactive";
        if (Field(value, "uploadedBy", out var uploader) && !(TryString(uploader, out var by) && !IsBlank(by) && by.Length <= 255))
            return "uploadedBy must be a non-blank name of at most 255 characters";
        return null;
    }

    private static bool Version1(JsonElement value) => value.TryGetProperty("version", out var version) && IsNumberEqual(version, 1);

    /// <summary>A present field (JSON null included, as JavaScript's "!== undefined" counts it).</summary>
    private static bool Field(JsonElement owner, string name, out JsonElement value) => owner.TryGetProperty(name, out value);

    private static readonly Regex MimeType = new(@"^[a-z0-9!#$&^_.+-]+/[a-z0-9!#$&^_.+-]+\z",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    private static readonly Regex Phone = new("^\\+?[0-9" + JsWhitespace + "().\\-#*]{1,80}$", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    private static readonly Regex Email = new("^[^" + JsWhitespace + "@<>?,;:]+@[^" + JsWhitespace + "@<>?,;:]+\\.[^" + JsWhitespace + "@<>?,;:]+$",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
}
