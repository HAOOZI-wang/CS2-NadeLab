using System.Numerics;
using System.Text;
using System.Text.Json.Nodes;
using NadeLab.Core;

internal static class DummyLayoutTests
{
    private const ulong Owner=76561198012345678;
    private static DummyLayoutMember Member(int label=0)=>new(label,new(label*100,64,12),64,new(-10,150,ThrowMode.Left),100,label%2==0 ? FlashRole.MustFlash : FlashRole.Protect);
    private static void WithStore(Action<DummyLayoutStore,string> action)
    {
        string temporary=Path.GetFullPath(Path.Combine(Path.GetTempPath(),"NadeLab-DummyLayouts-"+Guid.NewGuid().ToString("N")));
        try {action(new(temporary),temporary);}
        finally
        {
            string parent=Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar)+Path.DirectorySeparatorChar;
            if(!temporary.StartsWith(parent,StringComparison.OrdinalIgnoreCase) || !Path.GetFileName(temporary).StartsWith("NadeLab-DummyLayouts-",StringComparison.Ordinal)) throw new InvalidOperationException("Unsafe test cleanup path");
            if(Directory.Exists(temporary)) Directory.Delete(temporary,true);
        }
    }
    public static void Run(Action<string,Action> test,Action<bool,string> assert)
    {
        void Reject(Action action,string message)
        {try {action();}catch(InvalidDataException) {return;}throw new Exception(message);}
        test("Dummy combinations persist all eight slots and restore sparse colour IDs with roles and poses",()=>WithStore((store,directory)=>
        {
            assert(store.Read(Owner,"de_mirage").Count==0 && !Directory.Exists(directory),"reading an absent archive created data");
            for(int slot=1;slot<=8;slot++) store.Save(Owner,"de_mirage",slot,[Member(0),Member(3),Member(7) with {Armor=50,Role=FlashRole.Ignore}]);
            var layouts=store.Read(Owner,"de_mirage");
            assert(layouts.Count==8 && layouts.Select(x=>x.Slot).SequenceEqual(Enumerable.Range(1,8)),"one slot replaced another");
            foreach(var layout in layouts)
            {
                assert(layout.Members.Select(x=>x.LabelIndex).SequenceEqual(new[]{0,3,7}),"sparse colours were renumbered");
                assert(layout.Members[0]==Member(0) && layout.Members[1]==Member(3) && layout.Members[2].Armor==50 && layout.Members[2].Role==FlashRole.Ignore,"pose armour or flash requirement did not round-trip");
                assert(layout.SavedAt> DateTimeOffset.UtcNow.AddMinutes(-2),"save timestamp missing");
            }
            assert(Directory.GetFiles(directory,"*.tmp").Length==0,"temporary archive leaked");
        }));
        test("Dummy combination overwrite atomically preserves previous archive and unrelated player data",()=>WithStore((store,directory)=>
        {
            store.Save(Owner,"de_dust2",1,[Member(0)]);string path=store.FilePath(Owner,"de_dust2");byte[] first=File.ReadAllBytes(path);
            string other=Path.Combine(directory,"keep-player-data.txt");File.WriteAllText(other,"keep");
            store.Save(Owner,"de_dust2",2,[Member(3)]);byte[] second=File.ReadAllBytes(path);
            assert(File.ReadAllBytes(path+".bak").SequenceEqual(first),"first replacement did not retain prior archive");
            store.Save(Owner,"de_dust2",1,[Member(7)]);
            assert(File.ReadAllBytes(path+".bak").SequenceEqual(second),"backup was not the immediate previous complete archive");
            assert(store.Read(Owner,"de_dust2").Single(x=>x.Slot==1).Members.Single().LabelIndex==7 && store.Read(Owner,"de_dust2").Single(x=>x.Slot==2).Members.Single().LabelIndex==3,"overwriting one combination discarded another");
            assert(File.ReadAllText(other)=="keep" && DummyLayoutStore.Parse(second,Owner,"de_dust2").Count==2,"unrelated data or original backup damaged");
        }));
        test("Dummy combinations isolate players and maps and refuse path injection",()=>WithStore((store,directory)=>
        {
            store.Save(Owner,"de_mirage",1,[Member(0)]);store.Save(Owner+1,"de_mirage",1,[Member(1)]);store.Save(Owner,"de_dust2",1,[Member(2)]);
            assert(store.Read(Owner,"de_mirage").Single().Members.Single().LabelIndex==0 && store.Read(Owner+1,"de_mirage").Single().Members.Single().LabelIndex==1 && store.Read(Owner,"de_dust2").Single().Members.Single().LabelIndex==2,"map or player archives mixed");
            foreach(string map in new[]{"../de_mirage","de_mirage/../../other","de_mirage;quit","unknown"}) Reject(()=>store.FilePath(Owner,map),"unsafe map became a path");
            Reject(()=>store.FilePath(0,"de_mirage"),"zero Steam ID accepted");
            assert(store.FilePath(Owner,"DE_MIRAGE")==store.FilePath(Owner,"de_mirage"),"allowed map case created duplicate archives");
            byte[] data=File.ReadAllBytes(store.FilePath(Owner,"de_mirage"));
            Reject(()=>DummyLayoutStore.Parse(data,Owner+1,"de_mirage"),"wrong owner read another archive");Reject(()=>DummyLayoutStore.Parse(data,Owner,"de_dust2"),"wrong map read another archive");
        }));
        test("Dummy layout validation rejects nonfinite bounds duplicate labels and overlapping bodies before save",()=>WithStore((store,directory)=>
        {
            var invalid=new[]{Member() with {Feet=new(float.NaN,0,0)},Member() with {Feet=new(40000,0,0)},Member() with {EyeHeight=0},Member() with {EyeHeight=float.PositiveInfinity},
                Member() with {View=new(90,0,ThrowMode.Left)},Member() with {View=new(0,float.NaN,ThrowMode.Left)},Member() with {View=new(0,0,ThrowMode.Right)},
                Member() with {View=new(0,0,ThrowMode.Left,100)},Member() with {Armor=101},Member() with {LabelIndex=8},Member() with {Role=(FlashRole)99}};
            foreach(var item in invalid) Reject(()=>store.Save(Owner,"de_mirage",1,[item]),"malformed layout saved");
            Reject(()=>store.Save(Owner,"de_mirage",1,[]),"empty combination saved");
            Reject(()=>store.Save(Owner,"de_mirage",9,[Member()]),"ninth slot accepted");
            Reject(()=>store.Save(Owner,"de_mirage",1,[Member(),Member()]),"duplicate labels accepted");
            Reject(()=>store.Save(Owner,"de_mirage",1,[Member(),Member(1) with {Feet=Member().Feet+new Vector3(26,26,0)}]),"diagonally overlapping hulls accepted");
            Reject(()=>store.Save(Owner,"de_mirage",1,Enumerable.Range(0,9).Select(Member).ToArray()),"ninth dummy accepted");
            assert(!Directory.Exists(directory),"invalid saves touched the data folder");
        }));
        test("Dummy archive parser rejects unsupported version duplicate fields slots and labels",()=>WithStore((store,directory)=>
        {
            store.Save(Owner,"de_mirage",1,[Member()]);string text=File.ReadAllText(store.FilePath(Owner,"de_mirage"));
            byte[] Bytes(JsonNode node)=>Encoding.UTF8.GetBytes(node.ToJsonString());
            var version=JsonNode.Parse(text)!;version["Version"]=2;Reject(()=>DummyLayoutStore.Parse(Bytes(version),Owner,"de_mirage"),"future version silently imported");
            Reject(()=>DummyLayoutStore.Parse(Encoding.UTF8.GetBytes(text.Replace("\"Version\":1","\"Version\":1,\"Version\":1")),Owner,"de_mirage"),"duplicate root fields accepted");
            var slots=JsonNode.Parse(text)!;var list=slots["Layouts"]!.AsArray();list.Add(list[0]!.DeepClone());Reject(()=>DummyLayoutStore.Parse(Bytes(slots),Owner,"de_mirage"),"duplicate slots accepted");
            var labels=JsonNode.Parse(text)!;var members=labels["Layouts"]![0]!["Members"]!.AsArray();members.Add(members[0]!.DeepClone());Reject(()=>DummyLayoutStore.Parse(Bytes(labels),Owner,"de_mirage"),"duplicate labels accepted");
            var unknown=JsonNode.Parse(text)!;unknown["LivePawnHandle"]=42;Reject(()=>DummyLayoutStore.Parse(Bytes(unknown),Owner,"de_mirage"),"unsupported live state imported");
            var role=JsonNode.Parse(text)!;role["Layouts"]![0]!["Members"]![0]!["Role"]="1";Reject(()=>DummyLayoutStore.Parse(Bytes(role),Owner,"de_mirage"),"numeric role ambiguity accepted");
        }));
        test("Damaged or oversized dummy archives are preserved and cannot be overwritten by save",()=>WithStore((store,directory)=>
        {
            store.Save(Owner,"de_mirage",1,[Member()]);string path=store.FilePath(Owner,"de_mirage");
            foreach(byte[] malformed in new[]{Encoding.UTF8.GetBytes("{broken"),new byte[DummyLayoutStore.MaximumBytes+1],Array.Empty<byte>()})
            {
                File.WriteAllBytes(path,malformed);Reject(()=>store.Read(Owner,"de_mirage"),"damaged archive read as empty");
                Reject(()=>store.Save(Owner,"de_mirage",2,[Member(1)]),"save overwrote damaged archive");
                assert(File.ReadAllBytes(path).SequenceEqual(malformed) && Directory.GetFiles(directory,"*.tmp").Length==0,"invalid archive changed or temporary write leaked");
            }
        }));
        test("Dummy archive reads return independent snapshots and retain eight unique targets",()=>WithStore((store,directory)=>
        {
            var members=Enumerable.Range(0,8).Select(Member).ToArray();store.Save(Owner,"de_mirage",1,members);members[0]=Member(0) with {Armor=0};
            var a=store.Read(Owner,"de_mirage");var b=store.Read(Owner,"de_mirage");
            assert(a.Single().Members.Count==8 && a.Single().Members[0].Armor==100,"save retained caller's mutable member array");
            assert(!ReferenceEquals(a,b) && !ReferenceEquals(a.Single().Members,b.Single().Members),"archive calls shared mutable roster objects");
            assert(a.Single().Members.Select(x=>x.LabelIndex).Distinct().Count()==8,"full roster identity lost");
        }));
    }
}
