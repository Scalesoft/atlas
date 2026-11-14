-- The rerank profile (task search.rerank): a cross-encoder such as bge-reranker-v2-m3 on an OpenAI-compatible server that answers
-- POST {base}/rerank (llama.cpp --reranking, TEI, Infinity, Jina). A fourth profile kind next to chat, embeddings and quick tasks.
-- Existing revisions, assignments, saved profiles and usage rows stay unchanged.
ALTER TABLE "AiProfileRevision" DROP CONSTRAINT "AiProfileRevision_Kind_check";
ALTER TABLE "AiProfileRevision" ADD CONSTRAINT "CK_AiProfileRevision_Kind" CHECK ("Kind" IN ('chat', 'embeddings', 'quick', 'rerank'));
ALTER TABLE "AiProfileRevision" ADD CONSTRAINT "CK_AiProfileRevision_Rerank" CHECK ("Kind" <> 'rerank' OR "Provider" = 'openai-compatible');
ALTER TABLE "AiProfileAssignment" DROP CONSTRAINT "AiProfileAssignment_Kind_check";
ALTER TABLE "AiProfileAssignment" ADD CONSTRAINT "CK_AiProfileAssignment_Kind" CHECK ("Kind" IN ('chat', 'embeddings', 'quick', 'rerank'));
ALTER TABLE "AiSavedProfile" DROP CONSTRAINT "AiSavedProfile_Kind_check";
ALTER TABLE "AiSavedProfile" ADD CONSTRAINT "CK_AiSavedProfile_Kind" CHECK ("Kind" IN ('chat', 'embeddings', 'quick', 'rerank'));
ALTER TABLE "AiUsage" DROP CONSTRAINT "AiUsage_Operation_check";
ALTER TABLE "AiUsage" ADD CONSTRAINT "CK_AiUsage_Operation" CHECK ("Operation" IN ('chat', 'embedding', 'rerank'));
