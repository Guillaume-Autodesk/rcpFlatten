using System.Globalization;
using System.Text;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using Autodesk.AutoCAD.Windows;
using RcpFlatten.Ui;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;

[assembly: CommandClass(typeof(RcpFlatten.FlattenCommand))]

namespace RcpFlatten;

/// <summary>Values remembered between two runs of the command (same AutoCAD session).</summary>
internal static class FlattenSettings
{
    public static double Left { get; set; } = 5.0;
    public static double Right { get; set; } = 5.0;
    public static string LastInput { get; set; } = "";
}

public class FlattenCommand
{
    private static PaletteSet? _palette;
    private static FlattenPanel? _panel;

    /// <summary>Palette version, docked on the right by default. A polyline selected before the command is used as the path.</summary>
    [CommandMethod("RCPFLATTEN", CommandFlags.UsePickSet)]
    public void Flatten()
    {
        var doc = AcApp.DocumentManager.MdiActiveDocument;
        if (doc == null)
            return;

        bool first = _palette == null;
        if (_palette == null || _panel == null)
        {
            _panel = new FlattenPanel();
            _palette = new PaletteSet("rcpFlatten", "RCPFLATTEN", new Guid("7b1e5c2a-4f3d-4e8b-9a61-2c5d0f8e1b47"))
            {
                Style = PaletteSetStyles.ShowCloseButton | PaletteSetStyles.ShowAutoHideButton | PaletteSetStyles.Snappable,
                MinimumSize = new System.Drawing.Size(340, 420),
                Size = new System.Drawing.Size(380, 720),
                DockEnabled = DockSides.Left | DockSides.Right,
            };
            _palette.AddVisual("Aplatir", _panel);
            var palette = _palette;
            _panel.CloseRequested += (_, _) => palette.Visible = false;
        }

        // First display of the session: dock on the right. After that AutoCAD remembers where the user moved it.
        // The dock side is only honoured once the palette is visible.
        _palette.Visible = true;
        if (first)
            _palette.Dock = DockSides.Right;
        _panel.RefreshUnits();

        var implied = doc.Editor.SelectImplied();
        if (implied.Status == PromptStatus.OK && implied.Value.Count == 1)
            _panel.TrySetPolyline(implied.Value[0].ObjectId);
        doc.Editor.SetImpliedSelection(Array.Empty<ObjectId>());
    }

    /// <summary>Command-line version, for scripts and macros.</summary>
    [CommandMethod("-RCPFLATTEN")]
    public void FlattenCommandLine()
    {
        var doc = AcApp.DocumentManager.MdiActiveDocument;
        if (doc == null)
            return;
        var ed = doc.Editor;

        var polylineId = PickPolyline(ed);
        if (polylineId.IsNull)
            return;
        var polyline = PolylinePath.Read(doc.Database, polylineId);
        if (polyline.Error != null)
        {
            ed.WriteMessage($"\n{polyline.Error}");
            return;
        }

        double left = FlattenSettings.Left, right = FlattenSettings.Right;
        if (!PromptDistance(ed, "\nDistance de recherche à gauche de la polyligne", ref left))
            return;
        if (!PromptDistance(ed, "\nDistance de recherche à droite de la polyligne", ref right))
            return;
        if (left + right <= 0.0)
        {
            ed.WriteMessage("\nAu moins une des deux distances doit être supérieure à zéro.");
            return;
        }
        FlattenSettings.Left = left;
        FlattenSettings.Right = right;

        var input = ed.GetFileNameForOpen(new PromptOpenFileOptions("\nSélectionnez le RCP d'origine")
        {
            Filter = "Nuage de points ReCap (*.rcp)|*.rcp",
            InitialFileName = FlattenSettings.LastInput,
        });
        if (input.Status != PromptStatus.OK)
            return;
        FlattenSettings.LastInput = input.StringResult;

        var output = ed.GetFileNameForSave(new PromptSaveFileOptions("\nIndiquez le RCP à créer")
        {
            Filter = "Nuage de points ReCap (*.rcp)|*.rcp",
            InitialFileName = PolylinePath.SuggestOutput(input.StringResult),
        });
        if (output.Status != PromptStatus.OK)
            return;

        if (PolylinePath.SamePath(input.StringResult, output.StringResult))
        {
            ed.WriteMessage("\nLe RCP de sortie doit être différent du RCP d'origine.");
            return;
        }
        if (!File.Exists(CliRunner.ExePath))
        {
            ed.WriteMessage($"\nExécutable introuvable: {CliRunner.ExePath}");
            return;
        }

        string polyFile = polyline.WriteTempFile();
        try
        {
            ed.WriteMessage("\nAplatissement en cours...");
            var stage = FlattenStage.Reading;
            int lastPercent = -1;
            var outcome = CliRunner.Run(new FlattenRequest(input.StringResult, output.StringResult, polyFile, left, right), p =>
            {
                int percent = (int)(p.Fraction * 100);
                if (p.Stage != stage)
                {
                    stage = p.Stage;
                    lastPercent = -1;
                }
                if (percent / 10 == lastPercent / 10)
                    return;
                lastPercent = percent;
                ed.WriteMessage(p.Stage == FlattenStage.Reading
                    ? $"\n  Lecture des points : {percent} %"
                    : $"\n  Indexation du nuage : {percent} %");
            }, CancellationToken.None);

            ed.WriteMessage("\n" + Messages.Describe(outcome, output.StringResult).Text);
        }
        finally
        {
            try { File.Delete(polyFile); } catch (IOException) { }
        }
    }

