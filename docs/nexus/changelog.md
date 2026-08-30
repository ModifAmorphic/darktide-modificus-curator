<!-- converter_ignore -->
# Nexus Mods changelog

Source of truth for Modificus Curator release notes published on Nexus Mods.

Publish: markdown_to_bbcodenm -i docs/nexus/changelog.md -o <out>, then paste the output into the Nexus editor's BBCode source view. This preamble is wrapped in converter_ignore fences so it never reaches the page.

<!-- /converter_ignore -->
# Version 1.2.0

## Features / Improvements

- Added **Import mod list** to the Mods page for compatible line-format mod lists such as `mod_load_order.txt`.
  - **Reorder mods** applies the file's order to mods already in the active profile without importing anything.
  - **Reorder and import mods** reuses mods already in Curator and imports matching mod folders found beside the list. Local folders can be associated with their Nexus entries, while Premium users can also identify and queue missing Nexus mods for download.
  - Matches, skipped entries, versions, and planned actions can be reviewed before applying. Optional additions can be skipped.
- Added **Edit import details** to mod rows. It can correct a manually imported mod's name, Nexus association, and release version.
  - Nexus-associated local imports with no known release are marked as version unknown and can be resolved with the normal update action.
- Added **Clone profile**. Clones include profile details, launch settings, mods, enabled states, load order, order locks, and version choices. The clone is independent, while mod files remain shared and do not need to be downloaded again.
- Updated Mod Relay to load one mod per game update instead of loading the full mod list in one shot.
  - This more closely matches the established community mod loader's pacing and gives the game and earlier mods time to initialize between loads.

## Bug Fixes

- Fixed downloaded Nexus updates not becoming current when the previous version was imported manually. The downloaded update now remains current and is no longer deleted during startup cleanup.
- Fixed Nexus thumbnails being cropped in Detailed view. They now use responsive 16:9 frames and show the complete image.
