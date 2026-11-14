import type { LiveFeedListener, LiveFeedSource, LiveTopic } from "./liveFeed";

/**
 * An in-memory stand-in for /hubs/live (the gallery's examples and the tests, provided as `liveFeedSourceKey`): every topic is subscribed at
 * once, `emit` hands an event to the topics of a channel, `fail` makes them unavailable and `resubscribe` is a reconnect.
 */
export function createFakeLiveFeed() {
  const open = new Set<{ topic: LiveTopic; listener: LiveFeedListener }>();
  const source: LiveFeedSource = (topic, listener) => {
    const entry = { topic, listener };
    open.add(entry);
    queueMicrotask(() => { if (open.has(entry)) listener.subscribed(); });
    return () => { open.delete(entry); };
  };
  const of = (channel?: string) => [...open].filter((entry) => !channel || entry.topic.channel === channel);
  return {
    source,
    emit(channel: string, payload: unknown) { of(channel).forEach((entry) => entry.listener.message(payload)); },
    fail(channel?: string) { of(channel).forEach((entry) => entry.listener.unavailable()); },
    resubscribe(channel?: string) { of(channel).forEach((entry) => entry.listener.subscribed()); },
    /** The topics open now on a channel (all without one). */
    count: (channel?: string) => of(channel).length,
  };
}
