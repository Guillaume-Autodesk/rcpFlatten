# Changelog

All notable changes to this project are documented in this file.
The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and the project adheres to [Semantic Versioning](https://semver.org/).

## [Unreleased]

## [0.2.0] - 2026-11-25

### Added

- `RCPFLATTEN` now opens a palette (Weave 3.0 look, docked on the right by default) with every parameter in three numbered steps: polyline, left/right distances (typed, stepped with − / + or measured in the drawing), source and output `.rcp`, plus the source point and scan counts.
- Progress bar for both steps of the conversion (reading, indexing), elapsed time, and a button to stop the conversion and remove the incomplete output.
- `-RCPFLATTEN`: the previous command-line version, for scripts.
- `RcpFlattenCli` reports the indexing progress of the output (`INDEX <percent>`).

### Fixed

- Vertical lines at the vertices of the polyline in the flattened cloud. At a corner, the points of the outer wedge were all projected on the vertex and piled up on a single station. Points are now assigned to a segment by the bisector of each corner and projected without clamping. The station is rescaled between the two bisector lines of the segment, so it is continuous across corners (no gap, no overlap) and never drifts from the station along the polyline.

## [0.1.0] - 2026-10-30

### Added

- `RCPFLATTEN` AutoCAD command: unfolds a ReCap point cloud along a 2D polyline (lines and arcs) within a left/right search distance.
- `RcpFlattenCli`: native helper based on the ReCap SDK that writes the flattened `.rcp` (color, intensity and classification are kept).
- `build.ps1` and `package.ps1` to build the project and create the release archive.
