-- SearchDocument is a rebuildable projection of published content, never authoritative history.
ALTER TABLE "SearchDocument" ADD COLUMN "Headings" jsonb NOT NULL DEFAULT '[]';
ALTER TABLE "SearchDocument" ADD COLUMN "HeadingVector" tsvector NOT NULL DEFAULT '';
ALTER TABLE "SearchDocument" ADD COLUMN "PublishedAt" timestamptz NULL;

CREATE FUNCTION atlas_search_headings(markdown text, rich text) RETURNS jsonb LANGUAGE plpgsql STABLE AS $$
DECLARE line text; heading text; fence text; marker text; result jsonb := '[]'; node jsonb;
        normalized text; rich_title text; anchor text; anchors jsonb := '{}';
BEGIN
  -- Traverse canonical rich nodes once per publication, not once per heading or query.
  FOR node IN SELECT value FROM jsonb_path_query(rich::jsonb->'document',
    'strict $.** ? (@.type == "heading")', '{}'::jsonb, true) AS value LOOP
    SELECT string_agg(value #>> '{}','') INTO rich_title
    FROM jsonb_path_query(node,'strict $.** ? (exists(@.text)).text','{}'::jsonb,true) AS value;
    normalized := atlas_normalize_search_text(COALESCE(rich_title,''));
    anchor := CASE WHEN node->'attrs'->>'headingId' ~ '^[A-Za-z0-9][A-Za-z0-9_-]{0,127}$'
      THEN node->'attrs'->>'headingId' ELSE NULL END;
    anchors := jsonb_set(anchors,ARRAY[normalized],COALESCE(anchors->normalized,'[]'::jsonb)||jsonb_build_array(anchor));
  END LOOP;
  FOREACH line IN ARRAY string_to_array(markdown,E'\n') LOOP
    marker := substring(line from '^ {0,3}(`{3,}|~{3,})');
    IF fence IS NOT NULL THEN
      IF marker IS NOT NULL AND left(marker,1)=left(fence,1) AND length(marker)>=length(fence)
        AND line ~ '^ {0,3}(`{3,}|~{3,})[[:space:]]*$' THEN fence := NULL; END IF;
      CONTINUE;
    END IF;
    IF marker IS NOT NULL THEN fence := marker; CONTINUE; END IF;
    heading := substring(line from '^ {0,3}#{1,6}[[:blank:]]+(.+)$');
    IF heading IS NULL THEN CONTINUE; END IF;
    heading := trim(regexp_replace(heading,'[[:blank:]]+#+[[:blank:]]*$',''));
    heading := regexp_replace(heading,'!?\[([^]]+)\]\([^)]*\)','\1','g');
    heading := regexp_replace(heading,'[*_`]+','','g');
    normalized := atlas_normalize_search_text(heading);
    anchor := NULL;
    -- Duplicate titles consume their own persistent anchor in document order.
    anchor := anchors->normalized->>0;
    IF anchors ? normalized THEN anchors := jsonb_set(anchors,ARRAY[normalized],(anchors->normalized)-0); END IF;
    result := result || jsonb_build_array(jsonb_build_object('title',heading,'normalizedTitle',normalized,'headingId',anchor));
  END LOOP;
  RETURN result;
END $$;

CREATE FUNCTION atlas_update_search_structure() RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE heading jsonb; boundary tsvector := '''__scaleatlas_field_boundary__'':1'::tsvector;
BEGIN
  SELECT atlas_search_headings(r."Markdown",r."ContentJson"),
    COALESCE((SELECT v."ActiveFrom" FROM "PagePublication" v WHERE v."PageId"=NEW."PageId" AND v."ActiveTo" IS NULL),r."ActiveFrom")
  INTO NEW."Headings",NEW."PublishedAt" FROM "PageRevision" r WHERE r."Id"=NEW."RevisionId";
  NEW."HeadingVector" := ''::tsvector;
  FOR heading IN SELECT value FROM jsonb_array_elements(NEW."Headings") LOOP
    NEW."HeadingVector" := NEW."HeadingVector" || boundary || to_tsvector('simple',heading->>'normalizedTitle');
  END LOOP;
  NEW."HeadingVector" := ts_delete(NEW."HeadingVector",'__scaleatlas_field_boundary__');
  -- Preserve a positional gap between fields so quoted phrases cannot cross title/body or two headings.
  -- Delete the unqueryable separator lexeme after concatenation; its position gap remains.
  NEW."Vector" := ts_delete(setweight(to_tsvector('simple',NEW."NormalizedTitle"),'A') || boundary
    || setweight(NEW."HeadingVector",'B') || boundary || setweight(to_tsvector('simple',NEW."NormalizedBody"),'D'),
    '__scaleatlas_field_boundary__');
  RETURN NEW;
END $$;
CREATE TRIGGER "TR_SearchDocument_Structure" BEFORE INSERT OR UPDATE OF "RevisionId","Title","Body","NormalizedTitle","NormalizedBody"
  ON "SearchDocument" FOR EACH ROW EXECUTE FUNCTION atlas_update_search_structure();

-- The same publication trigger continues to own transactional refresh. Rebuild existing rows once.
UPDATE "SearchDocument" SET "RevisionId"="RevisionId";
