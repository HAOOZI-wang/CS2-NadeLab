param([Parameter(Mandatory=$true)][string]$Root,[Parameter(Mandatory=$true)][string]$Map)
$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot 'NadeLab.Common.ps1')
$config=Read-NlInstallation $Root
$logs=Join-Path $config.instance 'logs'
try {
    if ($Map -notin $script:NlMaps) { throw 'Unsupported map.' }
    Assert-NlInstanceStopped $config.instance
    # The hidden helper owns a real console. Source 2 needs CONIN$, not a pipe
    # or NUL, or its console polling can spam errors every server tick.
    Add-Type -TypeDefinition @'
using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
public static class NadeLabNativeLauncher {
    [StructLayout(LayoutKind.Sequential)] struct SECURITY_ATTRIBUTES { public int Length; public IntPtr Descriptor; [MarshalAs(UnmanagedType.Bool)] public bool Inherit; }
    [StructLayout(LayoutKind.Sequential, CharSet=CharSet.Unicode)] struct STARTUPINFO {
        public int cb; public string reserved,desktop,title; public int x,y,xSize,ySize,xChars,yChars,fill,flags; public short show,reservedBytes; public IntPtr reserved2,input,output,error;
    }
    [StructLayout(LayoutKind.Sequential)] struct PROCESS_INFORMATION { public IntPtr process,thread; public int pid,tid; }
    [StructLayout(LayoutKind.Sequential)] struct POWER_THROTTLING { public uint Version,ControlMask,StateMask; }
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)] static extern IntPtr CreateFile(string name,uint access,uint sharing,ref SECURITY_ATTRIBUTES attributes,uint creation,uint flags,IntPtr template);
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)] [return:MarshalAs(UnmanagedType.Bool)] static extern bool CreateProcess(string application,StringBuilder command,IntPtr processAttributes,IntPtr threadAttributes,bool inherit,uint flags,IntPtr environment,string directory,ref STARTUPINFO startup,out PROCESS_INFORMATION info);
    [DllImport("kernel32.dll",SetLastError=true)] static extern bool CloseHandle(IntPtr handle);
    [DllImport("kernel32.dll",SetLastError=true)] static extern uint WaitForSingleObject(IntPtr handle,uint milliseconds);
    [DllImport("kernel32.dll",SetLastError=true)] static extern bool GetExitCodeProcess(IntPtr handle,out uint code);
    [DllImport("kernel32.dll",SetLastError=true)] static extern bool SetProcessInformation(IntPtr process,int info,ref POWER_THROTTLING value,uint size);
    public static string Quote(string value) {
        if (value==null) throw new ArgumentNullException("value");
        var result=new StringBuilder("\""); int slashes=0;
        foreach(char c in value) {
            if(c=='\\') { slashes++; continue; }
            if(c=='\"') { result.Append('\\',slashes*2+1); result.Append(c); slashes=0; continue; }
            result.Append('\\',slashes); result.Append(c); slashes=0;
        }
        result.Append('\\',slashes*2); return result.Append('"').ToString();
    }
    static IntPtr Open(string name,uint access,uint create) {
        var attributes=new SECURITY_ATTRIBUTES {Length=Marshal.SizeOf(typeof(SECURITY_ATTRIBUTES)),Inherit=true};
        var handle=CreateFile(name,access,3,ref attributes,create,128,IntPtr.Zero);
        if(handle==new IntPtr(-1)) throw new Win32Exception(Marshal.GetLastWin32Error(),"Could not open the training server console/log.");
        return handle;
    }
    public static int Run(string exe,string[] arguments,string workingDirectory,string stdout,string stderr,string receipt) {
        IntPtr input=IntPtr.Zero,output=IntPtr.Zero,error=IntPtr.Zero; var info=new PROCESS_INFORMATION();
        try {
            input=Open("CONIN$",0x80000000,3); output=Open(stdout,0x40000000,2); error=Open(stderr,0x40000000,2);
            var start=new STARTUPINFO {cb=Marshal.SizeOf(typeof(STARTUPINFO)),flags=0x101,show=0,input=input,output=output,error=error};
            var command=new StringBuilder(Quote(exe)); foreach(var arg in arguments) command.Append(' ').Append(Quote(arg));
            if(!CreateProcess(exe,command,IntPtr.Zero,IntPtr.Zero,true,0,IntPtr.Zero,workingDirectory,ref start,out info)) throw new Win32Exception(Marshal.GetLastWin32Error(),"Could not launch the isolated training server.");
            var power=new POWER_THROTTLING {Version=1,ControlMask=4,StateMask=0};
            bool timer=SetProcessInformation(info.process,4,ref power,(uint)Marshal.SizeOf(typeof(POWER_THROTTLING)));
            System.IO.File.WriteAllText(receipt,"{\"pid\":"+info.pid+",\"honorTimerResolution\":"+(timer?"true":"false")+"}",new UTF8Encoding(false));
            WaitForSingleObject(info.process,0xFFFFFFFF); uint code; if(!GetExitCodeProcess(info.process,out code)) throw new Win32Exception(Marshal.GetLastWin32Error());
            return unchecked((int)code);
        } finally {
            foreach(var handle in new[]{input,output,error,info.process,info.thread}) if(handle!=IntPtr.Zero && handle!=new IntPtr(-1)) CloseHandle(handle);
        }
    }
}
'@
    $serverArgs=@('-dedicated','-insecure','-usercon','-ip','127.0.0.1','-port',[string]$config.port,'-game','csgo','+sv_lan','1','+game_type','0','+game_mode','1','+bot_quota','0','+map',$Map,'+exec','server.cfg')
    $code=[NadeLabNativeLauncher]::Run($config.executable,$serverArgs,(Split-Path -Parent $config.executable),(Join-Path $logs 'stdout.log'),(Join-Path $logs 'stderr.log'),(Join-Path $Root 'release-state/process.json'))
    [IO.File]::WriteAllText((Join-Path $logs 'launcher.log'),('Server exited with code '+$code))
    exit $code
} catch {
    [IO.File]::WriteAllText((Join-Path $logs 'launcher.log'),($_.Exception.Message+"`r`n"+$_.ScriptStackTrace))
    exit 1
}
