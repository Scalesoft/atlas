-- Atlas 3.6, no logic in the database (search): the code that changes a page's revisions, publication, space or imports rewrites its
-- search documents in its own transaction (SearchDocuments, one statement of built-in functions), boards and comments theirs
-- (BoardSearch, CommentWrites), and every search, similar-pages, hybrid-ranking, link-candidate and answer-source read is C# query text
-- (SearchQueries, SemanticSuggestionQueries, AnswerSourceQueries). The triggers and functions that did it go; the documents they wrote
-- stay as they are: the code computes the same rows (SearchWithoutDatabaseLogicMigrationTests), so nothing is rebuilt. The rebuild job
-- search.documents-rebuild stays for a run by hand from Úlohy.
DROP TRIGGER "TR_PageRevision_WorkingSearch" ON "PageRevision";
DROP TRIGGER "TR_WorkingSearchDocument_Project" ON "WorkingSearchDocument";
DROP TRIGGER "TR_Page_SearchDocument" ON "Page";
DROP TRIGGER "TR_Page_SearchDocumentChange" ON "Page";
DROP TRIGGER "TR_SearchDocument_Structure" ON "SearchDocument";
DROP TRIGGER "TR_SearchDocument_Aliases" ON "SearchDocument";
DROP TRIGGER "TR_PagePublication_SearchAliases" ON "PagePublication";
DROP TRIGGER "TR_WikiImportPage_SearchAliases" ON "WikiImportPage";
DROP TRIGGER "TR_WikiImportCheckpoint_SearchAliases" ON "WikiImportCheckpoint";

DROP FUNCTION atlas_refresh_working_search_document();
DROP FUNCTION atlas_project_working_search_document();
DROP FUNCTION atlas_refresh_search_document();
DROP FUNCTION atlas_refresh_published_search_document(uuid);
DROP FUNCTION atlas_update_search_structure();
DROP FUNCTION atlas_update_search_aliases();
DROP FUNCTION atlas_refresh_publication_aliases();
DROP FUNCTION atlas_refresh_import_page_aliases();
DROP FUNCTION atlas_refresh_import_checkpoint_aliases();
DROP FUNCTION atlas_refresh_board_search(uuid);
DROP FUNCTION atlas_refresh_comment_search(uuid);

DROP FUNCTION atlas_search_published(text, text, text, boolean, boolean, uuid, text, uuid, integer, integer);
DROP FUNCTION atlas_search_published_filtered(text, text, text, boolean, boolean, uuid, text, uuid, integer, integer, timestamp with time zone, boolean);
DROP FUNCTION atlas_search_drafts(text, text, text, boolean, boolean, uuid, text, uuid, integer, integer, timestamp with time zone, boolean);
DROP FUNCTION atlas_search_all(text, text, text, boolean, boolean, uuid, text, uuid, integer, integer, timestamp with time zone, boolean);
DROP FUNCTION atlas_search_spaces(text, text, text, boolean, boolean, uuid, text, uuid);
DROP FUNCTION atlas_search_did_you_mean(text, uuid, text, uuid);
DROP FUNCTION atlas_search_hybrid_all(text, text, text, boolean, boolean, uuid, text, uuid, integer, integer, timestamp with time zone, boolean, uuid, public.vector, real);
DROP FUNCTION atlas_search_hybrid_published(text, text, text, boolean, boolean, uuid, text, uuid, integer, integer, timestamp with time zone, boolean, uuid, public.vector, real);
DROP FUNCTION atlas_search_hybrid(text, text, text, boolean, boolean, uuid, text, uuid, integer, integer, timestamp with time zone, boolean, uuid, public.vector, real, boolean);
DROP FUNCTION atlas_search_hybrid_candidates(text, text, text, boolean, boolean, uuid, text, uuid, integer, timestamp with time zone, boolean, uuid, public.vector, real);
DROP FUNCTION atlas_similar_pages(text, text, uuid, text, uuid, uuid, integer);
DROP FUNCTION atlas_similar_pages_semantic(uuid, public.vector, uuid, text, uuid, uuid, integer, real);
DROP FUNCTION atlas_link_candidates(text[], uuid, text, uuid, uuid);
DROP FUNCTION atlas_link_candidates_semantic(uuid, text[], public.vector[], uuid, text, uuid, real);
DROP FUNCTION atlas_rank_by_context(uuid, public.vector, uuid[], uuid, text);
DROP FUNCTION atlas_placement_parent(uuid, public.vector, uuid, uuid, text, boolean, real);
DROP FUNCTION atlas_template_suggestion(uuid, public.vector, text, uuid, uuid, text, real);
DROP FUNCTION atlas_answer_sources(uuid, text, uuid, uuid, text, text, text, boolean, uuid, public.vector, real, integer, integer, integer);
DROP FUNCTION atlas_answer_source_status(uuid, text, uuid[], uuid[]);
-- Read by nothing since the assistant's sources moved to atlas_answer_source_status (migration 2026100700 created it).
DROP FUNCTION atlas_ai_dependency_status(uuid, text, uuid[]);

DROP FUNCTION atlas_search_headings(text, text);
DROP FUNCTION atlas_search_body(text, text);
DROP FUNCTION atlas_search_component_text(text);
DROP FUNCTION atlas_search_lexemes(text, text);
DROP FUNCTION atlas_search_identifiers(text);
DROP FUNCTION atlas_search_stem(text);
DROP FUNCTION atlas_comment_search_text(text);
DROP FUNCTION atlas_strip_secret_directives(text);
DROP FUNCTION atlas_normalize_search_text(text);
