-- 3.6: the server's own log (Správa wiki → Systémový log, atlas_read_logs). AppLogProvider queues what ILogger writes at the configured
-- level, AppLogWriter inserts it in batches with secrets redacted and texts capped; OperationalRetention ages it (14 days, 200 000 rows by
-- default). Operational: never in a backup, and a restore keeps it (the log of the running process, the restore's own lines included).
-- "Level" is Microsoft.Extensions.Logging.LogLevel (0 trace … 5 critical). "Id" orders the log and is the paging cursor. "JobRunId" is the
-- background job run the line was logged in (its log scope); no foreign key: the runs age out sooner than the log.
CREATE TABLE "AppLog" (
  "Id" bigint GENERATED ALWAYS AS IDENTITY CONSTRAINT "PK_AppLog" PRIMARY KEY,
  "At" timestamptz NOT NULL,
  "Level" smallint NOT NULL CONSTRAINT "CK_AppLog_Level" CHECK ("Level" BETWEEN 0 AND 5),
  "Category" varchar(200) NOT NULL,
  "EventId" integer NULL,
  "Message" text NOT NULL,
  "ExceptionType" varchar(300) NULL,
  "ExceptionMessage" text NULL,
  "Exception" text NULL,
  "TraceId" varchar(64) NULL,
  "UserId" uuid NULL,
  "JobRunId" bigint NULL);
-- Append-only in time order: a BRIN index of a few pages serves the age cleanup and the time filters.
CREATE INDEX "IX_AppLog_At" ON "AppLog" USING brin ("At");
-- The warnings and errors are the usual filter and a small part of the log.
CREATE INDEX "IX_AppLog_Warnings" ON "AppLog" ("Id") WHERE "Level" >= 3;
-- The log of one run (Úlohy na pozadí → běh); most lines belong to no run.
CREATE INDEX "IX_AppLog_JobRun" ON "AppLog" ("JobRunId", "Id") WHERE "JobRunId" IS NOT NULL;
