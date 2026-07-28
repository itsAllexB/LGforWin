# LGforWin — TODO

Ideas / next steps after **v2.0**.

## Distribution & polish
- [ ] **App icon** — replace the placeholder sun with a real designed icon.
- [ ] **Code signing** — get rid of the SmartScreen "unrecognized app" warning on first run.
- [x] **Installer** — an Inno Setup `setup.exe` (Start-menu shortcut, uninstall entry) as an
      alternative to the portable zip. See `installer.iss` / `build.ps1 -Package`.
- [x] Make the release tag and the in-app version read identically — both are `v2.0.0`.

## Features
- [x] **SSDP auto-discovery** — find LG TVs on the LAN so users don't have to type IPs.
- [x] **Power automation** — turn the TVs on/off with the PC (startup, sleep, shutdown, display sleep).
- [ ] **Extra picture sliders** — contrast / black level / energy-saving (OLED brightness limiter).
- [ ] **Sunrise–sunset auto-dim** — schedule brightness to the sun instead of fixed clock times.
- [ ] **Per-TV schedule targeting** — schedules currently apply to *all* TVs; allow per-TV rules.

## Maybe / later
- [ ] Per-TV power rules (power automation currently applies to all TVs, like schedules).
- [ ] Surface a TV's power state in the tray icon / tooltip, not just the Home card.
- [ ] MSIX packaging (Store-style install + auto-update).
- [ ] "Identify screens" helper on the Home page to make monitor pairing foolproof.
- [ ] Optional per-TV dedicated hotkey combos (in addition to cursor targeting).
