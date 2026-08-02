# Third-Party Notices

This plugin ships the **bsnes** SNES emulator core as `bsnes_libretro.dll`.

- **bsnes** — a Super Famicom / SNES emulator.
  - Copyright (c) bsnes contributors.
  - License: GNU General Public License v3.0 (GPLv3).
  - Source code: https://github.com/bsnes-emu/bsnes
  - The core binary is built and distributed by the libretro project
    (https://www.libretro.com/) and was obtained from
    https://buildbot.libretro.com/nightly/windows/x86_64/latest/.

The GPLv3 license text is available at https://www.gnu.org/licenses/gpl-3.0.html.
Under the GPLv3 you are entitled to the corresponding source code of bsnes; it is
available at the repository linked above.

This plugin also ships **OpenH264** for H.264 video encoding/decoding used in
the streaming feature.

- **OpenH264** — H.264/AVC encoder and decoder.
  - Copyright (c) 2013, Cisco Systems. All rights reserved.
  - License: BSD-2-Clause (see http://www.openh264.org/BINARY_LICENSE.txt).
  - Source code: https://github.com/cisco/openh264
  - Binary obtained from http://ciscobinary.openh264.org/openh264-2.6.0-win64.dll.bz2.

The `snes_h264.dll` wrapper is a thin C++ shim around OpenH264, built from
`native/snes_h264.cpp` in this repository.

The SnesEmulator plugin itself is licensed under the GNU Affero General Public
License v3.0 (AGPL-3.0-or-later), which is compatible with the GPLv3.
