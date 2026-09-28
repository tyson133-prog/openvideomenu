# Governance

Open Video Menu (working name during development: MenuStream) is an open format for disc-style menus over
existing video files. This document says who decides what, and how that will change as the project grows.
There is no foundation or company behind it; it doesn't need one yet.

## Roles

**Editor.** Tyson Treasure, who created the format, is its editor. The editor:

- decides which proposals go into the specification,
- publishes each numbered version of the specification (0.2, 0.3, … 1.0) at
  [openvideomenu.org](https://openvideomenu.org), the canonical home of the spec,
- maintains the conformance suite, which defines what "compatible" means (see [NAMING.md](NAMING.md)),
- keeps the [compatibility charter](spec/COMPATIBILITY.md), which binds the editor too.

**Contributors.** Anyone who opens an issue, proposes a change, writes an implementation or sends a pull
request. See [CONTRIBUTING.md](CONTRIBUTING.md).

**Implementers.** Anyone who builds a player, server, importer or tool that reads or writes the format. No
permission or fee is needed (see [spec/LICENSE.md](spec/LICENSE.md)).

## How the format changes

1. **Issue.** Describe the problem: what a disc (or an author) needs that the format can't express, or where
   the spec is unclear.
2. **Proposal.** A pull request against `spec/` that states the change, why it's needed, how it fits the
   compatibility charter, and the conformance cases that pin it down. A change without cases isn't finished.
3. **Discussion** happens in the open, on the pull request.
4. **Decision.** The editor accepts, asks for changes, or declines, with reasons written on the pull request.
5. **Release.** Accepted changes ship in the next numbered version. Only versions published at
   openvideomenu.org are Open Video Menu versions.

The editor aims to decide by what serves viewers and implementers, and to follow sources faithfully: menus
should behave as close to their source (a disc, a publisher's design) as the format allows.

## Growing up

This is a single-editor project today, as many formats begin. When there are several independent
implementations in use (for example two media servers or players from different organisations), the editor
will propose a small steering group drawn from implementers, with its process written here. The
specification and conformance suite may then move to a neutral steward (a fiscal sponsor or foundation). The
licenses and the charter are chosen so that move needs no one's permission.

If the editor steps down, they name a successor or hand the project to such a steward, and say so here.

## Code of conduct

Be kind, assume good faith, and keep discussion about the work. The editor may remove comments or contributors
that make the project worse for others.
