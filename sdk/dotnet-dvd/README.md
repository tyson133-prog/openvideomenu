# OpenVideoMenu.Dvd: DVD → Open Video Menu for .NET

Reads a DVD (an ISO image or a VIDEO_TS folder) and runs its menus and programs the way a DVD player does. It
translates them into an Open Video Menu manifest over the files you ripped, makes the menu art, and checks the
result against the disc. It's the reference importer for the [DVD profile](../../spec/profiles/dvd.md).

**It's for tools that turn DVDs into bundles:** rippers, media-server plugins, archiving tools. It's here so that
producing Open Video Menu is easy for any developer, which is how a format gets adopted. Players never read
discs: they play bundles with the core `OpenVideoMenu` SDK (`../dotnet`).

**Faithful by default.** One call exports the whole disc's logic, with motion, music and highlights when ffmpeg is
available. Anything less is an explicit choice.

Inputs must be decrypted: the package removes no copy protection. Apache-2.0.

## Quick start

```csharp
using OpenVideoMenu.Dvd.Export;
using OpenVideoMenu.Dvd.Machine;
using OpenVideoMenu.Dvd.Structure;

using var disc = DvdDisc.Open(@"D:\Rips\Heat\Heat.iso");

// The files, one per DVD title as MakeMKV and HandBrake rip them; refs are relative to the bundle.
// null = not ripped: its buttons stay but do nothing.
var files = DvdMedia.OneFilePerTitle(disc, title => $"Heat - title {title}.mkv");

var builder = new MenuManifestBuilder(disc, DiscMapBuilder.Build(disc), files.Media, files.Index,
    new DvdManifestOptions { Title = "Heat", Year = 1995, TrackChoices = true });

var result = await DvdExport.ToBundleAsync(builder, disc, @"D:\Rips\Heat\Heat (1995).ovmz",
    new DvdArtOptions { Decoder = new FfmpegMenuDecoder(@"C:\ffmpeg\bin\ffmpeg.exe") });
foreach (var w in result.Warnings) Console.WriteLine($"[{w.Code}] {w.Message}");
```

Or from the command line: `ovm-dvd export Heat.iso "Heat (1995).ovmz" --files "Heat - title {n}.mkv"`. For a
library's layout, name the feature on its own and give a pattern for each extras folder; the first file that
exists wins:

```
ovm-dvd export Heat.iso "Heat (1995).ovmz" --file "1=Heat (1995).mkv" --files "Featurettes/Title {n}.mkv" --files "Other/Title {n}.mkv"
```

## The layers

Each is public and usable on its own.

| Namespace | What it is |
|---|---|
| `.IO` | `IDvdFileSystem`: ISO (UDF, then ISO 9660) and VIDEO_TS folders built in. `DvdDisc.Open(path, fs)` takes your own: a drive, a stream, an archive, an in-memory image. |
| `.Structure` | `DvdDisc`: the IFO model (titles, PGCs, cells, commands, menu language units, attributes, palettes), buttons from the navigation packs, a cell's sectors. |
| `.Commands` | `DvdCommands`: decode 8-byte commands (`Decode`, a typed `DvdInstruction`), disassemble, evaluate. |
| `.Machine` | `DvdVm`: the disc's programs, stopping where a DVD player waits. `DvdPlayerSettings` holds what players differ in; `Observer` sees every step. `DiscMapBuilder`: every menu, every button's target, the title inventory. |
| `.Export` | `MenuManifestBuilder` (with `DvdManifestOptions`), `DvdMedia` (file layouts), `CellMediaIndex` (any layout), `DvdExport.ToBundleAsync`, `ManifestPasses`. |
| `.Art` | `MenuArtMaker`; `IMenuVideoDecoder` with `FfmpegMenuDecoder` built in; `MenuArtKinds`; the highlight renderer and `RgbaImage` (its own PNG writer). |
| `.Verify` | `MenuVerifier`: random walks of the disc's machine against a navigator over the export, reporting the first difference. `MenuVerifyOptions.Export` checks another exporter's manifest; `.Navigator` checks another navigator. |

Warnings carry a code (`DvdWarning.Code`) as well as a sentence.

**Times.** The disc's own times (`CellPlayback.Duration`, `Pgc.Duration`) are as the IFO states them. On NTSC
they count 30 frames a second, so the video really runs 1001/1000 as long: 8 s more by the end of a film.
`CellMediaIndex.CellsOf` and `CellsOfPgc` place cells on a rip's real clock (`CellMediaIndex.ClockScale`), so
chapters, cues and durations land where they are in the file. A title that lists the same cell more than once
(a clip listed many times for cell commands to pick from) is laid out once per listing, as MakeMKV and
HandBrake rip it; a ripper that keeps each distinct cell once passes `onceEach: true`.

Tracing: `DvdVm.Observer`, `DvdManifestOptions.CompileTrace` and the verifier's `WalkTrace` / `VmTrace` /
`CompileTrace`. In `ovm-dvd`, set `OVM_DVD_TRACE=walks,vm,compile`.

## The tool: `ovm-dvd`

`map`, `nav`, `disasm`, `export`, `verify`, `verify-all`, `stats`, `scan`, `uops`, `modes`, `sets`. Run
`ovm-dvd --help` for each command's options.

`ovm-dvd verify-all <folder> --out <dir>` checks a whole collection:
- each disc runs in its own process, with a timeout;
- every disc's output goes to its own log;
- the summary groups the problems by kind and gives the export sizes.

## Tests and checks

- `dotnet test OpenVideoMenu.Dvd.Tests` runs the unit tests (command decoding, navigation packs, subpictures,
  PNG) and end-to-end tests on a synthetic disc built in memory (`SyntheticDisc`): read, map, run, export,
  verify, bundle.
- Real discs can't be committed. The collection check runs locally with `ovm-dvd verify-all` (DVD Tamer's
  954-disc collection: every disc verifies).

## Status

Split out of DVD Tamer (see [PLAN.md](PLAN.md)). DVD Tamer uses this package, packed into its `local-packages/` by
its `scripts/pack-openvideomenu.ps1`.
