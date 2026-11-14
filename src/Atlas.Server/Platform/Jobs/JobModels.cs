using Atlas.Server.Platform.Persistence;
using FluentMigrator;
using NHibernate;
using NHibernate.Mapping.ByCode;
using NHibernate.Mapping.ByCode.Conformist;

namespace Atlas.Server.Platform.Jobs;

/// <summary>A job's state (§4.1). The start creates the row of every registered definition; the title and description live in code.
/// A row whose definition is gone stays and the UI shows it as „Neznámá úloha“ with its history.</summary>
public class Job
{
    public virtual string Id { get; set; } = "";
    public virtual string ModuleId { get; set; } = "";
    public virtual bool Paused { get; set; }
    public virtual Guid? PausedBy { get; set; }
    public virtual DateTime? PausedAt { get; set; }

    /// <summary>„Příště“ in the UI, written by whoever planned it (the recurring plan, the data schedule).</summary>
    public virtual DateTime? NextDueAt { get; set; }

    /// <summary>A <see cref="JobSchedule.Once"/> job only: when it first succeeded.</summary>
    public virtual DateTime? CompletedAt { get; set; }
}

/// <summary>One run of a job (§4.1): it replaces the run rows of the features (BackupRun, ContactLinkRun, …).</summary>
public class JobRun
{
    public virtual long Id { get; set; }
    public virtual string JobId { get; set; } = "";

    /// <summary>The key a keyed request merges on; null for an unkeyed one.</summary>
    public virtual string? Key { get; set; }

    public virtual string Trigger { get; set; } = JobTriggers.Event;
    public virtual string? Reason { get; set; }
    public virtual Guid? RequestedBy { get; set; }

    /// <summary>A job of a space (§4.6): its administrators see the run.</summary>
    public virtual Guid? SpaceId { get; set; }

    public virtual string State { get; set; } = JobStates.Queued;
    public virtual int Attempt { get; set; }
    public virtual DateTime QueuedAt { get; set; }
    public virtual DateTime? NotBefore { get; set; }
    public virtual DateTime? StartedAt { get; set; }
    public virtual DateTime? HeartbeatAt { get; set; }
    public virtual DateTime? FinishedAt { get; set; }
    public virtual bool CancelRequested { get; set; }

    /// <summary>jsonb: the job's checkpoint.</summary>
    public virtual string? Progress { get; set; }

    /// <summary>jsonb: the numbers for the UI.</summary>
    public virtual string? Summary { get; set; }

    /// <summary>The exception: its first line and stack.</summary>
    public virtual string? Error { get; set; }

    /// <summary>jsonb array of { at, level, message }, at most 500 entries.</summary>
    public virtual string? Log { get; set; }
}

public sealed class JobMap : ClassMapping<Job>
{
    public JobMap()
    {
        Table("`Job`");
        Id(x => x.Id, m => { m.Column("`Id`"); m.Generator(Generators.Assigned); });
        Property(x => x.ModuleId);
        Property(x => x.Paused);
        Property(x => x.PausedBy);
        Property(x => x.PausedAt, m => m.Type(NHibernateUtil.UtcDateTime));
        Property(x => x.NextDueAt, m => m.Type(NHibernateUtil.UtcDateTime));
        Property(x => x.CompletedAt, m => m.Type(NHibernateUtil.UtcDateTime));
    }
}

public sealed class JobRunMap : ClassMapping<JobRun>
{
    public JobRunMap()
    {
        Table("`JobRun`");
        Id(x => x.Id, m => { m.Column("`Id`"); m.Generator(Generators.Identity); });
        Property(x => x.JobId);
        Property(x => x.Key);
        Property(x => x.Trigger);
        Property(x => x.Reason);
        Property(x => x.RequestedBy);
        Property(x => x.SpaceId);
        Property(x => x.State);
        Property(x => x.Attempt);
        Property(x => x.QueuedAt, m => m.Type(NHibernateUtil.UtcDateTime));
        Property(x => x.NotBefore, m => m.Type(NHibernateUtil.UtcDateTime));
        Property(x => x.StartedAt, m => m.Type(NHibernateUtil.UtcDateTime));
        Property(x => x.HeartbeatAt, m => m.Type(NHibernateUtil.UtcDateTime));
        Property(x => x.FinishedAt, m => m.Type(NHibernateUtil.UtcDateTime));
        Property(x => x.CancelRequested);
        Property(x => x.Progress, m => m.Type<JsonbText>());
        Property(x => x.Summary, m => m.Type<JsonbText>());
        Property(x => x.Error);
        Property(x => x.Log, m => m.Type<JsonbText>());
    }
}

[Migration(2026110400, "Background jobs and their runs (3.3)")]
public sealed class JobsMigration() : EmbeddedSchemaMigration("2026110400_jobs.sql");
