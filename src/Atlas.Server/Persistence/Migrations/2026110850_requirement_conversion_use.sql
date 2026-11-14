-- Atlas 3.6 (owner's approval of the dry run, 2026-11-04): the requirement conversion admits only the approved cases, as a budget per
-- case kind (RequirementPageConversion.ApprovedCases). The job pages.requirements-headings and repair on open both convert pages, so the
-- use of the budget lives here, read and written under the conversion's advisory lock, not in one job run's checkpoint. A restore of a
-- backup starts it empty with the restored content (BackupCoverage.Operational), and the job runs again.
CREATE TABLE "RequirementConversionUse" (
  "Kind" text NOT NULL CONSTRAINT "PK_RequirementConversionUse" PRIMARY KEY,
  "Used" integer NOT NULL);
