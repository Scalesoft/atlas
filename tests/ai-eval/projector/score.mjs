// Scoring of one answer for the Projector evaluation (README.md). Pure, tested by score.test.mjs, used by
// experiments/Atlas.AiPrototype/agent-relevance.mjs.

/** Lower case, no diacritics, one space between words: "10. 9. 2026" and "10.9.2026" stay different, "Kostelec" matches "kostelec". */
export const normalize = (value) => value.normalize("NFD").replace(/\p{M}/gu, "").toLowerCase().replace(/\s+/g, " ").trim();

/** A fact is "a|b": any alternative in the normalized answer counts. */
export const mentions = (answer, fact) => fact.split("|").some((alternative) => normalize(answer).includes(normalize(alternative)));

/** An answerable question answered with "the wiki does not say": normalized stems of the usual Czech refusals. */
const REFUSAL = /nevyplyv|neobsahuj|nenachaz|neni uvedeno|nejsou uvedeny|nedocetl|nepodarilo|neuvadi|nenalezl|nemam informace|nelze zjistit|nic o /;
export const refuses = (answer) => REFUSAL.test(normalize(answer));

/**
 * @param item  case: facts (all must be mentioned), forbidden (none may be), expected (page paths), expectAll (every expected page must be
 *              cited, not just one), unanswerable cases have no expected pages and must cite nothing; an answerable case must not
 *              refuse ("nevyplývá", "neobsahuje", ...) unless allowRefusal is set.
 *              Write scenarios (write: true) are scored by the tool calls: expectCalls (tool names, each must be called) and expectArgs
 *              ([{ tool, arg, contains }]: some call of the tool has the argument, or an element of it, whose
 *              text contains `contains` without regard to case and diacritics: a moduleKey is the module heading's anchor, not a fixed key); they may
 *              cite any page, since the page they create has no fixed path.
 * @param run   answer text, cited (paths of cited sources), sources (paths of all sources), toolCalls ([{ name, input }], write scenarios)
 */
export function scoreAnswer(item, { answer, cited, sources, toolCalls = [] }) {
  const expected = item.expected ?? [];
  const missingFacts = (item.facts ?? []).filter((fact) => !mentions(answer, fact));
  const forbiddenHits = (item.forbidden ?? []).filter((fact) => mentions(answer, fact));
  const missingCalls = (item.expectCalls ?? []).filter((name) => !toolCalls.some((call) => call.name === name));
  const missingArgs = (item.expectArgs ?? []).filter(({ tool, arg, contains }) => !toolCalls.some((call) => call.name === tool
    && [call.input?.[arg]].flat().some((value) => typeof value === "string" && normalize(value).includes(normalize(contains)))));
  const quota = item.expectAll ? expected : expected.slice(0, 1);
  const hit = (paths) => expected.length === 0 ? null : item.expectAll ? expected.every((path) => paths.includes(path)) : expected.some((path) => paths.includes(path));
  return {
    answerOk: missingFacts.length === 0 && forbiddenHits.length === 0 && (expected.length > 0 || item.write || cited.length === 0)
      && (expected.length === 0 || item.allowRefusal || !refuses(answer)) && missingCalls.length === 0 && missingArgs.length === 0,
    missingFacts, forbiddenHits, missingCalls, missingArgs,
    inSources: hit(sources), citedOk: hit(cited),
    citedPages: quota.length === 0 ? null : quota.filter((path) => cited.includes(path)).length,
  };
}
