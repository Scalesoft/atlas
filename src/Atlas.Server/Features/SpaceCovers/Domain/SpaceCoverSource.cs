namespace Atlas.Server.Features.SpaceCovers.Domain;

/// <summary>Where the image of a space comes from. Auto: the server picks a catalog drawing on every read; Catalog: a drawing a
/// person chose; Upload: the uploaded file; None: no image by choice. Stored and sent by name. Auto is 0, so a new
/// <see cref="SpaceCover"/> without a file is a valid Auto row.</summary>
public enum SpaceCoverSource
{
    Auto,
    Catalog,
    Upload,
    None
}
