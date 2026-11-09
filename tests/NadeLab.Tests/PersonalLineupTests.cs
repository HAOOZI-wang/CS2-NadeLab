using System.Numerics;
using System.Text;
using System.Text.Json.Nodes;
using NadeLab.Core;

internal static class PersonalLineupTests
{
    private const ulong Owner=76561198012345678;
    private static PersonalLineup Entry(string name="我的窗口烟")=>new(Guid.NewGuid(),name,GrenadeKind.Smoke,new(100,64,12),64,-10,150,
        "对准窗角，站立左键投掷",null,null,"{}",new DateTimeOffset(2026,10,31,12,34,56,TimeSpan.FromHours(8)));
    private static void WithStore(Action<PersonalLineupStore,string> action)
    {
        string temporary=Path.GetFullPath(Path.Combine(Path.GetTempPath(),"NadeLab-PersonalLineups-"+Guid.NewGuid().ToString("N")));
        try {action(new(temporary),temporary);}
        finally
        {
            string parent=Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar)+Path.DirectorySeparatorChar;
            if(!temporary.StartsWith(parent,StringComparison.OrdinalIgnoreCase) || !Path.GetFileName(temporary).StartsWith("NadeLab-PersonalLineups-",StringComparison.Ordinal))
                throw new InvalidOperationException("Unsafe test cleanup path");
            if(Directory.Exists(temporary)) Directory.Delete(temporary,true);
        }
    }
    public static void Run(Action<string,Action> test,Action<bool,string> assert)
    {
        void Reject(Action action,string message)
        {try {action();}catch(InvalidDataException) {return;}throw new Exception(message);}
        test("Personal lineups round-trip every grenade kind and preserve raw world evidence",()=>WithStore((store,directory)=>
        {
            assert(store.Read(Owner,"de_mirage").Count==0 && !Directory.Exists(directory),"read created an absent archive");
            var entries=Enum.GetValues<GrenadeKind>().Select(kind=>Entry() with {Kind=kind,ActualImpact=new Vector3(1024,-20,36),ThrowId="manual-20261031-001",
                World="{\"Build\":\"14190\", \"Map\":\"de_mirage\",\"UnknownEvidence\":true}"}).ToArray();
            foreach(var entry in entries) store.Add(Owner,"de_mirage",entry);
            assert(store.Read(Owner,"de_mirage").SequenceEqual(entries),"saved entries lost kind, pose, notes, evidence, identity or timestamp");
            assert(Directory.GetFiles(directory,"*.tmp").Length==0,"temporary write leaked");
            assert(store.Read(Owner,"de_mirage").Select(x=>x.Name).Distinct().Count()==1,"same-name entries were renamed");
        }));
        test("Personal lineup archives isolate owners and maps and validate every public path",()=>WithStore((store,directory)=>
        {
            var a=Entry();var b=Entry();var c=Entry();
            store.Add(Owner,"de_mirage",a);store.Add(Owner+1,"de_mirage",b);store.Add(Owner,"de_dust2",c);
            assert(store.Read(Owner,"de_mirage").Single()==a && store.Read(Owner+1,"de_mirage").Single()==b && store.Read(Owner,"de_dust2").Single()==c,"owner or map data mixed");
            foreach(string map in new[]{"../de_mirage","de_mirage/../../other","de_mirage;quit","unknown",""})
            {
                Reject(()=>store.FilePath(Owner,map),"unsafe map became a path");Reject(()=>store.Read(Owner,map),"unsafe map read");
                Reject(()=>store.Add(Owner,map,a),"unsafe map saved");Reject(()=>store.Replace(Owner,map,a,a),"unsafe map replaced");Reject(()=>store.Delete(Owner,map,a),"unsafe map deleted");
            }
            Reject(()=>store.FilePath(0,"de_mirage"),"zero Steam ID accepted");
            assert(store.FilePath(Owner,"DE_MIRAGE")==store.FilePath(Owner,"de_mirage"),"map casing split the archive");
            assert(Path.GetFileName(store.FilePath(Owner,"de_mirage"))=="76561198012345678-de_mirage.json","entry name entered archive path");
        }));
        test("Personal lineup rename and deletion use stable IDs and preserve the immediately previous archive",()=>WithStore((store,directory)=>
        {
            var a=Entry();var b=Entry();store.Add(Owner,"de_mirage",a);store.Add(Owner,"de_mirage",b);
            string path=store.FilePath(Owner,"de_mirage");byte[] before=File.ReadAllBytes(path);
            var renamed=a with {Name="B区补充烟"};store.Replace(Owner,"de_mirage",a,renamed);
            assert(File.ReadAllBytes(path+".bak").SequenceEqual(before),"rename failed to preserve previous file");
            assert(store.Read(Owner,"de_mirage").SequenceEqual(new[]{renamed,b}),"rename selected the wrong same-name entry");
            before=File.ReadAllBytes(path);store.Delete(Owner,"de_mirage",b);
            assert(store.Read(Owner,"de_mirage").Single()==renamed && File.ReadAllBytes(path+".bak").SequenceEqual(before),"delete lost another entry or backup");
            store.Delete(Owner,"de_mirage",renamed);assert(store.Read(Owner,"de_mirage").Count==0,"last entry not deleted");
            assert(File.Exists(path),"empty archive disappeared");
        }));
        test("Personal lineup changes reject duplicate IDs, replaced identities and stale editor snapshots",()=>WithStore((store,directory)=>
        {
            var a=Entry();store.Add(Owner,"de_mirage",a);string path=store.FilePath(Owner,"de_mirage");
            Reject(()=>store.Add(Owner,"de_mirage",a with {Name="重复编号"}),"duplicate ID overwrote an entry");
            Reject(()=>store.Replace(Owner,"de_mirage",a,a with {Id=Guid.NewGuid()}),"replace changed stable identity");
            var updated=a with {Instruction="跳投"};new PersonalLineupStore(directory).Replace(Owner,"de_mirage",a,updated);
            byte[] before=File.ReadAllBytes(path),backup=File.ReadAllBytes(path+".bak");
            Reject(()=>store.Replace(Owner,"de_mirage",a,a with {Name="过期重命名"}),"stale replacement accepted");
            Reject(()=>store.Delete(Owner,"de_mirage",a),"stale deletion accepted");
            Reject(()=>store.Delete(Owner,"de_mirage",Entry()),"missing entry deletion accepted");
            assert(File.ReadAllBytes(path).SequenceEqual(before) && File.ReadAllBytes(path+".bak").SequenceEqual(backup),"rejected edit changed archive or backup");
        }));
        test("Personal lineup validation rejects unsafe poses, malformed text and unpaired impact evidence before writing",()=>WithStore((store,directory)=>
        {
            var a=Entry();var invalid=new[]{a with {Id=Guid.Empty},a with {Name=""},a with {Name="   "},a with {Name=new string('字',49)},a with {Name="bad\nname"},a with {Name=null!},
                a with {Kind=(GrenadeKind)99},a with {Feet=new(float.NaN,0,0)},a with {Feet=new(32769,0,0)},a with {EyeHeight=31},a with {EyeHeight=81},
                a with {EyeHeight=float.PositiveInfinity},a with {Pitch=-90},a with {Pitch=float.NaN},a with {Yaw=181},a with {Yaw=float.NegativeInfinity},
                a with {Instruction=new string('字',161)},a with {Instruction="bad\0note"},a with {Instruction=null!},a with {World="{broken"},a with {World="null"},
                a with {World="[]"},a with {World=null!},a with {World="{\"x\":\""+new string('x',4096)+"\"}"},a with {ActualImpact=Vector3.Zero},a with {ThrowId="throw"},
                a with {ActualImpact=Vector3.Zero,ThrowId=""},a with {ActualImpact=Vector3.Zero,ThrowId=new string('x',129)},
                a with {ActualImpact=new(float.PositiveInfinity,0,0),ThrowId="throw"},a with {ActualImpact=new(0,0,-32769),ThrowId="throw"}};
            foreach(var entry in invalid) Reject(()=>store.Add(Owner,"de_mirage",entry),"invalid entry was saved");
            assert(!Directory.Exists(directory),"invalid entries created files");
            var boundary=a with {Name=new string('字',48),Instruction=new string('字',160),Feet=new(-32768,32768,0),EyeHeight=32,Pitch=-89,Yaw=-180};
            store.Add(Owner,"de_mirage",boundary);assert(store.Read(Owner,"de_mirage").Single()==boundary,"valid boundary rejected");
        }));
        test("Personal library capacity refuses entry 129 while allowing rename and deletion",()=>WithStore((store,directory)=>
        {
            var entries=Enumerable.Range(0,128).Select(_=>Entry()).ToArray();foreach(var entry in entries) store.Add(Owner,"de_mirage",entry);
            string path=store.FilePath(Owner,"de_mirage");byte[] before=File.ReadAllBytes(path);
            Reject(()=>store.Add(Owner,"de_mirage",Entry()),"entry 129 accepted");assert(File.ReadAllBytes(path).SequenceEqual(before),"capacity failure changed archive");
            var renamed=entries[0] with {Name="满库重命名"};store.Replace(Owner,"de_mirage",entries[0],renamed);store.Delete(Owner,"de_mirage",entries[1]);store.Add(Owner,"de_mirage",Entry());
            assert(store.Read(Owner,"de_mirage").Count==128 && store.Read(Owner,"de_mirage")[0]==renamed,"full archive could not be edited");
        }));
        test("Corrupt personal files are never reset or implicitly restored from backup",()=>WithStore((store,directory)=>
        {
            var a=Entry();store.Add(Owner,"de_mirage",a);store.Add(Owner,"de_mirage",Entry());string path=store.FilePath(Owner,"de_mirage");byte[] backup=File.ReadAllBytes(path+".bak");
            foreach(byte[] malformed in new[]{Encoding.UTF8.GetBytes("{broken"),new byte[PersonalLineupStore.MaximumBytes+1],Array.Empty<byte>()})
            {
                File.WriteAllBytes(path,malformed);Reject(()=>store.Read(Owner,"de_mirage"),"corrupt file read as empty");
                Reject(()=>store.Add(Owner,"de_mirage",Entry()),"add overwrote corruption");Reject(()=>store.Replace(Owner,"de_mirage",a,a),"replace overwrote corruption");Reject(()=>store.Delete(Owner,"de_mirage",a),"delete overwrote corruption");
                assert(File.ReadAllBytes(path).SequenceEqual(malformed) && File.ReadAllBytes(path+".bak").SequenceEqual(backup),"corrupt file or backup modified");
                assert(Directory.GetFiles(directory,"*.tmp").Length==0,"corruption refusal leaked temporary files");
            }
        }));
        test("Personal archive parser refuses wrong owner, schema ambiguity and invalid persisted values",()=>WithStore((store,directory)=>
        {
            var a=Entry();store.Add(Owner,"de_mirage",a);string path=store.FilePath(Owner,"de_mirage"),text=File.ReadAllText(path);
            Action<JsonNode>[] mutations={n=>n["Version"]=2,n=>n["Steam"]="1",n=>n["Map"]="de_dust2",n=>n["Unknown"]=1,n=>n["Entries"]=null,
                n=>n["Entries"]!.AsArray().Add(n["Entries"]![0]!.DeepClone()),n=>n["Entries"]![0]!["Id"]="invalid",n=>n["Entries"]![0]!["Kind"]="1",
                n=>n["Entries"]![0]!["Kind"]="smoke",n=>n["Entries"]![0]!["Feet"]!["X"]=40000,n=>n["Entries"]![0]!["SavedAt"]="yesterday",
                n=>n["Entries"]![0]!["World"]=new JsonObject(),n=>n["Entries"]![0]!["ActualImpact"]=new JsonObject {["X"]=1,["Y"]=2,["Z"]=3},
                n=>n["Entries"]![0]!["Name"]=null};
            foreach(var mutate in mutations)
            {
                var node=JsonNode.Parse(text)!;mutate(node);string broken=node.ToJsonString();File.WriteAllText(path,broken);
                Reject(()=>store.Read(Owner,"de_mirage"),"malformed schema accepted");Reject(()=>store.Add(Owner,"de_mirage",Entry()),"malformed schema overwritten");
                assert(File.ReadAllText(path)==broken,"malformed schema not preserved");
            }
            string duplicate=text.Replace("\"Version\":1","\"Version\":1,\"Version\":1",StringComparison.Ordinal);File.WriteAllText(path,duplicate);
            Reject(()=>store.Read(Owner,"de_mirage"),"duplicate JSON property accepted");
        }));
        test("Personal store instances serialize concurrent additions without losing entries",()=>WithStore((store,directory)=>
        {
            var entries=Enumerable.Range(0,24).Select(_=>Entry()).ToArray();Parallel.ForEach(entries,entry=>new PersonalLineupStore(directory).Add(Owner,"de_mirage",entry));
            var saved=store.Read(Owner,"de_mirage");assert(saved.Count==24 && entries.All(saved.Contains),"concurrent store instances lost an addition");
        }));
    }
}
