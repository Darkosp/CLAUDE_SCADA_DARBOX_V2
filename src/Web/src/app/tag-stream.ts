import { Injectable, signal, computed } from '@angular/core';
import { HubConnection, HubConnectionBuilder, HubConnectionState } from '@microsoft/signalr';
import { TagSnapshot } from './tag';

export type ConnectionState = 'connecting' | 'connected' | 'disconnected';

/** Base URL of the gateway. The Angular dev server and the gateway run separately in development. */
const GATEWAY_URL = 'http://localhost:5220';

/**
 * Holds the live value of every tag, fed by the gateway's SignalR hub.
 *
 * Values are kept in a signal, so the zoneless application re-renders on a push without
 * any change-detection plumbing.
 */
@Injectable({ providedIn: 'root' })
export class TagStream {
  private readonly byTagId = signal(new Map<string, TagSnapshot>());
  private connection?: HubConnection;

  readonly state = signal<ConnectionState>('connecting');

  /** Every known tag, ordered by display path. */
  readonly tags = computed(() =>
    [...this.byTagId().values()].sort((left, right) => left.path.localeCompare(right.path)),
  );

  async start(): Promise<void> {
    if (this.connection) {
      return;
    }

    const connection = new HubConnectionBuilder()
      .withUrl(`${GATEWAY_URL}/hubs/tags`)
      .withAutomaticReconnect()
      .build();

    connection.on('tagValues', (snapshots: TagSnapshot[]) => this.merge(snapshots));
    connection.onreconnecting(() => this.state.set('connecting'));
    connection.onreconnected(() => {
      this.state.set('connected');
      void this.loadCurrentValues();
    });
    connection.onclose(() => this.state.set('disconnected'));

    this.connection = connection;

    try {
      await connection.start();
      this.state.set('connected');
      await this.loadCurrentValues();
    } catch {
      this.state.set('disconnected');
    }
  }

  /**
   * Pulls the current value of every tag, so a client connecting between scans renders
   * immediately instead of waiting for the next change.
   */
  private async loadCurrentValues(): Promise<void> {
    if (this.connection?.state !== HubConnectionState.Connected) {
      return;
    }

    const snapshots = await this.connection.invoke<TagSnapshot[]>('GetCurrentValues');
    this.merge(snapshots);
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
}
