using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace NadeLab.Core;

// Only serialized values cross threads. The game adapter owns all entity access.
public sealed class CompanionPeer(ulong steamId)
{
    public readonly string Id=Guid.NewGuid().ToString("N");
    public readonly ulong SteamId=steamId;
    public long HeardAt=Environment.TickCount64;
    public long Sequence;
    public volatile bool InputActive;
    public readonly CompanionAcknowledgements Acknowledgements=new();
    public CompanionSnapshot Snapshot=new("","",0,false,false,[]);
    public readonly ConcurrentQueue<CompanionRequest> Requests=new();
    public bool Fresh=>Environment.TickCount64-Interlocked.Read(ref HeardAt)<CompanionProtocol.LeaseMilliseconds;
}

public sealed class CompanionPipeServer : IDisposable
{
    private readonly CancellationTokenSource stop=new();
    private readonly Task worker;
    public CompanionPeer? Peer;
    public CompanionPipeServer(string name)=>worker=Task.Run(()=>Run(name,stop.Token));
    private async Task Run(string name,CancellationToken cancellation)
    {
        while(!cancellation.IsCancellationRequested)
        {
            try
            {
                using var pipe=new NamedPipeServerStream(name,PipeDirection.InOut,1,PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous|PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync(cancellation);
                using var connection=CancellationTokenSource.CreateLinkedTokenSource(cancellation);
                using var reader=new StreamReader(pipe,Encoding.UTF8,false,1024,true);
                using var writer=new StreamWriter(pipe,new UTF8Encoding(false),1024,true){AutoFlush=true};
                var hello=await reader.ReadLineAsync(connection.Token).AsTask().WaitAsync(TimeSpan.FromSeconds(3),connection.Token);
                if(hello is null || hello.Length>1024) continue;
                var request=JsonSerializer.Deserialize<CompanionRequest>(hello);
                if(request?.Action!="hello") continue;
                var peer=new CompanionPeer(request.SteamId);Volatile.Write(ref Peer,peer);
                var input=Task.Run(async ()=>
                {
                    while(!connection.IsCancellationRequested)
                    {
                        string? line=await reader.ReadLineAsync(connection.Token);
                        if(line is null || line.Length>2048) break;
                        var message=JsonSerializer.Deserialize<CompanionRequest>(line);
                        if(message is null || message.Session!=peer.Id) continue;
                        Interlocked.Exchange(ref peer.HeardAt,Environment.TickCount64);
                        if(message.Action=="heartbeat") {peer.InputActive=message.InputActive;continue;}
                        if(message.Sequence<=peer.Sequence || peer.Requests.Count>=32) continue;
                        peer.Sequence=message.Sequence;peer.Requests.Enqueue(message);
                    }
                },connection.Token);
                try
                {
                    while(!input.IsCompleted && !connection.IsCancellationRequested && peer.Fresh)
                    {
                        var snapshot=Volatile.Read(ref peer.Snapshot) with {Session=peer.Id};
                        await writer.WriteLineAsync(JsonSerializer.Serialize(snapshot).AsMemory(),connection.Token);
                        await Task.Delay(75,connection.Token);
                    }
                }
                finally
                {
                    connection.Cancel();pipe.Dispose();Volatile.Write(ref Peer,null);
                    try {await input;} catch(Exception ex) when(ex is IOException or OperationCanceledException or JsonException or ObjectDisposedException) { }
                }
            }
            catch(Exception ex) when(ex is IOException or OperationCanceledException or TimeoutException or JsonException or UnauthorizedAccessException or ObjectDisposedException) { }
            if(!cancellation.IsCancellationRequested) await Task.Delay(250,cancellation).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
    }
    public void Dispose(){stop.Cancel();Volatile.Write(ref Peer,null);}
}
