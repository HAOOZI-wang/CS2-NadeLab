using System.Collections.ObjectModel;
using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace NadeLab.Core;

public sealed record PersonalLineup(Guid Id,string Name,GrenadeKind Kind,Vector3 Feet,float EyeHeight,float Pitch,float Yaw,
    string Instruction,Vector3? ActualImpact,string? ThrowId,string World,DateTimeOffset SavedAt);

public sealed class PersonalLineupStore(string directory)
{
    public const int Version=1,MaximumEntries=128,MaximumBytes=4*1024*1024;
    // All plugin instances in this process share a gate so read-modify-write cannot lose additions.
    private static readonly object Gate=new();
    private readonly string root=Path.GetFullPath(directory);
    private static string Map(string map)=>MapCatalog.Find(map)?.Name ?? throw new InvalidDataException("地图不在训练地图列表中。");
    private static string Steam(ulong steam)=>steam>0 ? steam.ToString(CultureInfo.InvariantCulture) : throw new InvalidDataException("需要有效的玩家身份。");
    public string FilePath(ulong steam,string map)=>Path.Combine(root,Steam(steam)+"-"+Map(map)+".json");

    private static void RefuseLinks(string path)
    {
        // Inspect ancestors too: a regular child below a linked directory is still outside the store.
        for(string? current=Path.GetFullPath(path);current is not null;current=Path.GetDirectoryName(current))
        {
            try
            {
                if((File.GetAttributes(current)&FileAttributes.ReparsePoint)!=0)
                    throw new IOException("个人投掷物库路径不能是链接。");
            }
            catch(FileNotFoundException) { }
            catch(DirectoryNotFoundException) { }
        }
    }

