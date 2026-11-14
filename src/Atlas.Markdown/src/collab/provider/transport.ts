import { HubConnectionBuilder, LogLevel, type HubConnection } from '@microsoft/signalr'
import { IndefiniteBackoff } from './retryPolicy'

/** Minimal SignalR-shaped transport seam used by the provider and its tests. */
export interface MarkdownSyncConnection {
  start(): Promise<void>
  stop(): Promise<void>
  invoke<T>(method: string, ...args: unknown[]): Promise<T>
  send(method: string, ...args: unknown[]): Promise<void>
  on(method: string, handler: (...args: any[]) => void): void
  onReconnecting(handler: (error?: Error) => void): void
  onReconnected(handler: (connectionId?: string) => void): void
  onClose(handler: (error?: Error) => void): void
}

class SignalRConnection implements MarkdownSyncConnection {
  constructor(private readonly connection: HubConnection) {}

  start(): Promise<void> { return this.connection.start() }
  stop(): Promise<void> { return this.connection.stop() }
  invoke<T>(method: string, ...args: unknown[]): Promise<T> { return this.connection.invoke<T>(method, ...args) }
  send(method: string, ...args: unknown[]): Promise<void> { return this.connection.send(method, ...args) }
  on(method: string, handler: (...args: any[]) => void): void { this.connection.on(method, handler) }
  onReconnecting(handler: (error?: Error) => void): void { this.connection.onreconnecting(handler) }
  onReconnected(handler: (connectionId?: string) => void): void { this.connection.onreconnected(handler) }
  onClose(handler: (error?: Error) => void): void { this.connection.onclose(handler) }
}

export interface SignalRConnectionOptions {
  /** Client ping interval; defaults to the SignalR client default (15 s). */
  keepAliveIntervalMs?: number
  /**
   * Time without any server message before the client drops the connection. The server hub
   * pings every `serverKeepAliveMs` (ASP.NET Core default 15 s), so this must be at least twice
   * that value — otherwise every quiet period ends in a spurious disconnect.
   */
  serverTimeoutMs?: number
  /** The hub's KeepAliveInterval; only needed when the server is configured away from its 15 s default. */
  serverKeepAliveMs?: number
}

export function createSignalRConnection(hubUrl: string, accessToken?: () => string | Promise<string>, options: SignalRConnectionOptions = {}): MarkdownSyncConnection {
  const keepAlive = options.keepAliveIntervalMs ?? 15_000
  const serverKeepAlive = options.serverKeepAliveMs ?? 15_000
  const timeout = options.serverTimeoutMs ?? Math.max(30_000, serverKeepAlive * 2)
  if (timeout < serverKeepAlive * 2) throw new TypeError('serverTimeoutMs must be at least twice the server keepalive interval (serverKeepAliveMs, default 15 s).')
  return new SignalRConnection(new HubConnectionBuilder()
    // The provider inflates a gzip join snapshot and seed (protocol.ts snapshotEncoding, seed); a server that does not know the flags ignores them.
    .withUrl(`${hubUrl}${hubUrl.includes('?') ? '&' : '?'}snapshot=gzip&seed=gzip`, { accessTokenFactory: accessToken })
    .withAutomaticReconnect(new IndefiniteBackoff())
    .withKeepAliveInterval(keepAlive)
    .withServerTimeout(timeout)
    .configureLogging(LogLevel.Warning)
    .build())
}

export function isAuthorizationError(error: unknown): boolean {
  const status = (error as { statusCode?: number } | null)?.statusCode
  return status === 401 || status === 403 || /unauthorized|forbidden|access denied|not authorized/i.test(error instanceof Error ? error.message : String(error))
}
