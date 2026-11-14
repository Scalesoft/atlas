using Atlas.Server.Features.Pages.Contracts;
using Atlas.Server.Features.Pages.Domain;

namespace Atlas.Server.Features.Pages.Queries;

public sealed class GetPageHandler
{
    private readonly PageReader m_reader;

    public GetPageHandler(PageReader reader)
    {
        m_reader = reader;
    }

    public Task<PageDto> Handle(GetPage query, CancellationToken ct)
    {
        return m_reader.ReadAsync(query.Id, ct, recordOpening: true);
    }
}
