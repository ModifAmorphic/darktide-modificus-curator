<!-- converter_ignore -->
# Nexus Mods page

Source of truth for the Modificus Curator Nexus Mods page description.
Placeholders in [brackets] mark links to fill in.

Images use real markdown syntax. Until the Nexus page exists they point at the
repo copies so this file renders on GitHub; once the images are uploaded to
the Nexus page's image manager, swap each URL for the hosted one (the
markdown-to-BBCode converter turns `![alt](url)` into `[img]url[/img]`
automatically). Keep literal [square-bracket] text out of prose: it passes
through the converter as live BBCode.

Template sections are suggestions only; the page uses its own structure. No
Requirements section: the game is implicit (mods are organized under the
game), Curator has no hard dependencies, and the optional bits (Nexus
account, DMF) are covered in Getting started.

Publish: markdown_to_bbcodenm -i docs/nexus/description.md -o <out>, then
paste into the Nexus editor's BBCode source view. This preamble is wrapped in
converter_ignore fences so it never reaches the page.
<!-- /converter_ignore -->

## Description

**Modificus Curator** is a mod manager for
**Warhammer 40,000: Darktide**, for Windows, Linux, and the Steam Deck.
Source code and releases: [Curator GitHub repository](https://github.com/ModifAmorphic/darktide-modificus-curator).

Curator loads mods into Darktide via dll injection with [Mod Relay](https://github.com/ModifAmorphic/darktide-mod-relay). 
It does this without altering any Darktide files. Nothing is added or modified inside the game
directory, no patching, unpatching required. Mods are stored outside the game directory as well. To run vanilla Darktide, 
launch the game from Steam. To play modded, launch from Curator.

![Curator mod list, detailed view](https://staticdelivery.nexusmods.com/mods/4943/images/1196/1196-1786929470-1554360530.png)

## Installation

Modificus Curator can be installed on Windows, Linux or Steam Deck.  Instructions below for installers. For alternative options see the Curator [README.md](https://github.com/ModifAmorphic/darktide-modificus-curator/blob/main/README.md)

### Windows

- Download the latest `modificus-curator-setup.exe` from Files and run it.
- Adds Start Menu + desktop shortcut
- SmartScreen note: installer is not code-signed; **More info** > **Run anyway** on first run
- Antivirus note: Curator uses dll injection to load mods which can trigger antimalware and antivirus software. You may need to configure your antivirus to trust it. Every new build is published to virustotal.com for scanning. Scan results for every release: https://github.com/ModifAmorphic/darktide-modificus-curator/issues?q=is%3Aissue+label%3Avirus-scan+sort%3Acreated-desc


### Linux

- Run the AppImage installer script
```sh
curl https://raw.githubusercontent.com/ModifAmorphic/darktide-modificus-curator/main/scripts/install.sh | sh
```
- To run, find or search for **Modificus Curator** in your App Launcher. 

### Steam Deck

Modificus Curator runs on Steam Deck. Requires setup in Desktop Mode, but can be launched from Game Mode.

1. Open the Konsole terminal and run the installer script:

   ```sh
   curl https://raw.githubusercontent.com/ModifAmorphic/darktide-modificus-curator/main/scripts/install.sh | sh
   ```
2. In Steam, use **Add a non-Steam game** and select **Modificus Curator**.
   Change the name if desired, but don't force a compatibility tool. Curator is a native linux application.
3. While still in Desktop Mode, create a profile, add mods, sign in to Nexus (see Getting started below).

To run, launch **Modificus Curator** (or whatever name you chose) from Steam in either mode.

For full details, see [STEAMDECK.md](https://github.com/ModifAmorphic/darktide-modificus-curator/blob/main/STEAMDECK.md).

## Getting started

Curator finds your Steam and Darktide install automatically on first run.

1. Select **Profiles** from the menu and add a new profile. You can create multiple profiles for distinct sets of mods, or simply use one profile and call it a day.
2. (Optional, but recommended!) Navigate to **Nexus** and click the sign in button which will open a new tab on your default browser and request access for Curator. This lets Curator detect new versions of mods and import mods automatically (with #3)
3. (Optional) Enable **Nexus download links** on the same page so "Download with manager" buttons on the Nexus Mods site open Curator directly. This lets you click the Vortex or Mod Manager Download buttons on nexus and automatically downloads them into Curator.
4. **Mods**: Navigating to the mods page should prompt you to install DMF. Agreeing will open the mod page for DMF where you can download it.
5. **Add mods**: Once DMF is installed, find a mod and click **Vortex** or **Download with manager** on the Darktide mod page (if 2-3 were completed), or download them manually and add them to Curator.

Hit **Launch Darktide** to inject mods and launch the game.

**Note:** If your game was already patched then you'll need to restore it to "Vanilla" prior to launching. See the next section.


## Migrating from Patched Darktide

If you previously patched Darktide to load the Darktide Mod Loader, then it needs to be restored to Vanilla for mods to work properly. The easiest is to simply "Verify integrity of game files" from within Steam's Darktide properties under "Installed Files". This can take a bit of time (few minutes usually). You can also unpatch your game the same way you originally patched it. Whatever method you choose, DML (Not to be confused with DMF - Darktide Mod Framework) and Curator are not compatible.

## Vanilla

To play Darktide without mods, simply launch the game from Steam. Because Modificus Curator leverages Relay's dll injection to load mods at runtime there's no changes to revert. 

## Features

- Profiles: multiple mod lists, per-profile launch settings (env vars, game
  args, Lua logs, skip splash)
- Adding mods: one-click "Download with manager", archive/folder import,
  drag-and-drop, link an external folder without copying it
- Mod list: enable/disable, drag-reorder with per-row order locks,
  Latest/Pinned version policy, thumbnails + summaries
- Updates: update checks across the list, flagged rows, Premium in-app
  install + optional automatic update install
- Quality of life: DMF install prompt for new profiles, in-app self-update,
  themes + localizations, Steam Deck Gaming Mode support