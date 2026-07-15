# Third-party notices

Beflow for Windows is an independent graphical frontend. Release packages may aggregate the following command-line tools as separate executables:

- **BBDown 1.6.3** — MIT. Source and license: <https://github.com/nilaoda/BBDown>. The release package includes `licenses/BBDown-LICENSE.txt`.
- **aria2 1.37.0** — GPL-2.0-or-later. Source and license: <https://github.com/aria2/aria2>.
- **FFmpeg N-113240-g6d2f64534d-20240110 win64 GPL build** — GPLv3 build. Source project: <https://ffmpeg.org/>; Windows build project: <https://github.com/BtbN/FFmpeg-Builds>.
- **Microsoft Windows App SDK 1.8** — Microsoft software license terms.
- **CommunityToolkit.Mvvm 8.4.2** — MIT license.
- **Inno Setup 6** — used only to create the installer; <https://jrsoftware.org/isinfo.php>.

The release build copies the BBDown, aria2 and FFmpeg license files into the application `licenses` directory. Tool archives are not committed to this repository. Corresponding source locations are recorded in `THIRD_PARTY_SOURCES.md`.