    public IReadOnlyList<PersonalLineup> Read(ulong steam,string map)
    {
        lock(Gate)
        {
            string path=FilePath(steam,map);RefuseLinks(path);
            if(Directory.Exists(path)) throw new IOException("个人投掷物库文件路径被目录占用。");
            if(!File.Exists(path)) return Array.Empty<PersonalLineup>();
            using var stream=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read|FileShare.Delete);
            if(stream.Length is <=0 or >MaximumBytes) throw new InvalidDataException("个人投掷物库文件大小无效，原文件已保留。");
            var bytes=new byte[checked((int)stream.Length)];stream.ReadExactly(bytes);
            return Parse(bytes,steam,map);
        }
    }

    public void Add(ulong steam,string map,PersonalLineup entry)
    {
        Validate(entry);
        lock(Gate)
        {
            var entries=Read(steam,map).ToList();
            if(entries.Count>=MaximumEntries) throw new InvalidDataException("这张地图的个人投掷物库已满，最多保存128条。");
            if(entries.Any(x=>x.Id==entry.Id)) throw new InvalidDataException("该个人投掷物编号已存在。");
            entries.Add(entry);Write(steam,map,entries);
        }
    }

    public void Replace(ulong steam,string map,PersonalLineup expected,PersonalLineup updated)
    {
        Validate(expected);Validate(updated);
        if(expected.Id!=updated.Id) throw new InvalidDataException("修改不能改变个人投掷物编号。");
        lock(Gate)
        {
            var entries=Read(steam,map).ToList();int index=FindExpected(entries,expected);
            entries[index]=updated;Write(steam,map,entries);
        }
    }

    public void Delete(ulong steam,string map,PersonalLineup expected)
    {
        Validate(expected);
        lock(Gate)
        {
            var entries=Read(steam,map).ToList();int index=FindExpected(entries,expected);
            entries.RemoveAt(index);Write(steam,map,entries);
        }
    }

    private static int FindExpected(List<PersonalLineup> entries,PersonalLineup expected)
    {
        int index=entries.FindIndex(x=>x.Id==expected.Id);
        if(index<0 || entries[index]!=expected) throw new InvalidDataException("该记录已更新或删除，请刷新个人投掷物库后重试。");
        return index;
    }

    private static bool Position(Vector3 position)=>Geometry.Finite(position) && MathF.Abs(position.X)<=32768 && MathF.Abs(position.Y)<=32768 && MathF.Abs(position.Z)<=32768;
    private static bool Text(string? value,int maximum,bool required)=>value is not null && value.Length<=maximum && !value.Any(char.IsControl) && (!required || !string.IsNullOrWhiteSpace(value));
    private static void Validate(PersonalLineup entry)
    {
        if(entry is null || entry.Id==Guid.Empty || !Text(entry.Name,48,true) || !Enum.IsDefined(entry.Kind) || !Position(entry.Feet) ||
            !float.IsFinite(entry.EyeHeight) || entry.EyeHeight is <32 or >80 || !float.IsFinite(entry.Pitch) || entry.Pitch is <-89 or >89 ||
            !float.IsFinite(entry.Yaw) || entry.Yaw is <-180 or >180 || !Text(entry.Instruction,160,false))
            throw new InvalidDataException("个人投掷物的编号、名称、类型、位置、朝向或说明无效。");
        if(entry.ActualImpact.HasValue!=(entry.ThrowId is not null) || entry.ActualImpact is { } impact && (!Position(impact) || !Text(entry.ThrowId,128,true)))
            throw new InvalidDataException("实际落点与投掷记录编号必须同时存在且有效。");
        if(entry.World is null || entry.World.Length is <2 or >4096) throw new InvalidDataException("世界快照长度无效。");
        try
        {
            using var world=JsonDocument.Parse(entry.World,new JsonDocumentOptions {MaxDepth=32});
            if(world.RootElement.ValueKind!=JsonValueKind.Object) throw new InvalidDataException("世界快照必须是JSON对象文本。");
            // Preserve the evidence exactly. Syntactic validity does not establish a validated lineup.
        }
        catch(JsonException ex) {throw new InvalidDataException("世界快照不是有效的JSON文本。",ex);}
    }

    private static IReadOnlyList<PersonalLineup> Parse(ReadOnlyMemory<byte> bytes,ulong steam,string map)
    {
        if(bytes.Length is <=0 or >MaximumBytes) throw new InvalidDataException("个人投掷物库文件大小无效。");
        string expectedSteam=Steam(steam),expectedMap=Map(map);
        try
        {
            using var document=JsonDocument.Parse(bytes,new JsonDocumentOptions {MaxDepth=12});var root=document.RootElement;
            Properties(root,"Version","Steam","Map","Entries");
            if(root.GetProperty("Version").GetInt32()!=Version || root.GetProperty("Steam").GetString()!=expectedSteam || root.GetProperty("Map").GetString()!=expectedMap)
                throw new InvalidDataException("个人投掷物库版本、玩家或地图不匹配。");
            var list=root.GetProperty("Entries");
            if(list.ValueKind!=JsonValueKind.Array || list.GetArrayLength()>MaximumEntries) throw new InvalidDataException("个人投掷物库记录数量无效。");
            var entries=new List<PersonalLineup>();var ids=new HashSet<Guid>();
            foreach(var item in list.EnumerateArray())
            {
                Properties(item,"Id","Name","Kind","Feet","EyeHeight","Pitch","Yaw","Instruction","ActualImpact","ThrowId","World","SavedAt");
                if(!Guid.TryParseExact(item.GetProperty("Id").GetString(),"D",out var id) || !ids.Add(id)) throw new InvalidDataException("个人投掷物编号无效或重复。");
                string? kindText=item.GetProperty("Kind").GetString();
                if(!Enum.TryParse<GrenadeKind>(kindText,false,out var kind) || !Enum.IsDefined(kind) || kind.ToString()!=kindText)
                    throw new InvalidDataException("个人投掷物类型无效。");
                if(!DateTimeOffset.TryParseExact(item.GetProperty("SavedAt").GetString(),"O",CultureInfo.InvariantCulture,DateTimeStyles.RoundtripKind,out var saved))
                    throw new InvalidDataException("个人投掷物保存时间无效。");
                var actual=item.GetProperty("ActualImpact");
                var entry=new PersonalLineup(id,item.GetProperty("Name").GetString()!,kind,ReadPosition(item.GetProperty("Feet")),
                    item.GetProperty("EyeHeight").GetSingle(),item.GetProperty("Pitch").GetSingle(),item.GetProperty("Yaw").GetSingle(),
                    item.GetProperty("Instruction").GetString()!,actual.ValueKind==JsonValueKind.Null ? null : ReadPosition(actual),
                    item.GetProperty("ThrowId").GetString(),item.GetProperty("World").GetString()!,saved);
                Validate(entry);entries.Add(entry);
            }
            return new ReadOnlyCollection<PersonalLineup>(entries);
        }
        catch(Exception ex) when(ex is JsonException or InvalidOperationException or FormatException or OverflowException or KeyNotFoundException)
        {throw new InvalidDataException("个人投掷物库无法读取；未修改原文件。",ex);}
    }

    private static Vector3 ReadPosition(JsonElement element)
    {
        Properties(element,"X","Y","Z");return new(element.GetProperty("X").GetSingle(),element.GetProperty("Y").GetSingle(),element.GetProperty("Z").GetSingle());
    }
    private static void Properties(JsonElement element,params string[] required)
    {
        if(element.ValueKind!=JsonValueKind.Object) throw new InvalidDataException("个人投掷物库字段类型无效。");
        var names=element.EnumerateObject().Select(x=>x.Name).ToArray();
        if(names.Length!=required.Length || names.Distinct(StringComparer.Ordinal).Count()!=names.Length || required.Any(x=>!names.Contains(x,StringComparer.Ordinal)))
            throw new InvalidDataException("个人投掷物库字段缺失、重复或不支持。");
    }
    private static JsonObject WritePosition(Vector3 position)=>new() {["X"]=position.X,["Y"]=position.Y,["Z"]=position.Z};
    private void Write(ulong steam,string map,IReadOnlyList<PersonalLineup> entries)
    {
        var list=new JsonArray();
        foreach(var entry in entries)
            list.Add(new JsonObject {["Id"]=entry.Id.ToString("D"),["Name"]=entry.Name,["Kind"]=entry.Kind.ToString(),["Feet"]=WritePosition(entry.Feet),
                ["EyeHeight"]=entry.EyeHeight,["Pitch"]=entry.Pitch,["Yaw"]=entry.Yaw,["Instruction"]=entry.Instruction,
                ["ActualImpact"]=entry.ActualImpact is { } impact ? WritePosition(impact) : null,["ThrowId"]=entry.ThrowId,["World"]=entry.World,
                ["SavedAt"]=entry.SavedAt.ToString("O",CultureInfo.InvariantCulture)});
        var node=new JsonObject {["Version"]=Version,["Steam"]=Steam(steam),["Map"]=Map(map),["Entries"]=list};
        byte[] bytes=Encoding.UTF8.GetBytes(node.ToJsonString());_ = Parse(bytes,steam,map);
        string path=FilePath(steam,map),backup=path+".bak",temporary=path+"."+Guid.NewGuid().ToString("N")+".tmp";
        RefuseLinks(path);RefuseLinks(backup);Directory.CreateDirectory(root);
        try
        {
            using(var stream=new FileStream(temporary,FileMode.CreateNew,FileAccess.Write,FileShare.None)) {stream.Write(bytes);stream.Flush(true);}
            RefuseLinks(path);RefuseLinks(backup);
            if(File.Exists(path)) File.Replace(temporary,path,backup,true);else File.Move(temporary,path);
        }
        finally {if(File.Exists(temporary)) File.Delete(temporary);}
    }
}
