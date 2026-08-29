using CurrencyWarsAssistant.Core;
using CurrencyWarsAssistant.Game;
using CurrencyWarsAssistant.Tasks;
using CurrencyWarsAssistant.Vision;
using Xunit.Abstractions;

namespace CurrencyWarsAssistant.Tests;

/// <summary>
/// 装备图标匹配诊断（用户 2026-08-07）：
/// 3-7 帧 Front#0 的 3 个装备区实际有 3 个图标（绿板/红齿轮/绿板），
/// 调 iconRecognizer 识别看匹配结果（TemplateId/Confidence/IsKnown），
/// 定位"区域不对齐"还是"模板匹配分低"。
/// </summary>
public sealed class EquipmentIconMatchDiagnosisTests
{
    private readonly ITestOutputHelper _output;

    public EquipmentIconMatchDiagnosisTests(ITestOutputHelper output)
        => _output = output;

    [Fact]
    public void DiagnoseEquipmentIconMatching()
    {
        var dataDirectory = Path.Combine(RepositoryRoot, "data", "4.4");
        var gameData = GameDataCatalogLoader.Load(dataDirectory);
        var frame = CaptureFrameLoader.LoadFile(Path.Combine(
            RepositoryRoot,
            "tests",
            "CurrencyWarsAssistant.Tests",
            "Fixtures",
            "PageReplay",
            "prep_3_7_142723217.png"));
        using var iconRecognizer = new OpenCvPhase2IconRecognizer();
        var templates = Phase2IconTemplateCatalog.Load(dataDirectory);

        // Front#0 (compact) 与 Back#5 (非 compact) 都诊断
        var cases = new List<(Core.PixelRect Slot, string Label, bool Compact)>
        {
            (Phase2RecognitionRegions.PreparationCharacterSlots1920[0], "Front0", true),
            (Phase2RecognitionRegions.PreparationCharacterSlots1920[4], "Back5", false),
        };
        foreach (var item in cases)
        {
            _output.WriteLine($"=== {item.Label} (compact={item.Compact}) ===");
            var regions = Phase2RecognitionRegions.CharacterEquipmentSlots(
                item.Slot,
                compactFrontLayout: item.Compact);
            var results = iconRecognizer.Recognize(
                frame,
                "advanced-equipment",
                regions,
                templates);
            foreach (var r in results)
            {
                var px = r.Region;
                _output.WriteLine(
                    $"装备区[{r.SlotIndex}] px=({px.X},{px.Y},{px.Width}x{px.Height}) " +
                    $"TemplateId={r.TemplateId ?? "null"} Conf={r.Confidence:F3} " +
                    $"IsKnown={r.IsKnown} 候选=[{string.Join(",", (r.CandidateTemplateIds ?? []).Take(3))}]");
            }

            // HasDetailedForeground 判定（端到端用它先过滤）
            var method = typeof(Phase2OperationalScreenshotAnalyzer).GetMethod(
                "HasDetailedForeground",
                System.Reflection.BindingFlags.NonPublic |
                System.Reflection.BindingFlags.Static);
            foreach (var region in regions)
            {
                var has = (bool)method!.Invoke(null, [frame, region])!;
                _output.WriteLine(
                    $"  装备区前景判定: ({region.X:F3},{region.Y:F3}) => {has}");
            }
        }
    }

    private static string RepositoryRoot =>
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
}
