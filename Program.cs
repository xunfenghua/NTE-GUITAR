using System.Runtime.InteropServices;
using MidiAutoPlayer.Guitar;
using MidiAutoPlayer.Testing;
using MidiAutoPlayer.UI;

namespace MidiAutoPlayer;

static class Program
{
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AttachConsole(int dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetStdHandle(int nStdHandle);

    private const int STD_OUTPUT_HANDLE = -11;

    [STAThread]
    static int Main(string[] args)
    {
        AppDomain.CurrentDomain.UnhandledException += (s, e) =>
        {
            try { File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "crash.log"), e.ExceptionObject?.ToString() ?? "Unknown error"); } catch { }
        };
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (s, e) =>
        {
            try { File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "crash.log"), e.Exception.ToString()); } catch { }
        };

        if (args.Any(a => a == "--test" || a == "-t"))
        {
            AttachConsole(-1);
            var stdOutHandle = GetStdHandle(STD_OUTPUT_HANDLE);
            if (stdOutHandle != IntPtr.Zero && stdOutHandle != new IntPtr(-1))
            {
                var safeFileHandle = new Microsoft.Win32.SafeHandles.SafeFileHandle(stdOutHandle, false);
                var fileStream = new FileStream(safeFileHandle, FileAccess.Write);
                var standardOutput = new StreamWriter(fileStream, Console.OutputEncoding) { AutoFlush = true };
                Console.SetOut(standardOutput);
            }
            int exitCode = TestRunner.RunAllTests();
            Console.Out.Flush();
            return exitCode;
        }

        if (args.Any(a => a == "--recommended-smoke"))
        {
            AttachConsole(-1);
            var stdOutHandle = GetStdHandle(STD_OUTPUT_HANDLE);
            if (stdOutHandle != IntPtr.Zero && stdOutHandle != new IntPtr(-1))
            {
                var safeFileHandle = new Microsoft.Win32.SafeHandles.SafeFileHandle(stdOutHandle, false);
                var fileStream = new FileStream(safeFileHandle, FileAccess.Write);
                var standardOutput = new StreamWriter(fileStream, Console.OutputEncoding) { AutoFlush = true };
                Console.SetOut(standardOutput);
            }
            return Testing.RecommendedScoreIntegrationSmoke.RunAsync().GetAwaiter().GetResult();
        }

        if (args.Any(a => a == "--diagnose" || a == "-d"))
        {
            AttachConsole(-1);
            var stdOutHandle = GetStdHandle(STD_OUTPUT_HANDLE);
            if (stdOutHandle != IntPtr.Zero && stdOutHandle != new IntPtr(-1))
            {
                var safeFileHandle = new Microsoft.Win32.SafeHandles.SafeFileHandle(stdOutHandle, false);
                var fileStream = new FileStream(safeFileHandle, FileAccess.Write);
                var standardOutput = new StreamWriter(fileStream, Console.OutputEncoding) { AutoFlush = true };
                Console.SetOut(standardOutput);
            }
            int exitCode = GuitarDiagnostics.Run();
            Console.Out.Flush();
            return exitCode;
        }

        if (args.Length > 0 && (args[0] == "--build-icon"))
        {
            AttachConsole(-1);
            var stdOutHandle = GetStdHandle(STD_OUTPUT_HANDLE);
            if (stdOutHandle != IntPtr.Zero && stdOutHandle != new IntPtr(-1))
            {
                var safeFileHandle = new Microsoft.Win32.SafeHandles.SafeFileHandle(stdOutHandle, false);
                var fileStream = new FileStream(safeFileHandle, FileAccess.Write);
                var standardOutput = new StreamWriter(fileStream, Console.OutputEncoding) { AutoFlush = true };
                Console.SetOut(standardOutput);
            }

            string inputPath = args.Length > 1 ? args[1] : Path.Combine(AppContext.BaseDirectory, "input.jpg");
            string outputPath = args.Length > 2 ? args[2] : Path.Combine(AppContext.BaseDirectory, "app.ico");
            outputPath = Path.GetFullPath(outputPath);

            Console.WriteLine($"Building icon from {inputPath} to {outputPath}...");
            Utils.IconBuilder.InspectAndBuild(inputPath, outputPath);
            return 0;
        }

        if (args.Length > 0 && (args[0] == "--create-sample"))
        {
            string outPath = args.Length > 1 ? args[1] : Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "NTE-GUITAR", "示例曲目_小星星.mid");
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPath))!);
            var midi = new Melanchall.DryWetMidi.Core.MidiFile { TimeDivision = new Melanchall.DryWetMidi.Core.TicksPerQuarterNoteTimeDivision(480) };
            var track = new Melanchall.DryWetMidi.Core.TrackChunk();
            track.Events.Add(new Melanchall.DryWetMidi.Core.SetTempoEvent(500_000) { DeltaTime = 0 });

            // C4 C4 G4 G4 A4 A4 G4 (quarter notes = 480 ticks)
            byte[] notes = new byte[] { 60, 60, 67, 67, 69, 69, 67, 65, 65, 64, 64, 62, 62, 60 };
            foreach (var n in notes)
            {
                track.Events.Add(new Melanchall.DryWetMidi.Core.NoteOnEvent((Melanchall.DryWetMidi.Common.SevenBitNumber)n, (Melanchall.DryWetMidi.Common.SevenBitNumber)100) { DeltaTime = 0 });
                track.Events.Add(new Melanchall.DryWetMidi.Core.NoteOffEvent((Melanchall.DryWetMidi.Common.SevenBitNumber)n, (Melanchall.DryWetMidi.Common.SevenBitNumber)0) { DeltaTime = 480 });
            }
            midi.Chunks.Add(track);
            midi.Write(outPath, true);
            Console.WriteLine($"Sample MIDI created: {outPath}");
            return 0;
        }

        if (args.Any(a => a == "--screenshot"))
        {
            string screenshotDir = Path.Combine(Path.GetTempPath(), "NTE-GUITAR", "screenshots");
            Directory.CreateDirectory(screenshotDir);
            string logPath = Path.Combine(screenshotDir, "screenshot_log.txt");
            File.WriteAllText(logPath, "Starting screenshot...\n");
            try
            {
                ApplicationConfiguration.Initialize();

                // 1. MainForm
                using (var form = new MainForm())
                {
                    form.Show();
                    Application.DoEvents();
                    using var bmp = new System.Drawing.Bitmap(form.Width, form.Height);
                    form.DrawToBitmap(bmp, new System.Drawing.Rectangle(0, 0, form.Width, form.Height));
                    bmp.Save(Path.Combine(screenshotDir, "main_form_webview.png"), System.Drawing.Imaging.ImageFormat.Png);
                    File.AppendAllText(logPath, "Saved main_form_webview.png\n");
                    form.Close();
                }

                // 2. ConvertDialog
                var fakeNotes = new List<Models.RawMidiNote>
                {
                    new Models.RawMidiNote { StartUs = 0, DurationUs = 500000, NoteNumber = 60, Velocity = 100, Channel = 0, Program = 0 },
                    new Models.RawMidiNote { StartUs = 500000, DurationUs = 500000, NoteNumber = 64, Velocity = 100, Channel = 0, Program = 0 },
                    new Models.RawMidiNote { StartUs = 1000000, DurationUs = 500000, NoteNumber = 67, Velocity = 100, Channel = 0, Program = 0 },
                };
                var fakeMetas = new List<Models.TrackMetadata>
                {
                    new Models.TrackMetadata { TrackId = 0, Name = "主旋律 Vocal", Program = 73, NoteCount = 3 },
                    new Models.TrackMetadata { TrackId = 1, Name = "钢琴伴奏 Piano", Program = 0, NoteCount = 12 }
                };
                using (var dlg = new ConvertDialog("千本樱.mid", "testhash123", fakeNotes, trackMetas: fakeMetas))
                {
                    dlg.Show();
                    Application.DoEvents();
                    using var bmp = new System.Drawing.Bitmap(dlg.Width, dlg.Height);
                    dlg.DrawToBitmap(bmp, new System.Drawing.Rectangle(0, 0, dlg.Width, dlg.Height));
                    bmp.Save(Path.Combine(screenshotDir, "convert_dialog.png"), System.Drawing.Imaging.ImageFormat.Png);
                    File.AppendAllText(logPath, "Saved convert_dialog.png\n");
                    dlg.Close();
                }

                // 3. WheelCalibrationDialog
                using (var wheelDlg = new WheelCalibrationDialog(new Models.WheelPreset()))
                {
                    wheelDlg.Show();
                    Application.DoEvents();
                    using var bmp = new System.Drawing.Bitmap(wheelDlg.Width, wheelDlg.Height);
                    wheelDlg.DrawToBitmap(bmp, new System.Drawing.Rectangle(0, 0, wheelDlg.Width, wheelDlg.Height));
                    bmp.Save(Path.Combine(screenshotDir, "wheel_dialog.png"), System.Drawing.Imaging.ImageFormat.Png);
                    File.AppendAllText(logPath, "Saved wheel_dialog.png\n");
                    wheelDlg.Close();
                }

                File.AppendAllText(logPath, "All screenshots captured successfully!\n");
            }
            catch (Exception ex)
            {
                File.AppendAllText(logPath, "Exception: " + ex + "\n");
            }
            return 0;
        }

        try
        {
            ApplicationConfiguration.Initialize();
            Application.Run(new MainForm());
            return 0;
        }
        catch (Exception ex)
        {
            File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "crash.log"), ex.ToString());
            Console.Error.WriteLine("FATAL EXCEPTION: " + ex);
            MessageBox.Show($"程序启动发生异常:\n\n{ex.Message}\n\n详细信息请查看 crash.log", "启动错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }
    }
}
