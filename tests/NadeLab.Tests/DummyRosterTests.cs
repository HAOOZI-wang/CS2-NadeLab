using System.Text;
using NadeLab.Core;

internal static class DummyRosterTests
{
    public static void Run(Action<string,Action> test,Action<bool,string> assert)
    {
        test("Eight live dummy rows retain H, selection and predictions within the existing panel budget",()=>
        {
            var rows=Enumerable.Range(0,8).Select(index=>new DummyRosterItem(index,index==7,999,999,99999,99.9f,
                Prediction:DummyRoster.Prediction(new(true,GrenadeKind.HighExplosive,0,999,0,0,new(1,3))))).ToArray();
            string text=DummyRoster.Format(rows)!;
            assert(Encoding.UTF8.GetByteCount(text)<=900 && text.Split('\n').Length==9,"eight targets exceed the installed client protocol");
            foreach(var row in rows) assert(text.Contains(DummyRoster.Label(row.LabelIndex)),"a later target disappeared");
            assert(text.Contains("▶H白") && text.Contains("预测未校准") && text.Split('\n').Skip(1).All(line=>line.EndsWith("预雷0–999HP(部分挡)")),"selection or uncertainty/prediction was truncated");
            assert(!text.Contains('\uFFFD'),"UTF8 split while fitting eight targets");
        });
        test("Dummy rows keep measured blindness separate from flash predictions and lifecycle states",()=>
        {
            string prediction=DummyRoster.Prediction(new(true,GrenadeKind.Flash,0,6,0,0,new(0,1)));
            var rows=new[] {new DummyRosterItem(7,true,0,0,80,5,Dead:true,Prediction:prediction),
                new DummyRosterItem(4,false,100,100,0,float.NaN,Preparing:true),
                new DummyRosterItem(5,false,70,40,30,2.5f,Prediction:prediction)};
            string text=DummyRoster.Format(rows)!;
            assert(text.IndexOf("E紫",StringComparison.Ordinal)<text.IndexOf("F青",StringComparison.Ordinal) && text.IndexOf("F青",StringComparison.Ordinal)<text.IndexOf("H白",StringComparison.Ordinal),"stable identities reordered by selection");
            assert(text.Contains("E紫 准备中") && text.Contains("H白 已死亡") && !text.Contains("NaN"),"lifecycle state became misleading HP or blind duration");
            assert(text.Contains("掉血30 致盲2.5s | 预闪0.0–6.0s(挡)"),"measured and predicted flash merged");
            assert(DummyRoster.Format([]) is null,"last removal must hide the roster");
        });
        test("Eight dummy identities stay unique and the ninth is refused under both limits",()=>
        {
            assert(Enumerable.Range(0,8).Select(DummyRoster.Label).Distinct().Count()==8,"duplicate visible identity");
            assert(Enumerable.Range(0,8).Select(DummyRoster.ColorArgb).Distinct().Count()==8 && Enumerable.Range(0,8).All(i=>(DummyRoster.ColorArgb(i)>>24)==255),"duplicate or transparent model color");
            assert(DummyRoster.CanAdd(7,7) && !DummyRoster.CanAdd(8,8),"eighth refused or ninth admitted");
            assert(!DummyRoster.CanAdd(0,8) && DummyRoster.CanAdd(0,7),"another owner can bypass the shared server cap");
            assert(DummyRoster.CanAdd(7,7),"deleting one target did not release capacity");
        });
        test("Flash search retains every live health armor and blindness row instead of replacing it with candidate text",()=>
        {
            var estimate=new EffectEstimate(true,GrenadeKind.Flash,1,6,0,0,new(1,1));
            var rows=Enumerable.Range(0,8).Select(index=>new DummyRosterItem(index,index==7,100-index*5,100-index*3,index*5,2.5f,
                Objective:DummyRoster.FlashContext(FlashRole.MustFlash,null,estimate))).ToArray();
            string text=DummyRoster.Format(rows)!;
            assert(Encoding.UTF8.GetByteCount(text)<=900 && text.Split('\n').Length==9,"flash search crowding hid later targets");
            for(int index=0;index<8;index++)
            {
                string line=text.Split('\n')[index+1];
                assert(line.Contains(DummyRoster.Label(index)) && line.Contains($"{100-index*5}HP 甲{100-index*3}") && line.Contains("致盲2.5s"),"flash objective replaced current measured state");
                assert(line.EndsWith("要闪·预闪1.0–6.0s"),"candidate context lost its prediction qualifier");
            }
            var updated=rows[7] with {Health=12,Armor=7,BlindSeconds=0};
            string later=DummyRoster.Format([updated])!;
            assert(later.Contains("12HP 甲7") && later.Contains("未致盲") && !later.Contains("致盲2.5s"),"unchanged candidate froze live state");
        });
        test("Flash history cannot masquerade as current blindness or turn unknown exposure into zero",()=>
        {
            var recorded=new FlashMeasurement(1,"A红",FlashRole.MustFlash,FlashMeasurementState.ObservedBlind,4.26f,null);
            string context=DummyRoster.FlashContext(FlashRole.MustFlash,recorded,null);
            string text=DummyRoster.Format([new(0,true,100,100,0,0,Objective:context)])!;
            assert(text.Contains("100HP 甲100") && text.Contains("未致盲") && text.Contains("记录闪4.3s"),"historical event duration replaced current recovery state");
            assert(!text.Contains("预测未校准"),"a measured event record was labelled as a prediction");
            assert(DummyRoster.FlashContext(FlashRole.Protect,recorded with {State=FlashMeasurementState.Unknown,EventDuration=null},null)=="不闪·记录未知","missing observation was presented as safe");
            assert(DummyRoster.FlashContext(FlashRole.Protect,recorded with {State=FlashMeasurementState.NoBlindObserved,EventDuration=null},null)=="不闪·记录未见闪","bounded observation became a guaranteed no-flash claim");
            assert(DummyRoster.FlashContext(FlashRole.Protect,null,new(false,GrenadeKind.Flash,0,0,0,0,new(0,1,false)))=="不闪·预未知","invalid geometry became a blocked prediction");
            assert(DummyRoster.FlashContext(FlashRole.Ignore,recorded,null)=="不参与","ignored role acquired an objective verdict");
        });
        test("Current grenade preview wins over old flash search records while preserving role and live state",()=>
        {
            var recorded=new FlashMeasurement(1,"A红",FlashRole.MustFlash,FlashMeasurementState.ObservedBlind,4.26f,null);
            string context=DummyRoster.FlashContext(FlashRole.MustFlash,recorded,null,"预闪0.0–1.0s(挡)");
            string text=DummyRoster.Format([new(0,true,64,37,36,1.2f,Prediction:"预闪0.0–1.0s(挡)",Objective:context)])!;
            assert(text.Contains("64HP 甲37 掉血36 致盲1.2s") && text.Contains("要闪·预闪0.0–1.0s(挡)"),"live health or positioned-grenade preview was hidden by the flash objective");
            assert(text.Contains("预测未校准") && !text.Contains("记录闪"),"old event record covered the current uncalibrated preview");
            assert(DummyRoster.FlashContext(FlashRole.Ignore,recorded,null,"预雷5–10HP")=="不参与·预雷5–10HP","ignored search role incorrectly suppressed a live HE preview");
        });
        test("Selecting H remains visible in the existing scrolling menu",()=>
        {
            var labels=Enumerable.Range(0,8).Select(i=>DummyRoster.Label(i)+" 100HP / 100甲").Concat(["准星选择我的假人","返回假人管理"]).ToArray();
            string text=PanelText.Menu("选择假人",labels,7);
            assert(text.Contains("▶ H白") && text.Contains("8/10") && text.EndsWith(PanelText.MenuKeys()),"last dummy cannot be selected with the normal menu");
        });
    }
}
