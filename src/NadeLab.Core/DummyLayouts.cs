using System.Collections.ObjectModel;
using System.Globalization;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace NadeLab.Core;

public sealed record DummyLayoutMember(int LabelIndex,Vector3 Feet,float EyeHeight,Aim View,int Armor,FlashRole Role);
public sealed record DummyLayout(int Slot,DateTimeOffset SavedAt,IReadOnlyList<DummyLayoutMember> Members);

/// <summary>Player-owned, map-scoped dummy presets. No live handles or blind/damage history are stored.</summary>
public sealed class DummyLayoutStore(string directory)
{
    public const int Version=1,SlotCount=8,MaximumBytes=65536;
    private readonly string root=Path.GetFullPath(directory);
    public static void ValidateMembers(IReadOnlyList<DummyLayoutMember> members)
    {
        if(members.Count is <1 or >DummyRoster.PlayerLimit || members.Select(x=>x.LabelIndex).Distinct().Count()!=members.Count)
            throw new InvalidDataException("组合需要1至8个不同编号的假人。");
        foreach(var member in members)
            if(member.LabelIndex is <0 or >=DummyRoster.PlayerLimit || !Geometry.Finite(member.Feet) ||
                MathF.Abs(member.Feet.X)>32768 || MathF.Abs(member.Feet.Y)>32768 || MathF.Abs(member.Feet.Z)>32768 ||
                !float.IsFinite(member.EyeHeight) || member.EyeHeight is <32 or >80 ||
                !float.IsFinite(member.View.Pitch) || member.View.Pitch is <-89 or >89 ||
                !float.IsFinite(member.View.Yaw) || member.View.Yaw is <-180 or >180 ||
                member.View.Mode!=ThrowMode.Left || member.View.EffectiveAction!=ThrowAction.Standing ||
                member.View.ForwardSpeed!=0 || member.View.ForwardOffset!=0 || member.View.ReleaseHeight!=0 || member.View.VerticalSpeed!=0 ||
                member.Armor is <0 or >100 || !Enum.IsDefined(member.Role))
                throw new InvalidDataException("假人位置、朝向、护甲或角色无效。");
        for(int a=0;a<members.Count;a++) for(int b=a+1;b<members.Count;b++)
        {
            var delta=members[a].Feet-members[b].Feet;
            if(delta.Length()<36 || MathF.Abs(delta.X)<33 && MathF.Abs(delta.Y)<33 && MathF.Abs(delta.Z)<73)
                throw new InvalidDataException("组合中有重叠的假人。");
        }
    }
    private static string Map(string map)=>MapCatalog.Find(map)?.Name ?? throw new InvalidDataException("地图不在训练地图列表中。");
    private static string Steam(ulong steam)=>steam>0 ? steam.ToString(CultureInfo.InvariantCulture) : throw new InvalidDataException("需要有效的玩家身份。");
    public string FilePath(ulong steam,string map)=>Path.Combine(root,Steam(steam)+"-"+Map(map)+".json");
    private static void RefuseLink(string path)
    {
        if((File.Exists(path) || Directory.Exists(path)) && (File.GetAttributes(path)&FileAttributes.ReparsePoint)!=0)
            throw new IOException("组合路径不能是链接。");
    }
    public IReadOnlyList<DummyLayout> Read(ulong steam,string map)
    {
        string path=FilePath(steam,map);RefuseLink(root);RefuseLink(path);
        if(!File.Exists(path)) return Array.Empty<DummyLayout>();
        using var stream=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read|FileShare.Delete);
        if(stream.Length is <=0 or >MaximumBytes) throw new InvalidDataException("组合文件大小无效，原文件已保留。");
        var bytes=new byte[checked((int)stream.Length)];stream.ReadExactly(bytes);
        return Parse(bytes,steam,map);
    }
    public static IReadOnlyList<DummyLayout> Parse(ReadOnlyMemory<byte> bytes,ulong steam,string map)
    {
        if(bytes.Length is <=0 or >MaximumBytes) throw new InvalidDataException("组合文件大小无效。");
        string expectedSteam=Steam(steam),expectedMap=Map(map);
        try
        {
            using var document=JsonDocument.Parse(bytes,new JsonDocumentOptions {MaxDepth=12});var node=document.RootElement;
            Properties(node,"Version","Steam","Map","Layouts");
            if(node.GetProperty("Version").GetInt32()!=Version || node.GetProperty("Steam").GetString()!=expectedSteam || node.GetProperty("Map").GetString()!=expectedMap)
                throw new InvalidDataException("组合文件版本、玩家或地图不匹配。");
            var entries=node.GetProperty("Layouts");if(entries.ValueKind!=JsonValueKind.Array || entries.GetArrayLength()>SlotCount) throw new InvalidDataException("组合槽数量无效。");
            var layouts=new List<DummyLayout>();
            foreach(var item in entries.EnumerateArray())
            {
                Properties(item,"Slot","SavedAt","Members");int slot=item.GetProperty("Slot").GetInt32();
                if(slot is <1 or >SlotCount || layouts.Any(x=>x.Slot==slot) || !DateTimeOffset.TryParseExact(item.GetProperty("SavedAt").GetString(),"O",CultureInfo.InvariantCulture,DateTimeStyles.RoundtripKind,out var saved))
                    throw new InvalidDataException("组合槽或保存时间无效。");
                var list=item.GetProperty("Members");if(list.ValueKind!=JsonValueKind.Array || list.GetArrayLength() is <1 or >DummyRoster.PlayerLimit) throw new InvalidDataException("假人数量无效。");
                var members=new List<DummyLayoutMember>();
                foreach(var member in list.EnumerateArray())
                {
                    Properties(member,"LabelIndex","Feet","EyeHeight","Pitch","Yaw","Armor","Role");
                    var feet=member.GetProperty("Feet");Properties(feet,"X","Y","Z");
                    string? roleText=member.GetProperty("Role").GetString();
                    if(!Enum.TryParse<FlashRole>(roleText,false,out var role) || !Enum.IsDefined(role) || role.ToString()!=roleText) throw new InvalidDataException("假人角色无效。");
                    members.Add(new(member.GetProperty("LabelIndex").GetInt32(),new(feet.GetProperty("X").GetSingle(),feet.GetProperty("Y").GetSingle(),feet.GetProperty("Z").GetSingle()),
                        member.GetProperty("EyeHeight").GetSingle(),new(member.GetProperty("Pitch").GetSingle(),member.GetProperty("Yaw").GetSingle(),ThrowMode.Left),member.GetProperty("Armor").GetInt32(),role));
                }
                ValidateMembers(members);layouts.Add(new(slot,saved,new ReadOnlyCollection<DummyLayoutMember>(members.OrderBy(x=>x.LabelIndex).ToArray())));
            }
            return new ReadOnlyCollection<DummyLayout>(layouts.OrderBy(x=>x.Slot).ToArray());
        }
        catch(Exception ex) when(ex is JsonException or InvalidOperationException or FormatException or OverflowException or KeyNotFoundException)
        {throw new InvalidDataException("组合文件无法读取；未修改原文件。",ex);}
    }
    private static void Properties(JsonElement element,params string[] required)
    {
        if(element.ValueKind!=JsonValueKind.Object) throw new InvalidDataException("组合字段类型无效。");
        var names=element.EnumerateObject().Select(x=>x.Name).ToArray();
        if(names.Length!=required.Length || names.Distinct(StringComparer.Ordinal).Count()!=names.Length || required.Any(x=>!names.Contains(x,StringComparer.Ordinal)))
            throw new InvalidDataException("组合字段缺失、重复或不支持。");
    }
    public void Save(ulong steam,string map,int slot,IReadOnlyList<DummyLayoutMember> members)
    {
        if(slot is <1 or >SlotCount) throw new InvalidDataException("组合槽无效。");
        ValidateMembers(members);var layouts=Read(steam,map).Where(x=>x.Slot!=slot).ToList();
        layouts.Add(new(slot,DateTimeOffset.UtcNow,members.ToArray()));
        var entries=new JsonArray();
        foreach(var layout in layouts.OrderBy(x=>x.Slot))
        {
            var people=new JsonArray();foreach(var member in layout.Members.OrderBy(x=>x.LabelIndex))
                people.Add(new JsonObject {["LabelIndex"]=member.LabelIndex,["Feet"]=new JsonObject {["X"]=member.Feet.X,["Y"]=member.Feet.Y,["Z"]=member.Feet.Z},
                    ["EyeHeight"]=member.EyeHeight,["Pitch"]=member.View.Pitch,["Yaw"]=member.View.Yaw,["Armor"]=member.Armor,["Role"]=member.Role.ToString()});
            entries.Add(new JsonObject {["Slot"]=layout.Slot,["SavedAt"]=layout.SavedAt.ToString("O",CultureInfo.InvariantCulture),["Members"]=people});
        }
        var node=new JsonObject {["Version"]=Version,["Steam"]=Steam(steam),["Map"]=Map(map),["Layouts"]=entries};
        byte[] data=System.Text.Encoding.UTF8.GetBytes(node.ToJsonString());_ = Parse(data,steam,map);
        string path=FilePath(steam,map),backup=path+".bak",temporary=path+"."+Guid.NewGuid().ToString("N")+".tmp";
        RefuseLink(root);Directory.CreateDirectory(root);RefuseLink(path);RefuseLink(backup);
        try
        {
            using(var stream=new FileStream(temporary,FileMode.CreateNew,FileAccess.Write,FileShare.None))
            {stream.Write(data);stream.Flush(true);}
            if(File.Exists(path)) File.Replace(temporary,path,backup,true);else File.Move(temporary,path);
        }
        finally {if(File.Exists(temporary)) File.Delete(temporary);}
    }
}
