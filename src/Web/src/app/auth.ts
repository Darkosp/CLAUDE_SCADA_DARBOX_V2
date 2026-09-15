import { Injectable, computed, signal } from '@angular/core';
import { Access } from './models';

const TOKEN_KEY = 'scada-darbox.session';

/**
 * The signed-in session: its token and what the gateway says this user may do.
 *
 * What is shown or hidden from this is a convenience only. The gateway checks every
 * request itself (ADR-0011), so a button hidden here is a courtesy, never the protection.
 */
@Injectable({ providedIn: 'root' })
export class Auth {
  private readonly storedToken = signal<string | null>(readToken());

  /** The session token, or null when nobody is signed in. */
  readonly token = this.storedToken.asReadonly();

  /** The user's current roles, as last reported by the gateway. */
  readonly access = signal<Access | null>(null);

  readonly signedIn = computed(() => this.token() !== null && this.access() !== null);

  readonly isAdmin = computed(() => this.access()?.isAdmin ?? false);

  canView(siteId: string | null): boolean {
    const access = this.access();
    if (!access) {
      return false;
    }

    return access.isAdmin || (siteId !== null && access.sites.some((grant) => grant.siteId === siteId));
  }

  /** Whether the user may write tags, acknowledge and shelve alarms on this Site. */
  canOperate(siteId: string | null): boolean {
    const access = this.access();
    if (!access) {
      return false;
    }

    return (
      access.isAdmin ||
      (siteId !== null && access.sites.some((grant) => grant.siteId === siteId && grant.role === 'Operator'))
    );
  }

  begin(token: string, access: Access): void {
    writeToken(token);
    this.storedToken.set(token);
    this.access.set(access);
  }

  /** Forgets the session locally. Ending it on the gateway is the logout request's job. */
  end(): void {
    writeToken(null);
    this.storedToken.set(null);
    this.access.set(null);
  }
}

// Storage can be unavailable (private windows, blocked site data); a session that cannot
// be remembered simply lasts as long as the page.
function readToken(): string | null {
  try {
    return localStorage.getItem(TOKEN_KEY);
  } catch {
    return null;
  }
}

function writeToken(token: string | null): void {
  try {
    if (token === null) {
      localStorage.removeItem(TOKEN_KEY);
    } else {
      localStorage.setItem(TOKEN_KEY, token);
    }
  } catch {
    // See readToken.
  }
}
