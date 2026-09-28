import { networkInterfaces } from "node:os";
import { Bonjour, type Service } from "bonjour-service";
import { API_VERSION } from "./server.js";

/** DNS-SD service type TV apps browse for: _openvideomenu._tcp */
export const SERVICE_TYPE = "openvideomenu";

/** Non-internal IPv4 addresses of this machine (LAN adapters, but also VPN/virtual ones). */
export function localIPv4s(): string[] {
  return Object.values(networkInterfaces())
    .flat()
    .filter((i): i is NonNullable<typeof i> => !!i && i.family === "IPv4" && !i.internal)
    .map((i) => i.address);
}

/**
 * Announce the server on the home network (mDNS/DNS-SD) so TV apps find it without typing an IP.
 *
 * Machines often have several adapters (Ethernet plus VMware/WSL/Docker/VPN). Multicast from a
 * single responder goes out whichever adapter the OS prefers, which may not be the TV's network, so
 * one responder is bound to each IPv4 adapter. Apps try the advertised addresses until one answers.
 * In Docker this needs host networking; otherwise apps fall back to a manually entered address.
 */
export function advertise(port: number, name: string): () => Promise<void> {
  const addresses = localIPv4s();
  const responders = (addresses.length > 0 ? addresses : [undefined]).map((ip) => {
    // bonjour-service passes these options to multicast-dns, whose `interface` option its types omit.
    const bonjour = new Bonjour((ip ? { interface: ip } : {}) as ConstructorParameters<typeof Bonjour>[0]);
    const service = bonjour.publish({ name, type: SERVICE_TYPE, port, txt: { api: API_VERSION, path: "/v1" } });
    answerLegacyUnicast(bonjour, service);
    return { bonjour, service };
  });
  return () =>
    Promise.all(
      responders.map(
        ({ bonjour, service }) =>
          new Promise<void>((resolve) => {
            service.stop?.(() => bonjour.destroy(() => resolve()));
          }),
      ),
    ).then(() => {});
}

/** A DNS record as multicast-dns (dns-packet) encodes it. */
export interface DnsRecord {
  name: string;
  type: string;
  ttl?: number;
  flush?: boolean;
  data?: unknown;
}

export interface DnsQuery {
  id?: number;
  questions: { name: string; type: string; class?: string }[];
}

/**
 * The reply to a legacy unicast query (RFC 6762 §6.7): a query from a port other than 5353, from a
 * simple resolver that can't join the multicast group (a Roku app, for one). The reply goes back
 * to that port, repeats the query's ID and questions, and carries short TTLs without the
 * cache-flush bit. Returns undefined when nothing here answers the questions.
 */
export function legacyUnicastReply(query: DnsQuery, records: DnsRecord[]) {
  const legacy = (r: DnsRecord): DnsRecord => ({ ...r, flush: false, ttl: Math.min(r.ttl ?? 10, 10) });
  const answers: DnsRecord[] = [];
  const additionals: DnsRecord[] = [];
  for (const q of query.questions) {
    const matched = records.filter((r) => r.name.toLowerCase() === q.name.toLowerCase() && (q.type === "ANY" || r.type === q.type));
    answers.push(...matched);
    // As a multicast answer would: a PTR brings its SRV and TXT, an SRV its host's addresses.
    const instances = matched.filter((r) => r.type === "PTR").map((r) => String(r.data));
    const extra = records.filter((r) => instances.includes(r.name) && (r.type === "SRV" || r.type === "TXT"));
    const hosts = [...extra, ...matched].filter((r) => r.type === "SRV").map((r) => (r.data as { target: string }).target);
    extra.push(...records.filter((r) => hosts.includes(r.name) && (r.type === "A" || r.type === "AAAA")));
    for (const r of extra) if (!answers.includes(r) && !additionals.includes(r)) additionals.push(r);
  }
  if (answers.length === 0) return undefined;
  return { id: query.id ?? 0, questions: query.questions, answers: answers.map(legacy), additionals: additionals.map(legacy) };
}

/**
 * bonjour-service answers every query on the multicast group, which a legacy unicast resolver
 * never hears; answer those directly as well. (Apple's mDNSResponder and Avahi already do.)
 */
function answerLegacyUnicast(bonjour: Bonjour, service: Service) {
  // bonjour-service keeps its multicast-dns instance on a field its types mark private.
  const mdns = (bonjour as unknown as { server: { mdns: MulticastDns } }).server.mdns;
  mdns.on("query", (query: DnsQuery, rinfo: { address: string; port: number }) => {
    if (rinfo.port === 5353) return;
    const reply = legacyUnicastReply(query, service.records() as DnsRecord[]);
    if (reply) mdns.respond(reply, { address: rinfo.address, port: rinfo.port });
  });
}

interface MulticastDns {
  on(event: "query", listener: (query: DnsQuery, rinfo: { address: string; port: number }) => void): void;
  respond(response: object, rinfo: { address: string; port: number }): void;
}
