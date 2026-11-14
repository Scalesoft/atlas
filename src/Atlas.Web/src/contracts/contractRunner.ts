import { describe, expect, it } from "vitest";
import { interactionChecks, type InteractionCheck, type InteractionDeviation } from "./interaction";

/*
 * The one runner of the interaction contract suites (P16): a surface gives its descriptors (one row per interactive element) and a
 * probe per check; every (element, check) becomes a test. A probe answers `true` when the element follows the rule, or a string
 * that says what it did instead. A deviation listed for the pair turns the expectation around: the probe must still fail, and
 * once it passes the test asks for the row to be deleted, so the list shrinks as the alignment areas land.
 */

/** What every descriptor carries, whatever its surface. */
export interface InteractionDescriptor {
  readonly id: string;
  readonly label: string;
  /** P5: `live` saves as it goes (Esc closes and keeps), `draft` is a concept Esc throws away, `apply` a panel with „Použít“. */
  readonly saves: "live" | "draft" | "apply" | "none";
  /** P17: how it works at 375 px with touch, for the browser review cycles (jsdom has no layout or touch). */
  readonly touch: string;
  /** The checks that apply to it, by the rules (a list without a search has no P8). */
  readonly checks: readonly InteractionCheck[];
  /** Checks that apply but cannot be measured here (jsdom has no layout, a runtime needs a browser), with the reason: `it.todo`. */
  readonly unchecked?: Partial<Record<InteractionCheck, string>>;
}

export type ProbeResult = true | string;
export type Probe<D> = (descriptor: D) => Promise<ProbeResult>;

/** The deviation table is a set of TODOs: each names a descriptor and a check it runs, and none twice. */
export function checkDeviationTable(deviations: readonly InteractionDeviation[], descriptors: readonly InteractionDescriptor[]) {
  it("lists only deviations of elements and checks the suite runs, each once, each with a reason", () => {
    const byId = new Map(descriptors.map(descriptor => [descriptor.id, descriptor]));
    const problems = deviations.flatMap(row => {
      const descriptor = byId.get(row.element);
      if (!descriptor) return [`${row.element}: no descriptor`];
      if (!(row.check in interactionChecks)) return [`${row.element} ${row.check}: unknown check`];
      if (!descriptor.checks.includes(row.check as InteractionCheck)) return [`${row.element} ${row.check}: the descriptor does not run it`];
      return row.reason.trim() ? [] : [`${row.element} ${row.check}: no reason`];
    });
    const keys = deviations.map(row => `${row.element} ${row.check}`);
    problems.push(...keys.filter((key, index) => keys.indexOf(key) !== index).map(key => `${key}: listed twice`));
    expect(problems).toEqual([]);
  });
  it("gives every descriptor a unique id, a touch note for the browser review (P17) and its checks", () => {
    const ids = descriptors.map(descriptor => descriptor.id);
    expect(ids.filter((id, index) => ids.indexOf(id) !== index)).toEqual([]);
    expect(descriptors.filter(descriptor => !descriptor.touch.trim() || !descriptor.checks.length).map(descriptor => descriptor.id)).toEqual([]);
    // D5: a touch note never approves a target under 44 px.
    expect(descriptors.filter(descriptor => [...descriptor.touch.matchAll(/(\d+) ?px/g)].some(([, size]) => Number(size) < 44)).map(descriptor => descriptor.id)).toEqual([]);
  });
}

/** One test per (descriptor, check) with a probe; a deviation must still fail, a passing deviation asks to be deleted. */
export function runInteractionContract<D extends InteractionDescriptor>(options: {
  readonly surface: string;
  readonly descriptors: readonly D[];
  readonly deviations: readonly InteractionDeviation[];
  readonly probes: Partial<Record<InteractionCheck, Probe<D>>>;
  readonly timeout?: number;
}) {
  const listed = new Map(options.deviations.map(row => [`${row.element} ${row.check}`, row]));
  describe(`interaction contract: ${options.surface}`, { timeout: options.timeout ?? 30_000 }, () => {
    checkDeviationTable(options.deviations, options.descriptors);
    it("has a probe for every check a descriptor runs", () => {
      const missing = [...new Set(options.descriptors.flatMap(descriptor => descriptor.checks.filter(check => !options.probes[check])))];
      expect(missing).toEqual([]);
    });
    for (const descriptor of options.descriptors) {
      describe(descriptor.id, () => {
        for (const check of descriptor.checks) {
          const probe = options.probes[check];
          if (!probe) continue;
          const deviation = listed.get(`${descriptor.id} ${check}`);
          it(`${check}${deviation ? " (expected deviation)" : ""}: ${interactionChecks[check].text}`, async () => {
            let result: ProbeResult;
            try { result = await probe(descriptor); } catch (error) { result = `threw: ${error instanceof Error ? error.message : String(error)}`; }
            // The probe's own words in the message: an assertion on the value would cut them short.
            if (deviation && result === true) expect.fail(`${descriptor.id} ${check} now follows the rule: delete its deviation („${deviation.reason}“)`);
            if (!deviation && result !== true) expect.fail(`${descriptor.id} ${check}: ${result}`);
          });
        }
        for (const [check, reason] of Object.entries(descriptor.unchecked ?? {})) it.todo(`${check}: not measured here (${reason})`);
      });
    }
  });
}
