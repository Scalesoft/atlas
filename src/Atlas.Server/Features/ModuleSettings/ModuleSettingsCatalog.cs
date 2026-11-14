using System.Text.RegularExpressions;
using Atlas.Server.Features.ModuleSettings.Contracts;

namespace Atlas.Server.Features.ModuleSettings;

public sealed class ModuleSettingsCatalog
{
    public IReadOnlyList<ModuleSettingsDescriptor> Items { get; }

    public ModuleSettingsCatalog(IEnumerable<ModuleSettingsDescriptor> descriptors)
    {
        var items = descriptors.ToArray();
        if (items.Any(x => !Regex.IsMatch(x.Id, "^[a-z][a-z0-9-]{0,39}$") || string.IsNullOrWhiteSpace(x.SettingsUi))
            || items.Select(x => x.Id).Distinct(StringComparer.Ordinal).Count() != items.Length)
        {
            throw new InvalidOperationException("Module settings contributions must have unique stable IDs and a registered UI key.");
        }
        Items = items.OrderBy(x => x.Order).ThenBy(x => x.Id, StringComparer.Ordinal).ToArray();
    }
}
