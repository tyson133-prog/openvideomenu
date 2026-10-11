// The installer's helper: the work that's easier in JavaScript than in Inno Setup's Pascal (talking to Plex and
// Jellyfin, JSON, file permissions, the service, the firewall). It runs on the Node.js the installer ships, so setup
// needs nothing else from Windows: no PowerShell, no .NET. The installer runs one command at a time:
//
//   node.exe setup-helper.mjs <command> <in file> <out file>
//
// The in file is KEY=VALUE lines the installer writes (so secrets never appear on a command line); the out file is
// where the result goes, one line per value. The exit code is 0 on success; on failure the first line of the out
// file is the reason.
import { execFileSync } from "node:child_process";
import { existsSync, mkdirSync, readFileSync, writeFileSync } from "node:fs";
import { join } from "node:path";
import { setTimeout as sleep } from "node:timers/promises";

const [command, inFile, outFile] = process.argv.slice(2);

const ServiceId = "OpenVideoMenuServer";
const RuleName = "Open Video Menu menu server";
const DataDir = join(process.env.ProgramData ?? "C:\\ProgramData", "Open Video Menu");
const ConfigFile = join(DataDir, "menu-server.env");
const LogDir = join(DataDir, "logs");
const ClientName = "Open Video Menu menu server";
const System32 = join(process.env.SystemRoot ?? "C:\\Windows", "System32");

class Failure extends Error {}

function readInput() {
  const values = {};
  if (inFile && existsSync(inFile)) {
    for (const line of readFileSync(inFile, "utf8").replace(/^\uFEFF/, "").split(/\r?\n/)) {
      const i = line.indexOf("=");
      if (i > 0) values[line.slice(0, i)] = line.slice(i + 1);
    }
  }
  return values;
}

function writeOutput(lines) {
  if (outFile) writeFileSync(outFile, lines.join("\r\n") + "\r\n", "utf8");
}

// A Windows tool from System32. Returns its output; throws with its output when it fails, unless allowed to.
function run(tool, args, { allowFailure = false } = {}) {
  try {
    return execFileSync(join(System32, tool), args, { encoding: "utf8", windowsHide: true, stdio: ["ignore", "pipe", "pipe"] });
  } catch (e) {
    if (allowFailure) return null;
    const said = `${e.stdout ?? ""}${e.stderr ?? ""}`.trim();
    throw new Failure(`${tool} failed${said ? `: ${said}` : ` (${e.status ?? e.message})`}`);
  }
}

async function getJson(url, { method = "GET", headers = {}, body, timeoutSec = 15 } = {}) {
  const r = await fetch(url, {
    method,
    headers: { Accept: "application/json", ...(body ? { "Content-Type": "application/json" } : {}), ...headers },
    body: body ? JSON.stringify(body) : undefined,
    signal: AbortSignal.timeout(timeoutSec * 1000),
  });
  if (!r.ok) throw new Failure(`${new URL(url).host} answered ${r.status} ${r.statusText}`);
  const text = await r.text();
  return text ? JSON.parse(text) : null;
}

// Plex's headers for a client: who is asking, and the token when there is one.
function plexHeaders(clientId, token) {
  const h = { "X-Plex-Product": ClientName, "X-Plex-Client-Identifier": clientId };
  if (token) h["X-Plex-Token"] = token;
  return h;
}

function jellyfinAuth(token) {
  let a = `MediaBrowser Client="${ClientName}", Device="${process.env.COMPUTERNAME ?? "PC"}", DeviceId="ovm-menu-server-setup", Version="1.0"`;
  if (token) a += `, Token="${token}"`;
  return { Authorization: a };
}

// Registry values as reg.exe lists them: { "<key>": { "<name>": "<value>" } }.
function regQuery(args) {
  const out = run("reg.exe", ["query", ...args], { allowFailure: true });
  const keys = {};
  let key = null;
  for (const line of (out ?? "").split(/\r?\n/)) {
    if (/^HKEY_/.test(line)) { key = line.trim(); keys[key] = {}; continue; }
    const m = /^\s{4}(.+?)\s{4}REG_\w+\s{4}(.*)$/.exec(line);
    if (key && m) keys[key][m[1]] = m[2];
  }
  return keys;
}

// A local folder on a drive letter mapped to a share (from the installing user's persistent mappings, which an
// elevated installer can't see any other way) becomes the share's \\server\share path.
function mappedDrives() {
  const map = {};
  for (const [key, values] of Object.entries(regQuery(["HKCU\\Network", "/s", "/v", "RemotePath"]))) {
    if (values.RemotePath) map[key.split("\\").pop().toUpperCase()] = values.RemotePath;
  }
  return map;
}

function serviceExists() {
  return run("sc.exe", ["query", ServiceId], { allowFailure: true }) !== null;
}

// Only these accounts, nothing inherited: the settings hold a Plex token or a Jellyfin API key.
function grant(path, grants) {
  run("icacls.exe", [path, "/inheritance:r", ...grants.flatMap((g) => ["/grant:r", g])]);
}

