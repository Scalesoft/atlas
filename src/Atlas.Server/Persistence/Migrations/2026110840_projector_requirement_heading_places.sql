-- Atlas 3.6: fixed places and one facts grammar. The
-- dependency line is the node directly under the heading when it is a paragraph with attrs.dependencies true; the facts line is the next
-- node (after the dependency line, if any) when it is a paragraph whose first ' · ' segment reads as a party or a deadline; a dependencies
-- paragraph anywhere else is a note (2026110820 took the first one of the section, so a join moved lines between requirements). Segments are
-- trimmed of spaces and no-break spaces; a YouTrack issue reads as its key and a page link as its title in a facts line. The rules are
-- RequirementHeadings.cs's (tests/fixtures/content/requirement-headings.json keeps the two equal). The index is derived: rebuilt at the end.

-- The text of an inline content array: text, a line break as a space; with facts, a contact as its name, a YouTrack issue as its key and a
-- page link as its title, else (a title) a YouTrack issue as its key (RequirementText.Title and RequirementHeadings.Text).
CREATE OR REPLACE FUNCTION atlas_projector_text(content jsonb, facts boolean) RETURNS text LANGUAGE sql IMMUTABLE AS $$
  SELECT coalesce(string_agg(CASE t.c->>'type' WHEN 'text' THEN coalesce(t.c->>'text','') WHEN 'hardBreak' THEN ' '
      WHEN 'contactInline' THEN CASE WHEN facts THEN coalesce(t.c->'attrs'->'data'->'contacts'->0->>'name','') ELSE '' END
      WHEN 'pageReference' THEN CASE WHEN facts THEN coalesce(t.c->'attrs'->>'title','') ELSE '' END
      WHEN 'youtrackIssue' THEN btrim(coalesce(t.c->'attrs'->>'key',''),' ') ELSE '' END,'' ORDER BY t.o),'')
  FROM jsonb_array_elements(CASE WHEN jsonb_typeof(content)='array' THEN content ELSE '[]'::jsonb END) WITH ORDINALITY t(c,o)
$$;

-- The party of a facts segment: the rest after „od “ or „pro “, trimmed of spaces and no-break spaces; NULL for any other segment.
CREATE OR REPLACE FUNCTION atlas_projector_party(segment text) RETURNS text LANGUAGE sql IMMUTABLE AS $$
  SELECT CASE WHEN s LIKE 'od %' THEN btrim(substr(s,4),' '||chr(160)) WHEN s LIKE 'pro %' THEN btrim(substr(s,5),' '||chr(160)) END
  FROM (SELECT btrim(segment,' '||chr(160)) AS s) x
$$;

-- „do 15. 11. 2026“ as 2026-11-15 when the day is in the calendar (a space or a no-break space, chr(160), between the parts and around
-- them); NULL for any other segment. Nested CASE: no date is built from a bad part.
CREATE OR REPLACE FUNCTION atlas_projector_deadline(segment text) RETURNS text LANGUAGE sql IMMUTABLE AS $$
  SELECT CASE WHEN m IS NULL OR m[3]::int<1 OR m[2]::int NOT BETWEEN 1 AND 12 OR m[1]::int<1 THEN NULL
    ELSE CASE WHEN m[1]::int<=extract(day FROM make_date(m[3]::int,m[2]::int,1)+interval '1 month'-interval '1 day')
      THEN lpad(m[3],4,'0')||'-'||lpad(m[2],2,'0')||'-'||lpad(m[1],2,'0') END END
  FROM (SELECT regexp_match(btrim(segment,' '||chr(160)),replace('^do[ ~]+([0-9]{1,2})\.[ ~]*([0-9]{1,2})\.[ ~]*([0-9]{4})$','~',chr(160))) AS m) x
$$;

-- 2026110820's function with the fixed places: deps is the node at the heading + 1, facts the node right after it (or after the heading).
CREATE OR REPLACE FUNCTION atlas_projector_requirements(rich text, page uuid DEFAULT NULL)
RETURNS TABLE("Position" integer, "BlockId" text, "Key" text, "Title" text, "Direction" text, "State" text, "Date" text, "Party" text,
  "Where" text, "Requires" text, "ParentBlockId" text, "PartyContactId" text)
