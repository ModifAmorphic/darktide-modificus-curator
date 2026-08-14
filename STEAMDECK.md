# Modificus Curator on Steam Deck

Modificus Curator runs natively on the Steam Deck. This guide takes you from
installing Curator through launching a modded Darktide in SteamOS Gaming Mode.

Curator launches Darktide modded through
[Mod Relay](https://github.com/ModifAmorphic/darktide-mod-relay). Launching
Darktide normally from Steam stays vanilla, so you can switch between the two
freely.

Setup belongs in **Desktop Mode**. Routine play belongs in **Gaming Mode**.

## Desktop Mode: install and set up

Do all of the following in Desktop Mode: install Curator, add it to Steam,
configure Nexus, register download links, and add, import, update, or link mods.

### Install the AppImage

In Desktop Mode, open a terminal (the Konsole app) and run the recommended
Linux installer:

```sh
curl https://raw.githubusercontent.com/ModifAmorphic/darktide-modificus-curator/main/scripts/install.sh | sh
```

Add `--prerelease` to install the latest prerelease instead of stable:

```sh
curl https://raw.githubusercontent.com/ModifAmorphic/darktide-modificus-curator/main/scripts/install.sh | sh -s -- --prerelease
```

This installs a stable AppImage at
`~/.local/share/Modificus Curator/appimage/Modificus.Curator.AppImage`,
creates the **Modificus Curator** desktop application and icon, and adds the
`modificus-curator` command link. It uses no root privileges and leaves your
profiles, mods, config, logs, and any standalone install untouched.

### Add Curator to Steam

Still in Desktop Mode, add Curator as a non-Steam game so it appears in Gaming
Mode:

1. Open Steam.
2. Open Steam's **Add a non-Steam game** option to show the application list.
3. Select **Modificus Curator** and add it.
4. If Curator is not in the list, choose **Browse** and point Steam at the
   stable AppImage:
   `~/.local/share/Modificus Curator/appimage/Modificus.Curator.AppImage`

Do not force a Proton compatibility tool on the Curator shortcut. Curator is a
native Linux app. It launches Darktide through the game's own Proton
environment: in automatic discovery mode (the default), Curator reads the
Proton tool Steam has selected for Darktide and uses that, so changing Darktide's
compatibility tool in Steam is all you need to do. The Curator shortcut itself
always stays native.

Because the installer writes one stable AppImage path, the Steam shortcut stays
valid across Curator's in-app updates.

### Set up profiles, Nexus, and mods

Launch **Modificus Curator** from your applications (or run `modificus-curator`),
then finish setup in Desktop Mode:

- Create or select a profile.
- Sign in to Nexus under **Nexus** if you want Nexus integration.
- Enable **Nexus download links** under **Nexus** if you want
  "Download with manager" links on the Nexus Mods site to open Curator.
- Add, import, update, or link mods. Desktop Mode is the recommended workflow
  for all mod acquisition and setup, including for Premium users.
- File and folder pickers and open-folder actions are desktop tasks, so do them
  here.

## Touchscreen configuration

These steps enable native touch scrolling in Curator. You set them on
Curator's own controller configuration in Steam:

1. Open Curator's Steam library page, then open its controller configuration.
2. Choose **Edit Layout**.
3. Open **Action Sets**.
4. Open the settings (gear) for the **Default** or **In-Game Controls** action
   set.
5. Add an **Always-On Command**.
6. Choose **Add Command**, then **System > Touchscreen Native Support**.

With **Touchscreen Native Support** active, you can scroll Curator's lists by
touch scrolling: drag across a list to scroll it.

On the Mods list, touch scrolling starts anywhere outside the dedicated reorder
grip at the left edge of each row. That grip is reserved for reordering: drag it
up or down to move a mod, after a small movement threshold so a tap does not
reorder. Dragging anywhere else on the row stays touch scrolling, so you can
move through a long list without accidentally reordering mods. A mod whose
position you want to protect can be locked in place (the lock button beside Move
Up and Move Down); a locked row's grip is inert and its whole row scrolls with
touch input.

## Gaming Mode

Curator runs natively in Gaming Mode.

### Current controls

Curator does not yet support D-pad or stick navigation in Gaming Mode (#178).
For now, use the touchscreen, or a Steam Input mapping that provides mouse or
trackpad input, to move around the interface.

Activating a text field does not currently open Steam's keyboard automatically.
To type, focus or tap the field, then press **Steam + X** to open the keyboard
manually (#179).

### Routine use

1. Launch **Modificus Curator** from your Steam library.
2. Select the profile you want.
3. Make any routine supported changes to your mod list, such as enabling or
   disabling mods, reordering, or changing a mod's policy.
4. Press **Launch Darktide**.

Launching Darktide through Curator produces the modded launch. Launching
Darktide directly from Steam remains vanilla.

## Current limitations and guidance

- **Light theme under System:** with the theme preference set to **System**,
  Curator falls back to Light in Gaming Mode. Select **Dark** in **Preferences**
  if you want a dark interface (#180).
- **File and folder pickers and open-folder actions:** these desktop workflows
  are impractical in Gaming Mode. Return to Desktop Mode to add, import, or
  update mods, link an external folder, or open a mod's folder (#182).
- **Nexus setup and downloads:** Steam's built-in Gaming Mode browser does not
  hand `nxm://` links back to Curator. Do all Nexus sign-in, download-link
  registration, and mod acquisition in Desktop Mode (#183).

## Troubleshooting

- **Curator opens through Proton, or fails to start:** remove any forced
  compatibility tool from the Curator shortcut. Curator runs natively.
- **Touch drag selects text instead of scrolling:** confirm
  **Touchscreen Native Support** is on the active action set (see
  [Touchscreen configuration](#touchscreen-configuration)).
- **Need to add, import, or update mods, or open a folder:** switch back to
  Desktop Mode for those tasks.
- **Need to type:** focus or tap the field, then press **Steam + X**.

## Current compatibility work

The following open issues track Gaming Mode improvements:

- [#178, D-pad and stick navigation](https://github.com/ModifAmorphic/darktide-modificus-curator/issues/178)
- [#179, virtual keyboard](https://github.com/ModifAmorphic/darktide-modificus-curator/issues/179)
- [#180, System theme fallback](https://github.com/ModifAmorphic/darktide-modificus-curator/issues/180)
- [#182, desktop file and picker gating](https://github.com/ModifAmorphic/darktide-modificus-curator/issues/182)
- [#183, Nexus Desktop Mode guidance](https://github.com/ModifAmorphic/darktide-modificus-curator/issues/183)
