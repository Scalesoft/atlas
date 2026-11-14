// @vitest-environment node
import { describe, expect, it } from "vitest";
import { linkPhraseLimits, linkPhrases } from "./linkPhrases";

// Plan §19 E2: the phrases of a finished paragraph that may name a wiki page. Pure TypeScript, no editor.
const phrases = (text: string, options?: Parameters<typeof linkPhrases>[1]) => linkPhrases(text, options).map(found => found.phrase);

describe("linkPhrases", () => {
  it("builds one to five word phrases with czech word boundaries and skips stop words", () => {
    const text = "Před vzdálenou správou si nastavte VPN podle návodu.";
    const found = linkPhrases(text);
    // Every phrase is the exact text of its span, so the server's echo maps back to the words.
    for (const { phrase, span } of found) expect(text.slice(span.start, span.end)).toBe(phrase);
    const all = found.map(item => item.phrase);
    // An acronym in capitals and significant single words; never a short or a general word („návodu“, „podle“, „si“).
    expect(all).toEqual(expect.arrayContaining(["VPN", "vzdálenou", "správou", "nastavte", "vzdálenou správou", "nastavte VPN"]));
    expect(all).not.toEqual(expect.arrayContaining(["návodu"]));
    expect(all).not.toContain("si");
    expect(all).not.toContain("podle");
    // A phrase neither starts nor ends with a function word, and never runs over the full stop.
    expect(all.some(phrase => /^(?:si|podle)\s|\s(?:si|podle)$/u.test(phrase))).toBe(false);
    expect(all.every(phrase => !phrase.includes("."))).toBe(true);
    // One to five words, single words first.
    expect(Math.max(...found.map(item => item.words))).toBeLessThanOrEqual(5);
    expect(found.map(item => item.words)).toEqual([...found.map(item => item.words)].sort((a, b) => a - b));

    // Diacritics and inner function words: „Připojení k VPN“ is one phrase; „Zálohování“ is one word, not „Z“ and „lohov“.
    expect(phrases("Viz Připojení k VPN, Zálohování.")).toEqual(expect.arrayContaining(["Připojení k VPN", "Zálohování", "Připojení"]));
    expect(phrases("Viz Připojení k VPN, Zálohování.")).not.toContain("k VPN");
    // A lower-case three-letter word is no acronym; numbers alone are no page.
    expect(phrases("vpn a 2026")).toEqual([]);
    // Inline components (U+FFFC) and line breaks end a phrase like punctuation does.
    expect(phrases("Servisní ￼ smlouva")).not.toContain("Servisní ￼ smlouva");
    expect(phrases("Servisní\nsmlouva")).not.toContain("Servisní\nsmlouva");
  });

  it("never sends a paragraph with a detected password", () => {
    expect(phrases("Nouzový účet na serveru Atlas: heslo Srv#2026!rack")).toEqual([]);
    expect(phrases("Klíč ghp_abcdefghijklmnopqrstuvwxyz0123456789 pro nasazení Atlasu")).toEqual([]);
  });

  it("stays within the server's limits and leaves out spans that already link", () => {
    const long = Array.from({ length: 60 }, (_, index) => `Slovo${String.fromCharCode(97 + index % 26)}${index}`).join(" ");
    const found = linkPhrases(long);
    expect(found.length).toBeLessThanOrEqual(linkPhraseLimits.maxPhrases);
    expect(found.every(item => item.phrase.length <= linkPhraseLimits.maxLength)).toBe(true);
    // A phrase of the same text is sent once.
    expect(phrases("VPN tady a VPN tam")).toEqual(["VPN"]);
    // Text already inside a link (or code) is never offered again.
    const text = "Nastavte VPN podle Připojení k síti";
    const linked = { start: text.indexOf("VPN"), end: text.indexOf("VPN") + 3 };
    expect(phrases(text, { skip: [linked] })).not.toContain("VPN");
    expect(phrases(text, { skip: [linked] })).not.toContain("Nastavte VPN");
    expect(phrases(text, { skip: [linked] })).toContain("Připojení k síti");
  });
});
