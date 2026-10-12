# Windows installer and updater

`SpirulaSetup.exe` installs the latest Windows release from
[harry7557558/spirula-studio](https://github.com/harry7557558/spirula-studio/releases)
for the current user, with no administrator rights:

- `spirula.exe` and the launcher in `%LOCALAPPDATA%\Programs\Spirula Studio`
- Start menu entries (Spirula Studio, Check for Updates) and an optional desktop shortcut
- an entry in Settings > Apps, so it uninstalls like any other app
- optional "Open with" entries for `.ply .e57 .las .laz .obj .glb .gltf .mp4 .mov .insv`
  and an "Open in Spirula Studio" folder right-click item (never the default app)

The shortcuts start `SpirulaLauncher.exe` (a copy of the setup exe). At most
every 6 hours it asks the GitHub API for the latest release before starting
`spirula.exe`; if the tag is newer it shows the changelog with Update now /
Skip this version / Remind me later. The check times out after 4 s, so
offline launches are not held up. Settings and caches in `%APPDATA%` and
`%LOCALAPPDATA%\spirula-studio` are left alone by updates and uninstall.

## Your own build (CUDA)

Releases only carry the Vulkan build. To run a build you compiled, for
example `build_develop.bat -DSS_BACKEND=cuda`, pick "My own build" in the
wizard and point it at `build_cuda\spirula.exe`. Its folder's DLLs (cudart)
are copied with it. From then on the launcher never downloads over it: a new
release only shows a notice (with "Switch to official build"), and when the
exe at that path changes after a rebuild, the next launch copies it in. Run
Setup again and pick "Official release" to go back.

## Build

```bat
tools\windows_installer\build.bat [out.exe]
```

Uses the `csc.exe` that ships with Windows (.NET Framework 4), so no SDK is
needed. The source stays C# 5 for that compiler.

## Command line

| Command | Effect |
|---|---|
| `SpirulaSetup.exe` | installer window |
| `SpirulaLauncher.exe [paths...]` | update check, then the GUI with `paths` |
| `SpirulaLauncher.exe --update` | check now, report "up to date" too |
| `SpirulaLauncher.exe --uninstall` | uninstall (what Settings > Apps runs) |
| `... --install/--update/--uninstall --quiet` | same with no UI; errors go to `%TEMP%\SpirulaSetup.log` and exit code 1 |
| `SpirulaSetup.exe --install --quiet --from <spirula.exe>` | silent install of your own build |

The exe is unsigned, so SmartScreen warns the first time it is run from a
download ("More info" > "Run anyway").
