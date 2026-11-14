-- Keep coverage with the message so history cannot present a partial summary as complete.
ALTER TABLE "AiConversation" DROP CONSTRAINT "CK_AiConversation_Origin";
ALTER TABLE "AiConversation" ADD CONSTRAINT "CK_AiConversation_Origin" CHECK ("Origin" IN ('search','assistant','page'));
ALTER TABLE "AiMessage" ADD COLUMN "SourceCoverageTruncated" boolean NOT NULL DEFAULT false;
