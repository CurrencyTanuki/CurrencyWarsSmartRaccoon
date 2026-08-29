using System.Linq;
using CurrencyWarsAssistant.Advisor;
using CurrencyWarsAssistant.Core;
using CurrencyWarsAssistant.Game;
using CurrencyWarsAssistant.Tasks;
using CurrencyWarsAssistant.Vision;

namespace CurrencyWarsAssistant.Tests;

public sealed class EqBoxSurvey
{
    [Fact]
    public async Task Run()
    {
        var root = @"D:\CWAFix-20260814";
        using var characterRecognizer = new OpenCvCharacterCardRecognizer(
                lenientLeadOverCharacterIds: ["currency_wars_character_40", "currency_wars_character_56", "currency_wars_character_72", "currency_wars_character_trailblazer"]);
        using var iconRecognizer = new OpenCvPhase2IconRecognizer();
        var dd = System.IO.Path.Combine(root, "data", "4.4");
        var gameData = GameDataCatalogLoader.Load(dd);
        var analyzer = new Phase2OperationalScreenshotAnalyzer(
            characterRecognizer, LoadCharacterTemplates(gameData), iconRecognizer,
            Phase2IconTemplateCatalog.Load(dd),
            new WindowsOfflineOcr("zh-Hans", OfflineOcrRecognitionMode.Fast, maximumConcurrency: 4),
            gameData,
            new WindowsOfflineOcr("en-US", OfflineOcrRecognitionMode.Fast, maximumConcurrency: 4),
            enableRobustFallback: false);
        string[] frames={"125924","130104","132307"};
        foreach(var fn in frames){
            var fp=System.IO.Path.Combine(root,"tests","CurrencyWarsAssistant.Tests","Fixtures","phase2-2026-07-28",fn+".png");
            var frame=CaptureFrameLoader.LoadFile(fp);
            var st=await analyzer.AnalyzeAsync(frame,"preparation_1_1","fixture:"+fn,new RunSnapshot{RunId="diag"+fn,AsOf=frame.CapturedAt},default);
            System.Console.WriteLine($"=== {fn} {frame.Width}x{frame.Height} ===");
            foreach(var f in st.Formation.Value ?? [])
            {
                var eqs=f.FinalEquipmentSlots ?? [];
                var equipped=eqs.Count(s=>s.Occupancy==EquipmentSlotOccupancy.Equipped);
                System.Console.WriteLine($"  F/ B slot{f.SlotIndex} char={f.CharacterId} eq计={equipped} eqs=[{string.Join(',',eqs.Select(s=>s.Occupancy==EquipmentSlotOccupancy.Equipped?s.EquipmentId:"."))}]");
            }
        }
    }
    static IReadOnlyList<CharacterCardTemplateDefinition> LoadCharacterTemplates(GameDataCatalog g)
    {
        var d = System.IO.Path.Combine(@"D:\CWAFix-20260814", "data", "4.4", "character-card-templates");
        return g.CurrencyWarsCharacters.SelectMany(c => System.IO.Directory.GetFiles(d, $"{c.Id}__*.png").Select(f => new CharacterCardTemplateDefinition(c.Id, c.Name, f))).ToList();
    }
}
