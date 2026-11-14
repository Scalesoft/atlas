namespace Atlas.Server.Features.Search;

/// <summary>
/// The transaction-local settings of the searching reads, as one <c>SELECT set_config(...)</c> each caller composes: a statement
/// timeout, plans specific to the query's scope, no JIT (it compiled ~870 ms in a ~1 s query when missing ACL statistics inflated
/// plan costs), pg_trgm thresholds and the pgvector iterative scan, which keeps scanning HNSW until enough chunks pass the access
/// filters so that hidden pages never starve the visible ones.
/// </summary>
public static class PlannerSettings
{
    /// <summary>The search read and the answer's sources.</summary>
    public const string SearchTimeout = "2000ms";
    /// <summary>What runs while a title or a text is typed and must never hold up writing.</summary>
    public const string TypingTimeout = "1000ms";
    /// <summary>The did-you-mean threshold and the one search ranks with.</summary>
    public const string SearchSimilarity = "0.45";
    /// <summary>The trigram threshold of the title pre-selection.</summary>
    public const string TypingSimilarity = "0.3";

    /// <summary>The timeout, custom plans and JIT off.</summary>
    public static string Base(string timeout) => Setting("statement_timeout", timeout) + ", " + Setting("plan_cache_mode", "force_custom_plan")
        + ", " + Setting("jit", "off");

    public static string WordSimilarity(string threshold) => Setting("pg_trgm.word_similarity_threshold", threshold);

    public static string Similarity(string threshold) => Setting("pg_trgm.similarity_threshold", threshold);

    public static readonly string Vectors = Setting("hnsw.iterative_scan", "relaxed_order") + ", " + Setting("hnsw.ef_search", "100")
        + ", " + Setting("hnsw.max_scan_tuples", "20000");

    public static string Select(params string[] settings) => "SELECT " + string.Join(", ", settings);

    private static string Setting(string name, string value) => $"set_config('{name}','{value}',true)";
}
