rcpFlatten v{{VERSION}} (Windows x64)
==============================

AutoCAD plug-in that unfolds an Autodesk ReCap point cloud (.rcp) along a 2D
polyline and writes the result to a new .rcp:

    X = station along the polyline
    Y = original Z
    Z = signed lateral offset (positive on the left)

Project page: https://github.com/Guillaume-Autodesk/rcpFlatten


Contents of this archive
------------------------

    RcpFlatten.dll           AutoCAD 2027 plug-in (command RCPFLATTEN)
    cli\RcpFlattenCli.exe    Native helper (uses the ReCap SDK)
    LICENSE                  MIT license for rcpFlatten source code
    THIRD_PARTY_NOTICES.md   Notices for third-party components
    README.txt               This file


Required dependencies (NOT included)
------------------------------------

This archive does NOT ship the Autodesk ReCap SDK DLLs because their public
redistribution is not covered by our license.

You MUST have the Autodesk ReCap SDK (v27.0.3 or compatible) installed
locally, then copy into the "cli" folder, next to RcpFlattenCli.exe:

    - all the *.dll files from <ReCapSDK>\Bin\
    - the contents of <ReCapSDK>\Bin\dependencies\
    - the folder <ReCapSDK>\Bin\plugins\  (to be copied as cli\plugins\)

You also need:

    - AutoCAD 2027
    - the Microsoft Visual C++ 2015-2022 Redistributable (x64):
      https://learn.microsoft.com/cpp/windows/redistributing-visual-cpp-files


Quick usage
-----------

    1. In AutoCAD, run NETLOAD and select RcpFlatten.dll
    2. Run the RCPFLATTEN command
    3. Pick the 2D polyline, enter the search distance on the left and on the
       right, then choose the source .rcp and the .rcp to create

The polyline and the point cloud must share the same coordinate system.
The plug-in prompts are in French.

For the full documentation, see the project page:
  https://github.com/Guillaume-Autodesk/rcpFlatten


Licensing summary
-----------------

  - rcpFlatten source code    : MIT license (file LICENSE)
  - Autodesk ReCap SDK        : Autodesk EULA (NOT included in this archive)
  - Microsoft VC++ runtime    : Microsoft license (install the Redistributable)

See THIRD_PARTY_NOTICES.md for details.
