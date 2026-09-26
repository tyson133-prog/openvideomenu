import { Bonjour } from "bonjour-service";
import { API_VERSION } from "./server.js";

/** DNS-SD service type TV apps browse for: _menustream._tcp */
export const SERVICE_TYPE = "menustream";

/**
 * Announce the server on the home network (mDNS/DNS-SD) so TV apps find it without typing an IP.
 * In Docker this needs host networking; otherwise apps fall back to a manually entered address.
 */
export function advertise(port: number, name: string): () => Promise<void> {
  const bonjour = new Bonjour();
  const service = bonjour.publish({ name, type: SERVICE_TYPE, port, txt: { api: API_VERSION, path: "/v1" } });
  return () =>
    new Promise((resolve) => {
      service.stop?.(() => bonjour.destroy(() => resolve()));
    });
}
