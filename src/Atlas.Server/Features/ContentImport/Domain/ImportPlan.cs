using Atlas.Server.Features.ContentImport.Contracts;

namespace Atlas.Server.Features.ContentImport.Domain;

public sealed record ImportPlan(WikiSourcePage Source, ImportedDocument Document, Dictionary<Guid, WikiSourceFile> Assets, WikiImportItem Item);
