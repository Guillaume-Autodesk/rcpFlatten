using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Application;

namespace RcpFlatten.Ui;

/// <summary>RCPFLATTEN palette content: every parameter of the command, plus the conversion progress.</summary>
public partial class FlattenPanel : UserControl
{
    private const string RcpFilter = "Nuage de points ReCap (*.rcp)|*.rcp";
    private const string MessageTitle = "Aplatir un nuage de points";

    private PolylinePath? _polyline;
    private bool _showErrors;
    private bool _outputEdited;
    private bool _settingOutput;
    private string? _outputConfirmed;      // path whose overwrite was confirmed by the save dialog
    private CancellationTokenSource? _infoCancellation;

    private CancellationTokenSource? _runCancellation;
    private string? _lastOutput;
    private readonly Stopwatch _elapsed = new();
    private readonly DispatcherTimer _elapsedTimer;

    /// <summary>Raised by the close button of the header: the host hides the palette.</summary>
    public event EventHandler? CloseRequested;

    public FlattenPanel()
    {
        InitializeComponent();

        _elapsedTimer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => UpdateElapsed(), Dispatcher);
        _elapsedTimer.Stop();

        LeftBox.Text = FormatDistance(FlattenSettings.Left);
        RightBox.Text = FormatDistance(FlattenSettings.Right);
        InputBox.Text = FlattenSettings.LastInput;
        SetPolylineDot(selected: false);
        RefreshUnits();

