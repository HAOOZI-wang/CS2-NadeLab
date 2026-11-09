using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using NadeLab.Core;

if(!OperatingSystem.IsWindows()) return 2;
string? Argument(string name)=>args.SkipWhile(x=>x!=name).Skip(1).FirstOrDefault();
string? server=Argument("--game"),client=Argument("--client"),keys=Argument("--keys");
if(server is null || client is null || keys is null || !File.Exists(keys) || !File.Exists(client))
{Console.Error.WriteLine("NadeLab.Input --game <server game/csgo> --client <cs2.exe> --keys <personal keys.vcfg>");return 2;}
string pipeName=CompanionProtocol.PipeName(server);
using var instance=new Mutex(true,"Local\\"+pipeName,out bool unique);
if(!unique) return 0;
ulong.TryParse(Argument("--steam"),out var steam);
using var host=new InputHost(pipeName,Path.GetFullPath(client),Path.GetFullPath(keys),steam);
host.Run();return 0;

sealed class InputHost(string pipeName,string clientPath,string keyFile,ulong steamId) : IDisposable
{
    private readonly CompanionInputRouter router=new();
    private readonly CompanionTransition transition=new();
    private readonly CancellationTokenSource stop=new();
    private readonly ConcurrentQueue<CompanionRequest> outbound=new();
    private readonly HookProc keyboardCallback=KeyboardThunk,mouseCallback=MouseThunk;
    private static InputHost? current;
    private CompanionSnapshot? snapshot;
    private long received,sequence;
    private nint keyboard,mouse,gameWindow;
    private bool foreground,shift,alt,win;
    private bool cursorVisible;
    private bool routingReady;
    private int inputAvailable;
    private string routingPeer="";
    private int textMode; // 1 chat, 2 console, 3 escape menu. Never record typed text.
    private BindingProfile bindings=BindingProfile.Empty;
    private DateTime bindingStamp;
    private long lastConnection;

