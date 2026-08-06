# Building Image Tray

## Requirements

Windows 10 or 11, and the
[.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0). Running a released build
needs only the .NET 10 Desktop Runtime.

## Building

```powershell
dotnet build
dotnet test
dotnet run --project src/ImageTray
```

To produce the single-file executable, install it to
`%LOCALAPPDATA%\Programs\ImageTray`, and restart the running copy:

```powershell
./deploy.ps1
```

Installing matters for one reason: the autostart entry points at wherever the executable
was when you last enabled it. Running from `bin\Debug` means a `dotnet clean` leaves a dead
entry in the Run key, which is why `deploy.ps1` puts it somewhere stable.

## The installer

`./build-installer.ps1` publishes Release and compiles `installer/ImageTray.iss` into
`dist/ImageTraySetup.exe`. It needs [Inno Setup 6](https://jrsoftware.org/isdl.php)
(`winget install JRSoftware.InnoSetup`); the script says so and stops if it is missing.

The installer is per-user, so no UAC prompt: one executable into
`%LOCALAPPDATA%\Programs\ImageTray`, one Start menu entry, no desktop shortcut, then
it launches the app. Every wizard page is disabled because there is nothing to ask about,
which leaves a brief progress window. `/VERYSILENT` removes even that:

```powershell
./dist/ImageTraySetup.exe /VERYSILENT
```

Three things it handles that are easy to get wrong:

- The exe is framework-dependent, so a machine without the .NET 10 Desktop Runtime would
  get a successful install and an app that never starts. Setup checks for the runtime first
  and refuses with a message naming the download page.
- Installing and uninstalling both force-close a running copy, since a tray app is almost
  always running and a running exe cannot be replaced or deleted. Uninstalling would
  otherwise strand the executable *after* removing the uninstaller that would retry.
- Uninstalling deletes the `ImageTray` Run-key value, which the app itself only removes
  when you turn autostart off. Settings and the log in `%APPDATA%\ImageTray` are left
  alone.

## Layout

| Path | What lives there |
| --- | --- |
| `src/ImageTray/Services` | Folder watching, rotation, clipboard, thumbnails, window placement, autostart |
| `src/ImageTray/ViewModels` | `TrayViewModel` coordinates everything; `ShotViewModel` is one tile |
| `src/ImageTray/Controls` | `ShotTile`, including its animations and the width splitter |
| `src/ImageTray/Theme` | The light and dark palettes, swapped at runtime |
| `src/ImageTray/TileMetrics.cs` | All the tile sizing rules, deliberately pure and testable |
| `tests/ImageTray.Tests` | xunit tests |
| `tools/make-icon.ps1` | Regenerates `src/ImageTray/Assets/app.ico` |
| `installer/ImageTray.iss` | The Inno Setup script, built by `build-installer.ps1` |

## The app icon is generated

`src/ImageTray/Assets/app.ico` is committed, but it is produced by
`tools/make-icon.ps1` rather than drawn by hand. Edit the script and re-run it rather than
editing the `.ico`:

```powershell
./tools/make-icon.ps1
```

It writes nine frames, 16 through 256, all from one geometry. Resist the temptation to
simplify the small frames: an earlier version did, and the result was a strip icon that
did not match the tray icon.

## Settings and logs

Both live in `%APPDATA%\ImageTray\`:

- `settings.json` — watched folder, keep count, thumbnail width, theme, window placement.
  Delete it to start over; the app will ask for a folder on the next launch.
- `imagetray.log` — warnings only, truncated when it passes 256 KB.

Saves are written to a temp file and swapped into place, so a crash mid-write cannot leave
a corrupt settings file. A file that will not parse falls back to defaults rather than
refusing to start.

## Things that look wrong but aren't

A few decisions are non-obvious enough to be worth stating before someone "fixes" them.

- **Clipboard copies register four formats with `autoConvert: false`.** WPF's `DataObject`
  will synthesise the bitmap formats from one another, and an auto-conversion derived from
  the white-flattened fallback can silently overwrite the hand-built `CF_DIBV5`. That would
  strip alpha from anything pasted into Office.
- **`CF_DIB` is composited onto white on purpose.** Consumers of that format ignore alpha,
  and a transparent pixel is usually stored as black, which is where "my screenshot pasted
  with black corners" comes from.
- **Window position goes through `GetWindowPlacement`/`SetWindowPlacement`.** Saving
  `Left`/`Top`/`Width`/`Height` in device-independent units looks equivalent and breaks as
  soon as a monitor is unplugged or the DPI differs.
- **Every tile is exactly the tray height.** Deriving height from each image's aspect ratio
  produces a row of wildly different heights and destroys the drag-to-resize gesture.
- **The keep-count slider waits for you to stop dragging.** Lowering it recycles files, and
  a slider reports every value it passes through, so acting live would recycle at each step
  on the way down. The thumbnail-width slider has no such delay because it only affects
  layout.
- **Rotation only ever sends files to the Recycle Bin**, only touches image files, and never
  looks in subdirectories.
