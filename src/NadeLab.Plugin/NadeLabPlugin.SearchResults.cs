using System.Globalization;
using CounterStrikeSharp.API.Core;
using NadeLab.Core;

namespace NadeLab.Plugin;

public sealed partial class NadeLabPlugin
{
    private void OpenCandidateList(CCSPlayerController player,int requestedPage=0)
    {
        if(!Check(player,out _)) return;
        var session=Get(player);
        if(session.Result is not {Matches.Count:>0} result) {OpenMenu(player);return;}
        var page=SearchPresentation.Page(result,requestedPage);
        var menu=WorkflowPanel(player,$"投法列表 {page.PageIndex+1}/{page.PageCount} · 共{page.TotalCount}",out var Add,
            ()=>session.Result is {Matches.Count:>0});
        menu.Identity+=$"/{page.PageIndex}";
        menu.Back=OpenResultsMenu;
        menu.RestoreCount=page.Choices.Count;
        menu.Feedback=()=>"翻页保留当前投法；选择条目后准备手投，效果仍须实投验证。";
        foreach(var choice in page.Choices)
        {
            int number=choice.Number;var candidate=choice.Candidate;
            string quality=session.FlashMode ? "待核验" : SearchPresentation.IsRecommended(candidate) ? "较稳" : "低容错";
            Add(PanelText.Line($"{number}. {quality} · {ThrowInstructions.Compact(candidate.Aim)}",35),who=>
            {
                if(!ReferenceEquals(session.Result?.Matches.ElementAtOrDefault(number-1),candidate)) return;
                if(CurrentFlashVerification(who) is {Trial.Complete:false,Trial.ReleasedAt:not null})
                {Say(who,"本次闪光仍在观测；汇总完所有假人后再切换投法。");return;}
                PrepareThrow(who,number.ToString(CultureInfo.InvariantCulture));
            },hint:$"投法{number} · {RouteLabel(candidate)} · 选择后就位并准备手投。",key:number.ToString(CultureInfo.InvariantCulture));
        }
        if(page.PageIndex>0) Add("上一页",who=>OpenCandidateList(who,page.PageIndex-1),key:"previous");
        if(page.PageIndex+1<page.PageCount) Add("下一页",who=>OpenCandidateList(who,page.PageIndex+1),key:"next");
        Add("返回当前投法",OpenResultsMenu,key:"back");
        menu.Open(player);
    }
}
