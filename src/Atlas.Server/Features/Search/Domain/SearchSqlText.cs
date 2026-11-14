namespace Atlas.Server.Features.Search.Domain;

/// <summary>
/// Search's text processing as SQL expressions of built-in functions only (Atlas 3.6, no logic in the database): what the functions
/// atlas_normalize_search_text, atlas_strip_secret_directives, atlas_search_stem, atlas_search_identifiers, atlas_search_lexemes,
/// atlas_search_component_text, atlas_search_body, atlas_search_headings and atlas_comment_search_text computed, with the same text
/// configuration ('simple'), regular expressions and weights, so every stored vector and every ranking stays the same
/// (SearchParityTests, SearchWithoutDatabaseLogicMigrationTests). The normalization stays in SQL: unaccent and lower follow the database.
/// Each method takes SQL expressions and returns one; an argument used more than once should be a column or a LATERAL alias. A value
/// used more than once is computed once (OFFSET 0, MATERIALIZED): the planner would otherwise inline it at every use. The statements'
/// row estimates are of functions over whole documents, so their callers run them without JIT (it compiled them for ~300 ms each).
/// </summary>
public static class SearchSqlText
{
    /// <summary>Lower case, no diacritics, C#/C++ as words, any other run of non-alphanumerics one space (atlas_normalize_search_text).</summary>
    public static string Normalize(string value) => $$"""
        trim(regexp_replace(regexp_replace(regexp_replace(regexp_replace(regexp_replace(lower(public.unaccent({{value}})),
          '\mc\+\+(?!\+)','cplusplus','g'),'\mc#(?!#)','csharp','g'),'\mcpp\M','cplusplus','g'),'\mc[[:space:]]+sharp\M','csharp','g'),
          '[^[:alnum:]]+',' ','g'))
        """;

    /// <summary>Password references (editor directives) out of a text: each one a space (atlas_strip_secret_directives).</summary>
    public static string StripSecrets(string value) => $$"""regexp_replace({{value}},'::?secret\{[^{}' || chr(10) || ']*\}',' ','g')""";

    /// <summary>The light Czech stem of a normalized word (atlas_search_stem; the twin of <see cref="SearchText.Stem"/>).</summary>
    public static string Stem(string word) => $$"""COALESCE(substring({{word}} from '^(.{4,}?)(?:ami|ach|ich|ymi|ech|um|ou|em|im|am|a|u|y|e|i|o)$'),{{word}})""";

    /// <summary>
    /// The technical identifiers of a raw text (PageRevisionStore, my_func.name) as words: each identifier's parts and its suffixes
    /// joined, the last eight parts (atlas_search_identifiers). A token repeated in the text is split once: the result is a set.
    /// </summary>
    public static string Identifiers(string raw) => $$"""
        (SELECT string_agg(array_to_string(parts,' ') || ' ' ||
          (SELECT string_agg(array_to_string(ARRAY(SELECT part FROM unnest(parts) WITH ORDINALITY AS identifier_part(part,ord)
            WHERE ord>=suffix ORDER BY ord),''),' ' ORDER BY suffix) FROM generate_series(1,cardinality(parts)-1) AS identifier_suffix(suffix)),' ' ORDER BY parts)
        FROM (
          SELECT DISTINCT ARRAY(SELECT part FROM unnest(parts) WITH ORDINALITY AS identifier_part(part,ord)
            WHERE ord>=greatest(1,cardinality(parts)-7) ORDER BY ord) AS parts FROM (
            SELECT string_to_array({{Normalize("regexp_replace(regexp_replace(token,'([A-Z]+)([A-Z][a-z])','\\1 \\2','g'),'([a-z0-9])([A-Z])','\\1 \\2','g')")}},' ') AS parts
            FROM (SELECT DISTINCT btrim(token,'_.-') AS token FROM regexp_split_to_table(public.unaccent({{raw}}),'[^[:alnum:]_.-]+') AS token) identifier_token
            WHERE token ~ '^[[:alpha:]]' AND token ~ '[[:alnum:]][_.-]+[[:alnum:]]|[a-z0-9][A-Z]|[A-Z][A-Z][a-z]') identifier_split
          WHERE cardinality(parts)>1) identifier_parts)
        """;

