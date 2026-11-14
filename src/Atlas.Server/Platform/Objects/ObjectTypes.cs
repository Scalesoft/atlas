namespace Atlas.Server.Platform.Objects;

/// <summary>
/// The object types and views the enabled modules registered, resolved at run time from the scope (a factory, so Wolverine's
/// generated handler code is the same with any set of modules on). Lookups miss for anything not registered: fail closed.
/// </summary>
public sealed class ObjectTypes
{
    private readonly IServiceProvider m_services;

    public ObjectTypes(IServiceProvider services)
    {
        m_services = services;
    }

    public IObjectType? Find(string typeId) => m_services.GetServices<IObjectType>().FirstOrDefault(type => type.TypeId == typeId);

    /// <summary>The trash of a type that keeps its own (IObjectTrash: a page, a board); null for a stored type and anything unregistered.</summary>
    public IObjectTrash? Trash(string typeId) => m_services.GetServices<IObjectTrash>().FirstOrDefault(trash => trash.TypeId == typeId);

    public IObjectView? View(string kind) => m_services.GetServices<IObjectView>().FirstOrDefault(view => view.Kind == kind);

    public IReadOnlyList<IObjectType> All() => m_services.GetServices<IObjectType>().ToArray();

    /// <summary>Every type the enabled modules registered.</summary>
    public IReadOnlyList<string> TypeIds() => m_services.GetServices<IObjectType>().Select(type => type.TypeId).ToArray();

    /// <summary>The types whose objects the platform stores (their library is the object library).</summary>
    public IReadOnlyList<string> StoredTypeIds() => m_services.GetServices<IObjectType>().Where(type => type is IObjectChangeHandler).Select(type => type.TypeId).ToArray();
}
