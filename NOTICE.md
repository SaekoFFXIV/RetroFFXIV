# Third-Party Notices

This plugin ships libretro emulator cores: **bsnes** (SNES) as
`bsnes_libretro.dll`, and optional additional cores in the `cores/` folder.

- **bsnes** — a Super Famicom / SNES emulator.
  - Copyright (c) bsnes contributors.
  - License: GNU General Public License v3.0 (GPLv3).
  - Source code: https://github.com/bsnes-emu/bsnes
  - The core binary is built and distributed by the libretro project
    (https://www.libretro.com/) and was obtained from
    https://buildbot.libretro.com/nightly/windows/x86_64/latest/.

- **BlastEm** — a Sega Genesis / Mega Drive emulator (`cores/blastem_libretro.dll`).
  - Copyright (c) BlastEm contributors.
  - License: GNU General Public License v3.0 (GPLv3).
  - Source code: https://github.com/libretro/blastem
  - Binary obtained from
    https://buildbot.libretro.com/nightly/windows/x86_64/latest/.

- **mGBA** — a Game Boy Advance emulator (`cores/mgba_libretro.dll`).
  - Copyright (c) mGBA contributors.
  - License: Mozilla Public License 2.0 (MPL-2.0).
  - Source code: https://github.com/mgba-emu/mgba
  - Binary obtained from
    https://buildbot.libretro.com/nightly/windows/x86_64/latest/.

- **LRPS2** — a PlayStation 2 emulator based on PCSX2
  (`cores/pcsx2_libretro.dll`).
  - Copyright (c) PCSX2 and libretro contributors.
  - License: GNU General Public License v3.0 (GPLv3).
  - Source code: https://github.com/libretro/ps2 at commit
    `093f66ba2dca55e87e63dc6bdcb2d2fe3298b4b1`.
  - The shipped Windows x64 binary is rebuilt locally with the corresponding
    source patch in `native/lrps2/windows-large-iso.patch`.

- **Gambatte** — a Game Boy / Game Boy Color emulator
  (`cores/gambatte_libretro.dll`).
  - Copyright (c) Gambatte contributors.
  - License: GNU General Public License v2.0 (GPLv2).
  - Source code: https://github.com/libretro/gambatte
  - Binary obtained from
    https://buildbot.libretro.com/nightly/windows/x86_64/latest/.

The GPLv2 license text is available at
https://www.gnu.org/licenses/gpl-2.0.html, the GPLv3 license text at
https://www.gnu.org/licenses/gpl-3.0.html, and the MPL-2.0 license text at
https://www.mozilla.org/MPL/2.0/. Under the GPL you are entitled to the
corresponding source code of the GPL-licensed cores; it is available at the
repositories linked above.

This plugin also ships **OpenH264** for H.264 video encoding/decoding used in
the streaming feature.

- **OpenH264** — H.264/AVC encoder and decoder.
  - Copyright (c) 2013, Cisco Systems. All rights reserved.
  - License: BSD-2-Clause (see http://www.openh264.org/BINARY_LICENSE.txt).
  - Source code: https://github.com/cisco/openh264
  - Binary obtained from http://ciscobinary.openh264.org/openh264-2.6.0-win64.dll.bz2.

This plugin also ships **libopus** for Opus audio encoding/decoding used in
the streaming feature.

- **Opus** — low-latency lossy audio codec.
  - Copyright 2001-2024 Xiph.Org Foundation, Skype Limited, Octasic,
    Jean-Marc Valin, Timothy B. Terriberry, CSIRO, Gregory Maxwell,
    Mark Borgerding, Erik de Castro Lopo, and other Opus contributors.
  - License: BSD-3-Clause (see the COPYING file in the source repository).
  - Source code: https://gitlab.xiph.org/xiph/opus
  - The shipped `opus.dll` was built locally from the official v1.5.2
    release source (MSVC x64, CMake shared-library build).

The `snes_h264.dll` wrapper is a thin C++ shim around OpenH264, built from
`native/snes_h264.cpp` in this repository.  The `snes_opus.dll` wrapper is a
thin C shim around libopus, built from `native/snes_opus.cpp` in this
repository.

The RetroXIV plugin itself is licensed under the GNU Affero General Public
License v3.0 (AGPL-3.0-or-later), which is compatible with the GPLv3.
