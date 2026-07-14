# Third-party notices

BBDown for Windows is an independent graphical frontend. Release packages may aggregate the following command-line tools as separate executables:

- **BBDown 1.6.3** — <https://github.com/nilaoda/BBDown>. See the upstream repository and release archive for its license.
- **aria2 1.37.0** — GPL-2.0-or-later. Source and license: <https://github.com/aria2/aria2>.
- **FFmpeg N-113240-g6d2f64534d-20240110 win64 GPL build** — GPLv3 build. Source project: <https://ffmpeg.org/>; Windows build project: <https://github.com/BtbN/FFmpeg-Builds>.
- **Microsoft Windows App SDK 1.8** — Microsoft software license terms.
- **CommunityToolkit.Mvvm 8.4.2** — MIT license.
- **Inno Setup 6** — used only to create the installer; <https://jrsoftware.org/isinfo.php>.

The release build copies the license files distributed inside the aria2 and FFmpeg archives into the application `licenses` directory. Tool archives are not committed to this repository.
