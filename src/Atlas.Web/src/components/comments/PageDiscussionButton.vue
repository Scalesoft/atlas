<script setup lang="ts">
import type { BoardBlockHost } from "../../boards/editorBlocks";
import { commentApi, type CommentApi } from "../../comments/commentApi";
import type { ViewSessions } from "../../live/viewSession";
import { replyTo, usePageDiscussionFeed, type DiscussionBubble } from "../../comments/pageDiscussionFeed";
import { chordLabel } from "../../keyboard/shortcuts";
import t from "../../i18n/shell";
import AtlasIcon from "../AtlasIcon.vue";
import { Button } from "../ui/button";
import { Tooltip, TooltipContent, TooltipTrigger } from "../ui/tooltip";
import PageDiscussionPeek from "./PageDiscussionPeek.vue";

/**
 * The page's discussion at the top bar (App's `page-discussion-anchor`): the button of the dock's Diskuse (it toggles it; Alt C does the same)
 * with the comments' count over its corner, accented while a colleague's new comments are unread, and under it the peek with their latest
 * words (PageDiscussionPeek). The count and the bubbles come from the page's feed, read after the page's text; `open` (the page's thread is
 * shown: the dock's Diskuse, or the section under the content on a phone while it is on screen) hands the bubbles over to it and the feed reads no list. Never both
 * chats: while the dock is shown at all (`docked`, any tab) the bubbles fly toward it and wait in the feed until it closes. A bubble's „Odpovědět“ posts a comment to the page that mentions the bubble's author (`host`: the comment field's `@`, `[[`). One lazy part, so the first screen carries none of it. `pageId` undefined: nothing is read.
 */
const props = defineProps<{ pageId?: string; selfId?: string; open: boolean; docked?: boolean; host?: BoardBlockHost;
  /** The comments' path and the view session (a gallery's in-memory ones); the server's by default. */
  api?: Pick<CommentApi, "page" | "counts" | "post">; views?: Pick<ViewSessions, "open"> }>();
const emit = defineEmits<{ toggle: []; open: [] }>();
const api = props.api ?? commentApi;
const feed = usePageDiscussionFeed(() => props.pageId, { selfId: () => props.selfId, open: () => props.open }, api, props.views);
defineExpose({ /** The open thread knows its count at once, also after the reader's own comment. */ set: feed.set,
  /** The page's comments and a colleague's unread ones (the phone bar and the ⋯ menu show them as the button does). */ count: feed.count, unread: feed.unread });
/** A quick reply from the peek: a comment of the page like any other; the notice of the view session brings it to the open threads. */
async function reply(markdown: string, bubble: DiscussionBubble) {
  await api.post(props.pageId!, replyTo(bubble, markdown));
  feed.set(feed.count.value + 1);
}
const badge = () => feed.unread.value || feed.count.value;
</script>

<template>
  <Tooltip>
    <TooltipTrigger as-child>
      <Button variant="ghost" size="icon-sm" class="page-discussion-button text-muted-foreground" :aria-label="t.common.discussionCount(feed.count.value, feed.unread.value)" :aria-pressed="open"
        :data-fresh="feed.unread.value > 0 || undefined" data-page-discussion @click="emit('toggle')">
        <AtlasIcon name="quote" />
        <Transition name="page-discussion-count" mode="out-in"><span v-if="badge()" :key="badge()" class="page-discussion-count discussion-count" :data-fresh="feed.unread.value > 0 || undefined" data-page-discussion-count aria-hidden="true">{{ badge() }}</span></Transition>
      </Button>
    </TooltipTrigger>
    <TooltipContent>{{ t.common.discussion }} · {{ chordLabel("Alt+c") }}</TooltipContent>
  </Tooltip>
  <PageDiscussionPeek :bubbles="docked || open ? [] : feed.bubbles.value" :docked="docked || open" :host="host" :reply="feed.canPost.value ? reply : undefined" :draft-scope="pageId" @open="emit('open')" @dismiss="feed.dismiss" @replying="feed.pin" />
</template>

<style>
/* Comes with this lazy part, not with the eager refresh-article.css (the count's pill, `.discussion-count`, is there). The count sits over the button's corner, so a new number or a first comment never resizes the bar. */
.atlas .page-discussion-button[aria-pressed="true"]:not(.md-editor *) { background: var(--accent-soft); color: var(--accent); }
.atlas .page-discussion-button:not(.md-editor *) { position: relative; }
.atlas .page-discussion-button .page-discussion-count:not(.md-editor *) { position: absolute; top: -3px; right: -3px; }
.atlas .page-discussion-count-enter-active { animation: st-rise-in var(--atlas-motion-base) var(--atlas-ease-out) both; }
.atlas .page-discussion-count-leave-active { animation: st-fade-out var(--atlas-motion-fast) var(--atlas-ease-out) both; }
</style>
