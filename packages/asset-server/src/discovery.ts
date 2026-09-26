import { networkInterfaces } from "node:os";
import { Bonjour } from "bonjour-service";
import { API_VERSION } from "./server.js";

/** DNS-SD service type TV apps browse for: _menustream._tcp */
export const SERVICE_TYPE = "menustream";

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
