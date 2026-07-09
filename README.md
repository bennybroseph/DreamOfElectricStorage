# ⚡ Dream of Electric Storage

A different way to see your files.

Instead of clicking through folder lists and detail panes, **Dream of Electric Storage** indexes every file on your drives and shows them as an interactive **node graph** — zoom, pan, search, and manage your files as a living map instead of a tree of lists.

> 🚧 **Status:** early scaffold. Solution builds and tests pass; the real indexer and graph view are next.

## ✨ Goals

- 🗺️ **Graph, not lists** — files and folders are nodes you fly around, scaled and clustered visually
- ⚡ **Instant** — full-drive index using NTFS internals (the [Everything](https://www.voidtools.com/) approach), live-updated as files change
- 🔍 **Search-first** — filter the whole graph as fast as you can type
- 🔗 **Smart relationships** — edges for duplicates, similar names, file types, and date clusters, not just folder hierarchy
- 🖱️ **A real file manager** — open, move, rename, and delete straight from the graph

## 🛠️ Stack

| Layer | Tech | Why |
|---|---|---|
| Graph frontend | [WinUI 3](https://learn.microsoft.com/windows/apps/winui/winui3/) + C# (.NET 8) | Native Windows UI for the file-manager chrome, plus a GPU canvas ([Win2D](https://microsoft.github.io/Win2D/) → Direct3D/[Vortice](https://github.com/amerkoleci/Vortice.Windows)) for the graph |
| Indexer core | Plain C# / .NET class library | NTFS MFT + USN journal via P/Invoke; runs headless for tests, in-process with the app |
| Platform | Windows 10/11 | Leaning on NTFS is what makes full-drive indexing instant |

## 🏗️ Layout

```
src/DreamOfElectricStorage.Core/   # drive indexer (no UI dependency)
src/DreamOfElectricStorage.App/    # WinUI 3 app — the graph UI
tests/                             # indexer tests
```

## 📜 License

Copyright © 2026 Benjamin Odom — licensed under the [GNU General Public License v3.0 or later](LICENSE).
