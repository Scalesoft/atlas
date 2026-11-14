using Atlas.Server.Features.Contacts.LinkRun.Commands;
using Atlas.Server.Platform.Jobs;
using Wolverine;

namespace Atlas.Server.Features.Contacts.LinkRun;

/// <summary>
/// Gives every account its contact in the background (AccountContacts) as a job: at every start, for the accounts of the installation
/// after the deploy that adds it, and where an account is created (IJobRequests: the built-in invitation, the first OIDC or Hub sign-in,
/// the setup's founder) right after its commit. A batch per transaction until a batch matches nothing, so an account the directory refuses
/// never spins the loop. Test hosts send <see cref="SweepAccountContacts"/> or run the job with IJobRunner.
/// </summary>
public sealed class AccountContactsJob : IJobDefinition
{
    public const string JobId = "contacts.accounts";

    public string Id => JobId;

    public string Title => "Kontakty účtů";

    public string Description => "Dá každému aktivnímu účtu jeho kontakt v adresáři: propojí ten se stejnou adresou, jinak založí vlastní.";

    public JobSchedule? Schedule => JobSchedule.AtStartup;

    public async Task RunAsync(JobContext context, CancellationToken ct)
    {
        var bus = context.Services.GetRequiredService<IMessageBus>();
        int matched, accounts = 0;
        // shortcut: 50 accounts the directory keeps refusing hide the ones after them (the sweep takes them in id order), as the old worker did;
        // give the sweep a cursor if refusals ever pile up.
        while ((matched = await bus.InvokeAsync<int>(new SweepAccountContacts(), ct)) > 0)
        {
            accounts += matched;
        }
        context.Summarize(new { accounts });
        context.Log.LogInformation("Kontakty účtů: spárováno nebo založeno {Accounts}.", accounts);
    }
}
