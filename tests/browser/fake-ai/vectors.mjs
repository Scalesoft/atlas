// The deterministic fake embedding of the Atlas test suites: hashed character trigrams of the normalized words of a text.
// The SAME algorithm as FakeAi in tests/Atlas.Server.Tests/Ai/Fakes/FakeAi.cs (Counts, Vector, Near, Words, Fnv). Both are
// held to tests/fixtures/fake-ai-vectors.json (C#: Ai/FakeAiVectorParityTests, JS: server.test.mjs), so a change to either
// side fails a test instead of silently making the browser scenarios and the server suites disagree.
//
// Everything works on UTF-16 code units like the C# char loops: no code point iteration, no locale, no context-sensitive
// case mapping (per-unit lower casing keeps a final sigma and a dotted capital I as .NET's ToLowerInvariant does).

export const DIMENSIONS = 1024;

const LETTER_OR_DIGIT = /^[\p{L}\p{Nd}]$/u;
const NON_SPACING_MARK = /^\p{Mn}$/u;

function lowerInvariant(text) {
  let lowered = '';
  for (let i = 0; i < text.length; i++) {
    const unit = text[i];
    const mapped = unit.toLowerCase();
    lowered += mapped.length === 1 ? mapped : unit;
  }
  return lowered;
}

/** Lower case, decomposed, marks dropped, everything that is not a letter or digit a separator; split on single spaces. */
export function words(text) {
  const decomposed = lowerInvariant(text).normalize('NFD');
  let plain = '';
  for (let i = 0; i < decomposed.length; i++) {
    const unit = decomposed[i];
    if (NON_SPACING_MARK.test(unit)) continue;
    plain += LETTER_OR_DIGIT.test(unit) ? unit : ' ';
  }
  return plain.split(' ').filter(word => word.length > 0);
}

/** FNV-1a over UTF-16 code units, 32 bit unsigned: stable across processes and languages. */
export function fnv(text) {
  let hash = 2166136261;
  for (let i = 0; i < text.length; i++) {
    hash = Math.imul(hash ^ text.charCodeAt(i), 16777619) >>> 0;
  }
  return hash;
}

/** Raw trigram counts (integers), 1 024 of them: the exact numbers the fixture stores. */
export function counts(text) {
  const vector = new Float64Array(DIMENSIONS);
  for (const word of words(text)) {
    const padded = ` ${word} `;
    for (let i = 0; i + 3 <= padded.length; i++) {
      vector[fnv(padded.slice(i, i + 3)) % DIMENSIONS] += 1;
    }
  }
  return vector;
}

/** The unit vector a real model server would return (all zeros stay zeros). */
export function vector(text) {
  const raw = counts(text);
  let sum = 0;
  for (const value of raw) sum += value * value;
  const norm = Math.sqrt(sum);
  return norm === 0 ? raw : raw.map(value => value / norm);
}

/** A different text whose vector is close to the text's (cosine well above 0.8): the text plus its longest word. */
export function near(text) {
  const list = words(text);
  if (list.length === 0) return text;
  let longest = list[0];
  for (const word of list) if (word.length > longest.length) longest = word;
  return `${text} ${longest}`;
}
