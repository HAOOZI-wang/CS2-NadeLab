using NadeLab.Core;
static class MenuHoldTests
{
    public static void Run(Action<string,Action> test,Action<bool,string> assert)
    {
        test("Native controls advertise game actions without assuming personal bindings",()=>
        {
            foreach(var step in Enum.GetValues<WorkflowStep>())
            {
                var f=WorkflowModel.Resolve(new(Placement:step==WorkflowStep.Placement,Loading:step==WorkflowStep.Loading,HasTarget:true,HasOrigin:true,HasCandidate:true));
                string text=NativeControlText.Keys(f,false)+NativeControlText.Keys(f,true);
                assert(!text.Contains("F1") && text.Contains("请从启动器连接输入模块") && !text.Contains("静步+使用 上"),"native fallback should explain missing companion without obsolete navigation");
                assert(PanelText.Shortcuts(NativeControlText.Keys(f,false))==NativeControlText.Keys(f,false),"native footer exceeded its budget");
                assert(!NativeControlText.Keys(f,false).Contains("静步+使用"),"fallback advertised obsolete reposition chord");
            }
        });
        test("Native presentation preserves dummy identity and personal notes",()=>
        {
            string content=DummyRoster.Label(4)+"：不闪\n收藏 F1 / E / R / V · 按E是我的个人备注";
            var flow=WorkflowModel.Resolve(new(HasTarget:true,HasOrigin:true,HasCandidate:true));
            foreach(bool menu in new[]{false,true})
            {
                var shown=NativeControlText.Panel(content,flow,menu);
                assert(System.Text.Encoding.UTF8.GetBytes(shown.Text).SequenceEqual(System.Text.Encoding.UTF8.GetBytes(content)),
                    "native shortcut presentation changed a dummy label or personal note");
                assert(!shown.Keys.Contains("F1") && shown.Keys.Contains(menu ? "输入模块" : "静步+"),"native footer inherited personal key text");
            }
        });
        test("Menu chord waits, fires once and requires full release",()=>
        {
            var g=new MenuHold();assert(!g.Observe(10,true,true),"instant open");
            assert(!g.Observe(10.5f,true,true),"early open");assert(g.Observe(10.7f,true,true),"held chord missing");
            assert(!g.Observe(13,true,true),"repeated while held");g.Observe(14,false,true);
            g.Observe(15,true,true);assert(g.Observe(16,true,true),"release did not rearm");
        });
        test("Blocked and reset menu chords cannot become delayed actions",()=>
        {
            var g=new MenuHold();g.Observe(1,true,false);assert(!g.Observe(3,true,true),"blocked hold became action");
            g.Observe(4,false,true);g.Observe(5,true,true);g.Reset();assert(!g.Observe(6,true,true),"old lifecycle completed");
            assert(!g.Observe(float.NaN,true,true),"NaN triggered");assert(!g.Observe(4,true,true),"clock reversal triggered");
        });
    }
}
