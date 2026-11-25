import type { ObjectPresence } from "./viewSession";
import t from "../i18n/shell";

/**
 * Who works at which object (objects spec §4.1), from the view sessions' `ObjectPresence`: a colleague is present while their
 * presence is renewed (the sender repeats it) and gone after `{ editing: false }` or `ttlMs` without news. A presence outside the
 * viewport carries no payload and still counts: it says who is there, not what they do.
 */
export interface PresentPerson {
  /** Stable per person for the roster's life (the avatars key and animate by it). */
  clientId: number;
  userId: string;
  displayName: string;
  color: string;
  via: string | null;
}

export function createPresenceRoster(changed: () => void, ttlMs = 25_000) {
  const byObject = new Map<string, Map<string, PresentPerson & { timer: ReturnType<typeof setTimeout> }>>();
  const numbers = new Map<string, number>();
  function remove(objectId: string, userId: string) {
    const people = byObject.get(objectId);
    const person = people?.get(userId);
    if (!person) return;
    clearTimeout(person.timer);
    people!.delete(userId);
    if (!people!.size) byObject.delete(objectId);
    changed();
  }
  return {
    receive({ objectId, userId, displayName, color, payload, via }: ObjectPresence) {
      if (payload?.editing === false) return remove(objectId, userId);
      const people = byObject.get(objectId) ?? new Map();
      byObject.set(objectId, people);
      clearTimeout(people.get(userId)?.timer);
      if (!numbers.has(userId)) numbers.set(userId, numbers.size + 1);
      people.set(userId, { clientId: numbers.get(userId)!, userId, displayName, color, via: via ?? null, timer: setTimeout(() => remove(objectId, userId), ttlMs) });
      changed();
    },
    of(objectId: string): PresentPerson[] {
      return [...(byObject.get(objectId.toLowerCase())?.values() ?? [])].map(({ timer: _timer, ...person }) => person);
    },
    dispose() {
      byObject.forEach(people => people.forEach(person => clearTimeout(person.timer)));
      byObject.clear();
    },
  };
}
export type PresenceRoster = ReturnType<typeof createPresenceRoster>;

/** The label of a colleague at an object: through which view (its title, as the reader may know it), or elsewhere. */
export function presenceActivity(name: string, viewTitle: string | null) {
  return viewTitle ? t.presence.via(name, viewTitle) : t.presence.elsewhere(name);
}
