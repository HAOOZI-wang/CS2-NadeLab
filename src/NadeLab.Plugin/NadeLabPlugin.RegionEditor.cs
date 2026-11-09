using System.Drawing;
using System.Numerics;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Utils;
using NadeLab.Core;
using CsTrace=CounterStrikeSharp.API.Modules.Utils.Trace;

namespace NadeLab.Plugin;

public sealed partial class NadeLabPlugin
{
    private bool BeginTargetDraft(CCSPlayerController? p)
    {
        if(!Check(p,out var pawn)) return false;
        var s=Get(p!);
        if(s.Target is not { } target) {Say(p,"先选一个目标中心，再调整允许落点范围。");return false;}
        if(job?.Slot==p!.Slot) {Say(p,"请先从菜单取消本次搜索，再调整目标。");return false;}
        if(!target.IsArea)
        {
            var floor=new NativeWorld(pawn).Sweep(target.Position+new Vector3(0,0,6),target.Position-new Vector3(0,0,6),2);
            if(!floor.Hit || floor.StartSolid || floor.Normal.Z<.7f || MathF.Abs(floor.Position.Z-2-target.Position.Z)>3)
            {Say(p,"当前精确点不在可用地面上，请先从目标设置菜单重选地面目标。");return false;}
        }
        s.Pending=null;s.Arm=null;s.FlightCheck=null;s.Snapshot=null;s.PreparationIssue=null;s.PreparationRevision++;
        s.CueStage=ReleaseStage.None;s.CueUntil=0;s.HudNotice=null;s.NextHudAt=0;
        if(s.RegionDraft is null) {s.RegionDraft=TargetRegionEditor.AsArea(target.Snapshot());s.RegionSelection=0;s.ObjectiveCorner=null;}
        return true;
    }
    private PanelMenu DraftMenu(CCSPlayerController p,string title,out Action<string,Action<CCSPlayerController>> add,[System.Runtime.CompilerServices.CallerMemberName]string identity="")
    {
        var opened=Get(p);int revision=opened.Revision,epoch=grenadeEpoch;
        var menu=NewPanel(title,identity);menu.ExitButton=true;
        add=(label,action)=>menu.AddMenuOption(label,(who,_)=>
        {ClosePanel(who);QueueAction(who,current=>{if(MenuIsCurrent(current,opened,revision,epoch) && opened.RegionDraft is not null) action(current!);});});
        return menu;
    }
    private void OpenRegionEditor(CCSPlayerController? p,int page=0)
    {
        if(p is not null && Get(p).FlashMode) {OpenFlashRequirements(p);return;}
        if(p is not null && Get(p).Kind!=GrenadeKind.Smoke) {OpenGrenadeTargetEditor(p);return;}
        if(!BeginTargetDraft(p)) return;
        var s=Get(p!);var draft=s.RegionDraft!.Value;
        var selections=TargetRegionEditor.Selections(draft);
        if(!selections.Contains(s.RegionSelection)) s.RegionSelection=0;
        int selection=s.RegionSelection;var selected=TargetRegionEditor.Selected(draft,selection);var size=selected.AreaHalfSize!.Value;
        ClearPreview(p!.Slot);
        foreach(int index in selections)
        {
            var region=TargetRegionEditor.Selected(draft,index);
            var color=index<0 ? Color.FromArgb(180,255,90,90) : TargetRegionColor;
            if(index==selection) DrawTargetRegion(p,region,12,color);
            else
            {
                var center=region.Position+Vector3.UnitZ*2;
                Draw(p,[center-Vector3.UnitX*6,center+Vector3.UnitX*6],12,color,.45f);
                Draw(p,[center-Vector3.UnitY*6,center+Vector3.UnitY*6],12,color,.45f);
            }
        }
        string label=selection==0 ? "主允许区" : selection>0 ? $"允许区{selection+1}" : $"排除区{-selection}";
        var menu=DraftMenu(p,$"{label} · {size.X*2:F0}×{size.Y*2:F0}u · 高差±{size.Z:F0}u",out var Add);
        menu.Identity+="/"+page;
        menu.Back=page==0 ? OpenMenu : who=>OpenRegionEditor(who,page-1);
        void Change(CCSPlayerController who,Func<Target,Target> edit,int next=0)
        {var current=Get(who);current.RegionDraft=TargetRegionEditor.Edit(current.RegionDraft!.Value,current.RegionSelection,edit);OpenRegionEditor(who,next);}
        if(page==0)
        {
            Add(s.Start is null ? "应用目标，返回选择投掷站位" : "应用目标并重搜（保留原站位）",ApplyRegion);
            Add("当前区水平放宽25%",who=>Change(who,t=>TargetRegionEditor.Resize(t,1.25f)));
            Add("当前区水平收紧20%",who=>Change(who,t=>TargetRegionEditor.Resize(t,.8f)));
            Add("用准星指定当前区圆形边缘",SetRegionEdge);
            Add("多个落点区 / 排除区 / 封窗标记",who=>OpenRegionEditor(who,1));
            Add("取消全部调整",CancelRegion);
        }
        else if(page==1)
        {
            Add("切换到下一个区（亮圈）",who=>{var c=Get(who);var list=TargetRegionEditor.Selections(c.RegionDraft!.Value);c.RegionSelection=list[(Array.IndexOf(list,c.RegionSelection)+1)%list.Length];OpenRegionEditor(who,1);});
            Add("准星地面添加允许区（最多3个）",who=>AddRegion(who,false));
            Add("准星地面添加排除区（最多2个）",who=>AddRegion(who,true));
            Add(selection==0 ? "主允许区不可删除" : "删除当前区",who=>{var c=Get(who);c.RegionDraft=TargetRegionEditor.Remove(c.RegionDraft!.Value,c.RegionSelection);c.RegionSelection=0;OpenRegionEditor(who,1);});
            Add("形状、高差 / 封窗与观察点",who=>OpenRegionEditor(who,2));
            Add("返回应用 / 取消",who=>OpenRegionEditor(who));
        }
        else
        {
            Add("当前区横向加宽25%",who=>Change(who,t=>TargetRegionEditor.ResizeAxis(t,true,1.25f),2));
            Add("当前区纵向加长25%",who=>Change(who,t=>TargetRegionEditor.ResizeAxis(t,false,1.25f),2));
            Add("当前区允许高差增加8u",who=>Change(who,t=>TargetRegionEditor.Resize(t,1,8),2));
            Add("当前区允许高差减少8u",who=>Change(who,t=>TargetRegionEditor.Resize(t,1,-8),2));
            Add("标记要封住的窗口 / 观察点",OpenObjectiveEditor);
            Add("返回多个区域",who=>OpenRegionEditor(who,1));
        }
        Say(p,"蓝色允许、红色排除；细圈显示当前区，其他十字标中心。12秒后隐藏；排除优先。换弹键可暂时关闭，长按静步+换弹继续编辑，应用后才生效。");
        menu.Open(p);
    }
    private bool PickTargetPoint(CCSPlayerController p,bool ground,out Vector3 point,out Vector3 normal)
    {
        point=normal=default;if(!Check(p,out var pawn)) return false;
        var eye=NativeWorld.FromNative(pawn.AbsOrigin!)+new Vector3(0,0,pawn.ViewOffset.Z);
        var hit=CsTrace.TraceShape(NativeWorld.ToNative(eye),pawn.EyeAngles,pawn,new TraceOptions {InteractsWith=Masks.SolidBrushOnly|Contents.PhysicsProp});
        if(!hit.DidHit() || hit.IsAllSolid || (ground && hit.Normal.Z<.7f))
        {Say(p,ground ? "请瞄准地面再选择此项。" : "请瞄准窗框或门框的表面再选择此项。");return false;}
        point=NativeWorld.FromNative(hit.EndPos);normal=NativeWorld.FromNative(hit.Normal);return true;
    }
    private void AddRegion(CCSPlayerController p,bool exclude)
    {
        var s=Get(p);
        if(s.RegionDraft is { } draft && PickTargetPoint(p,true,out var center,out _))
            try {s.RegionDraft=TargetRegionEditor.Add(draft,center,exclude);s.RegionSelection=exclude ? -s.RegionDraft.Value.Exclusions!.Count : s.RegionDraft.Value.ExtraRegions!.Count;}
            catch(ArgumentException) {Say(p,"未添加：允许区最多3个、排除区最多2个，各区须在主目标1024u以内。");}
        OpenRegionEditor(p,1);
    }
    private void SetRegionEdge(CCSPlayerController p)
    {
        var s=Get(p);
        if(s.RegionDraft is { } draft && PickTargetPoint(p,true,out var edge,out _))
            try {s.RegionDraft=TargetRegionEditor.Edit(draft,s.RegionSelection,t=>TargetRegionEditor.WithEdge(t,edge));}
            catch(ArgumentException) {Say(p,"边缘需在当前区允许高差内，距中心8–256u；原范围保留。");}
        OpenRegionEditor(p);
    }
    private void OpenObjectiveEditor(CCSPlayerController? p)
    {
        if(p is not null && Get(p).Kind!=GrenadeKind.Smoke) {OpenGrenadeTargetEditor(p);return;}
        if(!BeginTargetDraft(p)) return;
        var s=Get(p!);var face=s.RegionDraft!.Value.Objective;
        ClearPreview(p!.Slot);
        if(face is not null)
        {
            Draw(p,face.Outline(),12,Color.FromArgb(160,210,140,255),.55f);
            if(face.ObserverEye is { } eye && face.ObserverView is { } view)
                Draw(p,[eye,eye+Geometry.Direction(view.Pitch,view.Yaw)*24],12,Color.FromArgb(150,240,200,80),.55f);
        }
        var menu=DraftMenu(p,face is null ? "封窗目标 · 尚未标记" : $"封窗标记 {face.HalfWidth*2:F0}×{face.HalfHeight*2:F0}u · {(face.ObserverEye is null ? "未选观察点" : "已选观察点")}",out var Add);
        menu.Back=who=>OpenRegionEditor(who,2);
        Add(s.ObjectiveCorner is null ? "准星记第一角（竖直窗框表面）" : "重选第一角",who=>
        {
            if(PickTargetPoint(who,false,out var point,out var normal))
            {
                if(MathF.Abs(normal.Z)>.35f) Say(who,"第一角须选竖直窗框正面，避免窗台顶部。");
                else {Get(who).ObjectiveCorner=point;Get(who).ObjectiveNormal=normal;Say(who,"已记第一角；再瞄准同一窗框正面的对角，选择第二项。");}
            }
            OpenObjectiveEditor(who);
        });
        Add("准星记对角，建立标记面",who=>
        {
            var current=Get(who);
            if(current.ObjectiveCorner is not { } first) Say(who,"请先记录第一角。");
            else if(PickTargetPoint(who,false,out var second,out _))
                try
                {
                    var objective=SmokeObjective.FromCorners(first,second,current.ObjectiveNormal);
                    var changed=current.RegionDraft!.Value with {Objective=objective};changed.Validate();current.RegionDraft=changed;current.ObjectiveCorner=null;
                }
                catch(ArgumentException) {Say(who,"未建立：两角须在同一正面，宽高16–512u，距落点中心不超过1024u。");}
            OpenObjectiveEditor(who);
        });
        Add("把当前视角记为封烟观察点",who=>
        {
            if(!Check(who,out var pawn)) return;
            var current=Get(who);var d=current.RegionDraft!.Value;
            if(d.Objective is null) Say(who,"先完成两角标记，再记录观察点。");
            else current.RegionDraft=d with {Objective=d.Objective with {ObserverEye=NativeWorld.FromNative(pawn.AbsOrigin!)+new Vector3(0,0,pawn.ViewOffset.Z),ObserverView=new(pawn.EyeAngles.X,pawn.EyeAngles.Y,ThrowMode.Left)}};
            OpenObjectiveEditor(who);
        });
        Add("清除标记面与观察点",who=>{var c=Get(who);c.RegionDraft=c.RegionDraft!.Value with {Objective=null};c.ObjectiveCorner=null;OpenObjectiveEditor(who);});
        Add("重显标记（12秒）",OpenObjectiveEditor);
        Add("返回应用 / 取消全部调整",who=>OpenRegionEditor(who));
        Say(p,"紫框表示想封住的窗口，是搜索的软提示，不强制弹道穿框；封满仍需实投判断。可从菜单开启飞行观察，金色短线表示预设视角；应用后生效。");
        menu.Open(p);
    }
    private void ApplyRegion(CCSPlayerController p)
    {
        var s=Get(p);if(s.RegionDraft is not { } draft) return;
        draft.Validate();Invalidate(p.Slot,s);s.Target=draft.Snapshot();
        Say(p,"已应用落点区域与封窗目标，旧方案已失效；落点有效不代表已验证封满视野。");
        if(s.Start is not null && job is null) Solve(p,"quick");else OpenMenu(p);
    }
    private void CancelRegion(CCSPlayerController p)
    {var s=Get(p);s.RegionDraft=null;s.ObjectiveCorner=null;s.RegionSelection=0;RefreshSelectedGuides(p);OpenMenu(p);}
}
