using System.Text;

namespace NadeLab.Plugin;

// Read a fixed byte snapshot rather than scanning a growing telemetry file.
// A partial first record and an unfinished last append are never evidence.
internal static class BoundedLogTail
{
    public static string[] ReadLines(string path,int maxBytes=8*1024*1024,int maxLines=128)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxLines);
        using var stream=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete);
        long length=stream.Length,start=Math.Max(0,length-maxBytes);
        bool partial=false;
        if(start>0) {stream.Position=start-1;partial=stream.ReadByte()!='\n';}
        stream.Position=start;
        var buffer=new byte[(int)(length-start)];
        int read=0;
        while(read<buffer.Length)
        {
            int count=stream.Read(buffer,read,buffer.Length-read);
            if(count==0) break;
            read+=count;
        }
        if(read==0) return [];
        int begin=partial ? Array.IndexOf(buffer,(byte)'\n',0,read)+1 : 0;
        if(partial && begin==0) return [];
        int end=Array.LastIndexOf(buffer,(byte)'\n',read-1,read);
        if(end<begin) return [];
        string text=Encoding.UTF8.GetString(buffer,begin,end-begin);
        if(start==0) text=text.TrimStart('\uFEFF');
        return text.Split('\n').Select(line=>line.TrimEnd('\r')).Where(line=>!string.IsNullOrWhiteSpace(line)).TakeLast(maxLines).ToArray();
    }
}
