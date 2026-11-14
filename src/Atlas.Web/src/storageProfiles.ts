export interface StorageProfile {
  id: string; name: string; destination: string; username: string; domain: string; hasPassword: boolean;
  enabled: boolean; allowPageExports: boolean; allowBackups: boolean; allSpaces: boolean; spaceIds: string[]; version: number;
}
