using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Atlas.Server.Features.Attachments;
using Atlas.Server.Features.Backups.Installation;
using Atlas.Server.Features.Vaults;
using Atlas.Server.Platform.Backup;
using Atlas.Server.Tests.Attachments;
using Atlas.Server.Tests.Platform;

namespace Atlas.Server.Tests.Backups;

/// <summary>The format v7 services over one test host, wired as BackupModule wires them; <see cref="Vaults"/> is the managed vault folder.</summary>
internal sealed class InstallationServices
{
    public InstallationServices(AtlasTestHost host, AttachmentFiles attachments, string root, IDataProtectionProvider? protection = null)
    {
        Protection = protection ?? new EphemeralDataProtectionProvider();
        var environment = new AttachmentTests.EnvironmentStub(root);
        Vaults = new VaultFolders(new ConfigurationBuilder().Build(), environment);
        Catalog = InstallationModules.Catalog(Protection, attachments, Vaults, NullLogger.Instance);
        Export = new InstallationExport(Catalog);
        Keys = new BackupKeys(Protection);
        Archive = new InstallationArchive(host.Database, Export, Keys);
        Restore = new InstallationRestore(host.Database, Catalog, NullLogger<InstallationRestore>.Instance);
    }

    public IDataProtectionProvider Protection { get; }
    public VaultFolders Vaults { get; }
    public BackupCatalog Catalog { get; }
    public InstallationExport Export { get; }
    public BackupKeys Keys { get; }
    public InstallationArchive Archive { get; }
    public InstallationRestore Restore { get; }
}
