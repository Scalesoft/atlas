import { MAX_OPENAPI_SOURCE_BYTES, normalizeOpenApi, type OpenApiData } from './data'
import { openapiStrings } from './strings'

export const isSpecFileName = (name: string) => /\.(?:ya?ml|json)$/i.test(name)

/** The data an uploaded file would give the block, after the same validation as the source dialog; throws its message. */
export async function specFromFile(file: File, current: OpenApiData, uploaderName?: string): Promise<OpenApiData> {
  if (file.size > MAX_OPENAPI_SOURCE_BYTES) throw new TypeError(openapiStrings.sourceTooLargeError)
  const uploader = uploaderName?.trim()
  return normalizeOpenApi({ ...current, filename: file.name, source: await file.text(), ...(uploader ? { uploadedBy: uploader } : {}) })
}
