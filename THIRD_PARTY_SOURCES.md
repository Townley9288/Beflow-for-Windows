# Third-party source availability

Beflow release packages aggregate separate command-line programs. Their source code and build information are available from the following upstream locations:

- BBDown 1.6.3 source commit `45622f79cd766e0fc6f5cbd49fcf4960340f35c3`: <https://github.com/nilaoda/BBDown/tree/45622f79cd766e0fc6f5cbd49fcf4960340f35c3>. Beflow's release build adds `122` → `4K·SDR增强` to the quality table and uses `fnval=143312` plus `drm_tech_type=3` for PGC WEB playurl requests so supported titles expose their clear SDR增强/4K/HDR/high-bitrate streams; ordinary AV/BV WEB requests retain `fnval=4048` before rebuilding the separate executable.
- aria2 1.37.0 source release: <https://github.com/aria2/aria2/releases/tag/release-1.37.0>
- FFmpeg source revision `6d2f64534d`: <https://github.com/FFmpeg/FFmpeg/commit/6d2f64534d>
- BtbN FFmpeg Windows build scripts: <https://github.com/BtbN/FFmpeg-Builds>

The exact source or binary archive names, versions and SHA-256 values used by the release build are recorded in `tools/tools.json`. The licenses copied from the upstream projects are included in each release package under `licenses`.

For difficulty obtaining the corresponding source for a distributed release, open an issue at <https://github.com/Townley9288/Beflow-for-Windows/issues> and identify the Beflow version and third-party component.
