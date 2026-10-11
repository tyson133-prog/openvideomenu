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

/** Who is asking: a sign-in from a Plex or Jellyfin server, as sent by a TV app. */
export interface Credential {
  kind: "plex" | "jellyfin";
  token: string;
}

export interface TokenCheck {
  /** Resolves true when this sign-in may read this server's menus. */
  allows(credential: Credential | undefined): Promise<boolean>;
  readonly description: string;
}

/** Remembers answers in memory by token hash (allowed: 10 min, refused: 30 s). Nothing is stored. */
class Verdicts {
  private readonly cache = new Map<string, { ok: boolean; until: number }>();
  constructor(private readonly now: () => number) {}

  async get(token: string, check: () => Promise<boolean>): Promise<boolean> {
    const key = createHash("sha256").update(token).digest("hex");
    const hit = this.cache.get(key);
    if (hit && hit.until > this.now()) return hit.ok;
    let ok = false;
    try {
      ok = await check();
    } catch {
      ok = false; // server unreachable: refuse, but only cache briefly
    }
    this.cache.set(key, { ok, until: this.now() + (ok ? 10 * 60_000 : 30_000) });
    return ok;
  }
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
 * the Plex server itself: a token it accepts is one of its owner's, shared users' or managed users'
 * (a token for another server isn't accepted). That needs no internet. But a Plex server set to let
 * the home network in without signing in accepts any token, so a made-up token is tried first (every
 * 10 minutes): when that gets in, or the server can't be reached, the token is checked with plex.tv
 * instead, which must list a resource whose clientIdentifier is this server's machineIdentifier.
 *
 * Viewers' tokens are never stored. Results are cached in memory by token hash (allowed: 10 min, refused: 30 s).
 */
export class PlexTokenCheck implements TokenCheck {
  readonly description: string;
  private machineId?: string;
  private readonly verdicts: Verdicts;
  private readonly clientId = `openvideomenu-asset-server-${randomUUID()}`;
  /** Whether the Plex server lets in any token (no sign-in needed on the home network), and until when that's known. */
  private openLan?: { open: boolean; until: number };

  constructor(
    private readonly plexUrl: string,
    private readonly fetchImpl: Fetch = fetch,
    private readonly now: () => number = Date.now,
  ) {
    this.description = `Plex token (server ${plexUrl})`;
    this.verdicts = new Verdicts(now);
  }

  private headers(token?: string): Record<string, string> {
    return {
      Accept: "application/json",
      "X-Plex-Product": "Open Video Menu Asset Server",
      "X-Plex-Version": "1.0.0",
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

  async allows(credential: Credential | undefined): Promise<boolean> {
    if (credential?.kind !== "plex" || !credential.token) return false;
    const token = credential.token;
    return this.verdicts.get(token, async () => (await this.localVerdict(token)) ?? (await this.plexTvVerdict(token)));
  }

  /** The Plex server's answer for this token: undefined when it can't be trusted (open to the LAN) or reached. */
  private async localVerdict(token: string): Promise<boolean | undefined> {
    try {
      if (await this.letsAnyoneIn()) return undefined;
      const status = await this.sectionsStatus(token);
      if (status === 200) return true;
      if (status === 401 || status === 403) return false;
      return undefined;
    } catch {
      return undefined; // unreachable: plex.tv decides
    }
  }

  /** True when the Plex server accepts a made-up token (it lets the home network in without signing in). */
  private async letsAnyoneIn(): Promise<boolean> {
    if (this.openLan && this.openLan.until > this.now()) return this.openLan.open;
    const open = (await this.sectionsStatus(`openvideomenu-probe-${randomUUID()}`)) === 200;
    this.openLan = { open, until: this.now() + 10 * 60_000 };
    return open;
  }

  private async sectionsStatus(token: string): Promise<number> {
    const res = await this.fetchImpl(new URL("/library/sections", this.plexUrl), { headers: this.headers(token), signal: AbortSignal.timeout(5_000) });
    return res.status;
  }

  private async plexTvVerdict(token: string): Promise<boolean> {
    const machineId = await this.serverId();
    const res = await this.fetchImpl("https://plex.tv/api/v2/resources?includeHttps=1", { headers: this.headers(token) });
    if (!res.ok) return false;
    const resources = (await res.json()) as { clientIdentifier?: string; provides?: string }[];
    return Array.isArray(resources) && resources.some((r) => r.clientIdentifier === machineId);
  }
}

/**
 * "If you can use this Jellyfin server, you can read its menus." Jellyfin tokens are issued by the
 * server itself, so the check is simply whether the configured server accepts the token: it answers
 * GET /Users/Me with the signed-in user (Jellyfin 12 needs the MediaBrowser Authorization header).
 * Results are cached in memory by token hash; nothing is stored.
 */
export class JellyfinTokenCheck implements TokenCheck {
  readonly description: string;
  private readonly verdicts: Verdicts;
  private readonly deviceId = `openvideomenu-asset-server-${randomUUID()}`;

  constructor(
    private readonly jellyfinUrl: string,
    private readonly fetchImpl: Fetch = fetch,
    now: () => number = Date.now,
  ) {
    this.description = `Jellyfin token (server ${jellyfinUrl})`;
    this.verdicts = new Verdicts(now);
  }

  async allows(credential: Credential | undefined): Promise<boolean> {
    if (credential?.kind !== "jellyfin" || !credential.token) return false;
    const token = credential.token.replace(/"/g, "");
    return this.verdicts.get(token, async () => {
      const auth = `MediaBrowser Client="Open Video Menu Asset Server", Device="asset-server", DeviceId="${this.deviceId}", Version="1.0.0", Token="${token}"`;
      const res = await this.fetchImpl(new URL("/Users/Me", this.jellyfinUrl), { headers: { Authorization: auth, Accept: "application/json" } });
      if (!res.ok) return false;
      const user = (await res.json()) as { Id?: string };
      return typeof user.Id === "string" && user.Id.length > 0;
    });
  }
}

/** Accepts a sign-in if the check for its kind of server does (e.g. both Plex and Jellyfin configured). */
export class AnyOf implements TokenCheck {
  readonly description: string;
  constructor(private readonly checks: TokenCheck[]) {
    this.description = checks.map((c) => c.description).join(" or ");
  }
  async allows(credential: Credential | undefined): Promise<boolean> {
    for (const c of this.checks) if (await c.allows(credential)) return true;
    return false;
  }
}
