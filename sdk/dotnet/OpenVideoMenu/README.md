# OpenVideoMenu for .NET

Read, write and validate [Open Video Menu](https://openvideomenu.org) manifests, pack and read
`.ovmz` bundles, and run menu navigation. Checked against the format's conformance suite, the
same cases as the reference TypeScript implementation.

```csharp
using OpenVideoMenu;

var result = Manifest.Load("The Film (1999).openvideomenu.json");   // or a .ovmz bundle
if (!result.Ok) foreach (var e in result.Errors) Console.WriteLine(e);

var manifest = result.Manifest!;
Bundle.Pack(manifest, "path/to/art", "The Film (1999).ovmz");
```

Licensed under Apache-2.0. The format itself is CC BY 4.0 with a royalty-free patent pledge.
