# LRPS2 local build

The bundled `pcsx2_libretro.dll` is built from
https://github.com/libretro/ps2 commit
`093f66ba2dca55e87e63dc6bdcb2d2fe3298b4b1` with
`windows-large-iso.patch` applied.

The patch fixes two Windows core defects:

- The frequent-access VFS path used 32-bit `lseek`/tell calls before creating
  a file mapping. Large DVD images such as the 4,508,221,440-byte Final
  Fantasy X ISO therefore failed during `DoCDVDopen()`.
- `cpu_thread_entry()` ignored a failed `VMManager::Initialize()`, forced the
  VM to `Running`, and dereferenced a null `Cpu` in `VMManager::Execute()`.

Build a full-featured Windows x64 Release core with Visual Studio 2022 and
CMake/Ninja after applying the patch:

```powershell
git clone --recursive https://github.com/libretro/ps2.git lrps2
Set-Location lrps2
git checkout 093f66ba2dca55e87e63dc6bdcb2d2fe3298b4b1
git apply path\to\windows-large-iso.patch

cmake -S . -B build-release -G Ninja -DLIBRETRO=ON `
  -DCMAKE_BUILD_TYPE=Release -DCMAKE_SYSTEM_NAME=Windows `
  -DCMAKE_SYSTEM_PROCESSOR=AMD64
cmake --build build-release --target pcsx2_libretro --parallel
```

`CMAKE_SYSTEM_PROCESSOR=AMD64` is required for this Ninja build so cpuinfo
includes its Windows x86 implementation. The output is
`build-release/libretro/pcsx2_libretro.dll`.
