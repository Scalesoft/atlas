-- Gateway shares the OpenAI transport, but remains a distinct company chat/quick provider.
-- Existing immutable revisions and their assignments stay unchanged.
ALTER TABLE "AiProfileRevision" DROP CONSTRAINT "AiProfileRevision_Provider_check";
ALTER TABLE "AiProfileRevision" ADD CONSTRAINT "CK_AiProfileRevision_Provider"
    CHECK ("Provider" IN ('openai-compatible', 'anthropic', 'gateway'));
ALTER TABLE "AiProfileRevision" ADD CONSTRAINT "CK_AiProfileRevision_Gateway"
    CHECK ("Provider" <> 'gateway' OR ("Location" = 'company' AND "Kind" IN ('chat', 'quick')));
