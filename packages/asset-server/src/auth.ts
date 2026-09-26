import { createHash, randomUUID } from "node:crypto";

/**
 * True for addresses on a home network or VPN: loopback, RFC 1918, link-local, IPv6 unique-local,
 * and 100.64.0.0/10 (carrier-grade NAT, which Tailscale uses). The server refuses everything else.
 */
export function isLocalAddress(address: string | undefined): boolean {
  if (!address) return false;
  let ip = address.toLowerCase();
  if (ip.startsWith("::ffff:")) ip = ip.slice(7); // IPv4-mapped IPv6
  if (ip === "::1") return true;
  if (ip.includes(":")) return /^f[cd][0-9a-f]{2}:/.test(ip) || /^fe[89ab][0-9a-f]:/.test(ip);
  const parts = ip.split(".").map(Number);
  if (parts.length !== 4 || parts.some((p) => !Number.isInteger(p) || p < 0 || p > 255)) return false;
  const [a, b] = parts as [number, number, number, number];
  return (
    a === 127 ||
    a === 10 ||
    (a === 172 && b >= 16 && b <= 31) ||
    (a === 192 && b === 168) ||
    (a === 169 && b === 254) ||
    (a === 100 && b >= 64 && b <= 127)
  );
}

export interface TokenCheck {
  /** Resolves true when the token may read this server's menus. */
  allows(token: string | undefined): Promise<boolean>;
  readonly description: string;
}

/** For local testing only: no token needed. */
export class NoAuth implements TokenCheck {
  readonly description = "none (testing only)";
  async allows() {
    return true;
  }
}

type Fetch = typeof fetch;

/**
 * "If you can use this Plex server, you can read its menus." The caller's token is checked against
 * plex.tv: it must list a resource whose clientIdentifier is this Plex server's machineIdentifier.
 * That covers owners, shared users and managed users. Just calling the Plex server with the token
 * isn't enough: servers set to allow LAN access without auth accept any token.
 *
 * Nothing is stored. Results are cached in memory by token hash (allowed: 10 min, refused: 30 s).
 */
export class PlexTokenCheck implements TokenCheck {
  readonly description: string;
  private machineId?: string;
  private readonly cache = new Map<string, { ok: boolean; until: number }>();
  private readonly clientId = `menustream-asset-server-${randomUUID()}`;

  constructor(
    private readonly plexUrl: string,
    private readonly fetchImpl: Fetch = fetch,
    private readonly now: () => number = Date.now,
  ) {
    this.description = `Plex token (server ${plexUrl})`;
  }

  private headers(token?: string): Record<string, string> {
    return {
      Accept: "application/json",
      "X-Plex-Product": "MenuStream Asset Server",
      "X-Plex-Version": "0.1",
      "X-Plex-Client-Identifier": this.clientId,
      ...(token ? { "X-Plex-Token": token } : {}),
    };
  }

  /** The Plex server's machineIdentifier (its /identity endpoint needs no token). */
  async serverId(): Promise<string> {
    if (this.machineId) return this.machineId;
    const res = await this.fetchImpl(new URL("/identity", this.plexUrl), { headers: this.headers() });
    if (!res.ok) throw new Error(`Plex server ${this.plexUrl} answered ${res.status} for /identity`);
    const body = (await res.json()) as { MediaContainer?: { machineIdentifier?: string } };
    const id = body.MediaContainer?.machineIdentifier;
    if (!id) throw new Error(`Plex server ${this.plexUrl} didn't report a machineIdentifier`);
    this.machineId = id;
    return id;
  }

  async allows(token: string | undefined): Promise<boolean> {
    if (!token) return false;
    const key = createHash("sha256").update(token).digest("hex");
    const hit = this.cache.get(key);
    if (hit && hit.until > this.now()) return hit.ok;

    let ok = false;
    try {
      const machineId = await this.serverId();
      const res = await this.fetchImpl("https://plex.tv/api/v2/resources?includeHttps=1", { headers: this.headers(token) });
      if (res.ok) {
        const resources = (await res.json()) as { clientIdentifier?: string; provides?: string }[];
        ok = Array.isArray(resources) && resources.some((r) => r.clientIdentifier === machineId);
      }
    } catch {
      ok = false; // plex.tv or the server unreachable: refuse, but only cache briefly
    }
    this.cache.set(key, { ok, until: this.now() + (ok ? 10 * 60_000 : 30_000) });
    return ok;
  }
}
