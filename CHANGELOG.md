# Changelog

All notable changes to this project are documented in this file.
The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and the project adheres to [Semantic Versioning](https://semver.org/).

## [0.1.0] - 2026-10-30

### Added

- `RCPFLATTEN` AutoCAD command: unfolds a ReCap point cloud along a 2D polyline (lines and arcs) within a left/right search distance.
- `RcpFlattenCli`: native helper based on the ReCap SDK that writes the flattened `.rcp` (color, intensity and classification are kept).
- `build.ps1` and `package.ps1` to build the project and create the release archive.
