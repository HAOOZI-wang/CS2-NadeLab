using System.Numerics;
using System.Text.Json;
using NadeLab.Core;

var builder=WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls("http://127.0.0.1:5178");
builder.Services.ConfigureHttpJsonOptions(o=>o.SerializerOptions.IncludeFields=true);
var app=builder.Build();
app.UseDefaultFiles(); app.UseStaticFiles();
var gate=new SemaphoreSlim(1,1);
app.MapGet("/api/info",() => new { Version=NadeLabVersion.Current, Mode="synthetic-demo", Calibrated=false, Plugin="NadeLab" });
app.MapPost("/api/solve",async (DemoRequest request,HttpContext context) =>
{
    // Loopback browser only; reject arbitrary cross-site requests to this local service.
    if (context.Request.Headers.TryGetValue("Origin",out var origin) && origin!="http://127.0.0.1:5178" && origin!="http://localhost:5178")
        return Results.BadRequest(new {error="Unsupported origin."});
    if(!await gate.WaitAsync(0,context.RequestAborted)) return Results.Conflict(new {error="已有一个搜索任务，请稍后再试。"});
    try
    {
        if(!Geometry.Finite(request.Start) || !Geometry.Finite(request.Target) || MathF.Abs(request.Start.X)>10000 ||
            MathF.Abs(request.Start.Y)>10000 || request.Start.Z is < 0 or > 1000 || request.Radius is < 1 or > 256 || !float.IsFinite(request.Radius))
            return Results.BadRequest(new {error="坐标或目标半径无效。"});
        if(request.Boxes is {Length:>16}) return Results.BadRequest(new {error="最多16个测试障碍。"});
        foreach(var b in request.Boxes ?? [])
            if(!Geometry.Finite(b.Min) || !Geometry.Finite(b.Max) || b.Min.X>=b.Max.X || b.Min.Y>=b.Max.Y || b.Min.Z>=b.Max.Z)
                return Results.BadRequest(new {error="障碍边界无效。"});
        var result=await Task.Run(() =>
        {
            var world=new AnalyticWorld(0,request.Boxes ?? []);
            using var search=new LineupSearch(world,new PhysicsProfile(),new(request.Start),new(request.Target,request.Radius),
                new SearchOptions {YawSpan=request.Wide ? 180 : 60,YawStep=request.Wide ? 10 : 6,MaxCandidates=request.Wide ? 7000 : 6000});
            int units=0;
            while(search.Advance()) if(++units%256==0) context.RequestAborted.ThrowIfCancellationRequested();
            return search.Result;
        },context.RequestAborted);
        return Results.Ok(new { SchemaVersion=1,Map="synthetic-test-field",Start=new ThrowStart(request.Start),Target=new Target(request.Target,request.Radius),Result=result });
    }
    catch(ArgumentException ex) { return Results.BadRequest(new {error=ex.Message}); }
    finally { gate.Release(); }
});
app.MapGet("/api/report",(IConfiguration config) =>
{
    string? directory=config["data"];
    if(string.IsNullOrWhiteSpace(directory)) return Results.Ok(new {connected=false,message="尚未指定训练服数据目录；可导入插件生成的 JSON/JSONL。"});
    try
    {
        string file=Path.Combine(Path.GetFullPath(directory),"searches.jsonl");
        if(!File.Exists(file)) return Results.Ok(new {connected=false,message="训练服尚未生成搜索结果。"});
        // Ignore an incomplete trailing record while the writer is appending.
        string? last=null;
        foreach(string line in File.ReadLines(file))
        {
            if(string.IsNullOrWhiteSpace(line)) continue;
            try { using var doc=JsonDocument.Parse(line); last=line; } catch(JsonException) { }
        }
        return last==null ? Results.Ok(new {connected=false,message="记录为空。"}) : Results.Text(last,"application/json");
    }
    catch(IOException) { return Results.Conflict(new {error="记录正在写入，请稍后重试。"}); }
});
app.Run();

sealed record DemoRequest(Vector3 Start,Vector3 Target,float Radius=32,bool Wide=false,Box[]? Boxes=null);
