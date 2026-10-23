using System.Text.Json;

namespace Nte.GuitarChart.Core;

public interface IExistingMidiAdapter
{
    IReadOnlyList<RawMidiNote> LoadAndSelectTargetNotes(string midiPath, string extractionStrategy);
}

public interface IExistingWheelTimingModel : IPositionTimingModel { }

public static class ChartCompilerUsage
{
    public static string CompileToJson(
        string midiPath,
        string extractionStrategy,
        IExistingMidiAdapter midiAdapter,
        IExistingWheelTimingModel wheelTimingModel)
    {
        var targetNotes = midiAdapter.LoadAndSelectTargetNotes(midiPath, extractionStrategy);
        var compiler = new GuitarChartCompiler(wheelTimingModel);
        var chart = compiler.Compile(targetNotes, new ChartCompileOptions
        {
            SourceName = Path.GetFileNameWithoutExtension(midiPath),
            SimultaneousToleranceUs = 20_000,
            UseViterbi = true
        });

        // Preserve the chart and issue list for the UI, export and diagnostics.
        return JsonSerializer.Serialize(chart, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = false
        });
    }
}
