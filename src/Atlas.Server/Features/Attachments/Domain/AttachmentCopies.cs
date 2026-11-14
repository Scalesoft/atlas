using Atlas.Server.Features.Backups.Archive;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.Attachments.Domain;

/// <summary>Content one page takes over from others (a template, a Projector copy or catalog block) shows their attachments: each
/// gets a copy of its own, with the ids remapped, before the revision binds them (<see cref="AttachmentReferences.BindRevisionAsync"/>).</summary>
public static class AttachmentCopies
{
    /// <summary>The content with the copies' ids, and the ids of the files written for them.</summary>
    public sealed record Copy(string ContentJson, IReadOnlyList<Guid> Files);

    /// <summary><paramref name="contentJson"/> with every attachment of <paramref name="fromPages"/> it references copied to
    /// <paramref name="pageId"/>. A reference of any other page stays as it is, for the binding to refuse. The caller runs what
    /// follows the copy through <see cref="DiscardOnFailureAsync{T}"/>.</summary>
    public static async Task<Copy> CopyAsync(ISession session, AttachmentFiles files, string contentJson, IReadOnlyCollection<Guid> fromPages,
        Guid pageId, Guid actorId, CancellationToken ct)
    {
        var ids = AttachmentReferences.Extract(contentJson);
        if (ids.Count == 0 || fromPages.Count == 0)
        {
            return new Copy(contentJson, []);
        }
        var sources = (await session.QueryOver<Attachment>().WhereRestrictionOn(x => x.Id).IsIn(ids.ToArray()).ListAsync(ct))
            .Where(x => x.PageId != pageId && fromPages.Contains(x.PageId)).ToArray();
        if (sources.Any(x => x.ActiveTo is not null))
        {
            throw new ApiException(400, "Příloha kopírované stránky už není dostupná.");
        }
        var replacements = new Dictionary<Guid, Guid>();
        try
        {
            foreach (var source in sources)
            {
                var id = Guid.NewGuid();
                await using var input = files.Open(source.Id);
                var (size, hash, mime) = await files.WriteAsync(id, input, source.IsImage, ct, source.Name);
                replacements.Add(source.Id, id);
                await session.SaveAsync(new Attachment
                {
                    Id = id, PageId = pageId, UploadedBy = actorId, Name = source.Name, MimeType = mime,
                    Size = size, Sha256 = hash, IsImage = source.IsImage, ActiveFrom = DateTime.UtcNow
                }, ct);
            }
            return new Copy(replacements.Count == 0 ? contentJson : BackupFormat.RemapJson(contentJson, replacements), replacements.Values.ToArray());
        }
        catch
        {
            Remove(files, replacements.Values);
            throw;
        }
    }

    /// <summary>
    /// <paramref name="work"/>, the rest of the write the copies were made for (review S2-4: the save's guards, the access of a copy).
    /// When it throws, nothing has committed the copies' rows, so their files go, as a failed upload's file goes; a failing COMMIT
    /// leaves them on purpose (an orphan is recoverable, deleting committed content is not).
    /// </summary>
    public static async Task<T> DiscardOnFailureAsync<T>(AttachmentFiles files, IReadOnlyCollection<Guid> copies, Func<Task<T>> work)
    {
        try
        {
            return await work();
        }
        catch
        {
            Remove(files, copies);
            throw;
        }
    }

    private static void Remove(AttachmentFiles files, IEnumerable<Guid> ids)
    {
        foreach (var id in ids)
        {
            files.Remove(id);
        }
    }
}
