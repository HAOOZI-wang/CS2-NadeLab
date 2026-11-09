namespace NadeLab.Core;

/// <summary>Chooses feedback content independently of menu choices and controls.</summary>
public static class PanelFeedback
{
    public static string Compose(string? timing,string? blocker,string? notice,string? context,string? status)
    {
        timing=Clean(timing);blocker=Clean(blocker);notice=Clean(notice);context=Clean(context);status=Clean(status);
        // A release cue or an unmet preparation condition must not share its
        // space with older success messages or unrelated passive context.
        if(timing.Length>0) return PanelText.Notice("【时机】"+timing);
        if(blocker.Length>0) return PanelText.Notice("【未就绪】"+blocker);
        if(notice.Length>0)
        {
            string? secondary=context.Length>0 && context!=notice ? "【说明】"+context :
                status.Length>0 && status!=notice ? "【状态】"+status : null;
            return PanelText.Notice("【反馈】"+notice,secondary);
        }
        if(context.Length>0)
            return PanelText.Notice("【说明】"+context,status.Length>0 && status!=context ? "【状态】"+status : null);
        return status.Length>0 ? PanelText.Notice("【状态】"+status) : "";
    }

    private static string Clean(string? text)=>PanelText.Line(text,int.MaxValue);
}
