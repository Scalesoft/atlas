import type { AttachmentUploadService } from "@atlas/markdown/plugins/attachments";
import shellMore from "./i18n/shell-more";
import { api } from "./api";
interface UploadedAttachment { id: string; name: string; size: number; mimeType: string; url: string; downloadUrl: string }
interface UploadContext { signal: AbortSignal }
export function createEditorUploads(current: () => { pageId?: string; canWrite: boolean }) {
  async function upload(file: File, kind: "image" | "file", { signal }: UploadContext) {
    const start = current();
    if (!start.pageId || !start.canWrite) throw new Error(shellMore.upload.needWrite);
    signal.throwIfAborted();
    const limit = (kind === "image" ? 10 : 25) * 1024 * 1024;
    if (file.size > limit) throw new Error(shellMore.upload.tooLarge(kind === "image" ? 10 : 25));
    const body = new FormData(); body.append("file", file); body.append("kind", kind);
    const result = await api<UploadedAttachment>(`/pages/${encodeURIComponent(start.pageId)}/attachments`, { method: "POST", body, signal });
    signal.throwIfAborted();
    const now = current();
    if (!now.canWrite || now.pageId !== start.pageId) throw new Error(shellMore.upload.changed);
    return result;
  }
  const attachment: AttachmentUploadService = { async upload(file, context) {
    const result = await upload(file, "file", context);
    return { url: result.downloadUrl, name: result.name, size: result.size, mimeType: result.mimeType };
  } };
  return { attachment, async image(file: File, context: UploadContext) {
    const result = await upload(file, "image", context); return { url: result.url, alt: result.name };
  } };
}
