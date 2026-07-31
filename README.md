# Screen Tray

A small Windows app that shows your most recent screenshots in a strip so you can put
one back on the clipboard with a single click.

![Screen Tray](docs/screen-tray.png)

Screenshot tools drop files in a folder and leave you there. Getting the shot from three
captures ago onto the clipboard means finding the folder, sorting by date, squinting at
timestamps, opening the right one, select all, copy. Screen Tray makes that one click.

## What it does

- Watches whichever folder your screenshot tool saves into.
- Shows the newest N screenshots as a row of thumbnails, newest on the left.
- **Click a thumbnail** to copy the image to the clipboard.
- **Click the file icon** in a thumbnail's top-right corner to copy its full path instead.
- **Click the trash icon** in the bottom-left corner to send that screenshot to the
  Recycle Bin.
- Keeps the folder trimmed: anything past the newest N goes to the Recycle Bin, never a
  permanent delete.
- Pops to the front when you take a screenshot, **without stealing focus**, so it cannot
  swallow what you are typing.
- Closing the window hides it to the tray; one left-click on the tray icon brings it
  back. Starts with Windows, in whichever state you left it.
- Remembers its size and position, including across monitors with different DPI.

Drag the tray's edge to resize the thumbnails. Drag the splitter that appears when you
point between two thumbnails to change how wide they are.

Copying puts the image on the clipboard as PNG, CF_DIBV5, and a white-flattened CF_DIB at
once, so transparency survives into anything that understands it and does not paste as
black boxes in anything that doesn't.

## Settings

![Screen Tray settings](docs/settings.png)

## Licence

[MIT](LICENSE)
