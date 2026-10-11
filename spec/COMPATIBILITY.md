# Compatibility charter

> In effect from **v1.0** (released as 1.0 preview). The v0.x drafts before it are read as 1.0.

Open Video Menu is meant to outlive any one player, tool or maintainer. These rules are permanent from v1.0 onward:

1. **Old manifests keep working.** Every valid manifest from any earlier version MUST keep working in every later player.
2. **Nothing is removed or redefined.** Fields are never removed or renamed, and never change meaning. Minor versions only add optional fields.
3. **Unknown fields are ignored.** Readers MUST ignore fields and extensions they don't understand, so newer manifests still work in older players.
4. **The basic floor never moves.** Every manifest MUST include the `fallback` list. A player that can show that list and play media works with every manifest ever written.
5. **The server API is covered too.** Endpoints and fields of a released API version (e.g. `/v1/`, see [server-api.md](server-api.md)) are never removed or redefined; breaking changes get a new version served alongside the old one.
6. **Extensions first, core later.** New features start as namespaced `x-<vendor>-*` extensions. They join the core only through a public proposal, with conformance tests.

## What "compatible" means

An implementation is compatible when it passes the conformance suite for the level it claims (0, 1, 2 or 3). The name, and any "compatible" badge, is reserved for implementations that pass. (A trademark policy will say this formally.)

The code is Apache-2.0, so forks are welcome. A fork that breaks these rules just can't call itself Open Video Menu compatible.