    /// <summary>
    /// The lexemes of a text (atlas_search_lexemes): its normalized words at their positions, each word's stem beside it at the same
    /// positions, then its <see cref="Identifiers"/>. Null when either argument is.
    /// </summary>
    public static string Lexemes(string raw, string normalized) => $$"""
        (SELECT CASE WHEN {{raw}} IS NULL OR lexeme_exact.vector IS NULL THEN NULL ELSE COALESCE((
            SELECT CAST(string_agg(quote_literal(word) || ':' || positions,' ') AS tsvector) FROM (
              SELECT word,string_agg(CAST(position AS text),',' ORDER BY position) AS positions FROM (
                SELECT lexeme_entry.lexeme AS word,lexeme_entry.positions FROM unnest(lexeme_exact.vector) lexeme_entry
                UNION ALL
                SELECT {{Stem("lexeme_entry.lexeme")}},lexeme_entry.positions FROM unnest(lexeme_exact.vector) lexeme_entry
                WHERE {{Stem("lexeme_entry.lexeme")}}<>lexeme_entry.lexeme
              ) lexeme_words CROSS JOIN LATERAL unnest(lexeme_words.positions) AS position GROUP BY word) lexeme_grouped),CAST('' AS tsvector))
          || to_tsvector('simple',COALESCE({{Identifiers(raw)}},'')) END
        FROM (SELECT to_tsvector('simple',{{normalized}}) AS vector OFFSET 0) lexeme_exact)
        """;

    /// <summary>The source texts of the OpenAPI components of a stored document, at most 300,000 characters (atlas_search_component_text).</summary>
    public static string ComponentText(string content) => $$"""
        (CASE WHEN strpos({{content}},'"openapi"')=0 THEN '' ELSE COALESCE((
          SELECT left(string_agg(source #>> '{}',E'\n\n'),300000)
          FROM jsonb_path_query(CAST({{content}} AS jsonb),'strict $.** ? (@.type == "openapi").attrs.data.source','{}',true) AS source
          WHERE jsonb_typeof(source)='string'),'') END)
        """;

    /// <summary>The searchable body of a revision: its Markdown without password references, then its components' text (atlas_search_body).</summary>
    public static string Body(string markdown, string content) => $$"""
        (SELECT CASE WHEN c.text='' THEN {{StripSecrets(markdown)}} ELSE {{StripSecrets(markdown)}} || E'\n\n' || c.text END
        FROM (SELECT {{ComponentText(content)}} AS text OFFSET 0) c)
        """;

