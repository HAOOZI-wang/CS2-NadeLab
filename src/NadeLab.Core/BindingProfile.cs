using System.Text.RegularExpressions;
namespace NadeLab.Core;

public sealed record BindingProfile(HashSet<int> SwitchKeys,HashSet<int> ChatKeys,HashSet<int> ConsoleKeys)
{
    public static BindingProfile Empty=>new([], [0x59,0x55,0x0D], [0xC0]);
    public static BindingProfile Parse(string text)
    {
        var profile=Empty;
        foreach(Match match in Regex.Matches(text,"\"(?<key>[^\"\\r\\n]+)\"\\s+\"(?<command>[^\"\\r\\n]*)\""))
        {
            int key=VirtualKey(match.Groups["key"].Value);if(key==0) continue;
            string command=match.Groups["command"].Value;
            if(Regex.IsMatch(command,@"(?:^|;)\s*(?:slot\d+|invnext\w*|invprev\w*|lastinv|\+quickinv|use\s+weapon_\w+)\b",RegexOptions.IgnoreCase)) profile.SwitchKeys.Add(key);
            if(Regex.IsMatch(command,@"(?:^|;)\s*messagemode[2]?\b",RegexOptions.IgnoreCase)) profile.ChatKeys.Add(key);
            if(Regex.IsMatch(command,@"(?:^|;)\s*toggleconsole\b",RegexOptions.IgnoreCase)) profile.ConsoleKeys.Add(key);
        }
        return profile;
    }
    public static int VirtualKey(string key)
    {
        key=key.ToUpperInvariant();if(key.Length==1 && char.IsAsciiLetterOrDigit(key[0])) return key[0];
        if(key.StartsWith('F')&&int.TryParse(key[1..],out int f)&&f is >=1 and <=12) return 0x6F+f;
        return key switch {"MWHEELUP"=>-1,"MWHEELDOWN"=>-2,"MOUSE1"=>1,"MOUSE2"=>2,"MOUSE3"=>4,"MOUSE4"=>5,"MOUSE5"=>6,
            "UPARROW"=>0x26,"DOWNARROW"=>0x28,"SPACE"=>0x20,"SHIFT"=>0x10,"CTRL"=>0x11,"ALT"=>0x12,"TAB"=>9,"ENTER"=>13,
            "ESCAPE"=>27,"`"=>0xC0,"-"=>0xBD,"="=>0xBB,"["=>0xDB,"]"=>0xDD,";"=>0xBA,"'"=>0xDE,","=>0xBC,"."=>0xBE,"/"=>0xBF,"\\"=>0xDC,_=>0};
    }
}
