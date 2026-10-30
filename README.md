# rcpFlatten

AutoCAD plug-in (Windows, x64) that **unfolds an Autodesk ReCap point cloud (`.rcp`) along a 2D polyline** and writes the result to a new `.rcp`.

You pick a polyline in the drawing, give a search distance on each side, and choose a source `.rcp`. Every point that lies within the search zone is re-expressed in a "flattened" frame:

| Output axis | Meaning |
|---|---|
| X | Station: distance measured along the polyline |
| Y | Original Z (elevation) |
| Z | Signed lateral offset from the polyline (positive on the left of the drawing direction) |

This is handy to get an elevation view or a developed profile of a façade, a tunnel, a road or a wall that follows a curved alignment.

The project has two parts:

- **`RcpFlattenPlugin`**: the AutoCAD .NET plug-in that provides the `RCPFLATTEN` command.
- **`RcpFlattenCli`**: a native helper (C++, Autodesk **ReCap SDK**) that reads and writes the point clouds. The plug-in runs it out of process, so the ReCap SDK never loads inside the AutoCAD process.

## Download (precompiled binaries)

Releases are published as ZIP archives on the [GitHub Releases](../../releases) page:

```
rcpflatten-vX.Y.Z-win-x64.zip
├── RcpFlatten.dll
├── cli/
│   └── RcpFlattenCli.exe
├── README.txt
├── LICENSE
└── THIRD_PARTY_NOTICES.md
```

> **Important: the ReCap SDK DLLs are not included.** The archive does **not** ship the Autodesk DLLs (`AdskRc*.dll`, `bin/dependencies/*.dll`, `bin/plugins/*`) because their public redistribution is not covered by our license. To run the plug-in you must:
>
> 1. Have the **Autodesk ReCap SDK** (v27.0.3 or compatible) installed locally,
> 2. Copy the following into the `cli` folder, next to `RcpFlattenCli.exe`:
>    - all the `*.dll` files from the SDK `Bin\` folder,
>    - the contents of `Bin\dependencies\`,
>    - the `Bin\plugins\` folder (copied as `cli\plugins\`).
>
> You also need **AutoCAD 2027** (.NET 10) and the **Microsoft Visual C++ 2015–2022 Redistributable (x64)** ([Microsoft download](https://learn.microsoft.com/cpp/windows/redistributing-visual-cpp-files)).

If you would rather build from source, see the **Building** section below.

## Usage

1. In AutoCAD, run `NETLOAD` and select `RcpFlatten.dll`.
2. Run the `RCPFLATTEN` command.
3. Select the 2D polyline to unfold the point cloud along.
4. Enter the search distance to the left, then to the right, of the polyline (in drawing units; the default is 5 for each side).
5. Select the source `.rcp`.
6. Choose the `.rcp` to create (the suggested name is `<source>_flat.rcp`). It must differ from the source.

The command line reports the progress and the number of points written.

Notes:

- The polyline must be a lightweight polyline (`LWPOLYLINE`) drawn in the XY plane of the WCS (normal = Z). Line segments and arcs (bulges) are supported, as well as closed polylines.
- The polyline and the point cloud must share the same coordinate system. If no point is found in the search zone, the plug-in tells you so and no file is created.
- Color, intensity and classification are kept when the source has them. All points of the source are considered, at maximum density, without applying user edits (regions, clips).
- The plug-in prompts are currently in French.

### Using the native helper on its own

`RcpFlattenCli.exe` can be run directly, which is useful for scripting:

```text
RcpFlattenCli --in <source.rcp> --out <flat.rcp> --poly <polyline.txt> --left <distance> --right <distance>
RcpFlattenCli --info <file.rcp>
```

The polyline file is a text file with one header line and one line per vertex (`bulge` is the AutoCAD bulge value):

```text
closed 0
v <x> <y> <bulge>
v <x> <y> <bulge>
```

The tool prints `PROGRESS <processed> <total>`, then `RESULT <kept points>` or `ERROR <message>`, on its standard output.

### Exit codes (`RcpFlattenCli`)

| Code | Meaning |
|---|---|
| `0` | Success |
| `1` | Error (bad arguments, cannot open or create a project, polyline file invalid) |
| `2` | No point found in the search zone |

## Building

### Prerequisites

- **Visual Studio 2022** (or Build Tools) with the "Desktop development with C++" workload (MSVC v143 toolset, x64).
- **.NET SDK 10** (the plug-in targets `net10.0-windows`).
- **AutoCAD 2027**, installed. The plug-in references `accoremgd.dll` and `acdbmgd.dll` from its installation folder.
- **Autodesk ReCap SDK** v27.0.3 (or compatible). The SDK is not redistributed here; download it from [Autodesk Platform Services](https://aps.autodesk.com/).

### Build

From a PowerShell prompt at the repository root:

```powershell
.\build.ps1 -RecapSdkRoot "C:\path\to\ReCapSDK_v27.0.3"
```

`-RecapSdkRoot` is the folder that contains `Include\`, `Lib\` and `Bin\`. You can set the `RECAP_SDK_ROOT` environment variable instead. If AutoCAD 2027 is not in `C:\Program Files\Autodesk\AutoCAD 2027\`, add `-AutoCadDir "<path>"`.

The script builds the native helper, then the plug-in. The result is in `build\plugin\`:

- `RcpFlatten.dll`: the plug-in,
- `cli\RcpFlattenCli.exe`, with the ReCap SDK DLLs copied next to it by a post-build step.

### Package a release

```powershell
.\package.ps1 -Version 0.1.0
```

This creates `Dist\rcpflatten-v0.1.0-win-x64.zip` (without the Autodesk DLLs) and prints its SHA256.

## Licenses

- Source code in this repository: **MIT** (see [`LICENSE`](LICENSE)).
- Third-party components (ReCap SDK, AutoCAD, VC++ runtime): see [`THIRD_PARTY_NOTICES.md`](THIRD_PARTY_NOTICES.md). In particular, the **ReCap SDK** is governed by the Autodesk EULA and is **not** covered by the MIT license of this repository.

## Known limitations

- Only a single lightweight polyline can be used as the unfolding path.
- The output project contains a single scan (`Flattened`) with no coordinate system assigned.
- The search zone is the same distance on the whole length of the polyline; there is no option to limit the elevation range or the density.
- The plug-in messages are in French only.
