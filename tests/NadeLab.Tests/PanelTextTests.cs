using System.Text;
using NadeLab.Core;

public static class PanelTextTests
{
    public static void Run(Action<string,Action> test,Action<bool,string> assert)
    {
        test("All six root actions remain visible and selectable under the shared text budget",()=>
        {
            var labels=new[]{"当前任务："+new string('烟',100),"目标与站位","假人与寻闪","我的道具库","切换地图","其他设置"};
            for(int selected=0;selected<6;selected++)
            {
                string body=PanelText.MenuBody("NadeLab · 烟雾弹",labels,selected,root:true);
                assert(body.Split('\n').Length==7 && body.Contains("其他设置"),"root action hidden below viewport");
                assert(body==PanelText.Bounded(body) && Encoding.UTF8.GetByteCount(body)<=480,"transport clips the final root row");
                assert(body.Split('\n').Count(line=>line.StartsWith("▶ "))==1,"root selection not unique");
            }
        });
        test("Notifications wrap at word boundaries without splitting decimal angles",()=>
        {
            foreach(string message in new[]{new string('烟',29)+" · 水平105.61°",new string('烟',30)+"·水平105.61°"})
            foreach(string? status in new string?[]{null,"落点有效 · 封烟未确认"})
            {
                string notice=PanelText.Notice(message,status);
                assert(notice.Split('\n').Any(line=>line.Contains("水平105.61°")),"decimal angle split across notification lines");
                assert(Encoding.UTF8.GetByteCount(notice)<=360 && notice.Count(c=>c=='\n')<=2,"boundary wrapping exceeds notification budget");
                if(status is not null) assert(notice.EndsWith(status),"boundary wrapping displaced the reserved status line");
            }
        });
        test("Native menu shows five options and every selected action survives long Unicode labels",()=>
        {
            foreach(string content in new[]{new string('烟',300),string.Concat(Enumerable.Repeat("💥",300))})
            for(int selected=0;selected<12;selected++)
            {
                var labels=Enumerable.Range(0,12).Select(i=>$"选项{i:D2} "+content).ToArray();
                string body=PanelText.MenuBody(content,labels,selected);
                var rows=body.Split('\n');
                assert(rows.Length==6 && rows.Count(row=>row.StartsWith("▶ "))==1,"five-option window or unique selection lost");
                assert(rows.Any(row=>row.StartsWith($"▶ 选项{selected:D2} ")),"selected action was clipped by another label");
                assert(Encoding.UTF8.GetByteCount(body)<=480 && !body.Contains('\uFFFD'),"body exceeded network budget or split Unicode");
                assert(!body.Contains("F1") && !body.Contains("滚轮"),"shortcut text was mixed into the native body");
                string fallback=PanelText.WithKeys(body,PanelText.MenuKeys());
                assert(fallback.Contains($"▶ 选项{selected:D2} ") && fallback.EndsWith(PanelText.MenuKeys()),"fallback lost the active action or controls");
            }
        });
        test("Menu viewport handles small lists stale selection and empty state",()=>
        {
            assert(PanelText.MenuBody("菜单",[],99).Contains("暂无可用操作"),"empty menu missing");
            assert(PanelText.MenuBody("菜单",["A","B"],99).EndsWith("▶ B"),"stale selection was not clamped");
            string first=PanelText.MenuBody("菜单",["A","B","C","D","E","F"],0);
            string last=PanelText.MenuBody("菜单",["A","B","C","D","E","F"],5);
            assert(first.Contains("▶ A") && first.EndsWith("   E") && !first.Contains("   F"),"first viewport incorrect");
            assert(last.Split('\n')[1]=="   B" && last.EndsWith("▶ F"),"last viewport incorrect");
        });
        test("Every shortcut context fits its independent compact two-row label",()=>
        {
            var all=new List<string>();
            foreach(bool placement in new[]{false,true})
            foreach(bool pages in new[]{false,true})
            foreach(bool region in new[]{false,true})
            foreach(bool flash in new[]{false,true})
            foreach(bool root in new[]{false,true})
                all.Add(PanelText.MenuKeys(placement,pages,region,flash,root));
            foreach(bool flying in new[]{false,true})
            {
                all.AddRange(Enum.GetValues<WorkflowStep>().Select(step=>WorkflowKeys.Text(step,flying)));
                foreach(bool searching in new[]{false,true})
                foreach(bool candidate in new[]{false,true}) all.Add(WorkflowKeys.Flash(searching,candidate,flying));
            }
            foreach(string keys in all)
                assert(PanelText.Shortcuts(keys)==keys && keys.Split('\n').Length<=2,"shortcut context was cut or had too many rows");
            bool rejected=false;
            try {PanelText.Shortcuts("第一行\n第二行\n不能静默丢失");} catch(ArgumentException) {rejected=true;}
            assert(rejected && PanelText.Shortcuts(null)=="","invalid footer silently dropped controls or empty footer failed to clear");
        });
        test("Flash objective menu reserves the correct controls under maximum content",()=>
        {
            string keys=PanelText.MenuKeys(flashObjective:true);
            string text=PanelText.Menu(new string('闪',500),Enumerable.Repeat(new string('闪',500),12).ToArray(),8,flashObjective:true);
            assert(text.EndsWith(keys) && Encoding.UTF8.GetByteCount(text)<=480 && text.Count(c=>c=='\n')<=5,"flash menu exceeds display budget");
            assert(keys.Contains("F2要求") && keys.Contains("F4结果") && keys.Contains("V飞行") && !keys.Contains("F2区域"),"flash controls describe point-target actions");
        });
        test("Panel text bounds Chinese and emoji without splitting UTF8",()=>
        {
            string text=PanelText.Bounded(string.Concat(Enumerable.Repeat("烟雾💥\n",200)));
            assert(Encoding.UTF8.GetByteCount(text)<=480 && text.Count(c=>c=='\n')<=6,"network/line budget exceeded");
            assert(!text.Contains('\uFFFD') && Encoding.UTF8.GetString(Encoding.UTF8.GetBytes(text))==text,"invalid Unicode");
        });
        test("Panel rejects markup and controls while preserving footer under long labels",()=>
        {
            var text=PanelText.Menu(new string('烟',500),Enumerable.Repeat("<font>\u0001"+new string('火',500),12).ToArray(),8);
            assert(!text.Contains('<') && !text.Contains('\u0001'),"untrusted control/markup passed");
            assert(text.EndsWith(PanelText.MenuKeys()) && Encoding.UTF8.GetByteCount(text)<=480,"navigation lost to overflow");
        });
        test("Panel navigation wraps both ways and handles stale or empty indexes",()=>
        {
            assert(PanelText.Move(0,5,-1)==4 && PanelText.Move(4,5,1)==0,"wrap failed");
            assert(PanelText.Move(99,0,1)==0 && PanelText.Move(99,5,1)==0,"invalid index failed");
            assert(PanelText.Move(0,5,int.MinValue)>=0,"integer overflow");
        });
        test("Every workflow keeps the complete shortcut footer even with oversized feedback",()=>
        {
            foreach(var step in Enum.GetValues<WorkflowStep>())
            foreach(bool flying in new[]{false,true})
            {
                string keys=WorkflowKeys.Text(step,flying);
                string text=PanelText.WithKeys(string.Concat(Enumerable.Repeat(new string('烟',200)+"\n",10)),keys);
                assert(text.EndsWith(keys) && Encoding.UTF8.GetByteCount(text)<=480 && text.Count(c=>c=='\n')<=5,
                    $"{step}, flying={flying}: shortcut hints truncated or HUD budget exceeded");
                assert(keys.Contains("F1") && (step==WorkflowStep.Loading || keys.Contains("F4")),$"{step}: common active controls missing");
                if(step==WorkflowStep.Placement) assert(keys.Contains("F9") && keys.Contains("F5取消") && !keys.Contains("F3"),"placement controls inconsistent");
                else if(step==WorkflowStep.Loading) assert(keys.Contains("加载进度") && keys.Contains("WASD移动") && !keys.Contains("F3") && !keys.Contains("F5"),"loading advertises unavailable training controls");
                else if(step==WorkflowStep.Region)
                    assert(keys.Contains("F2/F6/F7/F8编辑") && keys.Contains("F3换道具并弃草稿") && !keys.Contains("F5") && !keys.Contains("换站位") && !keys.Contains("重选"),"draft advertises blocked actions");
                else assert(keys.Contains("F2") && keys.Contains("F3") && keys.Contains("F5"),$"{step}: target controls missing");
            }
        });
        test("Flight hints distinguish selectable targets from grounded preparation",()=>
        {
            assert(WorkflowKeys.Text(WorkflowStep.Target,true).Contains("E/F6选点"),"flying target selection hidden");
            assert(WorkflowKeys.Text(WorkflowStep.Origin,true).Contains("先退出飞行落地，再E/F6记站位"),"flying origin capture advertised without landing");
            var result=WorkflowKeys.Text(WorkflowStep.Result,true);
            assert(result.Contains("落地后E/F6就位") && result.Contains("R/F7看方案") && result.Contains("落地后Shift+R/F8换站位"),"flight result omits landing requirement or viewing controls");
            assert(WorkflowKeys.Text(WorkflowStep.Searching,true).Contains("采用前落地"),"flight search adoption omits landing requirement");
        });
        test("Menu and details show wheel navigation and movement without cutting their footer",()=>
        {
            foreach(bool placement in new[]{false,true})
            foreach(bool regionDraft in new[]{false,true})
            {
                var keys=PanelText.MenuKeys(placement,regionDraft:regionDraft);
                string text=PanelText.Menu(new string('烟',100),Enumerable.Repeat(new string('火',200),8).ToArray(),7,placement,regionDraft);
                assert(text.EndsWith(keys) && text.Count(c=>c=='\n')<=5 && Encoding.UTF8.GetByteCount(text)<=480,"menu keys overflow");
                assert(keys.Contains("滚轮") && keys.Contains("WASD") && !keys.Contains("W/S"),"movement keys advertised as navigation");
                assert(!keys.Contains("Shift+E上") && !keys.Contains("Shift+R下"),"removed Shift navigation still advertised");
                assert(placement || keys.Contains("V飞行"),"flight shortcut missing");
                if(!placement && regionDraft) assert(!keys.Contains("F5") && keys.Contains("F3换道具弃草稿"),"draft menu advertises blocked retarget or hides discard");
                if(placement) assert(keys.Contains("F5取消定位") && !keys.Contains("F3"),"placement menu omits cancel or advertises kind switch");
                foreach(var page in PanelText.Pages([new string('烟',1000)]))
                {
                    var detail=PanelText.WithKeys("详情\n"+page,PanelText.MenuKeys(placement,true,regionDraft));
                    assert(detail.EndsWith(PanelText.MenuKeys(placement,true,regionDraft)),"details cut active keys");
                }
            }
        });
        test("Independent notifications retain target status within their own budget",()=>
        {
            string notice=PanelText.Notice(new string('烟',300),"假人 2/4 · HP 50 · 累计伤害 50");
            assert(Encoding.UTF8.GetByteCount(notice)<=360 && notice.Count(c=>c=='\n')<=2,"notification exceeds independent budget");
            assert(notice.Contains("假人 2/4") && notice.EndsWith("伤害 50"),"long feedback hides target status");
            assert(PanelText.Notice(null,null)=="","expired empty feedback must hide");
        });
        test("Notification reserves its final context line before clipping long Chinese and emoji",()=>
        {
            foreach(string primary in new[]{new string('烟',300),string.Concat(Enumerable.Repeat("💥",300))})
            foreach(string secondary in new[]{"末行"+new string('闪',34)+"完成","末行"+string.Concat(Enumerable.Repeat("💥",32))+"完成"})
            {
                string text=PanelText.Notice(primary,secondary);
                assert(text.EndsWith(secondary),"long primary clipped the reserved context line");
                assert(Encoding.UTF8.GetByteCount(text)<=360 && text.Count(c=>c=='\n')<=2,"reserved context overflowed notification budget");
                assert(!text.Contains('\uFFFD') && Encoding.UTF8.GetString(Encoding.UTF8.GetBytes(text))==text,"notification split a Unicode character");
            }
            string longStatus=PanelText.Notice(new string('烟',500),string.Concat(Enumerable.Repeat("💥",500)));
            assert(longStatus.EndsWith('…') && longStatus.Split('\n')[^1].StartsWith("💥"),"long context line disappeared instead of being bounded independently");
            assert(Encoding.UTF8.GetByteCount(longStatus)<=360 && longStatus.Count(c=>c=='\n')<=2,"long context overflowed notification budget");
        });
        test("Timing and preparation blockers exclusively own the feedback area",()=>
        {
            string timing=PanelFeedback.Compose("现在松键","没有停稳","组合已保存","正在预览","已选A");
            assert(timing=="【时机】现在松键","timing was mixed with a blocker or old success");
            string blocker=PanelFeedback.Compose(null,"没有停稳","组合已保存","正在预览","已选A");
            assert(blocker=="【未就绪】没有停稳","blocker was mixed with unrelated success or context");
        });
        test("Ordinary feedback preserves context and falls back to status without implying success",()=>
        {
            string notice=PanelFeedback.Compose(null,null,"组合已加载","要闪 A / 保护 B","当前2个假人");
            assert(notice.StartsWith("【反馈】组合已加载") && notice.EndsWith("【说明】要闪 A / 保护 B") && !notice.Contains("当前2个假人"),"notice did not preserve the higher-priority context line");
            string status=PanelFeedback.Compose(null,null,"已选A",null,"仍在准备");
            assert(status.EndsWith("【状态】仍在准备") && !status.Contains("成功"),"ordinary status was hidden or mislabelled as success");
            string context=PanelFeedback.Compose(null,null,null,"位置已保留","搜索中");
            assert(context=="【说明】位置已保留\n【状态】搜索中","context and status were not separated");
            assert(PanelFeedback.Compose(null,null,null,null,"搜索中")=="【状态】搜索中","status-only feedback used the wrong label");
        });
        test("Feedback deduplicates repeated content and clears when all inputs expire",()=>
        {
            assert(PanelFeedback.Compose(null,null,"已选A","已选A","已选A")=="【反馈】已选A","same notice was repeated as context or status");
            assert(PanelFeedback.Compose(null,null,null,"已选A","已选A")=="【说明】已选A","same context repeated as status");
            assert(PanelFeedback.Compose(null,null,"已选A","已选A","搜索中").EndsWith("【状态】搜索中"),"duplicate context prevented distinct status fallback");
            assert(PanelFeedback.Compose(null,null,null,null,null)=="" && PanelFeedback.Compose("\t",null,"\u0001"," ","\r\n")=="","expired or blank feedback left a visible label");
        });
        test("Feedback labels and mixed Unicode obey the independent notification budget",()=>
        {
            string text=PanelFeedback.Compose(null,null,string.Concat(Enumerable.Repeat("💥",500)),"末行"+new string('闪',28)+"完成",new string('烟',500));
            assert(text.StartsWith("【反馈】") && text.EndsWith("【说明】末行"+new string('闪',28)+"完成"),"labels or long primary displaced the context line");
            assert(Encoding.UTF8.GetByteCount(text)<=360 && text.Count(c=>c=='\n')<=2 && !text.Contains('\uFFFD'),"labelled feedback overflowed or broke Unicode");
            assert(!PanelFeedback.Compose(null,null,"<font>\u0001",null,null).Contains('<'),"feedback passed markup to the panel");
        });
        test("Root menus advertise closing while child menus and details advertise returning",()=>
        {
            assert(PanelText.MenuKeys(root:true).Contains("R关闭") && !PanelText.MenuKeys(root:true).Contains("R返回"),"root menu advertises a nonexistent parent");
            assert(PanelText.MenuKeys().Contains("R返回") && PanelText.MenuKeys(pages:true,root:true).Contains("R返回"),"child or detail return semantics changed");
            assert(PanelText.Menu("主页",["选择目标"],0,root:true).EndsWith(PanelText.MenuKeys(root:true)),"root option menu lost its closing footer");
            assert(PanelText.Menu("主页",[],0,root:true).EndsWith(PanelText.MenuKeys(root:true)),"empty root menu lost its closing footer");
        });
        test("Flash workflow shortcuts follow idle searching and candidate states while preserving their budget",()=>
        {
            foreach(var state in new[]{(Searching:false,Candidate:false),(Searching:true,Candidate:false),(Searching:false,Candidate:true)})
            foreach(bool flying in new[]{false,true})
            {
                string keys=WorkflowKeys.Flash(state.Searching,state.Candidate,flying);
                assert(Encoding.UTF8.GetByteCount(keys)<=360 && keys.Count(c=>c=='\n')<=2,"flash shortcut footer exceeds its reserved budget");
                string text=PanelText.WithKeys(new string('闪',1000),keys);
                assert(text.EndsWith(keys) && Encoding.UTF8.GetByteCount(text)<=480 && text.Count(c=>c=='\n')<=5,"flash footer was clipped by long workflow text");
                foreach(string key in new[]{"F1菜单","F2要求","Shift+R/F8换站位","F3道具","F4结果","F5/Shift+E改爆点"})
                    assert(keys.Contains(key),$"flash footer omits {key}");
                assert(keys.Contains(flying ? "V退出飞行" : "V飞行"),"flash flight toggle describes the wrong state");
                if(state.Searching)
                    assert(keys.StartsWith("E/F6进度或采用") && !keys.Contains("R/F7") && (!flying || keys.Contains("采用前落地")),"searching advertises next candidate or omits the adoption landing condition");
                else if(state.Candidate)
                    assert(keys.Contains(flying ? "落地后E/F6就位 · R/F7看方案" : "E/F6就位 · R/F7下一候选"),"candidate controls omit preparation or flight viewing semantics");
                else
                    assert(keys.StartsWith(flying ? "落地后E/F6搜索" : "E/F6搜索") && !keys.Contains("R/F7"),"idle flash search advertises a nonexistent next candidate or omits landing");
                if(flying) assert(keys.Contains("落地后Shift+R/F8换站位"),"airborne stance replacement omits landing requirement");
            }
            assert(WorkflowKeys.Flash(searching:true,hasCandidate:true).StartsWith("E/F6就位") &&
                WorkflowKeys.Flash(searching:true,hasCandidate:true).Contains("R/F7"),"background search hid published candidate controls");
        });
    }
}
