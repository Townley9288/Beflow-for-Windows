# Third-party notices

Beflow for Windows is an independent graphical frontend. Release packages may aggregate the following command-line tools as separate executables:

- **BBDown 1.6.3 with Beflow new-quality compatibility patches** — MIT. The patches add Bilibili quality `122` (`4K·SDR增强`) and update PGC WEB playurl capability flag `fnval`/`CURRENT_FNVAL` from `4048` to the current player value `143312`; ordinary AV/BV WEB requests retain `4048`. BBDown's download implementation is otherwise unchanged. Source and license: <https://github.com/nilaoda/BBDown/tree/45622f79cd766e0fc6f5cbd49fcf4960340f35c3>. The release package includes `licenses/BBDown-LICENSE.txt`.
- **aria2 1.37.0** — GPL-2.0-or-later. Source and license: <https://github.com/aria2/aria2>.
- **FFmpeg N-113240-g6d2f64534d-20240110 win64 GPL build** — GPLv3 build. Source project: <https://ffmpeg.org/>; Windows build project: <https://github.com/BtbN/FFmpeg-Builds>.
- **Microsoft Windows App SDK 1.8** — Microsoft software license terms.
- **CommunityToolkit.Mvvm 8.4.2** — MIT license.
- **Inno Setup 6** — used only to create the installer; <https://jrsoftware.org/isinfo.php>.

The release build rebuilds the pinned BBDown source with the documented compatibility patches, then copies the BBDown, aria2 and FFmpeg license files into the application `licenses` directory. Tool archives are not committed to this repository. Corresponding source locations are recorded in `THIRD_PARTY_SOURCES.md`.