    internal static ObjectId PickPolyline(Editor ed)
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
}

/// <summary>A polyline read from the drawing, in the exchange format of RcpFlattenCli.</summary>
internal sealed class PolylinePath
{
    public string? Error { get; private init; }
    public int Vertices { get; private init; }
    public bool Closed { get; private init; }
    public double Length { get; private init; }
    private string _content = "";

    // Exchange format read by RcpFlattenCli:
    //   closed <0|1>
    //   v <x> <y> <bulge>
    public static PolylinePath Read(Database db, ObjectId id)
    {
        using var tr = db.TransactionManager.StartOpenCloseTransaction();
        if (tr.GetObject(id, OpenMode.ForRead) is not Autodesk.AutoCAD.DatabaseServices.Polyline pline)
            return new PolylinePath { Error = "Seule une polyligne 2D est acceptée." };

        // Vertices are expressed in the OCS: it must match the WCS in plan
        if (!pline.Normal.IsEqualTo(Vector3d.ZAxis))
            return new PolylinePath { Error = "La polyligne doit être dessinée dans le plan XY du SCG (normale = Z)." };
        if (pline.NumberOfVertices < 2)
            return new PolylinePath { Error = "La polyligne doit avoir au moins deux sommets." };

        var sb = new StringBuilder();
        sb.Append(CultureInfo.InvariantCulture, $"closed {(pline.Closed ? 1 : 0)}\n");
        for (int i = 0; i < pline.NumberOfVertices; i++)
        {
            var p = pline.GetPoint2dAt(i);
            double bulge = pline.GetBulgeAt(i);
            sb.Append(CultureInfo.InvariantCulture, $"v {p.X:R} {p.Y:R} {bulge:R}\n");
        }

        return new PolylinePath
        {
            Vertices = pline.NumberOfVertices,
            Closed = pline.Closed,
            Length = pline.Length,
            _content = sb.ToString(),
        };
    }

    public string WriteTempFile()
    {
        string path = Path.Combine(Path.GetTempPath(), $"rcpflatten_{Guid.NewGuid():N}.txt");
        File.WriteAllText(path, _content, Encoding.ASCII);
        return path;
    }

    public static string SuggestOutput(string input) =>
        Path.Combine(Path.GetDirectoryName(input) ?? "", Path.GetFileNameWithoutExtension(input) + "_flat.rcp");

    public static bool SamePath(string a, string b) =>
        string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);
}

internal enum MessageKind
{
    Success,
    Warning,
    Error,
}

internal static class Messages
{
    public static (MessageKind Kind, string Text) Describe(FlattenOutcome outcome, string output)
    {
        if (outcome.Cancelled)
            return (MessageKind.Warning, "Aplatissement arrêté. Aucun fichier n'a été conservé.");
        return outcome.ExitCode switch
        {
            FlattenOutcome.ExitOk => (MessageKind.Success, $"Terminé : {FormatCount(outcome.Points)} points écrits dans {output}"),
            FlattenOutcome.ExitNoPoint => (MessageKind.Warning,
                "Aucun point n'a été trouvé dans la zone de recherche de la polyligne. " +
                "Vérifiez que la polyligne est dans le même système de coordonnées que le nuage de points."),
            _ => (MessageKind.Error, $"Échec de l'aplatissement : {outcome.Error ?? $"code de sortie {outcome.ExitCode}"}"),
        };
    }

    public static string FormatCount(string? points) =>
        long.TryParse(points, CultureInfo.InvariantCulture, out var n) ? n.ToString("N0", CultureInfo.CurrentCulture) : points ?? "?";
}
