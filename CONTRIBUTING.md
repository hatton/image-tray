# Building Screen Tray

## Requirements

Windows 10 or 11, and the
[.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0). Running a released build
needs only the .NET 10 Desktop Runtime.

## Building

```powershell
dotnet build
dotnet test
dotnet run --project src/ScreenTray
```

To produce the single-file executable, install it to
`%LOCALAPPDATA%\Programs\ScreenTray`, and restart the running copy:

```powershell
./deploy.ps1
```

Installing matters for one reason: the autostart entry points at wherever the executable
was when you last enabled it. Running from `bin\Debug` means a `dotnet clean` leaves a dead
entry in the Run key, which is why `deploy.ps1` puts it somewhere stable.

## Layout

| Path | What lives there |
| --- | --- |
| `src/ScreenTray/Services` | Folder watching, rotation, clipboard, thumbnails, window placement, autostart |
| `src/ScreenTray/ViewModels` | `TrayViewModel` coordinates everything; `ShotViewModel` is one tile |
| `src/ScreenTray/Controls` | `ShotTile`, including its animations and the width splitter |
| `src/ScreenTray/Theme` | The light and dark palettes, swapped at runtime |
| `src/ScreenTray/TileMetrics.cs` | All the tile sizing rules, deliberately pure and testable |
| `tests/ScreenTray.Tests` | xunit tests |
| `tools/make-icon.ps1` | Regenerates `src/ScreenTray/Assets/app.ico` |

## The app icon is generated

`src/ScreenTray/Assets/app.ico` is committed, but it is produced by
`tools/make-icon.ps1` rather than drawn by hand. Edit the script and re-run it rather than
editing the `.ico`:

```powershell
./tools/make-icon.ps1
```

It writes nine frames, 16 through 256, all from one geometry. Resist the temptation to
simplify the small frames: an earlier version did, and the result was a taskbar icon that
did not match the tray icon.

## Settings and logs

Both live in `%APPDATA%\ScreenTray\`:

- `settings.json` — watched folder, keep count, thumbnail width, theme, window placement.
  Delete it to start over; the app will ask for a folder on the next launch.
- `screentray.log` — warnings only, truncated when it passes 256 KB.

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
