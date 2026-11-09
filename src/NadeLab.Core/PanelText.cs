using System.Text;

namespace NadeLab.Core;

public static class PanelText
{
    // All in-game text has one bounded plain-text path, including dynamically
    // supplied notices. Do not cut a UTF-8 sequence or pass markup/control codes.
    public static string Line(string? text,int maxRunes=40)
    {
        if(string.IsNullOrEmpty(text)) return "";
        var output=new StringBuilder();int count=0;
        foreach(var rune in text.EnumerateRunes())
        {
            if(Rune.IsControl(rune)) {if(rune.Value is 10 or 13 or 9) output.Append(' ');continue;}
            if(count++>=maxRunes) {output.Append('…');break;}
            output.Append(rune.Value is 60 or 62 ? " " : rune.ToString());
        }
        return output.ToString().Trim();
    }

    public static string Bounded(string? text,int maxBytes=480,int maxLines=7)
    {
        var output=new StringBuilder();int bytes=0,lines=1;
        foreach(var rune in (text ?? "").EnumerateRunes())
        {
            if(rune.Value==13) continue;
            if(rune.Value==10)
            {if(lines++>=maxLines || bytes+1>maxBytes) break;output.Append('\n');bytes++;continue;}
            if(Rune.IsControl(rune)) continue;
            string value=rune.Value is 60 or 62 ? " " : rune.ToString();
            int next=Encoding.UTF8.GetByteCount(value);
            if(bytes+next>maxBytes) break;
            output.Append(value);bytes+=next;
        }
        return output.ToString();
    }

    public static int Move(int selected,int count,int delta)=>count<=0 ? 0 :
        (int)(((long)Math.Clamp(selected,0,count-1)+delta%count+count)%count);

    public static string WithKeys(string body,string keys)
    {
        int footerBytes=Encoding.UTF8.GetByteCount(keys),footerLines=keys.Count(c=>c=='\n')+1;
        if(footerBytes>360 || footerLines>3) throw new ArgumentException("Shortcut footer exceeds its reserved budget.",nameof(keys));
        if(keys.Length==0) return Bounded(body);
        int bodyBytes=480-footerBytes-1,bodyLines=6-footerLines;
        string clipped=Bounded(body,bodyBytes,bodyLines).TrimEnd();
        // Plain center-text fallback has less room than the two-label custom
        // HUD. Preserve the selected action even near the end of its window.
        string? selected=body.Split('\n').FirstOrDefault(line=>line.StartsWith("▶ "));
        if(selected is not null && !clipped.Split('\n').Contains(selected))
            clipped=Bounded(body.Split('\n')[0]+'\n'+selected,bodyBytes,bodyLines).TrimEnd();
        return clipped+'\n'+keys;
    }

    // A separate, smaller label gets an independent budget. Reject accidental
    // extra rows rather than silently dropping an active shortcut.
    public static string Shortcuts(string? keys)
    {
        keys??="";
        if(Encoding.UTF8.GetByteCount(keys)>360 || keys.Count(c=>c=='\n')>1)
            throw new ArgumentException("Shortcut label exceeds its two-row budget.",nameof(keys));
        return Bounded(keys,360,2);
    }

    // Independent feedback budget; never append transient feedback to Menu().
    public static string Notice(string? message,string? status=null)
    {
        var runes=Line(message,74).EnumerateRunes().ToArray();
        var lines=new List<string>();
        for(int start=0;start<runes.Length;)
        {
            int end=Math.Min(start+38,runes.Length);
            if(end<runes.Length)
            {
                int boundary=end;
                while(boundary>start && !Rune.IsWhiteSpace(runes[boundary-1]) && runes[boundary-1].Value is not (0xB7 or ',' or ';' or 0xFF0C or 0xFF1B or 0x3001)) boundary--;
                if(boundary>start) end=boundary;
            }
            lines.Add(string.Concat(runes.Skip(start).Take(end-start).Select(r=>r.ToString())).TrimEnd());
            start=end;
            while(start<runes.Length && Rune.IsWhiteSpace(runes[start])) start++;
        }
        // The secondary line carries context that must survive a long primary
        // message. Reserve it first, including four-byte Unicode characters.
        string secondary=Bounded(Line(status,40),164,1).Trim();
        if(secondary.Length==0) return Bounded(string.Join('\n',lines),360,3);
        int reserved=Encoding.UTF8.GetByteCount(secondary)+1;
        string primary=Bounded(string.Join('\n',lines),360-reserved,2).TrimEnd();
        return primary.Length==0 ? secondary : primary+'\n'+secondary;
    }

    public static string[] Pages(IEnumerable<string> paragraphs)
    {
        var lines=new List<string>();
        foreach(var paragraph in paragraphs)
            foreach(var raw in paragraph.Split('\n'))
            {
                var runes=Line(raw,int.MaxValue).EnumerateRunes().ToArray();
                for(int i=0;i<runes.Length;i+=28)
                    lines.Add(string.Concat(runes.Skip(i).Take(28).Select(r=>r.ToString())));
            }
        return lines.Chunk(2).Select(page=>string.Join('\n',page)).DefaultIfEmpty("暂无记录").ToArray();
    }

    public static string MenuKeys(bool placement=false,bool pages=false,bool regionDraft=false,bool flashObjective=false,bool root=false)=>
        "滚轮/↑↓/F6/F7"+(pages ? "翻页" : "选择")+" · "+
        (pages ? "R返回" : root ? "E/F8确定 · R关闭" : "E/F8确定 · R返回")+" · F1关闭\nWASD移动 · "+
        (placement ? "F4预测 · F9视角 · F5取消定位" : regionDraft ? "V飞行 · F2区域 · F3换道具弃草稿 · F4效果" : flashObjective ? "V飞行 · F2要求 · F3道具 · F4结果 · F5改爆点" : "V飞行 · F2区域 · F3道具 · F4效果 · F5重选");

    public static string Menu(string title,IReadOnlyList<string> labels,int selected,bool placement=false,bool regionDraft=false,bool flashObjective=false,bool root=false)
        =>WithKeys(MenuBody(title,labels,selected,root),MenuKeys(placement,regionDraft:regionDraft,flashObjective:flashObjective,root:root));

    public static string MenuBody(string title,IReadOnlyList<string> labels,int selected,bool root=false)
    {
        if(labels.Count==0) return Bounded(Line(title,28)+"\n暂无可用操作");
        selected=Math.Clamp(selected,0,labels.Count-1);
        int rows=root ? 6 : 5;
        int first=Math.Clamp(selected-1,0,Math.Max(0,labels.Count-rows));
        // Each row has its own budget, including the last selected option.
        int titleBytes=root ? 66 : 80,rowBytes=root ? 68 : 79;
        var lines=new List<string> {Bounded($"{Line(title,22)}  {selected+1}/{labels.Count}",titleBytes,1)};
        for(int i=first;i<Math.Min(first+rows,labels.Count);i++)
            lines.Add(Bounded((i==selected ? "▶ " : "   ")+Line(labels[i],root ? 20 : 24),rowBytes,1));
        return string.Join('\n',lines);
    }
}
