using CurrencyWarsAssistant.Advisor;
using CurrencyWarsAssistant.Core;
using CurrencyWarsAssistant.Game;
using CurrencyWarsAssistant.Tasks;
using CurrencyWarsAssistant.Vision;

namespace CurrencyWarsAssistant.Tests;

public sealed class LivePopProbe
{
    [Fact]
    public async Task Run()
    {
        var root=@"D:\CWAFix-20260814";
        string[] frames={"preparation-1-2-blank-board.png","preparation-1-3-gold-23-user.png","preparation-1-4-user-2026-08-01.png","preparation-1-7-user.png","run-171955-preparation-1-3-early.png","run-171955-preparation-1-3-stable.png","run-171955-preparation-entry-transition.png"};
        var dd=System.IO.Path.Combine(root,"data","4.4");
        var gd=GameDataCatalogLoader.Load(dd);
        using var charRec=new OpenCvCharacterCardRecognizer(lenientLeadOverCharacterIds:["currency_wars_character_40","currency_wars_character_56","currency_wars_character_72","currency_wars_character_trailblazer"]);
        using var icon=new OpenCvPhase2IconRecognizer();
        var an=new Phase2OperationalScreenshotAnalyzer(charRec,Load(dd,gd),icon,Phase2IconTemplateCatalog.Load(dd),
            new WindowsOfflineOcr("zh-Hans",OfflineOcrRecognitionMode.Fast,maximumConcurrency:4),gd,
            new WindowsOfflineOcr("en-US",OfflineOcrRecognitionMode.Fast,maximumConcurrency:4),enableRobustFallback:false);
        // warm up OCR（冷启动 first OCR 可能读不准/乱码，先跑一帧预热）
        {
            var wp=System.IO.Path.Combine(root,"tests","CurrencyWarsAssistant.Tests","Fixtures","phase2-live-2026-07-29","preparation-1-4-user-2026-08-01.png");
            if(System.IO.File.Exists(wp)){ var wf=CaptureFrameLoader.LoadFile(wp); await an.AnalyzeAsync(wf,"preparation_1_1","warm",new RunSnapshot{RunId="w",AsOf=wf.CapturedAt},default); }
        }
        foreach(var fn in frames){
            var p=System.IO.Path.Combine(root,"tests","CurrencyWarsAssistant.Tests","Fixtures","phase2-live-2026-07-29",fn);
            if(!System.IO.File.Exists(p)){ System.Console.WriteLine($"[POP] {fn} 缺失"); continue; }
            var f=CaptureFrameLoader.LoadFile(p);
            var st=await an.AnalyzeAsync(f,"preparation_1_1","p:"+fn,new RunSnapshot{RunId="lp",AsOf=f.CapturedAt},default);
            string pop= st.Population.Status==ObservationStatus.Known ? (st.Population.Value.ToString()+" OCR:"+string.Join("/",st.Population.Evidence.Select(e=>e.Summary??""))) : ("Unknown("+string.Join("/",st.Population.Evidence.Select(e=>e.Summary??""))+")");
            string sl = st.StoreLevel.Status==ObservationStatus.Known ? st.StoreLevel.Value.ToString() : "u";
            System.Console.WriteLine($"[POP] {fn}: 人口={pop} 商店Lv={sl} 后台槽位={st.Formation.Value!.Count(x=>x.Zone==FormationZone.Back)}");
        }
    }
    static IReadOnlyList<CharacterCardTemplateDefinition> Load(string dir,GameDataCatalog g){var d=System.IO.Path.Combine(dir,"character-card-templates");return g.CurrencyWarsCharacters.SelectMany(c=>System.IO.Directory.GetFiles(d,$"{c.Id}__*.png").Select(x=>new CharacterCardTemplateDefinition(c.Id,c.Name,x))).ToList();}
}
