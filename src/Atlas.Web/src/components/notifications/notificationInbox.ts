import { api, post, put } from "../../api";
import { contentItemLink, pageCommentLink } from "../../pageLinks";
import { unreadNotificationsApi } from "../../notifications";
import t from "../../i18n/components-search";

/**
 * The inbox „Oznámení“ (inbox spec 2026-10-29): one list of mentions and shares per account, read under the reader's current rights.
 * Loaded with the inbox; only the badge (../../notifications.ts) is part of the first screen.
 */
export type NotificationKind = "comment.mention" | "page.mention" | "item.shared" | "item.share_changed" | "item.unshared" | "job.failed"
  | "page.verification_due" | "page.verification_expiring" | "page.verification_expired" | "page.reported_outdated";
export interface NotificationItem {
  /** `subjectType` null: an ended share the reader may not open any more, told without its title. */
  id: string; kind: NotificationKind | string; subjectId: string; subjectType: string | null; subjectTitle: string;
  spaceId: string | null; spaceName: string | null; anchor: string | null; actorId: string | null; actorName: string | null;
  count: number; createdAt: string; updatedAt: string; read: boolean;
  /** A mention in a comment: the comment that mentions the reader and a short plain-text excerpt of it (null once it is deleted). */
  commentId?: string | null; excerpt?: string | null;
}
export interface NotificationList { items: NotificationItem[]; next: string | null; unread: number }
export interface NotificationPreference { kind: NotificationKind | string; enabled: boolean }
export interface NotificationsMarked { marked: number; unread: number }

export const notificationApi = {
  list: (before?: string | null, signal?: AbortSignal) => api<NotificationList>(`/notifications${before ? `?before=${encodeURIComponent(before)}` : ""}`, { signal }),
  unread: unreadNotificationsApi.unread,
  markRead: (id: string) => post<NotificationsMarked>(`/notifications/${encodeURIComponent(id)}/read`),
  markAllRead: () => post<NotificationsMarked>("/notifications/read-all"),
  preferences: (signal?: AbortSignal) => api<{ items: NotificationPreference[] }>("/notifications/preferences", { signal }),
  setPreference: (kind: string, enabled: boolean) => put<NotificationPreference>(`/notifications/preferences/${encodeURIComponent(kind)}`, { enabled }),
};
export type NotificationApi = typeof notificationApi;

/** Who did what, in the words of the list: „Jana Nová vás zmínila v komentáři“. */
export const kindLabels = (): Record<NotificationKind, { title: string; action: string; icon: string }> => ({
  "comment.mention": { title: t.notifications.mentionsInComments, action: t.notifications.mentionedInComment, icon: "comment-mention" },
  "page.mention": { title: t.notifications.mentionsInPages, action: t.notifications.mentionedInPage, icon: "page-mention" },
  "item.shared": { title: t.notifications.sharing, action: t.notifications.sharedWithYou, icon: "shared" },
  "item.share_changed": { title: t.notifications.sharingChanges, action: t.notifications.changedSharing, icon: "shared" },
  "item.unshared": { title: t.notifications.sharingEnded, action: t.notifications.removedAccess, icon: "shared" },
  // To the wiki administrators, from no one: the row says what happened, the subject is the job.
  "job.failed": { title: t.notifications.jobFailed, action: "", icon: "alert" },
  // 5.2: the steward's reminders, from no one (their preference is page.verification_due), and a reader's report with its note as the excerpt.
  "page.verification_due": { title: t.notifications.verificationReminders, action: "", icon: "shield-check" },
  "page.verification_expiring": { title: t.notifications.verificationExpiring, action: "", icon: "shield-check" },
  "page.verification_expired": { title: t.notifications.verificationExpired, action: "", icon: "alert" },
  "page.reported_outdated": { title: t.notifications.outdatedReports, action: t.notifications.reportedOutdated, icon: "alert" },
});
/** The kinds a preference switches; a share's change and end follow „Sdílení“. */
export const preferenceKinds: NotificationKind[] = ["comment.mention", "page.mention", "item.shared", "page.verification_due", "page.reported_outdated"];
/** What the row names: the subject's title, or for an ended share only that it was one. */
export const subjectTitle = (item: Pick<NotificationItem, "subjectType" | "subjectTitle">) =>
  item.subjectType === null ? t.notifications.noAccess : item.subjectTitle;
export function kindOf(kind: string) {
  return kindLabels()[kind as NotificationKind] ?? { title: kind, action: "", icon: "notifications" };
}

/** The subject at its anchor (a page's heading), a failed job at its page in Správa wiki (the anchor is its id); undefined for a type the
 * shell does not open by address. */
export function notificationLink(item: Pick<NotificationItem, "subjectId" | "subjectType" | "anchor" | "commentId">) {
  if (item.subjectType === "atlas.job") return item.anchor ? `#/settings/general/jobs/${encodeURIComponent(item.anchor)}` : undefined;
  if (!item.subjectType) return undefined;
  if (item.commentId && item.subjectType === "atlas.page") return pageCommentLink(item.subjectId, item.commentId);
  return contentItemLink({ id: item.subjectId, type: "reference", targetId: item.subjectId, targetTypeId: item.subjectType, anchor: item.anchor });
}

/** „Dnes“, „Včera“, „Tento týden“, „Starší“ by the last event, in the order of the list. */
export function groupByDay(items: NotificationItem[], now = new Date()) {
  const day = (date: Date) => new Date(date.getFullYear(), date.getMonth(), date.getDate()).getTime();
  const today = day(now), dayMs = 86_400_000;
  const label = (at: string) => {
    const days = Math.round((today - day(new Date(at))) / dayMs);
    return days <= 0 ? t.notifications.today : days === 1 ? t.notifications.yesterday : days < 7 ? t.notifications.thisWeek : t.notifications.older;
  };
  const groups: { label: string; items: NotificationItem[] }[] = [];
  for (const item of items) {
    const name = label(item.updatedAt);
    const last = groups.at(-1);
    if (last?.label === name) last.items.push(item);
    else groups.push({ label: name, items: [item] });
  }
  return groups;
}
