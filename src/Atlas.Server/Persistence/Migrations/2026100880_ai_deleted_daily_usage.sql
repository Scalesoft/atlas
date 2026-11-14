-- Content-free daily consumption survives deletion of private conversations.
-- Plain mutable counts, not temporal history. Owner/task/day is the complete natural key.
-- No backfill: live AiMessage rows remain counted; only deletion moves usage here, atomically.
CREATE TABLE "AiDeletedDailyUsage" (
    "OwnerId" uuid NOT NULL CONSTRAINT "FK_AiDeletedDailyUsage_Owner" REFERENCES "UserAccount"("Id"),
    "Task" varchar(48) NOT NULL,
    "Day" timestamptz NOT NULL CONSTRAINT "CK_AiDeletedDailyUsage_Day" CHECK ("Day"=date_trunc('day',"Day",'UTC')),
    "Count" bigint NOT NULL CONSTRAINT "CK_AiDeletedDailyUsage_Count" CHECK ("Count">0),
    CONSTRAINT "PK_AiDeletedDailyUsage" PRIMARY KEY ("OwnerId","Task","Day")
);
CREATE INDEX "IX_AiDeletedDailyUsage_Day" ON "AiDeletedDailyUsage" ("Day");
