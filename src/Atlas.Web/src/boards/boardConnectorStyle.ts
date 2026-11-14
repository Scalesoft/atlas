import { browserStorage } from "../lib/browserStorage";
import { boardColors, type BoardColor, type BoardProps, type BoardPropsPatch } from "./boardApi";

/**
 * A connector's quick styles (owner feedback 27. 10.): its direction as one choice of four (the two arrows), and the style the
 * next new connector takes — the last one this person gave a connector, kept per browser (a convenience, not shared state).
 */
export type ConnectorDirection = "none" | "forward" | "backward" | "both";
export function directionOf(props: Pick<BoardProps, "arrowStart" | "arrowEnd">): ConnectorDirection {
  const start = props.arrowStart === "arrow", end = (props.arrowEnd ?? "arrow") === "arrow";
  return start && end ? "both" : start ? "backward" : end ? "forward" : "none";
}
export const directionPatch = (direction: ConnectorDirection): Pick<BoardProps, "arrowStart" | "arrowEnd"> => ({
  arrowStart: direction === "backward" || direction === "both" ? "arrow" : "none",
  arrowEnd: direction === "forward" || direction === "both" ? "arrow" : "none",
});

/** What a new connector takes over: its look, never its ends, sides, label or state. */
export type ConnectorStyle = Pick<BoardProps, "route" | "arrowStart" | "arrowEnd" | "dash" | "animated" | "thickness"> & { color?: BoardColor };
const allowed: Record<keyof ConnectorStyle, readonly unknown[]> = {
  route: ["elbow", "curved", "straight"], arrowStart: ["none", "arrow"], arrowEnd: ["none", "arrow"], dash: ["solid", "dashed"],
  animated: [true], thickness: ["s", "m", "l"], color: boardColors,
};
/** Only the known keys with known values: what a restyle or the browser's storage holds may be anything. */
function clean(values: Record<string, unknown>): ConnectorStyle {
  return Object.fromEntries(Object.entries(values).filter(([key, value]) => allowed[key as keyof ConnectorStyle]?.includes(value))) as ConnectorStyle;
}

/** The style after a restyle of connectors: its style keys and colour over the last one, a null forgetting one. */
export function rememberConnectorStyle(style: ConnectorStyle, patch: { color?: BoardColor | null; props?: BoardPropsPatch }): ConnectorStyle {
  const next: Record<string, unknown> = { ...style };
  for (const [key, value] of Object.entries({ ...patch.props, ...(patch.color !== undefined && { color: patch.color }) })) {
    if (!(key in allowed)) continue;
    if (value === null || value === undefined) delete next[key]; else next[key] = value;
  }
  return clean(next);
}

const key = "atlas.board.connectorStyle";
type Store = Pick<Storage, "getItem" | "setItem">;
export function loadConnectorStyle(store: Store | undefined = browserStorage()): ConnectorStyle {
  try {
    const value: unknown = JSON.parse(store?.getItem(key) ?? "{}");
    return value && typeof value === "object" ? clean(value as Record<string, unknown>) : {};
  } catch { return {}; }
}
export function saveConnectorStyle(style: ConnectorStyle, store: Store | undefined = browserStorage()) {
  try { store?.setItem(key, JSON.stringify(style)); } catch { /* private mode, blocked storage: the style is just not kept */ }
}
