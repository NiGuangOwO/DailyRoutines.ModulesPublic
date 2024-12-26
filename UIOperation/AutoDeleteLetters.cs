using System.Numerics;
using ClickLib;
using DailyRoutines.Abstracts;
using DailyRoutines.Managers;
using DailyRoutines.Windows;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Interface.Colors;
using FFXIVClientStructs.FFXIV.Client.UI.Info;
using ImGuiNET;
using Lumina.Excel.Sheets;

namespace DailyRoutines.Modules;

public unsafe class AutoDeleteLetters : DailyModuleBase
{
    public override ModuleInfo Info => new()
    {
        Title = GetLoc("AutoDeleteLettersTitle"),
        Description = GetLoc("AutoDeleteLettersDescription"),
        Category = ModuleCategories.UIOperation,
    };

    public override void Init()
    {
        TaskHelper ??= new TaskHelper();
        Overlay ??= new Overlay(this);

        DService.AddonLifecycle.RegisterListener(AddonEvent.PostSetup, "SelectYesno", AlwaysYes);
        DService.AddonLifecycle.RegisterListener(AddonEvent.PostSetup, "LetterList", OnAddonLetterList);
        DService.AddonLifecycle.RegisterListener(AddonEvent.PreFinalize, "LetterList", OnAddonLetterList);

        if (LetterList != null) OnAddonLetterList(AddonEvent.PostSetup, null);
    }

    public override void OverlayUI()
    {
        var addon = LetterList;
        if (addon == null) return;

        var pos = new Vector2(addon->GetX() - ImGui.GetWindowSize().X, addon->GetY() + 6);
        ImGui.SetWindowPos(pos);

        ImGui.TextColored(ImGuiColors.DalamudYellow, Lang.Get("AutoDeleteLettersTitle"));

        ImGui.Separator();

        ImGui.BeginDisabled(TaskHelper.IsBusy);
        if (ImGui.Button(Lang.Get("Start"))) TaskHelper.Enqueue(RightClickLetter);
        ImGui.EndDisabled();

        ImGui.SameLine();
        if (ImGui.Button(Lang.Get("Stop"))) TaskHelper.Abort();
    }

    public bool? RightClickLetter()
    {
        var addon = LetterList;
        if (addon == null || !IsAddonAndNodesReady(addon)) return false;

        var infoProxy = InfoProxyLetter.Instance();
        var category = CurrentlySelectedCategory();
        if (category == -1)
        {
            TaskHelper.Abort();
            return true;
        }

        var letterAmount = category switch
        {
            0 => infoProxy->NumLettersFromFriends,
            1 => infoProxy->NumLettersFromPurchases,
            2 => infoProxy->NumLettersFromGameMasters,
            _ => 0,
        };

        if (letterAmount == 0)
        {
            TaskHelper.Abort();
            return true;
        }

        Callback(addon, true, 0, category == 1 ? 0 : 1, 0, 1); // 第二个 0 是索引

        TaskHelper.DelayNext(100, "Delay_RightClickLetter");
        TaskHelper.Enqueue(ClickDeleteEntry);
        return true;
    }

    public bool? ClickDeleteEntry()
    {
        var addon = ContextMenu;
        if (addon == null || !IsAddonAndNodesReady(addon)) return false;

        if (!ClickContextMenu(LuminaCache.GetRow<Addon>(431).Text.ExtractText())) return false;

        TaskHelper.DelayNext(100, "Delay_ClickDelete");
        TaskHelper.Enqueue(RightClickLetter);
        return true;

    }

    private void AlwaysYes(AddonEvent type, AddonArgs args)
    {
        if (!TaskHelper.IsBusy) return;
        Click.SendClick("select_yes");
    }

    private void OnAddonLetterList(AddonEvent type, AddonArgs? _)
    {
        Overlay.IsOpen = type switch
        {
            AddonEvent.PostSetup => true,
            AddonEvent.PreFinalize => false,
            _ => Overlay.IsOpen,
        };
    }

    private static int CurrentlySelectedCategory()
    {
        var addon = LetterList;
        if (addon == null) return -1;

        for (var i = 6U; i < 9U; i++)
        {
            var buttonNode = addon->GetButtonNodeById(i);
            if (buttonNode == null) continue;

            if ((buttonNode->Flags & 0x40000) != 0) return (int)(i - 6);
        }

        return -1;
    }

    public override void Uninit()
    {
        DService.AddonLifecycle.UnregisterListener(OnAddonLetterList);
        DService.AddonLifecycle.UnregisterListener(AlwaysYes);

        base.Uninit();
    }
}
