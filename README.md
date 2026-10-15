# Content Warning → iOS (personal port tooling)

Tools and code that turn a legally owned Windows copy of *Content Warning* (Landfall) into an iOS app for one
person's own jailbroken / TrollStore iPhone. **This repository contains no game files** and nothing derived from
them: the patched assemblies, converted data and IPAs exist only on the owner's PC and in a private repository.

No Steam, Discord, Landfall or Photon Cloud contact from the iOS app: Steam and Discord are stubbed, the version
check and server settings download are removed, and every Photon connection is limited to the local network.

* `shim/` – `CWShim.dll`: runtime the patched game calls instead of desktop APIs (Input, Cursor, PlayerPrefs,
  Microphone, Process, Screen), local player identity, the network guard.
* `platform/` – `CWPlatform.dll`: the iOS `IPlatform` (replaces `SteamRuntimeManager`).
* `mod/` – `CWIOS.dll`: log (`Documents/ContentWarning-iOS.log`, previous run `-prev`), on-screen log (tap the
  FPS counter), offline solo setup, touch → virtual mouse / keyboard / gamepad.
* `tools/patcher` – Mono.Cecil patcher for the retail assemblies (IL2CPP-safe, Steam-free, verified).
* `tools/build_plugins.py` – builds shim/platform/mod and runs the patcher into the private repo's `Plugins`.
* `shell/` – minimal Unity 6000.0.67f1 project; CI builds the unsigned, ldid-fakesigned shell IPA from it.
* `tools/convert_*.py`, `dx2metal.py`, `transcode_tex.py` – convert the Windows data for iOS (DX11 → Metal
  shaders, BC → ASTC textures, iOS platform tag).
