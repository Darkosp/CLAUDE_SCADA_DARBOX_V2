import { Injectable } from '@angular/core';
import { Alarm, AlarmDefinition, Site, SiteTree, TagHistory } from './models';

/** Base URL of the gateway. The Angular dev server and the gateway run separately. */
export const GATEWAY_URL = 'http://localhost:5220';

/** An error carrying the message the gateway gave, so the UI can show the real reason. */
export class ApiError extends Error {
  constructor(
    message: string,
    readonly status: number,
  ) {
    super(message);
  }
}

@Injectable({ providedIn: 'root' })
export class Api {
  sites(): Promise<Site[]> {
    return this.get<Site[]>('/api/sites');
  }

  tree(siteId: string): Promise<SiteTree> {
    return this.get<SiteTree>(`/api/sites/${siteId}/tree`);
  }

  history(tagId: string, from: Date, to: Date): Promise<TagHistory> {
    const query = `from=${from.toISOString()}&to=${to.toISOString()}`;
    return this.get<TagHistory>(`/api/tags/${tagId}/history?${query}`);
  }

  createFolder(siteId: string, body: { name: string; parentFolderId: string | null }): Promise<unknown> {
    return this.send('POST', `/api/sites/${siteId}/folders`, body);
  }

  saveDevice(siteId: string, deviceId: string | null, body: unknown): Promise<unknown> {
    return deviceId === null
      ? this.send('POST', `/api/sites/${siteId}/devices`, body)
      : this.send('PUT', `/api/sites/${siteId}/devices/${deviceId}`, body);
  }

  saveTag(deviceId: string, tagId: string | null, body: unknown): Promise<unknown> {
    return tagId === null
      ? this.send('POST', `/api/devices/${deviceId}/tags`, body)
      : this.send('PUT', `/api/devices/${deviceId}/tags/${tagId}`, body);
  }

  alarmsOf(tagId: string): Promise<AlarmDefinition[]> {
    return this.get<AlarmDefinition[]>(`/api/tags/${tagId}/alarms`);
  }

  alarms(): Promise<Alarm[]> {
    return this.get<Alarm[]>('/api/alarms');
  }

  acknowledge(definitionId: string): Promise<unknown> {
    return this.send('POST', `/api/alarms/${definitionId}/acknowledge`, null);
  }

  shelve(definitionId: string): Promise<unknown> {
    return this.send('POST', `/api/alarms/${definitionId}/shelve`, null);
  }

  saveAlarm(
    tagId: string,
    definitionId: string | null,
    body: { highLimit: number | null; lowLimit: number | null },
  ): Promise<unknown> {
    return definitionId === null
      ? this.send('POST', `/api/tags/${tagId}/alarms`, body)
      : this.send('PUT', `/api/tags/${tagId}/alarms/${definitionId}`, body);
  }

  deleteAlarm(tagId: string, definitionId: string): Promise<unknown> {
    return this.send('DELETE', `/api/tags/${tagId}/alarms/${definitionId}`, null);
  }

  deleteFolder(siteId: string, folderId: string): Promise<unknown> {
    return this.send('DELETE', `/api/sites/${siteId}/folders/${folderId}`, null);
  }

  deleteDevice(siteId: string, deviceId: string): Promise<unknown> {
    return this.send('DELETE', `/api/sites/${siteId}/devices/${deviceId}`, null);
  }

  deleteTag(deviceId: string, tagId: string): Promise<unknown> {
    return this.send('DELETE', `/api/devices/${deviceId}/tags/${tagId}`, null);
  }

  private async get<T>(path: string): Promise<T> {
    const response = await fetch(`${GATEWAY_URL}${path}`);
    if (!response.ok) {
      throw new ApiError(await this.reasonFrom(response), response.status);
    }
    return (await response.json()) as T;
  }

  private async send(method: string, path: string, body: unknown): Promise<unknown> {
    const response = await fetch(`${GATEWAY_URL}${path}`, {
      method,
      headers: body === null ? undefined : { 'Content-Type': 'application/json' },
      body: body === null ? undefined : JSON.stringify(body),
    });

    if (!response.ok) {
      throw new ApiError(await this.reasonFrom(response), response.status);
    }

    return response.status === 204 ? null : await response.json();
  }

  /**
   * The gateway answers a refused write with the rule that was broken — a cross-site
   * placement, a folder cycle. Surfacing that beats replacing it with a generic failure.
   */
  private async reasonFrom(response: Response): Promise<string> {
    try {
      const body = (await response.json()) as { error?: string };
      if (body?.error) {
        return body.error;
      }
    } catch {
      // Not every failure carries a JSON body; fall through to the status text.
    }

    return `${response.status} ${response.statusText}`;
  }
}
