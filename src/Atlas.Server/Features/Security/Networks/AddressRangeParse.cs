namespace Atlas.Server.Features.Security.Networks;

/// <summary>The outcome of <see cref="AddressRange.Parse"/>: the range, or an error for the field. Normalized: host bits were cleared,
/// so the stored range differs from what was typed.</summary>
public sealed record AddressRangeParse(AddressRange? Range, bool Normalized, string? Error)
{
    public static AddressRangeParse Invalid(string error) => new(null, false, error);
}
