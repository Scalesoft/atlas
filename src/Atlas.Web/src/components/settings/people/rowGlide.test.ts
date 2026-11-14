// @vitest-environment node
import { describe, expect, it, vi } from "vitest";
import { createLeaveWave, createRowGlide } from "./rowGlide";

// A row that only knows where its layout puts it and how far a glide has carried it from there; the layout of a real table
// is the browser's. What the eye sees (and getBoundingClientRect reports) is the layout place plus the glide's offset.
class Row {
  offset = 0;
  constructor(public layout: number) {}
  getBoundingClientRect() { return { top: this.layout + this.offset }; }
  get seen() { return this.layout + this.offset; }
}
const asElements = (rows: Row[]) => rows as unknown as Element[];

/** A glide that starts the row at `from` pixels from its place; `cancel` takes the offset away like a cancelled animation. */
function player() {
  const cancel = vi.fn();
  const play = vi.fn((row: Element, from: number) => {
    const fake = row as unknown as Row;
    fake.offset = from;
    return { cancel: () => { cancel(row); fake.offset = 0; } };
  });
  return { play, cancel };
}
const rowsAt = (...layouts: number[]) => layouts.map((layout) => new Row(layout));

describe("createRowGlide", () => {
  it("reads where the rows are to the eye, a running glide included, and not past the limit", () => {
    const { play } = player();
    const glide = createRowGlide(play);
    const [a, b, c] = rowsAt(100, 158, 216);
    b.offset = 30;
    expect([...glide.tops(asElements([a, b, c])).values()]).toEqual([100, 188, 216]);
    expect(glide.tops(asElements([a, b, c]), 2).size).toBe(2);
  });

  it("glides the rows that stay from where they were to where they are", () => {
    const { play } = player();
    const glide = createRowGlide(play);
    const [first, second, third] = rowsAt(100, 158, 216);
    const before = glide.tops(asElements([first, second, third]));
    // The second row left: the third one now sits where the second one was.
    third.layout = 158;
    expect(glide.glide(asElements([first, third]), before)).toBe(1);
    expect(play).toHaveBeenCalledTimes(1);
    expect(play).toHaveBeenCalledWith(third, 58);
    expect(third.seen).toBe(216);
  });

  it("leaves rows that did not move and rows it never saw alone, and ignores a sub-pixel difference", () => {
    const { play } = player();
    const glide = createRowGlide(play);
    const [stays, almost, arrived] = rowsAt(100, 158, 300);
    const before = glide.tops(asElements([stays, almost]));
    almost.layout = 158.4;
    expect(glide.glide(asElements([stays, almost, arrived]), before)).toBe(0);
    expect(play).not.toHaveBeenCalled();
  });

  it("starts every glide where the eye saw the row, so a second change in a row never moves a row against its way", () => {
    const { play } = player();
    const glide = createRowGlide(play);
    const [a, b, c, d] = rowsAt(0, 58, 116, 174);
    const everySeen = [c.seen, d.seen];

    // First change: b leaves, c and d move up by a row.
    let before = glide.tops(asElements([a, b, c, d]));
    c.layout = 58; d.layout = 116;
    glide.glide(asElements([a, c, d]), before);
    expect([c.seen, d.seen]).toEqual(everySeen);

    // The glide has run a part of its way when the second change comes: a leaves, c and d move up by another row.
    c.offset *= 0.5; d.offset *= 0.5;
    const seenBetween = [c.seen, d.seen];
    before = glide.tops(asElements([a, c, d]));
    c.layout = 0; d.layout = 58;
    glide.glide(asElements([c, d]), before);
    expect([c.seen, d.seen]).toEqual(seenBetween);
    expect(seenBetween[0]).toBeLessThan(everySeen[0]!);
    expect(c.seen).toBeGreaterThan(c.layout);
    expect(d.seen).toBeGreaterThan(d.layout);
  });

  it("replaces a glide that is still running with one that continues from where the row is", () => {
    const { play, cancel } = player();
    const glide = createRowGlide(play);
    const [row] = rowsAt(100);
    glide.glide(asElements([row]), new Map([[row as unknown as Element, 150]]));
    expect(row.offset).toBe(50);
    row.offset = 20; // the glide has carried it most of the way
    const before = glide.tops(asElements([row]));
    expect(glide.glide(asElements([row]), before)).toBe(1);
    expect(cancel).toHaveBeenCalledTimes(1);
    expect(row.seen).toBe(120);
    expect(row.offset).toBe(20);
  });

  it("glides the rows that stay down when rows are added above them, and not the new ones", () => {
    const { play } = player();
    const glide = createRowGlide(play);
    const [a, b] = rowsAt(0, 58);
    const before = glide.tops(asElements([a, b]));
    const fresh = new Row(0);
    a.layout = 58; b.layout = 116;
    expect(glide.glide(asElements([fresh, a, b]), before)).toBe(2);
    expect(play).toHaveBeenCalledWith(a, -58);
    expect(play).toHaveBeenCalledWith(b, -58);
    expect([a.seen, b.seen]).toEqual([0, 58]);
    expect(fresh.offset).toBe(0);
  });

  it("takes the places of a wave once, removes its rows, and glides the rest in the frame of the last one", () => {
    const { play } = player();
    const glide = createRowGlide(play);
    const resets: (() => void)[] = [];
    const wave = createLeaveWave(glide, (reset) => resets.push(reset));
    const [a, b, c, d, e] = rowsAt(0, 58, 116, 174, 232);
    const body = { children: [a, b, c, d, e] as unknown as Element[] } as unknown as Element & { children: Element[] };
    const take = (...gone: Row[]) => () => {
      (body as unknown as { children: Element[] }).children = body.children.filter((row) => !gone.includes(row as unknown as Row));
      body.children.forEach((row, index) => ((row as unknown as Row).layout = index * 58));
    };

    // b and c start to leave in the same tick and end in the same frame: one wave.
    const [finishB, finishC] = [wave(body), wave(body)];
    resets.shift()!();
    finishB(take(b));
    expect(play).not.toHaveBeenCalled();
    finishC(take(c));
    // The glide starts in the frame of the last row, with nothing painted in between: the rows are where the eye saw them.
    expect(play).toHaveBeenCalledTimes(2);
    expect([d.seen, e.seen]).toEqual([174, 232]);

    // d and e are part of the way when the next change takes a row out, a wave later: it starts from where they are, not from the first wave's places.
    d.offset *= 0.4; e.offset *= 0.4;
    const between = [d.seen, e.seen];
    const finishA = wave(body);
    resets.shift()!();
    finishA(take(a));
    expect([d.seen, e.seen]).toEqual(between);
    expect(between[0]).toBeLessThan(174);
    expect(d.seen).toBeGreaterThan(d.layout);
  });

  it("keeps two waves that overlap in time apart: the second one's rows are still leaving when the first one is taken out", () => {
    const { play } = player();
    const glide = createRowGlide(play);
    const resets: (() => void)[] = [];
    const wave = createLeaveWave(glide, (reset) => resets.push(reset));
    const [a, b, c, d] = rowsAt(0, 58, 116, 174);
    const body = { children: [a, b, c, d] as unknown as Element[] } as unknown as Element & { children: Element[] };
    const take = (gone: Row) => () => {
      (body as unknown as { children: Element[] }).children = body.children.filter((row) => row !== (gone as unknown as Element));
      body.children.forEach((row, index) => ((row as unknown as Row).layout = index * 58));
    };
    // Change 1 sends a away, change 2 (70 ms later) sends b away; a's fade ends first, b's still runs.
    const finishA = wave(body);
    resets.shift()!();
    const finishB = wave(body);
    resets.shift()!();
    finishA(take(a));
    // a is gone, b is still there (leaving): c and d moved up by one row and glide, b too.
    expect(play).toHaveBeenCalledTimes(3);
    expect([b.seen, c.seen, d.seen]).toEqual([58, 116, 174]);
    const seenAtSecondWave = [c.seen, d.seen].map((seen) => seen * 0.8);
    c.offset = seenAtSecondWave[0]! - c.layout; d.offset = seenAtSecondWave[1]! - d.layout;
    finishB(take(b));
    // The second wave reads the places again, with the first glide running: nothing moves against its way.
    expect([c.seen, d.seen]).toEqual(seenAtSecondWave);
    expect(c.seen).toBeGreaterThan(c.layout);
  });

  it("does nothing for a body that is no longer there", () => {
    const { play } = player();
    const wave = createLeaveWave(createRowGlide(play), () => undefined);
    const remove = vi.fn();
    wave(null)(remove);
    expect(remove).toHaveBeenCalledTimes(1);
    expect(play).not.toHaveBeenCalled();
  });

  it("handles a change that adds and removes at once from the places the rows were seen at", () => {
    const { play } = player();
    const glide = createRowGlide(play);
    const [a, b, c] = rowsAt(0, 58, 116);
    const before = glide.tops(asElements([a, b, c]));
    // b leaves and two rows come in above a: c is not far from where it was, a moved a lot.
    a.layout = 116; c.layout = 174;
    glide.glide(asElements([new Row(0), new Row(58), a, b, c]), before);
    expect([a.seen, b.seen, c.seen]).toEqual([0, 58, 116]);
  });
});
