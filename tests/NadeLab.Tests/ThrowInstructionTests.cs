using NadeLab.Core;

public static class ThrowInstructionTests
{
    public static void Run(Action<string,Action> test,Action<bool,string> assert)
    {
        test("Slow run-jump short instructions retain Shift for every throw strength",()=>
        {
            var families=ThrowCatalog.Families(new PhysicsProfile(),new SearchOptions {IncludeActions=true,ForwardSpeed=245},2000,0);
            foreach(var mode in Enum.GetValues<ThrowMode>())
            {
                var slow=families.Single(a=>a.EffectiveAction==ThrowAction.RunJump && a.Mode==mode && MathF.Abs(a.ForwardSpeed-127.4f)<.01f);
                var fast=families.Single(a=>a.EffectiveAction==ThrowAction.RunJump && a.Mode==mode && a.ForwardSpeed==245);
                assert(slow.Instruction.Contains("W+Shift"),$"slow {mode} run-jump omitted Shift: {slow.Instruction}");
                assert(fast.Instruction.Contains("W") && !fast.Instruction.Contains("Shift"),$"full-speed {mode} run-jump asked for slow movement");
                assert(ThrowInstructions.Operation(slow).Contains("W+Shift"),"slow run-jump HUD omitted Shift");
                assert(!ThrowInstructions.Operation(fast).Contains("Shift"),"full-speed run-jump HUD asked for slow movement");
            }
        });
        test("Movement guidance keeps walking running and stationary actions distinct",()=>
        {
            var walk=new Aim(-25,0,ThrowMode.Left,130,ThrowAction.Walk);
            var run=new Aim(-25,0,ThrowMode.Left,250,ThrowAction.Run);
            var jump=new Aim(-25,0,ThrowMode.Left,Action:ThrowAction.Jump);
            assert(ThrowInstructions.Operation(walk).Contains("W+Shift"),"walking guidance omitted the walk modifier");
            assert(ThrowInstructions.Operation(run).Contains("W") && !ThrowInstructions.Operation(run).Contains("Shift"),"running guidance gave walking keys");
            assert(!ThrowInstructions.Operation(jump).Contains("W"),"stationary jump guidance asked the player to move");
        });
    }
}