    /// <summary>
    /// The headings of a revision as a JSON array of {title, normalizedTitle, headingId} in Markdown order (atlas_search_headings): the
    /// ATX headings outside code fences, their links as text and emphasis markers dropped; the k-th heading of a normalized title takes
    /// the anchor of the k-th heading node of that title in the rich document (a valid headingId, else null).
    /// </summary>
    public static string Headings(string markdown, string rich) => $$"""
        (WITH RECURSIVE line AS (
          SELECT l.ord,l.text,substring(l.text from '^ {0,3}(`{3,}|~{3,})') AS marker
          FROM unnest(string_to_array({{markdown}},E'\n')) WITH ORDINALITY AS l(text,ord)
          WHERE l.text ~ '^ {0,3}(#|`{3}|~{3})'
        ), mark AS MATERIALIZED (
          SELECT ord,text,marker,row_number() OVER (ORDER BY ord) AS n FROM line WHERE marker IS NOT NULL
        ), fence(n,ord,open) AS (
          SELECT CAST(0 AS bigint),CAST(0 AS bigint),CAST(NULL AS text)
          UNION ALL
          SELECT m.n,m.ord,CASE WHEN f.open IS NULL THEN m.marker
            WHEN left(m.marker,1)=left(f.open,1) AND length(m.marker)>=length(f.open) AND m.text ~ '^ {0,3}(`{3,}|~{3,})[[:space:]]*$' THEN NULL
            ELSE f.open END
          FROM fence f JOIN mark m ON m.n=f.n+1
        ), heading AS MATERIALIZED (
          SELECT l.ord,t.title,{{Normalize("t.title")}} AS normalized
          FROM line l
          CROSS JOIN LATERAL (SELECT substring(l.text from '^ {0,3}#{1,6}[[:blank:]]+(.+)$') AS raw) h
          CROSS JOIN LATERAL (SELECT regexp_replace(regexp_replace(trim(regexp_replace(h.raw,'[[:blank:]]+#+[[:blank:]]*$','')),
            '!?\[([^]]+)\]\([^)]*\)','\1','g'),'[*_`]+','','g') AS title) t
          WHERE l.marker IS NULL AND h.raw IS NOT NULL
            AND (SELECT f.open FROM fence f WHERE f.ord<l.ord ORDER BY f.ord DESC LIMIT 1) IS NULL
        ), anchor AS MATERIALIZED (
          SELECT n.normalized,row_number() OVER (PARTITION BY n.normalized ORDER BY node.ord) AS k,
            CASE WHEN node.value->'attrs'->>'headingId' ~ '^[A-Za-z0-9][A-Za-z0-9_-]{0,127}$' THEN node.value->'attrs'->>'headingId' END AS id
          FROM jsonb_path_query(CAST({{rich}} AS jsonb)->'document','strict $.** ? (@.type == "heading")','{}',true) WITH ORDINALITY AS node(value,ord)
          CROSS JOIN LATERAL (SELECT {{Normalize("COALESCE((SELECT string_agg(value #>> '{}','') FROM jsonb_path_query(node.value,'strict $.** ? (exists(@.text)).text','{}',true) AS value),'')")}} AS normalized) n
        ), numbered AS MATERIALIZED (
          SELECT h.*,row_number() OVER (PARTITION BY h.normalized ORDER BY h.ord) AS k FROM heading h
        )
        SELECT COALESCE(jsonb_agg(jsonb_build_object('title',h.title,'normalizedTitle',h.normalized,'headingId',a.id) ORDER BY h.ord),CAST('[]' AS jsonb))
        FROM numbered h LEFT JOIN anchor a ON a.normalized=h.normalized AND a.k=h.k)
        """;

    /// <summary>The headings' lexemes in their order, each after a gap, as one vector (the HeadingVector of the search documents).</summary>
    public static string HeadingVector(string headings) => $$"""
        (WITH RECURSIVE heading AS MATERIALIZED (
          SELECT e.ord,{{Lexemes("e.value->>'title'", "e.value->>'normalizedTitle'")}} AS lexemes
          FROM jsonb_array_elements({{headings}}) WITH ORDINALITY AS e(value,ord)
        ), joined(ord,vector) AS (
          SELECT CAST(0 AS bigint),CAST('' AS tsvector)
          UNION ALL
          SELECT h.ord,j.vector || {{FieldBoundary}} || h.lexemes FROM joined j JOIN heading h ON h.ord=j.ord+1
        )
        SELECT ts_delete(vector,'__scaleatlas_field_boundary__') FROM joined ORDER BY ord DESC LIMIT 1)
        """;

    /// <summary>
    /// The search vector of a document: the title's lexemes (A), the headings' (B) and the body's (D), apart, so a phrase never spans
    /// two fields.
    /// </summary>
    public static string Vector(string title, string normalizedTitle, string headingVector, string body, string normalizedBody) => $$"""
        ts_delete(setweight({{Lexemes(title, normalizedTitle)}},'A') || {{FieldBoundary}} || setweight({{headingVector}},'B') || {{FieldBoundary}}
          || setweight({{Lexemes(body, normalizedBody)}},'D'),'__scaleatlas_field_boundary__')
        """;

    /// <summary>A comment's text without its links' targets (atlas_comment_search_text).</summary>
    public static string CommentText(string body) => $$"""regexp_replace({{body}},'\]\([^)]*\)',']','g')""";

    /// <summary>A position gap that no query can match, deleted once the parts are joined.</summary>
    private const string FieldBoundary = "CAST('''__scaleatlas_field_boundary__'':1' AS tsvector)";
}