LANGUAGE sql IMMUTABLE AS $$
  WITH RECURSIVE top AS MATERIALIZED (
    SELECT c.value AS node, c.ordinality::int AS i
    FROM jsonb_array_elements(CASE WHEN jsonb_typeof(rich::jsonb->'document'->'content')='array'
      THEN rich::jsonb->'document'->'content' ELSE '[]'::jsonb END) WITH ORDINALITY c
  ), walk(node, parent, path) AS (
    SELECT node, NULL::text, ARRAY[i] FROM top
    UNION ALL
    SELECT c.value, CASE WHEN w.node->>'type'='requirement' THEN coalesce(w.node->'attrs'->>'id','') ELSE w.parent END, w.path || c.ordinality::int
    FROM walk w CROSS JOIN LATERAL jsonb_array_elements(CASE WHEN jsonb_typeof(w.node->'content')='array'
      THEN w.node->'content' ELSE '[]'::jsonb END) WITH ORDINALITY c
  ), heads AS MATERIALIZED (
    SELECT i, node, coalesce(lead(i) OVER (ORDER BY i), 2147483647) AS fin FROM top WHERE node->>'type'='heading'
  ), reqs AS MATERIALIZED (
    SELECT i, fin, node, node->'attrs' AS a FROM heads WHERE jsonb_typeof(node->'attrs'->'state')='string'
  ), local AS (
    SELECT coalesce(a->>'headingId','') AS id FROM reqs
  ), deps AS (
    SELECT r.i, t.i AS at, t.node FROM reqs r JOIN top t ON t.i=r.i+1 AND t.i<r.fin
    WHERE t.node->>'type'='paragraph' AND t.node->'attrs'->'dependencies'='true'::jsonb
  ), facts AS (
    SELECT r.i, t.node, atlas_projector_text(t.node->'content', true) AS text
    FROM reqs r LEFT JOIN deps d ON d.i=r.i JOIN top t ON t.i=coalesce(d.at,r.i)+1 AND t.i<r.fin AND t.node->>'type'='paragraph'
    WHERE atlas_projector_party(split_part(atlas_projector_text(t.node->'content', true),' · ',1)) IS NOT NULL
      OR atlas_projector_deadline(split_part(atlas_projector_text(t.node->'content', true),' · ',1)) IS NOT NULL
  ), heading_rows AS (
    SELECT ARRAY[r.i] AS path, coalesce(r.a->>'headingId','') AS id,
      btrim(atlas_projector_text(r.node->'content', false),' ') AS title,
      coalesce(r.a->>'direction','internal') AS direction, r.a->>'state' AS state,
      coalesce((SELECT atlas_projector_deadline(s) FROM unnest(string_to_array(f.text,' · ')) WITH ORDINALITY u(s,o)
        WHERE atlas_projector_deadline(s) IS NOT NULL ORDER BY o LIMIT 1),'') AS date,
      (SELECT atlas_projector_party(s) FROM unnest(string_to_array(f.text,' · ')) WITH ORDINALITY u(s,o)
        WHERE atlas_projector_party(s) IS NOT NULL ORDER BY o LIMIT 1) AS party,
      f.node AS facts,
      (SELECT string_agg(ref, ',' ORDER BY o) FROM (
        SELECT DISTINCT ON (ref) ref, o FROM (
          SELECT CASE WHEN (page IS NOT NULL AND lower(e->'attrs'->>'id')=page::text) OR (page IS NULL AND h IN (SELECT id FROM local))
            THEN h ELSE lower(e->'attrs'->>'id')||'#'||h END AS ref, o
          FROM jsonb_array_elements(CASE WHEN jsonb_typeof(d.node->'content')='array' THEN d.node->'content' ELSE '[]'::jsonb END) WITH ORDINALITY x(e,o)
          CROSS JOIN LATERAL (SELECT substring(coalesce(e->'attrs'->>'url','') FROM '[?&]heading=([^&#]+)') AS h) y
          WHERE e->>'type'='pageReference' AND h ~ '^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$'
            AND coalesce(e->'attrs'->>'id','') ~* '^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$') z
        ORDER BY ref, o) w) AS requires
    FROM reqs r LEFT JOIN deps d ON d.i=r.i LEFT JOIN facts f ON f.i=r.i
  ), rows AS (
    SELECT path, coalesce(a->>'id','') AS "BlockId", coalesce(a->>'key','') AS "Key",
      coalesce(btrim((SELECT string_agg(CASE t.c->>'type' WHEN 'text' THEN coalesce(t.c->>'text','') WHEN 'hardBreak' THEN ' ' ELSE '' END,'' ORDER BY t.o)
        FROM jsonb_array_elements(CASE WHEN jsonb_typeof(node->'content'->0->'content')='array' THEN node->'content'->0->'content' ELSE '[]'::jsonb END)
          WITH ORDINALITY t(c,o)),' '),'') AS "Title",
      coalesce(a->>'direction','internal') AS "Direction", coalesce(a->>'state','planned') AS "State", coalesce(a->>'date','') AS "Date",
      coalesce(a->>'party','') AS "Party", coalesce(a->>'where','') AS "Where", coalesce(a->>'requires','') AS "Requires", parent AS "ParentBlockId",
      coalesce(a->>'partyContactId','') AS "PartyContactId"
    FROM (SELECT node, parent, path, node->'attrs' AS a FROM walk WHERE node->>'type'='requirement') b
    UNION ALL
    SELECT path, id, id, title, direction, state, date, coalesce(party,''), '', coalesce(requires,''), NULL,
      CASE WHEN party IS NULL THEN '' ELSE coalesce((SELECT c->'attrs'->'data'->'contacts'->0->>'objectId'
        FROM jsonb_array_elements(CASE WHEN jsonb_typeof(facts->'content')='array' THEN facts->'content' ELSE '[]'::jsonb END) WITH ORDINALITY x(c,o)
        WHERE c->>'type'='contactInline' AND coalesce(c->'attrs'->'data'->'contacts'->0->>'objectId','')<>'' ORDER BY o LIMIT 1),'') END
    FROM heading_rows
  )
  SELECT CAST(row_number() OVER (ORDER BY path) AS integer), "BlockId", "Key", "Title", "Direction", "State", "Date", "Party", "Where", "Requires",
    "ParentBlockId", "PartyContactId"
  FROM rows
$$;

SELECT atlas_rebuild_projector_requirements();
