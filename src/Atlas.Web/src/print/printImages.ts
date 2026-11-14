/**
 * The rich renderer gives every image a neutral brightness/contrast/saturation filter. Chromium cannot write a CSS filter
 * into a PDF and rasterizes each filtered image at 300 DPI of its printed area instead of embedding it: several times the
 * pixels, memory and print time of the image itself, and a resampled copy. Only a real adjustment keeps its filter.
 */
export function dropNeutralFilter(image: HTMLElement) {
  const filter = getComputedStyle(image).filter;
  if (filter && filter !== "none" && !filter.replace(/(brightness|contrast|saturate)\((1|100%)\)/g, "").trim()) image.style.filter = "none";
}
