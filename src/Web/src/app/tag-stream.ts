import { Injectable, computed, inject, signal } from '@angular/core';
import { HubConnection, HubConnectionBuilder, HubConnectionState } from '@microsoft/signalr';
import { Auth } from './auth';
import { Alarm } from './models';
import { TagSnapshot } from './tag';

export type ConnectionState = 'connecting' | 'connected' | 'disconnected';

/**
 * Holds the live value of every tag this session may see, fed by the gateway's SignalR hub.
 *
 * Values are kept in a signal, so the zoneless application re-renders on a push without
 * any change-detection plumbing.
 */
@Injectable({ providedIn: 'root' })
export class TagStream {
  private readonly auth = inject(Auth);
  private readonly byTagId = signal(new Map<string, TagSnapshot>());
  private connection?: HubConnection;

  readonly state = signal<ConnectionState>('disconnected');

  /** Standing alarms across every Site this session may see. */
  readonly alarms = signal<Alarm[]>([]);

  /** Every visible tag, ordered by display path. */
  readonly tags = computed(() =>
    [...this.byTagId().values()].sort((left, right) => left.path.localeCompare(right.path)),
  );

  /**
   * Called when the connection has ended for good — which is also what happens when the
   * gateway ends the session, so the app then checks whether it is still signed in.
   */
  onClosed: (() => void) | null = null;

  /**
   * Called after the gateway reports that what this session may see has changed, once the
   * live values have been reloaded. The rest of the screen — the Site list, a loaded tree,
   * a fetched trend — is the app's to bring in line.
   */
  onAccessChanged: (() => void) | null = null;

  async start(): Promise<void> {
    if (this.connection) {
      return;
    }

    const connection = new HubConnectionBuilder()
      .withUrl('/hubs/tags', {
        // A browser cannot put a header on a WebSocket handshake, so the client sends the
        // token in the URL. The gateway accepts it only on this path and removes it before
        // anything can log the request (ADR-0011).
        accessTokenFactory: () => this.auth.token() ?? '',
      })
      .withAutomaticReconnect()
      .build();

    connection.on('tagValues', (snapshots: TagSnapshot[]) => this.merge(snapshots));
    connection.on('alarms', (siteId: string, alarms: Alarm[]) => this.replaceSiteAlarms(siteId, alarms));

    // What this session may see changed while it was connected. Reloading drops the
    // values of a Site no longer permitted, instead of leaving them frozen on screen.
    connection.on('accessChanged', () => {
      void this.reload().then(() => this.onAccessChanged?.());
    });

    connection.onreconnecting(() => this.state.set('connecting'));
    connection.onreconnected(() => {
      this.state.set('connected');
      void this.reload();
    });
    connection.onclose(() => {
      // A deliberate stop has already let go of this connection; only an ending the app
      // did not ask for is worth reporting.
      if (this.connection === connection) {
        this.connection = undefined;
        this.state.set('disconnected');
        this.onClosed?.();
      }
    });

    this.connection = connection;
    this.state.set('connecting');

    try {
      await connection.start();
      this.state.set('connected');
      await this.reload();
    } catch {
      this.connection = undefined;
      this.state.set('disconnected');
      this.onClosed?.();
    }
  }

  /** Closes the connection and forgets everything it delivered — on logout. */
  async stop(): Promise<void> {
    const connection = this.connection;
    this.connection = undefined;

    try {
      await connection?.stop();
    } finally {
      this.byTagId.set(new Map());
      this.alarms.set([]);
      this.state.set('disconnected');
    }
  }

  /**
   * Pulls everything this session may currently see, so a client connecting between scans
   * renders immediately — and, after a permission change, sees exactly the new set.
   */
  private async reload(): Promise<void> {
    const connection = this.connection;
    if (connection?.state !== HubConnectionState.Connected) {
      return;
    }

    // Replaced rather than merged: the answer is the whole of what may be seen now, and a
    // tag missing from it must disappear.
    const snapshots = await connection.invoke<TagSnapshot[]>('GetCurrentValues');
    this.byTagId.set(new Map(snapshots.map((snapshot) => [snapshot.tagId, snapshot])));

    this.alarms.set(newestFirst(await connection.invoke<Alarm[]>('GetCurrentAlarms')));
  }

  private merge(snapshots: TagSnapshot[]): void {
    this.byTagId.update((current) => {
      const next = new Map(current);
      for (const snapshot of snapshots) {
        next.set(snapshot.tagId, snapshot);
      }
      return next;
    });
  }

  /** Each push is one Site's complete standing list: it replaces that Site's, and only that. */
  private replaceSiteAlarms(siteId: string, alarms: Alarm[]): void {
    this.alarms.update((current) =>
      newestFirst([...current.filter((alarm) => alarm.siteId !== siteId), ...alarms]),
    );
  }
}

function newestFirst(alarms: Alarm[]): Alarm[] {
  return [...alarms].sort((left, right) => Date.parse(right.raisedAtUtc) - Date.parse(left.raisedAtUtc));
}
