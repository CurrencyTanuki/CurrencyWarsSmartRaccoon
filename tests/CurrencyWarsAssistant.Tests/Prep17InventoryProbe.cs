using CurrencyWarsAssistant.Advisor;
using CurrencyWarsAssistant.Core;
using CurrencyWarsAssistant.Game;
using CurrencyWarsAssistant.Tasks;
using CurrencyWarsAssistant.Vision;

namespace CurrencyWarsAssistant.Tests;

public sealed class Prep17InventoryProbe
{
    [Fact]
    public async Task Run()
    {
        var root=@"D:\CWAFix-20260814";
        var dd=System.IO.Path.Combine(root,"data","4.4");
        var gd=GameDataCatalogLoader.Load(dd);
        using var charRec=new OpenCvCharacterCardRecognizer(lenientLeadOverCharacterIds:["currency_wars_character_40","currency_wars_character_56","currency_wars_character_72","currency_wars_character_trailblazer"]);
        using var icon=new OpenCvPhase2IconRecognizer();
        var an=new Phase2OperationalScreenshotAnalyzer(charRec,Load(dd,gd),icon,Phase2IconTemplateCatalog.Load(dd),
            new WindowsOfflineOcr("zh-Hans",OfflineOcrRecognitionMode.Fast,maximumConcurrency:4),gd,
            new WindowsOfflineOcr("en-US",OfflineOcrRecognitionMode.Fast,maximumConcurrency:4),enableRobustFallback:false);
        var f=CaptureFrameLoader.LoadFile(System.IO.Path.Combine(root,"tests","CurrencyWarsAssistant.Tests","Fixtures","phase2-live-2026-07-29","preparation-1-7-user.png"));
        var st=await an.AnalyzeAsync(f,"preparation_1_1","inv17",new RunSnapshot{RunId="i17",AsOf=f.CapturedAt},default);
        foreach(var s in st.InventorySlots.Value ?? [])
            System.Console.WriteLine($"[INV] slot={s.SlotIndex} occ={s.Occupancy} kind={s.ItemKind} id={s.ItemId} conf={s.Confidence:F3}");
        System.Console.WriteLine($"[INV] DismantleTool 数量(槽): {(st.InventorySlots.Value??[]).Count(x=>x.ItemId=="currency_wars_equipment_153")}");
    }
    static IReadOnlyList<CharacterCardTemplateDefinition> Load(string dir,GameDataCatalog g){var d=System.IO.Path.Combine(dir,"character-card-templates");return g.CurrencyWarsCharacters.SelectMany(c=>System.IO.Directory.GetFiles(d,$"{c.Id}__*.png").Select(x=>new CharacterCardTemplateDefinition(c.Id,c.Name,x))).ToList();}
}