        AcApp.DocumentManager.DocumentBecameCurrent += (_, _) => RefreshUnits();
    }

    private bool IsRunning => _runCancellation != null;

    private static Document? ActiveDocument => AcApp.DocumentManager.MdiActiveDocument;

    // ----- Units ----------------------------------------------------------------------------

    private string _unit = "";
    private double _step = 1;

    /// <summary>Unit suffix and stepper increment, from the INSUNITS of the active drawing.</summary>
    public void RefreshUnits()
    {
        var units = ActiveDocument?.Database.Insunits ?? UnitsValue.Undefined;
        (_unit, _step) = units switch
        {
            UnitsValue.Millimeters => ("mm", 100.0),
            UnitsValue.Centimeters => ("cm", 50.0),
            UnitsValue.Meters => ("m", 0.5),
            UnitsValue.Kilometers => ("km", 0.1),
            UnitsValue.Inches => ("in", 12.0),
            UnitsValue.Feet => ("ft", 1.0),
            UnitsValue.Yards => ("yd", 1.0),
            UnitsValue.Miles => ("mi", 0.1),
            _ => ("", 1.0),
        };
        LeftUnit.Text = RightUnit.Text = _unit;
        LeftUnit.Visibility = RightUnit.Visibility = _unit.Length > 0 ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
    }

    private string WithUnit(double value) => _unit.Length > 0 ? $"{FormatDistance(value)} {_unit}" : FormatDistance(value);

    // ----- Polyline -------------------------------------------------------------------------

    public void TrySetPolyline(ObjectId id)
    {
        var doc = ActiveDocument;
        if (doc == null)
            return;

        var polyline = PolylinePath.Read(doc.Database, id);
        if (polyline.Error != null)
        {
            _polyline = null;
            PolylineSummary.Text = "Aucune polyligne sélectionnée";
            SetPolylineDot(selected: false);
            SetHelper(PolylineHelper, polyline.Error, error: true);
            return;
        }

        _polyline = polyline;
        RefreshUnits();
        PolylineSummary.Text = string.Format(CultureInfo.CurrentCulture, "Polyligne {0} · {1} sommets · longueur {2}",
            polyline.Closed ? "fermée" : "ouverte", polyline.Vertices, WithUnit(polyline.Length));
        SetPolylineDot(selected: true);
        SetHelper(PolylineHelper, "Polyligne 2D dans le plan XY du SCG. Segments et arcs acceptés.", error: false);
    }

    private void SetPolylineDot(bool selected) =>
        PolylineDot.Fill = (Brush)FindResource(selected ? "Weave.Alert.SuccessIcon" : "Weave.OutlineBorder");

    /// <summary>
    /// Runs an editor prompt from the palette. A modeless palette is outside any command, so the prompt
    /// has to be wrapped in a command context.
    /// </summary>
    private static async Task<T?> PromptAsync<T>(Func<Editor, T> prompt)
    {
        var doc = ActiveDocument;
        if (doc == null)
            return default;

        T? result = default;
        await AcApp.DocumentManager.ExecuteInCommandContextAsync(_ =>
        {
            result = prompt(doc.Editor);
            return Task.CompletedTask;
        }, null);
        return result;
    }

    private async void OnPickPolyline(object sender, RoutedEventArgs e)
    {
        var id = await PromptAsync(FlattenCommand.PickPolyline);
        if (!id.IsNull)
            TrySetPolyline(id);
    }

    // ----- Distances ------------------------------------------------------------------------

    private void OnPickLeft(object sender, RoutedEventArgs e) => PickDistance(LeftBox, "\nDistance de recherche à gauche de la polyligne: ");

    private void OnPickRight(object sender, RoutedEventArgs e) => PickDistance(RightBox, "\nDistance de recherche à droite de la polyligne: ");

    private async void PickDistance(TextBox target, string message)
    {
        var result = await PromptAsync(ed => ed.GetDistance(new PromptDistanceOptions(message) { AllowNegative = false, AllowZero = true }));
        if (result is { Status: PromptStatus.OK })
            target.Text = FormatDistance(result.Value);
    }

    /// <summary>The − and + buttons: Tag is "Left,-1" or "Right,1".</summary>
    private void OnStep(object sender, RoutedEventArgs e)
    {
        var parts = ((string)((Button)sender).Tag).Split(',');
        var box = parts[0] == "Left" ? LeftBox : RightBox;
        int direction = int.Parse(parts[1], CultureInfo.InvariantCulture);

        RefreshUnits();
        TryParseDistance(box.Text, out double value);
        box.Text = FormatDistance(Math.Max(0, Math.Round(value + direction * _step, 6)));
        box.Focus();
        box.CaretIndex = box.Text.Length;
    }

    private static string FormatDistance(double value) => value.ToString("0.###", CultureInfo.CurrentCulture);

    private static bool TryParseDistance(string text, out double value)
    {
        text = text.Trim();
        if (double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out value) ||
            double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value))
            return !double.IsNaN(value) && !double.IsInfinity(value);

        // Drawing units notation (e.g. 5'6" in architectural units)
        try
        {
            value = Autodesk.AutoCAD.Runtime.Converter.StringToDistance(text);
            return true;
        }
        catch (Autodesk.AutoCAD.Runtime.Exception)
        {
            value = 0;
            return false;
        }
    }

    private void OnFieldChanged(object sender, TextChangedEventArgs e)
    {
        if (_showErrors)
            Validate(out _);
    }

    // ----- Files ----------------------------------------------------------------------------

    private void OnBrowseInput(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Sélectionnez le RCP d'origine",
            Filter = RcpFilter,
        };
        SetInitialLocation(dialog, InputBox.Text);
        if (dialog.ShowDialog() == true)
            InputBox.Text = dialog.FileName;
    }

    private void OnBrowseOutput(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Indiquez le RCP à créer",
            Filter = RcpFilter,
            DefaultExt = ".rcp",
            OverwritePrompt = true,
        };
        SetInitialLocation(dialog, OutputBox.Text);
        if (dialog.ShowDialog() != true)
            return;
        _outputEdited = true;
        OutputBox.Text = dialog.FileName;
        _outputConfirmed = dialog.FileName;
    }

    private static void SetInitialLocation(Microsoft.Win32.FileDialog dialog, string current)
    {
        try
        {
            string? dir = string.IsNullOrWhiteSpace(current) ? null : Path.GetDirectoryName(current);
            if (dir != null && Directory.Exists(dir))
            {
                dialog.InitialDirectory = dir;
                dialog.FileName = Path.GetFileName(current);
            }
        }
        catch (ArgumentException) { }
    }

    private void OnInputChanged(object sender, TextChangedEventArgs e)
    {
        string input = InputBox.Text.Trim();

        if (!_outputEdited && input.Length > 0)
        {
            _settingOutput = true;
            try { OutputBox.Text = PolylinePath.SuggestOutput(input); }
            catch (ArgumentException) { }
            _settingOutput = false;
        }

        ShowPathEnd(InputBox);
        LoadInputInfo(input);
        if (_showErrors)
            Validate(out _);
    }

    private void OnOutputChanged(object sender, TextChangedEventArgs e)
    {
        if (!_settingOutput)
            _outputEdited = OutputBox.Text.Trim().Length > 0;
        ShowPathEnd(OutputBox);
        if (_showErrors)
            Validate(out _);
    }

    /// <summary>For a path set by the program, keep the file name visible rather than the drive.</summary>
    private void ShowPathEnd(TextBox box)
    {
        if (box.IsKeyboardFocused)
            return;
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            box.CaretIndex = box.Text.Length;
            box.ScrollToHorizontalOffset(double.MaxValue);
        });
    }

    private const string InputHint = "Tous les points sont lus, à densité maximale, sans les modifications utilisateur (régions, découpes).";

    /// <summary>Shows the point and scan counts of the source (RcpFlattenCli --info, in the background).</summary>
    private void LoadInputInfo(string input)
    {
        _infoCancellation?.Cancel();
        _infoCancellation = null;

        if (!IsExistingRcp(input))
        {
            SetHelper(InputHelper, InputHint, error: false);
            return;
        }

        var cancellation = new CancellationTokenSource();
        _infoCancellation = cancellation;
        SetHelper(InputHelper, "Lecture des informations du nuage…", error: false);

        Task.Run(() => CliRunner.GetInfo(input, cancellation.Token), cancellation.Token).ContinueWith(task =>
        {
            if (cancellation.IsCancellationRequested)
                return;
            var info = task.Status == TaskStatus.RanToCompletion ? task.Result : null;
            SetHelper(InputHelper, info == null
                ? InputHint
                : string.Format(CultureInfo.CurrentCulture, "{0:N0} points · {1} scan{2}", info.Points, info.Scans, info.Scans > 1 ? "s" : ""),
                error: false);
        }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.FromCurrentSynchronizationContext());
    }

    private static bool IsExistingRcp(string path) =>
        path.EndsWith(".rcp", StringComparison.OrdinalIgnoreCase) && File.Exists(path);

    // ----- Validation -----------------------------------------------------------------------

    private sealed record Parameters(double Left, double Right, string Input, string Output);

    private bool Validate(out Parameters parameters)
    {
        parameters = new Parameters(0, 0, "", "");
        bool ok = true;

        if (_polyline == null)
        {
            if (PolylineHelper.Tag as string != "error")
                SetHelper(PolylineHelper, "Sélectionnez une polyligne dans le dessin.", error: true);
            ok = false;
        }

        bool leftOk = TryParseDistance(LeftBox.Text, out double left) && left >= 0;
        bool rightOk = TryParseDistance(RightBox.Text, out double right) && right >= 0;
        LeftField.Tag = leftOk ? null : "error";
        RightField.Tag = rightOk ? null : "error";
        if (!leftOk || !rightOk)
        {
            SetHelper(DistanceHelper, "Saisissez une distance positive ou nulle.", error: true);
            ok = false;
        }
        else if (left + right <= 0)
        {
            LeftField.Tag = RightField.Tag = "error";
            SetHelper(DistanceHelper, "Au moins une des deux distances doit être supérieure à zéro.", error: true);
            ok = false;
        }
        else
            SetHelper(DistanceHelper, "Distance de part et d'autre de la polyligne, dans le sens de dessin, en unités du dessin.", error: false);

        string input = InputBox.Text.Trim();
        if (!IsExistingRcp(input))
        {
            InputField.Tag = "error";
            SetHelper(InputHelper, input.Length == 0 ? "Indiquez le nuage de points à aplatir." : "Ce fichier .rcp est introuvable.", error: true);
            ok = false;
        }
        else
        {
            InputField.Tag = null;
            if (InputHelper.Tag as string == "error")
                LoadInputInfo(input);
        }

        string output = OutputBox.Text.Trim();
        string? outputError = null;
        if (output.Length == 0)
            outputError = "Indiquez le RCP à créer.";
        else
        {
            try
            {
                if (!output.EndsWith(".rcp", StringComparison.OrdinalIgnoreCase))
                    output += ".rcp";
                output = Path.GetFullPath(output);
                if (!Directory.Exists(Path.GetDirectoryName(output)))
                    outputError = "Le dossier de destination n'existe pas.";
                else if (IsExistingRcp(input) && PolylinePath.SamePath(input, output))
                    outputError = "Le RCP de sortie doit être différent du RCP d'origine.";
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                outputError = "Ce chemin n'est pas valide.";
            }
        }
        OutputField.Tag = outputError == null ? null : "error";
        OutputHelper.Visibility = outputError == null ? System.Windows.Visibility.Collapsed : System.Windows.Visibility.Visible;
        if (outputError != null)
        {
            SetHelper(OutputHelper, outputError, error: true);
            ok = false;
        }

        if (ok)
            parameters = new Parameters(left, right, Path.GetFullPath(input), output);
        return ok;
    }

    private static void SetHelper(TextBlock helper, string text, bool error)
    {
        helper.Text = text;
        helper.Tag = error ? "error" : null;
    }

    // ----- Run ------------------------------------------------------------------------------

    private async void OnRun(object sender, RoutedEventArgs e)
    {
        if (IsRunning)
            return;

        _showErrors = true;
        if (!Validate(out var p) || _polyline == null)
            return;

        if (!File.Exists(CliRunner.ExePath))
        {
            ShowAlert(MessageKind.Error, $"Exécutable introuvable : {CliRunner.ExePath}", showFolder: false);
            return;
        }

        if (File.Exists(p.Output) && !string.Equals(_outputConfirmed, p.Output, StringComparison.OrdinalIgnoreCase) &&
            MessageBox.Show($"{p.Output} existe déjà.\nVoulez-vous le remplacer ?", MessageTitle,
                MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes)
            return;

        FlattenSettings.Left = p.Left;
        FlattenSettings.Right = p.Right;
        FlattenSettings.LastInput = p.Input;

        // What is ours to clean up if the run is stopped
        string supportDir = Path.Combine(Path.GetDirectoryName(p.Output)!, Path.GetFileNameWithoutExtension(p.Output) + " Support");
        bool outputExisted = File.Exists(p.Output);
        bool supportExisted = Directory.Exists(supportDir);

        string polyFile = _polyline.WriteTempFile();
        var cancellation = new CancellationTokenSource();
        _runCancellation = cancellation;
        SetRunning(true);

        FlattenOutcome outcome;
        try
        {
            var request = new FlattenRequest(p.Input, p.Output, polyFile, p.Left, p.Right);
            outcome = await Task.Run(() => CliRunner.Run(request,
                progress => Dispatcher.BeginInvoke(DispatcherPriority.Background, () => ShowProgress(progress)),
                cancellation.Token));
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or InvalidOperationException)
        {
            outcome = new FlattenOutcome(1, false, null, ex.Message);
        }
        finally
        {
            try { File.Delete(polyFile); } catch (IOException) { }
            _runCancellation = null;
            cancellation.Dispose();
        }

        // Let the queued progress updates go through before showing the result
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Background);
        SetRunning(false);

        var (kind, text) = Messages.Describe(outcome, Path.GetFileName(p.Output));
        string commandLineText = Messages.Describe(outcome, p.Output).Text;
        if (outcome.Cancelled)
        {
            bool cleaned = true;
            if (!outputExisted)
                cleaned &= TryDelete(() => File.Delete(p.Output));
            if (!supportExisted)
                cleaned &= TryDelete(() => { if (Directory.Exists(supportDir)) Directory.Delete(supportDir, recursive: true); });
            text = outputExisted || supportExisted
                ? "Aplatissement arrêté. Le RCP existant a pu être modifié : il est probablement inutilisable."
                : cleaned ? "Aplatissement arrêté. Aucun fichier n'a été conservé."
                          : "Aplatissement arrêté. Certains fichiers temporaires n'ont pas pu être supprimés.";
            commandLineText = text;
        }

        _lastOutput = kind == MessageKind.Success ? p.Output : null;
        ShowAlert(kind, text, showFolder: _lastOutput != null);
        ActiveDocument?.Editor.WriteMessage($"\n{commandLineText}");
    }

    private static bool TryDelete(Action delete)
    {
        try
        {
            delete();
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private void SetRunning(bool running)
    {
        InputsPanel.IsEnabled = !running;
        RunButton.IsEnabled = !running;
        StopButton.Visibility = running ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
        StopButton.IsEnabled = true;

        if (running)
        {
            AlertPanel.Visibility = System.Windows.Visibility.Collapsed;
            ProgressPanel.Visibility = System.Windows.Visibility.Visible;
            ShowProgress(new FlattenProgress(FlattenStage.Reading, 0));
            _elapsed.Restart();
            UpdateElapsed();
            _elapsedTimer.Start();
        }
        else
        {
            _elapsed.Stop();
            _elapsedTimer.Stop();
            UpdateElapsed();
            Progress.Tag = null;
            ProgressPanel.Visibility = System.Windows.Visibility.Collapsed;
        }
    }

    private void ShowProgress(FlattenProgress progress)
    {
        if (!IsRunning)
            return;
        StageText.Text = progress.Stage == FlattenStage.Reading
            ? "Étape 1 sur 2 : lecture des points"
            : "Étape 2 sur 2 : indexation du nuage";
        PercentText.Text = string.Format(CultureInfo.CurrentCulture, "{0:0} %", progress.Fraction * 100);
        Progress.Value = progress.Fraction;
        // The SDK reports the indexing in coarse steps: keep showing activity between them
        Progress.Tag = progress.Stage == FlattenStage.Indexing && progress.Fraction < 1 ? "busy" : null;
    }

    private void UpdateElapsed()
    {
        var t = _elapsed.Elapsed;
        ElapsedText.Text = "Temps écoulé : " + (t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss"));
    }

    private void OnStop(object sender, RoutedEventArgs e)
    {
        StopButton.IsEnabled = false;
        StageText.Text = "Arrêt en cours…";
        _runCancellation?.Cancel();
    }

    // ----- Result and closing ---------------------------------------------------------------

    private void ShowAlert(MessageKind kind, string text, bool showFolder)
    {
        string key = kind switch
        {
            MessageKind.Success => "Success",
            MessageKind.Warning => "Warning",
            _ => "Error",
        };
        AlertPanel.Background = (Brush)FindResource($"Weave.Alert.{key}Fill");
        AlertPanel.BorderBrush = (Brush)FindResource($"Weave.Alert.{key}Border");
        AlertIcon.Foreground = (Brush)FindResource($"Weave.Alert.{key}Icon");
        AlertIcon.Text = kind switch
        {
            MessageKind.Success => "",   // CheckMark
            MessageKind.Warning => "",   // Warning
            _ => "",                     // ErrorBadge
        };
        AlertText.Text = text;
        OpenFolderButton.Visibility = showFolder ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
        AlertPanel.Visibility = System.Windows.Visibility.Visible;
    }

    private void OnOpenFolder(object sender, RoutedEventArgs e)
    {
        if (_lastOutput == null)
            return;
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe") { ArgumentList = { "/select,", _lastOutput } });
        }
        catch (System.ComponentModel.Win32Exception) { }
    }

    /// <summary>Hiding the palette does not stop a running conversion: the result is shown when it reopens.</summary>
    private void OnClose(object sender, RoutedEventArgs e) => CloseRequested?.Invoke(this, EventArgs.Empty);
}
