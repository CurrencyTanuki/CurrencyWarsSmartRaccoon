using CurrencyWarsAssistant.Advisor;
using CurrencyWarsAssistant.Core;
using CurrencyWarsAssistant.Game;
using CurrencyWarsAssistant.Tasks;
using CurrencyWarsAssistant.Vision;

namespace CurrencyWarsAssistant.Tests;

public sealed class SlotDeriveProbe
{
    [Fact]
    public async Task Run()
    {
        var root=@"D:\CWAFix-20260814";
        string[] frames={"125924.png","130104.png","130112.png","130123.png","132307.png","132328.png"};
        var dd=System.IO.Path.Combine(root,"data","4.4");
        var gd=GameDataCatalogLoader.Load(dd);
        using var charRec=new OpenCvCharacterCardRecognizer(lenientLeadOverCharacterIds:["currency_wars_character_40","currency_wars_character_56","currency_wars_character_72","currency_wars_character_trailblazer"]);
        using var icon=new OpenCvPhase2IconRecognizer();
        var an=new Phase2OperationalScreenshotAnalyzer(charRec,Load(dd,gd),icon,Phase2IconTemplateCatalog.Load(dd),
            new WindowsOfflineOcr("zh-Hans",OfflineOcrRecognitionMode.Fast,maximumConcurrency:4),gd,
            new WindowsOfflineOcr("en-US",OfflineOcrRecognitionMode.Fast,maximumConcurrency:4),enableRobustFallback:false);
        foreach(var fn in frames){
            var f=CaptureFrameLoader.LoadFile(System.IO.Path.Combine(root,"tests","CurrencyWarsAssistant.Tests","Fixtures","phase2-2026-07-28",fn));
            var st=await an.AnalyzeAsync(f,"preparation_1_1","s:"+fn,new RunSnapshot{RunId="sd",AsOf=f.CapturedAt},default);
            string pop= st.Population.Status==ObservationStatus.Known ? st.Population.Value.ToString() : (st.Population.Status.ToString()+" OCR:"+string.Join("/",st.Population.Evidence.Select(e=>e.Summary??"")));
            string sl = st.StoreLevel.Status==ObservationStatus.Known ? st.StoreLevel.Value.ToString() : (st.StoreLevel.Status.ToString()+" OCR:"+string.Join("/",st.StoreLevel.Evidence.Select(e=>e.Summary??"")));
            System.Console.WriteLine($"[DERIVE] {fn} population={pop} storeLevel={sl} 小计pop-sl={ (pop!="Unknown"&&sl!="Unknown" ? (int.Parse(pop)-int.Parse(sl)).ToString():"?") }");
            System.Console.WriteLine($"[DERIVE] {fn} -> 前台+后台 阵容槽数 = {st.Formation.Value!.Count(x=>x.Zone==FormationZone.Front||x.Zone==FormationZone.Back)} , 后台区槽位 = {st.Formation.Value!.Count(x=>x.Zone==FormationZone.Back)}");
        }
    }
    static IReadOnlyList<CharacterCardTemplateDefinition> Load(string dir,GameDataCatalog g){var d=System.IO.Path.Combine(dir,"character-card-templates");return g.CurrencyWarsCharacters.SelectMany(c=>System.IO.Directory.GetFiles(d,$"{c.Id}__*.png").Select(x=>new CharacterCardTemplateDefinition(c.Id,c.Name,x))).ToList();}
}
