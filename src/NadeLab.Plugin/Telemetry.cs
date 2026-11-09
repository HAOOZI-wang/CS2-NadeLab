using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace NadeLab.Plugin;

// Work items capture only completed managed snapshots, never game entities.
internal sealed class Telemetry : IDisposable
{
    private readonly Channel<(string Path, Func<string> Serialize, bool Append)> queue = Channel.CreateBounded<(string,Func<string>,bool)>(
        new BoundedChannelOptions(256) { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
    private readonly Task writer;
    private readonly ILogger logger;
    public Telemetry(ILogger logger)
    {
        this.logger = logger;
        writer = Task.Run(async () =>
        {
            await foreach (var item in queue.Reader.ReadAllAsync())
            {
                try
                {
                    Directory.CreateDirectory(System.IO.Path.GetDirectoryName(item.Path)!);
                    string json=item.Serialize();
                    if (item.Append) await File.AppendAllTextAsync(item.Path, json + Environment.NewLine);
                    else
                    {
                        string temporary=item.Path+"."+Guid.NewGuid().ToString("N")+".tmp";
                        try
                        {
                            await File.WriteAllTextAsync(temporary,json);
                            File.Move(temporary,item.Path,true);
                        }
                        finally {if(File.Exists(temporary)) File.Delete(temporary);}
                    }
                }
                catch (Exception ex) { logger.LogError(ex,"NadeLab telemetry write failed: {Path}",item.Path); }
            }
        });
    }
    public bool Write(string path, string json, bool append = true)
        => Write(path,()=>json,append);
    public bool Write(string path,Func<string> serialize,bool append=true)
    {
        bool ok = queue.Writer.TryWrite((path,serialize,append));
        if (!ok) logger.LogWarning("NadeLab telemetry queue full; record was not saved.");
        return ok;
    }
    public void Dispose() { queue.Writer.TryComplete(); writer.GetAwaiter().GetResult(); }
}
