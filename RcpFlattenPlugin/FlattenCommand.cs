using System.Diagnostics;
using System.Globalization;
using System.Text;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;

[assembly: CommandClass(typeof(RcpFlatten.FlattenCommand))]

namespace RcpFlatten;

public class FlattenCommand
{
    private const int ExitOk = 0;
    private const int ExitNoPoint = 2;

    private static double _left = 5.0;
    private static double _right = 5.0;
    private static string _lastInput = "";

    [CommandMethod("RCPFLATTEN")]
    public void Flatten()
    {
        var doc = AcApp.DocumentManager.MdiActiveDocument;
        if (doc == null)
            return;
        var ed = doc.Editor;

        var polylineId = PickPolyline(ed);
        if (polylineId.IsNull)
            return;

        if (!PromptDistance(ed, "\nDistance de recherche à gauche de la polyligne", ref _left))
            return;
        if (!PromptDistance(ed, "\nDistance de recherche à droite de la polyligne", ref _right))
            return;
        if (_left + _right <= 0.0)
        {
            ed.WriteMessage("\nAu moins une des deux distances doit être supérieure à zéro.");
            return;
        }

        var input = ed.GetFileNameForOpen(new PromptOpenFileOptions("\nSélectionnez le RCP d'origine")
        {
            Filter = "Nuage de points ReCap (*.rcp)|*.rcp",
            InitialFileName = _lastInput,
        });
        if (input.Status != PromptStatus.OK)
            return;
        _lastInput = input.StringResult;

        var output = ed.GetFileNameForSave(new PromptSaveFileOptions("\nIndiquez le RCP à créer")
        {
            Filter = "Nuage de points ReCap (*.rcp)|*.rcp",
            InitialFileName = Path.Combine(Path.GetDirectoryName(input.StringResult) ?? "",
                Path.GetFileNameWithoutExtension(input.StringResult) + "_flat.rcp"),
        });
        if (output.Status != PromptStatus.OK)
            return;

        if (string.Equals(Path.GetFullPath(input.StringResult), Path.GetFullPath(output.StringResult), StringComparison.OrdinalIgnoreCase))
        {
            ed.WriteMessage("\nLe RCP de sortie doit être différent du RCP d'origine.");
            return;
        }

        string polyFile = Path.Combine(Path.GetTempPath(), $"rcpflatten_{Guid.NewGuid():N}.txt");
        try
        {
            if (!ExportPolyline(ed, doc.Database, polylineId, polyFile))
                return;

            Run(ed, input.StringResult, output.StringResult, polyFile);
        }
        finally
        {
            try { File.Delete(polyFile); } catch (IOException) { }
        }
    }

    private static ObjectId PickPolyline(Editor ed)
    {
        var options = new PromptEntityOptions("\nSélectionnez la polyligne 2D le long de laquelle aplatir le nuage de points: ")
        {
            AllowNone = false,
        };
        options.SetRejectMessage("\nSeule une polyligne 2D est acceptée.");
        options.AddAllowedClass(typeof(Autodesk.AutoCAD.DatabaseServices.Polyline), true);

        var result = ed.GetEntity(options);
        return result.Status == PromptStatus.OK ? result.ObjectId : ObjectId.Null;
    }

    private static bool PromptDistance(Editor ed, string message, ref double value)
    {
        var options = new PromptDistanceOptions($"{message} <{value.ToString("0.###", CultureInfo.CurrentCulture)}>: ")
        {
            AllowNegative = false,
            AllowNone = true,
            AllowZero = true,
            DefaultValue = value,
            UseDefaultValue = true,
        };
        var result = ed.GetDistance(options);
        if (result.Status != PromptStatus.OK)
            return false;
        value = result.Value;
        return true;
    }

    // Exchange format read by RcpFlattenCli:
    //   closed <0|1>
    //   v <x> <y> <bulge>
    private static bool ExportPolyline(Editor ed, Database db, ObjectId id, string path)
    {
        using var tr = db.TransactionManager.StartTransaction();
        var pline = (Autodesk.AutoCAD.DatabaseServices.Polyline)tr.GetObject(id, OpenMode.ForRead);

        // Vertices are expressed in the OCS: it must match the WCS in plan
        if (!pline.Normal.IsEqualTo(Vector3d.ZAxis))
        {
            ed.WriteMessage("\nLa polyligne doit être dessinée dans le plan XY du SCG (normale = Z).");
            return false;
        }
        if (pline.NumberOfVertices < 2)
        {
            ed.WriteMessage("\nLa polyligne doit avoir au moins deux sommets.");
            return false;
        }

        var sb = new StringBuilder();
        sb.Append(CultureInfo.InvariantCulture, $"closed {(pline.Closed ? 1 : 0)}\n");
        for (int i = 0; i < pline.NumberOfVertices; i++)
        {
            var p = pline.GetPoint2dAt(i);
            double bulge = pline.GetBulgeAt(i);
            sb.Append(CultureInfo.InvariantCulture, $"v {p.X:R} {p.Y:R} {bulge:R}\n");
        }
        File.WriteAllText(path, sb.ToString(), Encoding.ASCII);

        tr.Commit();
        return true;
    }

    private static void Run(Editor ed, string input, string output, string polyFile)
    {
        string exe = Path.Combine(Path.GetDirectoryName(typeof(FlattenCommand).Assembly.Location) ?? "", "cli", "RcpFlattenCli.exe");
        if (!File.Exists(exe))
        {
            ed.WriteMessage($"\nExécutable introuvable: {exe}");
            return;
        }

        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(exe)!,
        };
        foreach (var arg in new[]
                 {
                     "--in", input,
                     "--out", output,
                     "--poly", polyFile,
                     "--left", _left.ToString("R", CultureInfo.InvariantCulture),
                     "--right", _right.ToString("R", CultureInfo.InvariantCulture),
                 })
            psi.ArgumentList.Add(arg);

        ed.WriteMessage("\nAplatissement en cours...");

        string? error = null;
        string? result = null;
        using var process = Process.Start(psi)!;
        string? line;
        while ((line = process.StandardOutput.ReadLine()) != null)
        {
            if (line.StartsWith("PROGRESS ", StringComparison.Ordinal))
            {
                var parts = line.Split(' ');
                if (parts.Length == 3 && double.TryParse(parts[1], CultureInfo.InvariantCulture, out var done) &&
                    double.TryParse(parts[2], CultureInfo.InvariantCulture, out var total) && total > 0)
                    ed.WriteMessage($"\n  {done / total:P0} des points parcourus");
            }
            else if (line.StartsWith("ERROR ", StringComparison.Ordinal))
                error = line[6..];
            else if (line.StartsWith("RESULT ", StringComparison.Ordinal))
                result = line[7..];
        }
        process.WaitForExit();

        switch (process.ExitCode)
        {
            case ExitOk:
                ed.WriteMessage($"\nTerminé: {result} points écrits dans {output}");
                break;
            case ExitNoPoint:
                ed.WriteMessage("\nAucun point n'a été trouvé dans la zone de recherche de la polyligne. " +
                                "Vérifiez que la polyligne est dans le même système de coordonnées que le nuage de points.");
                break;
            default:
                ed.WriteMessage($"\nÉchec de l'aplatissement: {error ?? $"code de sortie {process.ExitCode}"}");
                break;
        }
    }
}
