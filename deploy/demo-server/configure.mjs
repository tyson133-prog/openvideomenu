#!/usr/bin/env node
/**
 * First-run setup of the demo's Jellyfin, through its API: the admin account, the Movies and TV
 * libraries over /media, and the review account (a plain user) that store reviewers sign in with.
 *
 *   OVM_DEMO_ADMIN_PASSWORD=… OVM_DEMO_REVIEW_PASSWORD=… node configure.mjs [--url http://127.0.0.1:18096]
 *
 * Run it before the site is public (through an SSH tunnel: ssh -L 18096:127.0.0.1:8096 …), so no
 * one else can finish Jellyfin's setup wizard first. Passwords come from the environment and are
 * never printed. Prints the review account's name and the IDs to give as deep-link test values.
 */
const arg = (name, fallback) => {
  const i = process.argv.indexOf(name);
  return i >= 0 && process.argv[i + 1] ? process.argv[i + 1] : fallback;
};
const base = arg("--url", "http://127.0.0.1:18096");
const adminPassword = process.env.OVM_DEMO_ADMIN_PASSWORD;
const reviewPassword = process.env.OVM_DEMO_REVIEW_PASSWORD;
if (!adminPassword || !reviewPassword) throw new Error("Set OVM_DEMO_ADMIN_PASSWORD and OVM_DEMO_REVIEW_PASSWORD.");

const client = 'MediaBrowser Client="ovm-demo-setup", Device="setup", DeviceId="ovm-demo-setup", Version="1.0"';
let token = "";
async function call(method, path, body) {
  const auth = token ? `${client}, Token="${token}"` : client;
  const res = await fetch(base + path, {
    method,
    headers: { Authorization: auth, "Content-Type": "application/json" },
    body: body === undefined ? undefined : JSON.stringify(body),
  });
  if (!res.ok) throw new Error(`${method} ${path}: HTTP ${res.status} ${await res.text()}`);
  const text = await res.text();
  return text ? JSON.parse(text) : undefined;
}

const info = await call("GET", "/System/Info/Public");
if (info.StartupWizardCompleted) {
  console.log("Setup wizard already done; signing in as admin.");
} else {
  await call("POST", "/Startup/Configuration", { UICulture: "en-US", MetadataCountryCode: "US", PreferredMetadataLanguage: "en" });
  await call("GET", "/Startup/User");
  await call("POST", "/Startup/User", { Name: "admin", Password: adminPassword });
  await call("POST", "/Startup/RemoteAccess", { EnableRemoteAccess: true, EnableAutomaticPortMapping: false });
  await call("POST", "/Startup/Complete");
  console.log("Setup wizard done.");
}
token = (await call("POST", "/Users/AuthenticateByName", { Username: "admin", Pw: adminPassword })).AccessToken;

await call("POST", "/System/Configuration", { ...(await call("GET", "/System/Configuration")), ServerName: "Open Video Menu demo" });

// Libraries. Image extraction is off: the host is small, and the demo needs none of it.
const options = {
  EnableTrickplayImageExtraction: false,
  ExtractTrickplayImagesDuringLibraryScan: false,
  EnableChapterImageExtraction: false,
  ExtractChapterImagesDuringLibraryScan: false,
  EnableRealtimeMonitor: false,
};
// The Example Show is made up: online metadata would match it to some real show and rename it, so
// the TV library reads only what's on disk. Sintel is real, and gets its poster and details online.
const localOnly = { ...options, TypeOptions: ["Series", "Season", "Episode"].map((Type) => ({ Type, MetadataFetchers: [], ImageFetchers: [] })) };
const libraries = [["Movies", "movies", "/media/Movies", options], ["TV", "tvshows", "/media/TV", localOnly]];
const existing = await call("GET", "/Library/VirtualFolders");
for (const [name, type, path, libraryOptions] of libraries) {
  const folder = existing.find((f) => f.Name === name);
  if (folder) {
    await call("POST", "/Library/VirtualFolders/LibraryOptions", { Id: folder.ItemId, LibraryOptions: { ...folder.LibraryOptions, ...libraryOptions } });
    continue;
  }
  const q = new URLSearchParams({ name, collectionType: type, refreshLibrary: "false" });
  q.append("paths", path);
  await call("POST", `/Library/VirtualFolders?${q}`, { LibraryOptions: libraryOptions });
  console.log(`Library ${name} added.`);
}

// The review account: an ordinary user who can see and play everything here, and change nothing.
const users = await call("GET", "/Users");
let review = users.find((u) => u.Name === "review");
if (!review) {
  review = await call("POST", "/Users/New", { Name: "review", Password: reviewPassword });
  console.log("User review added.");
} else {
  await call("POST", `/Users/${review.Id}/Password`, { NewPw: reviewPassword });
  console.log("User review: password set.");
}
const policy = (await call("GET", `/Users/${review.Id}`)).Policy;
await call("POST", `/Users/${review.Id}/Policy`, {
  ...policy,
  IsAdministrator: false,
  EnableAllFolders: true,
  EnableContentDeletion: false,
  EnableRemoteAccess: true,
  EnableMediaPlayback: true,
  EnableUserPreferenceAccess: false,
});

await call("POST", "/Library/Refresh");
console.log("Library scan started. Once it's done, the titles' IDs (for deep-link testing):");
for (let i = 0; i < 30; i++) {
  await new Promise((r) => setTimeout(r, 5000));
  const items = await call("GET", `/Items?userId=${review.Id}&recursive=true&includeItemTypes=Movie,Series,Episode&fields=Path`);
  if (items.Items.some((it) => it.Type === "Movie") && items.Items.some((it) => it.Type === "Series")) {
    for (const it of items.Items) console.log(`  ${it.Type.padEnd(8)} ${it.Id}  ${it.Name}`);
    process.exit(0);
  }
}
console.log("  (the scan is still running; run this again later to list them)");