    public void Run()
    {
        current=this;ReloadBindings();lastConnection=Environment.TickCount64;
        keyboard=SetWindowsHookEx(13,keyboardCallback,GetModuleHandle(null),0);
        mouse=SetWindowsHookEx(14,mouseCallback,GetModuleHandle(null),0);
        if(keyboard==0 || mouse==0) throw new InvalidOperationException("无法启用训练输入接收："+Marshal.GetLastWin32Error());
        _=Task.Run(ConnectionLoop);
        // Message-only timer keeps foreground/process checks off hook callbacks.
        nint timer=SetTimer(0,0,100,0);
        try {while(GetMessage(out var message,0,0,0)>0) {if(message.Id==0x113) Refresh();TranslateMessage(ref message);DispatchMessage(ref message);}}
        finally {KillTimer(0,timer);Dispose();}
    }
    private void Refresh()
    {
        nint window=GetForegroundWindow();bool active=false;
        if(window!=0)
        {
            GetWindowThreadProcessId(window,out uint pid);
            try {using var process=Process.GetProcessById((int)pid);active=string.Equals(process.MainModule?.FileName,clientPath,StringComparison.OrdinalIgnoreCase);}
            catch(Exception ex) when(ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { }
        }
        foreground=active;gameWindow=active ? window : 0;
        if(!active) {shift=alt=win=false;}
        else
        {
            shift=(GetAsyncKeyState(0x10)&0x8000)!=0;
            alt=(GetAsyncKeyState(0x12)&0x8000)!=0;
            win=(GetAsyncKeyState(0x5B)&0x8000)!=0 || (GetAsyncKeyState(0x5C)&0x8000)!=0;
        }
        var cursor=new CursorInfo {Size=Marshal.SizeOf<CursorInfo>()};
        cursorVisible=GetCursorInfo(ref cursor) && (cursor.Flags&1)!=0;
        if(active && textMode==3 && !cursorVisible) textMode=0;
        var frame=Volatile.Read(ref snapshot);
        bool ready=active && CompanionProtocol.CanRoute(frame,DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),Environment.TickCount64-Interlocked.Read(ref received));
        if(ready && (!routingReady || routingPeer!=frame!.Session))
        {
            router.Disarm(Enumerable.Range(1,255).Where(key=>(GetAsyncKeyState(key)&0x8000)!=0));
            routingPeer=frame!.Session;
        }
        routingReady=ready;
        Volatile.Write(ref inputAvailable,ready && textMode==0 && !cursorVisible && !alt && !win ? 1 : 0);
        if(Environment.TickCount64%1000<110) ReloadBindings();
        // Exit once training has ended; a new launcher start creates a new companion.
        if(Environment.TickCount64-lastConnection>120000) PostQuitMessage(0);
    }
    private void ReloadBindings()
    {
        try
        {
            var stamp=File.GetLastWriteTimeUtc(keyFile);if(stamp==bindingStamp) return;
            bindings=BindingProfile.Parse(File.ReadAllText(keyFile));bindingStamp=stamp;
        }
        catch(IOException) { } catch(UnauthorizedAccessException) { }
    }
    private async Task ConnectionLoop()
    {
        while(!stop.IsCancellationRequested)
        {
            try
            {
                using var pipe=new NamedPipeClientStream(".",pipeName,PipeDirection.InOut,PipeOptions.Asynchronous|PipeOptions.CurrentUserOnly);
                await pipe.ConnectAsync(2000,stop.Token);
                using var reader=new StreamReader(pipe,Encoding.UTF8,false,2048,true);
                using var writer=new StreamWriter(pipe,new UTF8Encoding(false),1024,true){AutoFlush=true};
                using var connection=CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
                await writer.WriteLineAsync(JsonSerializer.Serialize(new CompanionRequest("","",0,"hello",steamId)));
                var read=Task.Run(async ()=>
                {
                    while(!connection.IsCancellationRequested)
                    {
                        string? line=await reader.ReadLineAsync(connection.Token);if(line is null || line.Length>16384) break;
                        var state=JsonSerializer.Deserialize<CompanionSnapshot>(line);
                        if(state is null || state.Keys.Length>32) break;
                        Volatile.Write(ref snapshot,state);Interlocked.Exchange(ref received,Environment.TickCount64);
                        Interlocked.Exchange(ref lastConnection,Environment.TickCount64);
                    }
                },connection.Token);
                try
                {
                    while(!read.IsCompleted && !stop.IsCancellationRequested)
                    {
                        var state=Volatile.Read(ref snapshot);
                        if(state is not null)
                        {
                            await writer.WriteLineAsync(JsonSerializer.Serialize(new CompanionRequest(state.Session,state.Context,0,"heartbeat",InputActive:Volatile.Read(ref inputAvailable)==1)));
                            for(int count=0;count<32&&outbound.TryDequeue(out var request);count++)
                                if(request.Session==state.Session) await writer.WriteLineAsync(JsonSerializer.Serialize(request));
                        }
                        await Task.Delay(15,stop.Token);
                    }
                }
                finally
                {
                    Volatile.Write(ref snapshot,null);connection.Cancel();pipe.Dispose();
                    while(outbound.TryDequeue(out _)){}
                    try {await read;} catch(Exception ex) when(ex is IOException or OperationCanceledException or JsonException or ObjectDisposedException) { }
                }
            }
            catch(Exception ex) when(ex is IOException or TimeoutException or OperationCanceledException or UnauthorizedAccessException or JsonException or ObjectDisposedException) { }
            if(!stop.IsCancellationRequested) await Task.Delay(500,stop.Token).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
    }
    private bool Route(int key,bool down)
    {
        var state=Volatile.Read(ref snapshot);
        bool active=routingReady && foreground && GetForegroundWindow()==gameWindow && !alt && !win &&
            CompanionProtocol.CanRoute(state,DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),Environment.TickCount64-Interlocked.Read(ref received));
        bool typing=textMode!=0 || cursorVisible;
        if(active && down)
        {
            if(textMode==0 && bindings.ConsoleKeys.Contains(key)) {textMode=2;typing=true;}
            else if(textMode==0 && bindings.ChatKeys.Contains(key)) {textMode=1;typing=true;}
            else if(key==0x1B) {textMode=textMode==0 ? 3 : 0;typing=true;}
            else if((textMode==1 && key==0x0D) || (textMode==2 && bindings.ConsoleKeys.Contains(key))) {textMode=0;typing=true;}
        }
        bool pending=state is not null && transition.Pending(state.Session,state.Acknowledged,Environment.TickCount64);
        // A menu may already be opening in the game before its reply reaches
        // this process. Do not let the first wheel notch leak to a weapon bind.
        IReadOnlyCollection<int> blocked=pending ? bindings.SwitchKeys.Concat(state!.Keys.Select(k=>k.Key)).Concat([-1,-2]).Distinct().ToArray() : bindings.SwitchKeys;
        var decision=router.Route(key,down,shift,active,typing,pending || state?.Menu==true,pending ? [] : state?.Keys ?? [],blocked);
        if(decision.Action is { } action && state is not null && outbound.Count<32)
        {
            long id=Interlocked.Increment(ref sequence);
            outbound.Enqueue(new(state.Session,state.Context,id,action,Key:key));
            if(action is not ("up" or "down" or "effect" or "flight" or "camera")) transition.Begin(state.Session,id,Environment.TickCount64);
        }
        return decision.Block;
    }
    private static nint KeyboardThunk(int code,nint message,nint pointer)
    {
        var h=current;if(code<0 || h is null) return CallNextHookEx(0,code,message,pointer);
        var key=Marshal.PtrToStructure<KeyboardEvent>(pointer);bool down=message is 0x100 or 0x104;
        if(key.Key is 0x10 or 0xA0 or 0xA1) h.shift=down;
        if(key.Key is 0x12 or 0xA4 or 0xA5) h.alt=down;
        if(key.Key is 0x5B or 0x5C) h.win=down;
        try {if(h.Route((int)key.Key,down)) return 1;} catch(Exception) {h.router.Reset();}
        return CallNextHookEx(0,code,message,pointer);
    }
    private static nint MouseThunk(int code,nint message,nint pointer)
    {
        var h=current;if(code<0 || h is null || message==0x200) return CallNextHookEx(0,code,message,pointer);
        var data=Marshal.PtrToStructure<MouseEvent>(pointer);
        int key=message switch {0x20A=>(short)(data.Data>>16)>0 ? -1 : -2,0x201 or 0x202=>1,0x204 or 0x205=>2,0x207 or 0x208=>4,0x20B or 0x20C=>(data.Data>>16)==1 ? 5 : 6,_=>0};
        bool down=message is not (0x202 or 0x205 or 0x208 or 0x20C);
        try {if(key!=0 && h.Route(key,down)) return 1;} catch(Exception) {h.router.Reset();}
        return CallNextHookEx(0,code,message,pointer);
    }
    public void Dispose(){stop.Cancel();if(keyboard!=0) {UnhookWindowsHookEx(keyboard);keyboard=0;}if(mouse!=0) {UnhookWindowsHookEx(mouse);mouse=0;}current=null;}
    private delegate nint HookProc(int code,nint message,nint data);
    [StructLayout(LayoutKind.Sequential)] private struct KeyboardEvent {public uint Key,Scan,Flags,Time;public nuint Extra;}
    [StructLayout(LayoutKind.Sequential)] private struct Point {public int X,Y;}
    [StructLayout(LayoutKind.Sequential)] private struct CursorInfo {public int Size,Flags;public nint Cursor;public Point Position;}
    [StructLayout(LayoutKind.Sequential)] private struct MouseEvent {public Point Point;public uint Data,Flags,Time;public nuint Extra;}
    [StructLayout(LayoutKind.Sequential)] private struct Message {public nint Window;public uint Id;public nuint WParam;public nint LParam;public uint Time;public Point Point;public uint Private;}
    [DllImport("user32.dll",SetLastError=true)] private static extern nint SetWindowsHookEx(int id,HookProc callback,nint module,uint thread);
    [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(nint hook);
    [DllImport("user32.dll")] private static extern nint CallNextHookEx(nint hook,int code,nint message,nint data);
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode)] private static extern nint GetModuleHandle(string? name);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll")] private static extern bool GetCursorInfo(ref CursorInfo info);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window,out uint process);
    [DllImport("user32.dll")] private static extern int GetMessage(out Message message,nint window,uint min,uint max);
    [DllImport("user32.dll")] private static extern bool TranslateMessage(ref Message message);
    [DllImport("user32.dll")] private static extern nint DispatchMessage(ref Message message);
    [DllImport("user32.dll")] private static extern nint SetTimer(nint window,nint id,uint milliseconds,nint callback);
    [DllImport("user32.dll")] private static extern bool KillTimer(nint window,nint timer);
    [DllImport("user32.dll")] private static extern void PostQuitMessage(int code);
}