function xmlEscape(s) {
  return s.replace(/&/g, "&amp;").replace(/</g, "&lt;").replace(/>/g, "&gt;").replace(/"/g, "&quot;").replace(/'/g, "&apos;");
}

function splitAccount(account) {
  const i = account.indexOf("\\");
  return i >= 0 ? { domain: account.slice(0, i), user: account.slice(i + 1) } : { domain: ".", user: account };
}

const commands = {
  async "plex-local-token"() {
    // Plex on this PC keeps its sign-in in the user's registry.
    const values = Object.values(regQuery(["HKCU\\Software\\Plex, Inc.\\Plex Media Server", "/v", "PlexOnlineToken"]))[0];
    if (!values?.PlexOnlineToken) throw new Failure("Plex isn't signed in on this PC.");
    return [values.PlexOnlineToken];
  },

  async "plex-pin-start"(v) {
    const r = await getJson("https://plex.tv/api/v2/pins?strong=false", { method: "POST", headers: plexHeaders(v.CLIENT_ID) });
    return [String(r.id), String(r.code)];
  },

  async "plex-pin-check"(v) {
    const r = await getJson(`https://plex.tv/api/v2/pins/${v.PIN_ID}`, { headers: plexHeaders(v.CLIENT_ID) });
    if (!r.authToken) throw new Failure("The code hasn't been entered at plex.tv/link yet.");
    return [String(r.authToken)];
  },

  async "plex-folders"(v) {
    const r = await getJson(v.URL.replace(/\/+$/, "") + "/library/sections", { headers: plexHeaders(v.CLIENT_ID, v.TOKEN) });
    const paths = [];
    for (const d of r?.MediaContainer?.Directory ?? []) {
      if (d.type === "movie" || d.type === "show") for (const l of d.Location ?? []) paths.push(String(l.path));
    }
    if (paths.length === 0) throw new Failure("Plex has no movie or TV libraries.");
    return [...new Set(paths)];
  },

  async "jellyfin-key"(v) {
    const base = v.URL.replace(/\/+$/, "");
    const auth = await getJson(`${base}/Users/AuthenticateByName`, {
      method: "POST", headers: jellyfinAuth(), body: { Username: v.USER, Pw: v.PASSWORD },
    });
    if (!auth?.User?.Policy?.IsAdministrator) throw new Failure(`${v.USER} isn't an administrator of this Jellyfin server.`);
    const h = jellyfinAuth(auth.AccessToken);
    const app = "Open Video Menu menu server";
    await getJson(`${base}/Auth/Keys?app=${encodeURIComponent(app)}`, { method: "POST", headers: h });
    const keys = await getJson(`${base}/Auth/Keys`, { headers: h });
    const key = (keys?.Items ?? []).filter((k) => k.AppName === app)
      .sort((a, b) => String(b.DateCreated).localeCompare(String(a.DateCreated)))[0];
    if (!key) throw new Failure("Jellyfin didn't create the API key.");
    return [String(key.AccessToken)];
  },

  async "jellyfin-folders"(v) {
    const r = await getJson(v.URL.replace(/\/+$/, "") + "/Library/VirtualFolders", { headers: jellyfinAuth(v.TOKEN) });
    const paths = [];
    for (const f of r ?? []) {
      if ([undefined, null, "movies", "tvshows", "mixed"].includes(f.CollectionType)) paths.push(...(f.Locations ?? []).map(String));
    }
    if (paths.length === 0) throw new Failure("Jellyfin has no movie or TV libraries.");
    return [...new Set(paths)];
  },

  async classify(v) {
    // One line per folder from the input (FOLDER1=…, FOLDER2=…): "share|<path>", "mapped|<path>|<\\server\share>"
    // or "local|<path>". Folders that look like another OS's paths (/data/movies) are "other".
    const drives = mappedDrives();
    return Object.keys(v).filter((k) => /^FOLDER\d+$/.test(k)).sort((a, b) => Number(a.slice(6)) - Number(b.slice(6)))
      .map((k) => {
        const p = v[k];
        if (p.startsWith("\\\\")) return `share|${p}`;
        const m = /^([A-Za-z]):/.exec(p);
        if (m) {
          const letter = m[1].toUpperCase();
          return drives[letter] ? `mapped|${p}|${drives[letter]}` : `local|${p}`;
        }
        return `other|${p}`;
      });
  },

  async "write-config"(v) {
    // Every setting from the input except the account goes into the server's settings file.
    mkdirSync(LogDir, { recursive: true });
    const account = v.ACCOUNT || "NT AUTHORITY\\LocalService";
    const lines = ["# Open Video Menu menu server settings, written by its installer. Run the installer again to change them."];
    for (const k of ["PLEX_URL", "PLEX_TOKEN", "JELLYFIN_URL", "JELLYFIN_API_KEY", "MEDIA_ROOTS", "PATH_MAP", "SERVER_NAME", "PORT"]) {
      if (v[k]) lines.push(`${k}=${v[k]}`);
    }
    writeFileSync(ConfigFile, lines.join("\r\n") + "\r\n", "utf8");
    grant(ConfigFile, ["*S-1-5-18:F", "*S-1-5-32-544:F", `${account}:R`]);
    grant(LogDir, ["*S-1-5-18:(OI)(CI)F", "*S-1-5-32-544:(OI)(CI)F", `${account}:(OI)(CI)M`]);
    return [ConfigFile];
  },

  async "install-service"(v) {
    const exe = join(v.APP_DIR, `${ServiceId}.exe`);
    const xml = join(v.APP_DIR, `${ServiceId}.xml`);
    const template = readFileSync(join(v.APP_DIR, `${ServiceId}.template.xml`), "utf8").replace(/^\uFEFF/, "");
    const winsw = (verb, opts) => {
      try { execFileSync(exe, [verb], { windowsHide: true, stdio: "pipe" }); return true; }
      catch (e) {
        if (opts?.allowFailure) return false;
        const said = `${e.stdout ?? ""}${e.stderr ?? ""}`.trim();
        throw new Failure(`The service wrapper couldn't ${verb} the service${said ? `: ${said}` : ` (exit code ${e.status})`}.`);
      }
    };
    // Stop and remove an earlier install first (running setup again changes the settings).
    if (serviceExists()) {
      winsw("stop", { allowFailure: true });
      winsw("uninstall", { allowFailure: true });
      await sleep(2000);
    }
    let account = "";
    if (v.ACCOUNT) {
      const { domain, user } = splitAccount(v.ACCOUNT);
      // WinSW reads the password only while installing (and grants "Log on as a service"); Windows' service manager
      // keeps it from then on. The file is rewritten without it right after.
      account = `<serviceaccount><domain>${xmlEscape(domain)}</domain><user>${xmlEscape(user)}</user>` +
        `<password>${xmlEscape(v.PASSWORD ?? "")}</password><allowservicelogon>true</allowservicelogon></serviceaccount>`;
    }
    writeFileSync(xml, template.replace("<!--ACCOUNT-->", account), "utf8");
    try { winsw("install"); }
    finally { writeFileSync(xml, template.replace("<!--ACCOUNT-->", ""), "utf8"); }
    if (!v.ACCOUNT) run("sc.exe", ["config", ServiceId, "obj=", "NT AUTHORITY\\LocalService", "password=", ""]);
    winsw("start", { allowFailure: true });
    return ["ok"];
  },

  async "remove-service"(v) {
    if (serviceExists()) {
      const exe = join(v.APP_DIR ?? "", `${ServiceId}.exe`);
      if (v.APP_DIR && existsSync(exe)) {
        for (const verb of ["stop", "uninstall"]) {
          try { execFileSync(exe, [verb], { windowsHide: true, stdio: "ignore" }); } catch { /* already stopped or gone */ }
        }
      } else {
        run("sc.exe", ["stop", ServiceId], { allowFailure: true });
        run("sc.exe", ["delete", ServiceId], { allowFailure: true });
      }
    }
    return ["ok"];
  },

  async "firewall-add"(v) {
    run("netsh.exe", ["advfirewall", "firewall", "delete", "rule", `name=${RuleName}`], { allowFailure: true });
    const port = v.PORT || "8790";
    // Home networks only (the Private profile): the server answers local addresses anyway.
    for (const [protocol, localport] of [["TCP", port], ["UDP", "5353"]]) {
      run("netsh.exe", ["advfirewall", "firewall", "add", "rule", `name=${RuleName}`, "dir=in", "action=allow",
        `protocol=${protocol}`, `localport=${localport}`, "profile=private"]);
    }
    return ["ok"];
  },

  async "firewall-remove"() {
    run("netsh.exe", ["advfirewall", "firewall", "delete", "rule", `name=${RuleName}`], { allowFailure: true });
    return ["ok"];
  },

  async health(v) {
    const port = v.PORT || "8790";
    let last = "The menu server didn't answer.";
    // Up to about 30 s: the service starts, then the server reads the libraries and scans them.
    for (let i = 0; i < 15; i++) {
      try {
        const h = await getJson(`http://127.0.0.1:${port}/v1/health`, { timeoutSec: 2 });
        if (h?.ok && !h.scanning) return ["ok", String(h.manifests), String(h.auth)];
        last = "The menu server is still scanning.";
      } catch (e) {
        last = `The menu server didn't answer: ${e.message}`;
      }
      await sleep(1000);
    }
    throw new Failure(last);
  },
};

// Node ends by itself once the answer is written. (process.exit() right after a fetch can trip a libuv assertion on
// Windows and exit with 127, which would read as a failure.)
try {
  const handler = commands[command];
  if (!handler) throw new Failure(`Unknown command: ${command}`);
  writeOutput(await handler(readInput()));
  process.exitCode = 0;
} catch (e) {
  const reason = e instanceof Failure ? e.message
    : e?.name === "TimeoutError" ? "It didn't answer in time."
    : e?.cause?.message ? `${e.message} (${e.cause.message})` : String(e?.message ?? e);
  writeOutput([reason.replace(/\r?\n/g, " ")]);
  process.exitCode = 1;
}
