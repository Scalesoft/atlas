-- The ZIP and Git backups run as jobs (3.3 plan, rows 10 and 11). Every BackupRun becomes a run of the job backups.zip, so the history
-- of the checks is on the jobs page: a succeeded or skipped check succeeded, a failed one failed with its message as the error; the
-- message is the run's log line either way, as the runner logs it. A check still running at deployment was cut off by it: it fails as the recovery of the old scheduler failed it,
-- and an enabled schedule is due at once, so the job repeats the check right after the start, as that recovery did. BackupRun keeps
-- its rows: the archives (succeeded) for the download and retention, the other states read-only for one release.
UPDATE "BackupSettings" SET "NextRunAt" = now()
WHERE "Enabled" AND EXISTS (SELECT 1 FROM "BackupRun" WHERE "Status" = 'running');

UPDATE "BackupRun" SET "Status" = 'failed', "FinishedAt" = greatest(now(), "StartedAt"),
  "Message" = 'Předchozí běh byl přerušen restartem nebo ztrátou připojení. Předchozí zálohy zůstaly zachovány.'
WHERE "Status" = 'running';

INSERT INTO "JobRun" ("JobId", "Trigger", "State", "Attempt", "QueuedAt", "StartedAt", "FinishedAt", "Error", "Log")
SELECT 'backups.zip', CASE r."Trigger" WHEN 'manual' THEN 'manual' ELSE 'schedule' END,
  CASE r."Status" WHEN 'failed' THEN 'failed' ELSE 'succeeded' END, 1, r."StartedAt", r."StartedAt",
  coalesce(r."FinishedAt", r."StartedAt"), CASE WHEN r."Status" = 'failed' THEN r."Message" END,
  CASE WHEN r."Message" <> '' THEN jsonb_build_array(jsonb_build_object('at', coalesce(r."FinishedAt", r."StartedAt"),
    'level', CASE r."Status" WHEN 'failed' THEN 'Error' ELSE 'Information' END, 'message', r."Message")) END
FROM "BackupRun" r
ORDER BY r."StartedAt", r."Id";

-- The Git backup's own retry counter gives way to the job's retries; its last error stays for the page and the warning.
ALTER TABLE "GitBackupSettings" DROP COLUMN "Failures", DROP COLUMN "NextAttemptAt";
