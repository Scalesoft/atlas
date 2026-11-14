import { api } from "../api";
import t from "../i18n/contacts";

/**
 * A contact's logo or photo (Atlas 3.5 package G): the picture a person chose, made a small square here (the server only checks it
 * and keeps it, at most 64 KB) and uploaded to the contact (PUT /api/contacts/{id}/photo), whose photo field then names it. The chip
 * shows it at 18 px; four times that is sharp on a HiDPI screen and a few KB.
 */
export const contactPhotoSize = 64;

/** The whole picture fitted into the square (a logo is never cut), WebP where the browser writes it, else PNG. */
export async function contactPhotoBlob(file: Blob, size = contactPhotoSize): Promise<Blob> {
  const image = await createImageBitmap(file);
  const canvas = document.createElement("canvas");
  canvas.width = canvas.height = size;
  const scale = Math.min(size / image.width, size / image.height), width = image.width * scale, height = image.height * scale;
  canvas.getContext("2d")!.drawImage(image, (size - width) / 2, (size - height) / 2, width, height);
  image.close();
  return new Promise((resolve, reject) => canvas.toBlob(blob => (blob ? resolve(blob) : reject(new Error(t.photo.resizeFailed))), "image/webp", 0.9));
}

/** Uploads the picture; answers the contact's photo field now. */
export async function uploadContactPhoto(contactId: string, file: Blob): Promise<string> {
  const body = new FormData();
  body.append("file", await contactPhotoBlob(file), "photo");
  return (await api<{ photo: string }>(`/contacts/${encodeURIComponent(contactId)}/photo`, { method: "PUT", body })).photo;
}
