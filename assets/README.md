# Logo files

| File | Used for | Made from |
| --- | --- | --- |
| `logo.svg` | The documentation site's favicon | Drawn by hand |
| `icon.png` | The NuGet package icon | `logo.svg`, rendered at 256 by 256 pixels with a transparent background |
| `wordmark-dark.svg`, `wordmark-light.svg` | The site header and the repository README: dark text for light backgrounds, light text for dark backgrounds | `logo.svg` and the name, made by `make_wordmark.py` |

NuGet only accepts PNG and JPEG icons, which is why `icon.png` exists. After changing `logo.svg`, render `icon.png`
again. `icon.html` shows `logo.svg` at the right size, and Edge can screenshot it. From this folder:

```powershell
& "${env:ProgramFiles(x86)}\Microsoft\Edge\Application\msedge.exe" --headless --disable-gpu --hide-scrollbars `
    --allow-file-access-from-files --default-background-color=00000000 --window-size=256,256 `
    --screenshot="$PWD\icon.png" "file:///$($PWD.Path.Replace('\', '/'))/icon.html"
```

The name in the wordmarks is drawn as outlines rather than text, so it looks the same on every machine. The font is
Inter Display ExtraBold, which is under the SIL Open Font License. To make the wordmarks again, install fontTools and
download Inter from <https://github.com/rsms/inter/releases>, then run:

```bash
python make_wordmark.py InterDisplay-ExtraBold.ttf logo.svg .
```
