# Third-party notices

Beflow for Windows is an independent graphical frontend. Release packages may aggregate the following command-line tools as separate executables:

- **BBDown 1.6.3 with Beflow compatibility patches** — MIT. The patches add Bilibili quality `122` (`4K·SDR增强`), request PGC WEB playurl with Bilibili's current capabilities (`fnval=143312` plus `drm_tech_type=3`), expose the clear SDR增强/4K/HDR/high-bitrate streams returned for supported titles, and preserve interactive stream indices across BBDown's internal download retries; ordinary AV/BV WEB requests retain `fnval=4048`. Console output is explicitly UTF-8 to preserve Chinese text when redirected. Internal media transfers and file-size probes use a dedicated direct HTTP client with proxies disabled; API and login networking are unchanged. Beflow also explicitly disables aria2 media proxy options. Batch downloads exchange freshly parsed stream choices and output plans with Beflow over a local pipe, avoiding a second metadata parse before each transfer. Information-only parsing skips chapter metadata requests; actual downloads continue to fetch chapters for muxing. Source and license: <https://github.com/nilaoda/BBDown/tree/45622f79cd766e0fc6f5cbd49fcf4960340f35c3>. The release package includes `licenses/BBDown-LICENSE.txt`.
- **aria2 1.37.0** — GPL-2.0-or-later. Source and license: <https://github.com/aria2/aria2>.
- **FFmpeg N-113240-g6d2f64534d-20240110 win64 GPL build** — GPLv3 build. Source project: <https://ffmpeg.org/>; Windows build project: <https://github.com/BtbN/FFmpeg-Builds>.
- **Microsoft Windows App SDK 1.8** — Microsoft software license terms.
- **CommunityToolkit.Mvvm 8.4.2** — MIT license.
- **Inno Setup 6** — used only to create the installer; <https://jrsoftware.org/isinfo.php>.

The release build rebuilds the pinned BBDown source with the documented compatibility patches, then copies the BBDown, aria2 and FFmpeg license files into the application `licenses` directory. Tool archives are not committed to this repository. Corresponding source locations are recorded in `THIRD_PARTY_SOURCES.md`.
