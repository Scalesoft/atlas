namespace Atlas.Server.Features.PersonalApi.Domain;

public static class ApiPagination
{
    public static (int Skip, int Take) Resolve(int? offset, int? limit)
    {
        var start = offset ?? 0;
        var size = limit ?? 50;
        if (start is < 0 or > 100_000 || size is < 1 or > 100)
        {
            throw new ApiException(400, "Offset musí být 0 až 100000 a limit 1 až 100.");
        }
        return (start, size);
    }
}
