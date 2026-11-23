// @vitest-environment node
import { describe, expect, it } from "vitest";
import { compareReleases, firstPoint, groupReleases, parseReleaseNote, points, releaseNotes, releaseNotesFrom, unseenReleases } from "./releaseNotes";

describe("release notes", () => {
  it("lists the releases newest first, by number", () => {
    const notes = releaseNotesFrom({ "../release-notes/3.9.0.md": "a", "../release-notes/3.10.0.md": "b", "../release-notes/3.9.1.md": "c", "../release-notes/README.md": "x" });
    expect(notes.map(note => note.version)).toEqual(["3.10.0", "3.9.1", "3.9.0"]);
    expect(compareReleases("3.4.3", "3.4.3")).toBe(0);
  });

  it("bundles a note for every release since 3.0, each opened by its date", () => {
    const versions = releaseNotes.map(note => note.version);
    for (const release of ["3.0.0", "3.1.0", "3.2.0", "3.3.0", "3.4.0", "3.4.3"]) expect(versions).toContain(release);
    expect(versions).toEqual([...versions].sort(compareReleases));
    for (const note of releaseNotes) expect(note.text, note.version).toMatch(/^\*(\d{1,2}\. \d{1,2}\. \d{4}|Připravuje se)/);
  });

  it("reads the codename, the mark and the highlights, and keeps them out of the changelog text", () => {
    const raw = "codename: Performant Pear\r\nmark: pear\r\n\r\n*2. 11. 2026*\r\n\r\n### Hlavně\r\nSvižné stránky.\r\n"
      + "- **Přepnutí do úprav** `126 ms`: dřív 441 ms.\r\n- **Loga a fotky:** v čipu.\r\n\r\n### Nové\r\n- **Loga a fotky:** v čipu.\r\n";
    const note = parseReleaseNote("3.5.0", raw);
    expect(note).toMatchObject({ codename: "Performant Pear", mark: "pear", date: "2. 11. 2026", lead: "Svižné stránky." });
    expect(note.highlights).toEqual([{ title: "Přepnutí do úprav", key: "126 ms", text: "dřív 441 ms." }, { title: "Loga a fotky", text: "v čipu." }]);
    expect(note.text).toMatch(/^\*2\. 11\. 2026\*\s+### Nové/);
    // A release without them looks as before; a mark that is not bundled is dropped.
    const hotfix = parseReleaseNote("3.4.3", "*2. 11. 2026*\n\n### Opravy\n- Panel. Vpravo.\n");
    expect([hotfix.codename, hotfix.mark, hotfix.highlights]).toEqual([undefined, undefined, []]);
    expect(parseReleaseNote("3.6.0", "mark: none\n*1. 1. 2027*\n").mark).toBeUndefined();
    expect(parseReleaseNote("3.6.0", "codename: Quiet Quince\nmark: quince\ncover: secret\n\n*1. 1. 2027*\n", { quince: 1 })).toMatchObject({ mark: "quince", cover: "secret", date: "1. 1. 2027" });
    // `demos:` gives the highlights in turn their live demo; `-` and a demo that is not bundled give none.
    const demos = parseReleaseNote("5.1.0", "codename: Tandem Tangerine\nmark: none\ndemos: qr - nope\n\n### Hlavně\nVěta.\n- **A:** a.\n- **B:** b.\n- **C:** c.\n", {}, { qr: 1 });
    expect(demos.highlights.map(item => item.demo)).toEqual(["qr", undefined, undefined]);
    expect(firstPoint(parseReleaseNote("3.4.3", "*2. 11. 2026*\n\n### Opravy\n- **Panel** stojí vpravo. Vždy.\n"))).toBe("Panel stojí vpravo");
  });

  it("the 3.5 notes carry Performant Pear with its bundled mark", () => {
    const pear = releaseNotes.find(note => note.version === "3.5.0")!;
    expect(pear).toMatchObject({ codename: "Performant Pear", mark: "pear" });
    expect(pear.highlights).toHaveLength(6);
  });

  it("an account sees the releases after its last one, the newest named one as the motif", () => {
    const notes = releaseNotesFrom({ "3.5.0.md": "codename: Performant Pear\nmark: pear\n", "3.4.3.md": "", "3.4.2.md": "", "3.4.0.md": "" });
    const fromOld = unseenReleases(notes, "3.4.0", "3.5.0");
    expect(fromOld.unseen.map(note => note.version)).toEqual(["3.5.0", "3.4.3", "3.4.2"]);
    expect(fromOld.featured?.version).toBe("3.5.0");
    expect(fromOld.others.map(note => note.version)).toEqual(["3.4.3", "3.4.2"]);
    // Nothing unseen without a last release, nor once it is the current one; a hotfix alone has no motif.
    expect(unseenReleases(notes, undefined, "3.5.0").unseen).toEqual([]);
    expect(unseenReleases(notes, "3.5.0", "3.5.0").unseen).toEqual([]);
    expect(unseenReleases(notes, "3.4.2", "3.4.3").featured).toBeUndefined();
  });

  it("groups the releases by named version, its patches beneath it newest first", () => {
    const notes = releaseNotesFrom({ "3.6.0.md": "", "3.5.2.md": "", "3.5.1.md": "", "3.5.0.md": "codename: Performant Pear\n", "3.4.3.md": "", "3.4.2.md": "" });
    expect(groupReleases(notes).map(({ release, patches }) => [release.version, patches.map(note => note.version)]))
      .toEqual([["3.6.0", []], ["3.5.0", ["3.5.2", "3.5.1"]], ["3.4.2", ["3.4.3"]]]);
    // The real notes: every named version heads its own group, no patch stands as one.
    for (const { release } of groupReleases(releaseNotes)) expect(release.version).toMatch(/\.0$/);
  });

  it("an update by patches only is a small one of the named release the account knows", () => {
    const notes = releaseNotesFrom({ "3.6.0.md": "codename: Quiet Quince\nmark: quince\n", "3.5.2.md": "", "3.5.1.md": "", "3.5.0.md": "codename: Performant Pear\nmark: pear\n" });
    const patchOnly = unseenReleases(notes, "3.5.0", "3.5.2");
    expect(patchOnly.featured).toBeUndefined();
    expect(patchOnly.patched?.codename).toBe("Performant Pear");
    expect(patchOnly.unseen.map(note => note.version)).toEqual(["3.5.2", "3.5.1"]);
    // A new named release is no patch update, nor are patches of a version the account has not seen.
    expect(unseenReleases(notes, "3.5.2", "3.6.0").patched).toBeUndefined();
    expect(unseenReleases(releaseNotesFrom({ "3.5.2.md": "", "3.5.0.md": "" }), "3.4.3", "3.5.2").patched).toBeUndefined();
    expect(points(parseReleaseNote("3.5.2", "*3. 11. 2026*\n\n### Opravy\n- **Tabule:** drží. Vždy.\n- Kontakty `ok`.\n"))).toEqual(["Tabule: drží", "Kontakty ok"]);
  });
});
