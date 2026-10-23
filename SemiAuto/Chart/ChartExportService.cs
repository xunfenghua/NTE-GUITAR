using System.Text.Json;
using System.Text.Json.Serialization;

namespace Nte.GuitarChart.Core;

public static class ChartExportService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static string ExportChartJson(GuitarChart chart)
    {
        return JsonSerializer.Serialize(chart, JsonOptions);
    }

    public static GuitarChart? ImportChartJson(string json)
    {
        return JsonSerializer.Deserialize<GuitarChart>(json, JsonOptions);
    }
}
