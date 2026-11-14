import type { BoardTimeline } from "./boardApi";
import t from "../i18n/boards";

/**
 * The timeline background (interaction spec §3.7): a horizontal axis whose origin date lies at board x = 0, `pxPerDay` board units a
 * day. By zoom it divides by days, weeks (from Monday), months, quarters, years or decades: the finest period whose division is at
 * least 48 px apart on screen; the next finer one fades in from 24 to 48 px, as the dot grid does. Dates are calendar days in UTC,
 * so a change to summer time moves nothing. Pure; the canvas draws the ticks and keeps each label at the top of the view.
 */
export type TimelineLevel = "day" | "week" | "month" | "quarter" | "year" | "decade";
export interface TimelineTick { x: number; label: string }

const DAY = 86_400_000;
const levels: { level: TimelineLevel; days: number }[] = [
  { level: "day", days: 1 }, { level: "week", days: 7 }, { level: "month", days: 30.44 },
  { level: "quarter", days: 91.31 }, { level: "year", days: 365.25 }, { level: "decade", days: 3652.5 },
];
const READABLE = 48, FADE_FROM = 24;

const parse = (date: string) => { const [y, m, d] = date.split("-").map(Number); return Date.UTC(y!, (m ?? 1) - 1, d ?? 1); };
const iso = (ms: number) => new Date(ms).toISOString().slice(0, 10);
const days = (timeline: BoardTimeline, ms: number) => Math.round((ms - parse(timeline.origin)) / DAY);

export const xOfDate = (timeline: BoardTimeline, date: string) => days(timeline, parse(date)) * timeline.pxPerDay;
/** The calendar day under board x. */
export const dateAt = (timeline: BoardTimeline, x: number) => iso(parse(timeline.origin) + Math.floor(x / timeline.pxPerDay) * DAY);

export function timelineLevel(timeline: BoardTimeline, zoom: number): TimelineLevel {
  const perDay = timeline.pxPerDay * zoom;
  return (levels.find(entry => entry.days * perDay >= READABLE) ?? levels.at(-1)!).level;
}

/** The start of the period of `level` that holds the day `ms`. */
function periodStart(ms: number, level: TimelineLevel): number {
  const date = new Date(ms), y = date.getUTCFullYear(), m = date.getUTCMonth();
  switch (level) {
    case "day": return ms;
    case "week": return ms - ((date.getUTCDay() + 6) % 7) * DAY;
    case "month": return Date.UTC(y, m, 1);
    case "quarter": return Date.UTC(y, m - (m % 3), 1);
    case "year": return Date.UTC(y, 0, 1);
    case "decade": return Date.UTC(y - (((y % 10) + 10) % 10), 0, 1);
  }
}
function nextPeriod(ms: number, level: TimelineLevel): number {
  const date = new Date(ms), y = date.getUTCFullYear(), m = date.getUTCMonth();
  switch (level) {
    case "day": return ms + DAY;
    case "week": return ms + 7 * DAY;
    case "month": return Date.UTC(y, m + 1, 1);
    case "quarter": return Date.UTC(y, m + 3, 1);
    case "year": return Date.UTC(y + 1, 0, 1);
    case "decade": return Date.UTC(y + 10, 0, 1);
  }
}
function label(ms: number, level: TimelineLevel): string {
  const date = new Date(ms), y = date.getUTCFullYear(), m = date.getUTCMonth();
  switch (level) {
    case "day": case "week": return t.timeline.day(date.getUTCDate(), m + 1);
    case "month": return `${t.timeline.months[m]} ${y}`;
    case "quarter": return `Q${Math.floor(m / 3) + 1} ${y}`;
    case "year": return `${y}`;
    case "decade": return `${y}–${y + 9}`;
  }
}

/** shortcut: at most 400 ticks a level; a view wider than that at its level is impossible within 5–400 % and the scale limits. */
const MAX_TICKS = 400;
function ticksOf(timeline: BoardTimeline, level: TimelineLevel, view: { x: number; width: number }): TimelineTick[] {
  const origin = parse(timeline.origin);
  const end = origin + ((view.x + view.width) / timeline.pxPerDay) * DAY;
  const ticks: TimelineTick[] = [];
  for (let ms = periodStart(origin + Math.floor(view.x / timeline.pxPerDay) * DAY, level); ms <= end && ticks.length < MAX_TICKS; ms = nextPeriod(ms, level))
    ticks.push({ x: days(timeline, ms) * timeline.pxPerDay, label: label(ms, level) });
  return ticks;
}

/** The divisions of the level shown at `zoom` in the visible board range, from the period the view starts in, and the finer level fading in. */
export function timelineTicks(timeline: BoardTimeline, zoom: number, view: { x: number; width: number }) {
  const level = timelineLevel(timeline, zoom);
  const index = levels.findIndex(entry => entry.level === level);
  const finer = index > 0 ? levels[index - 1]! : undefined;
  const spacing = finer ? finer.days * timeline.pxPerDay * zoom : 0;
  const t = Math.min(1, Math.max(0, (spacing - FADE_FROM) / (READABLE - FADE_FROM)));
  const finerOpacity = t * t * (3 - 2 * t);
  return {
    level, ticks: ticksOf(timeline, level, view),
    finer: finer?.level, finerOpacity, finerTicks: finer && finerOpacity > 0 ? ticksOf(timeline, finer.level, view) : [],
  };
}

/** Where today is on the board, for the "dnes" marker. */
export const todayX = (timeline: BoardTimeline, now = new Date()) => xOfDate(timeline, iso(Date.UTC(now.getFullYear(), now.getMonth(), now.getDate())));
