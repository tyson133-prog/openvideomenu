/**
 * Every place the format's name appears is defined here, so names change in one edit.
 */
export const FORMAT_NAME = "Open Video Menu";
export const FORMAT_ID = "openvideomenu";
export const FORMAT_VERSION = "1.0";
export const MANIFEST_SUFFIX = ".openvideomenu.json";
export const BUNDLE_EXTENSION = ".ovmz";
export const BUNDLE_MANIFEST_NAME = "manifest.json";
export const SCHEMA_ID = "urn:openvideomenu:schema:manifest:1.0";

/** Whether a file name is a manifest's (any case). */
export function isManifestFileName(name: string): boolean {
  return name.toLowerCase().endsWith(MANIFEST_SUFFIX);
}

/** Whether a file name is a bundle's, `<Title>.ovmz` (any case). Spec §2.1. */
export function isBundleFileName(name: string): boolean {
  return name.toLowerCase().endsWith(BUNDLE_EXTENSION);
}

/** Whether a manifest's `format` value is this format. */
export function isFormatId(value: unknown): boolean {
  return value === FORMAT_ID;
}
