namespace OpenVideoMenu;

/// <summary>Names and versions of the format. Mirrors packages/core/src/constants.ts.</summary>
public static class Format
{
    /// <summary>The format's name, for people.</summary>
    public const string Name = "Open Video Menu";

    /// <summary>The value of a manifest's <c>format</c> field.</summary>
    public const string Id = "openvideomenu";

    /// <summary>The version this library reads and writes (MAJOR.MINOR).</summary>
    public const string Version = "0.1";

    /// <summary>A loose manifest's file name ends with this: <c>&lt;Title&gt;.openvideomenu.json</c>.</summary>
    public const string ManifestSuffix = ".openvideomenu.json";

    /// <summary>A bundle's file name ends with this: <c>&lt;Title&gt;.ovmz</c> (spec §2.1).</summary>
    public const string BundleExtension = ".ovmz";

    /// <summary>The manifest's name inside a bundle.</summary>
    public const string BundleManifestName = "manifest.json";

    /// <summary>The JSON Schema's id.</summary>
    public const string SchemaId = "urn:openvideomenu:schema:manifest:0.1";

    /// <summary>Whether a file name is a loose manifest's (any case).</summary>
    public static bool IsManifestFileName(string name) => name.EndsWith(ManifestSuffix, StringComparison.OrdinalIgnoreCase);

    /// <summary>Whether a file name is a bundle's (any case).</summary>
    public static bool IsBundleFileName(string name) => name.EndsWith(BundleExtension, StringComparison.OrdinalIgnoreCase);
}
