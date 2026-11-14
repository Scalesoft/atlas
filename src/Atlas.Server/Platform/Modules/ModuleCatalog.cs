namespace Atlas.Server.Platform.Modules;

/// <summary>
/// The modules of the distribution split by the startup configuration (<c>Modules:{Id}:Enabled</c>; optional modules default
/// to off). Built once at startup by AtlasServices; an inconsistent distribution or configuration stops the startup with a
/// message naming the module, instead of running with a module silently missing.
/// </summary>
public sealed class ModuleCatalog
{
    private ModuleCatalog(IReadOnlyList<IAtlasModule> all, IReadOnlyList<IAtlasModule> enabled)
    {
        All = all;
        Enabled = enabled;
        Disabled = all.Except(enabled).ToArray();
    }

    public IReadOnlyList<IAtlasModule> All { get; }

    public IReadOnlyList<IAtlasModule> Enabled { get; }

    public IReadOnlyList<IAtlasModule> Disabled { get; }

    public bool IsEnabled(string id)
    {
        return Enabled.Any(module => module.Descriptor.Id == id);
    }

    public ModuleList Describe()
    {
        return new ModuleList(Enabled.Select(module => new ModuleInfo(module.Descriptor.Id, module.Descriptor.Version)).ToArray(),
            Enabled.SelectMany(module => module.Descriptor.ContentTypes).ToArray());
    }

    public static ModuleCatalog Build(IConfiguration configuration, IReadOnlyList<IAtlasModule> modules)
    {
        var owners = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var module in modules)
        {
            var id = module.Descriptor.Id;
            if (modules.Count(other => other.Descriptor.Id == id) > 1)
            {
                throw new InvalidOperationException($"Module '{id}' is declared twice in the distribution.");
            }
            foreach (var type in module.Descriptor.ContentTypes)
            {
                if (!owners.TryAdd(type, id))
                {
                    throw new InvalidOperationException($"Content type '{type}' is declared by both '{owners[type]}' and '{id}'.");
                }
            }
        }
        var enabled = modules.Where(module => IsSwitchedOn(configuration, module)).ToArray();
        foreach (var module in enabled)
        {
            foreach (var required in module.Descriptor.Requires)
            {
                if (modules.All(other => other.Descriptor.Id != required))
                {
                    throw new InvalidOperationException($"Module '{module.Descriptor.Id}' requires '{required}', which this distribution does not contain.");
                }
                if (enabled.All(other => other.Descriptor.Id != required))
                {
                    throw new InvalidOperationException($"Module '{module.Descriptor.Id}' requires '{required}', which is disabled (Modules:{required}:Enabled).");
                }
            }
        }
        return new ModuleCatalog(modules, enabled);
    }

    private static bool IsSwitchedOn(IConfiguration configuration, IAtlasModule module)
    {
        var key = $"Modules:{module.Descriptor.Id}:Enabled";
        if (!module.Mandatory)
        {
            return configuration.GetValue(key, false);
        }
        if (!configuration.GetValue(key, true))
        {
            throw new InvalidOperationException($"Module '{module.Descriptor.Id}' is mandatory and cannot be disabled; remove {key}.");
        }
        return true;
    }
}
