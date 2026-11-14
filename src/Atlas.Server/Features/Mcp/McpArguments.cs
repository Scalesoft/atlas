using System.Text.Json;

namespace Atlas.Server.Features.Mcp;

/// <summary>Validation of tool arguments shared by the tools: every refusal is 400 invalid_argument and never echoes the value.</summary>
public static class McpArguments
{
    public static Guid RequiredId(string? value, string name)
        => OptionalId(value, name) ?? throw new ApiException(400, $"{name} je povinné (UUID).", "invalid_argument");

    public static Guid? OptionalId(string? value, string name)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }
        return Guid.TryParse(value.Trim(), out var id) ? id : throw new ApiException(400, $"{name} musí být UUID.", "invalid_argument");
    }

    /// <summary>The SDK's binding of the arguments failed: a missing required one, or one that does not deserialize to the parameter's type.</summary>
    public static bool IsBindingFailure(Exception error)
        => error.GetType() == typeof(ArgumentException) || (error is not OperationCanceledException && Causes(error).Any(cause => cause is JsonException));

    /// <summary>Names the argument and the type it should have (from the tool's schema); without a wrong-typed one, the SDK's parameter name.</summary>
    public static string BindingMessage(Exception error, JsonElement schema, IDictionary<string, JsonElement>? arguments, string tool)
    {
        if (WrongType(schema, arguments) is var (name, expected))
        {
            return $"Argument {name} má špatný typ: očekává se {expected}. Zkontrolujte typy argumentů v popisu nástroje {tool}.";
        }
        return Causes(error).OfType<ArgumentException>().Select(cause => cause.ParamName).FirstOrDefault(parameter => parameter is { Length: > 0 }) is { } parameter
            ? $"Chybí nebo je neplatný argument {parameter}. Zkontrolujte názvy argumentů v popisu nástroje {tool}."
            : $"Chybějící nebo neplatné argumenty. Zkontrolujte názvy argumentů v popisu nástroje {tool}.";
    }

    private static IEnumerable<Exception> Causes(Exception error)
    {
        for (var cause = error; cause is not null; cause = cause.InnerException)
        {
            yield return cause;
        }
    }

    private static (string Name, string Expected)? WrongType(JsonElement schema, IDictionary<string, JsonElement>? arguments)
    {
        if (arguments is null || schema.ValueKind != JsonValueKind.Object || !schema.TryGetProperty("properties", out var properties) || properties.ValueKind != JsonValueKind.Object)
        {
            return null;
        }
        foreach (var (name, value) in arguments)
        {
            if (!properties.TryGetProperty(name, out var property) || !property.TryGetProperty("type", out var type))
            {
                continue;
            }
            var allowed = type.ValueKind == JsonValueKind.Array ? type.EnumerateArray().Select(item => item.GetString()).ToList() : [type.GetString()];
            if (value.ValueKind != JsonValueKind.Null && !allowed.Any(kind => Matches(kind, value)))
            {
                return (name, string.Join(" nebo ", allowed.Where(kind => kind != "null").Select(Czech)));
            }
        }
        return null;
    }

    private static bool Matches(string? kind, JsonElement value) => kind switch
    {
        "string" => value.ValueKind == JsonValueKind.String,
        "integer" => value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out _),
        "number" => value.ValueKind == JsonValueKind.Number,
        "boolean" => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
        "object" => value.ValueKind == JsonValueKind.Object,
        "array" => value.ValueKind == JsonValueKind.Array,
        "null" => value.ValueKind == JsonValueKind.Null,
        _ => true
    };

    private static string Czech(string? kind) => kind switch
    {
        "string" => "řetězec (string)",
        "integer" => "celé číslo",
        "number" => "číslo",
        "boolean" => "true/false",
        "object" => "objekt",
        "array" => "pole",
        _ => kind ?? "?"
    };
}
