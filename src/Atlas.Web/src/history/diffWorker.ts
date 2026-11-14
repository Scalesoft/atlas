import { compareHistoryText } from "../historyDiff";

self.onmessage = (event: MessageEvent<{ id: number; before: string; after: string }>) => {
  const { id, before, after } = event.data;
  (self as unknown as Worker).postMessage({ id, diff: compareHistoryText(before, after) });
};
