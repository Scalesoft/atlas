namespace Atlas.Server.Tests.Assistant;

/// <summary>
/// Raw outputs of the production answer prompt (answer@3) recorded on 2026-10-27 from the company Gateway (qwen3.8-flash-next-iq3_s,
/// reasoning none, <c>json_schema</c> in <c>response_format</c>) over the Projector eval chunks (<c>tests/ai-eval/projector</c>):
/// Gateway does not enforce the schema, and 2 of 26 answers came back with plain-string sentences.
/// </summary>
internal static class RecordedAnswers
{
    /// <summary>"Co zbývá u strukturovaných požadavků v KBN?" with 4 sources: the schema ignored.</summary>
    public const string StringSentences = """
        {"answerable":true,"sentences":["U strukturovaných požadavků v KBN je hotové formuláře příjem a propuštění, zatímco zbývá dokončit konzilium a předávací zprávu.","V rámci tohoto úkolu je stále rozpracovaná i položka Šablony formulářů od primářů.","Provoz strukturovaných formulářů závisí na zajištění SSL certifikátu.","Informace o strukturovaných požadavcích jsou uvedeny v sekci Aplikace, kde je také specifikován klíč forms."]}
        """;

    /// <summary>The same question answered in the requested shape (the corrective retry of <see cref="StringSentences"/>).</summary>
    public const string ObjectSentences = """
        {"answerable":true,"sentences":[{"text":"U strukturovaných požadavků v KBN je hotová formulář příjmu, zatímco formuláře konzilia a předávací zprávy zbývá dokončit.","sources":[4]},{"text":"Dále je rozpracována činnost spojená se šablonami formulářů od primářů, které má dodat MUDr. Králová.","sources":[4]}]}
        """;
}
