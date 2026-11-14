using Microsoft.AspNetCore.Http;

namespace Atlas.Server.Features.ContentImport.Contracts;

public sealed record WikiImportUpload(IFormFile File, string Options);
