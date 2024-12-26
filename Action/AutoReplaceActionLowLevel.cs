using System.Collections.Generic;
using System.Numerics;
using DailyRoutines.Abstracts;
using Dalamud.Hooking;
using Dalamud.Interface.Utility.Raii;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using ImGuiNET;
using Lumina.Excel.Sheets;

namespace DailyRoutines.Modules;

public unsafe class AutoReplaceActionLowLevel : DailyModuleBase
{
    public override ModuleInfo Info => new()
    {
        Title = GetLoc("AutoReplaceActionLowLevelTitle"),
        Description = GetLoc("AutoReplaceActionLowLevelDescription"),
        Category = ModuleCategories.Action,
    };

    private static readonly CompSig IsActionReplaceableSig =
        new("E8 ?? ?? ?? ?? 84 C0 0F 84 ?? ?? ?? ?? C6 83 ?? ?? ?? ?? ?? 48 8B 5C 24");
    private delegate bool IsActionReplaceableDelegate(uint actionID);
    private static Hook<IsActionReplaceableDelegate> IsActionReplaceableHook;

    private static readonly CompSig GetAdjustedActionIDSig = new("E8 ?? ?? ?? ?? 89 03 8B 03");
    private delegate uint GetAdjustedActionIDDelegate(ActionManager* manager, uint actionID);
    private static Hook<GetAdjustedActionIDDelegate> GetAdjustedActionIDHook;

    private static readonly CompSig GetIconIDForSlotSig = new("40 57 48 83 EC ?? 0F B6 C2 41 8B F8");
    private delegate uint GetIconIDForSlotDelegate(
        RaptureHotbarModule.HotbarSlot* slot, RaptureHotbarModule.HotbarSlotType type, uint actionID);
    private static Hook<GetIconIDForSlotDelegate> GetIconIDForSlotHook;

    // 原技能 ID - 替换后技能 ID (递归替换)
    private static readonly Dictionary<uint, uint> ActionReplacements = new()
    {
        // 狂喜之心 - 医济
        [16534] = 133,
        // 医济 - 医治
        [133] = 124,
        // 安慰之心 - 救疗
        [16531] = 135,
        // 救疗 - 治疗
        [135] = 120,
        // 鼓舞激励之策 - 医术
        [185] = 190,
        // 福星 - 吉星
        [3610] = 3594,
        // 阳星相位 - 阳星
        [3601] = 3600,
    };

    public override void Init()
    {
        IsActionReplaceableHook ??=
            DService.Hook.HookFromSignature<IsActionReplaceableDelegate>(
                IsActionReplaceableSig.Get(), IsActionReplaceableDetour);
        IsActionReplaceableHook.Enable();

        GetAdjustedActionIDHook ??=
            DService.Hook.HookFromSignature<GetAdjustedActionIDDelegate>(
                GetAdjustedActionIDSig.Get(), GetAdjustedActionIDDetour);
        GetAdjustedActionIDHook.Enable();

        GetIconIDForSlotHook ??=
            DService.Hook.HookFromSignature<GetIconIDForSlotDelegate>(
                GetIconIDForSlotSig.Get(), GetIconIDForSlotDetour);
        GetIconIDForSlotHook.Enable();
    }

    public override void ConfigUI()
    {
        var tableSize = new Vector2(ImGui.GetContentRegionAvail().X / 2, 0);
        using var table = ImRaii.Table("ActionReplacementDisplayTable", 3, ImGuiTableFlags.None, tableSize);
        if (!table) return;

        // 让它们在视觉上看起来更平均
        ImGui.TableSetupColumn("技能1", ImGuiTableColumnFlags.None, 30);
        ImGui.TableSetupColumn("箭头", ImGuiTableColumnFlags.None, 10);
        ImGui.TableSetupColumn("技能2", ImGuiTableColumnFlags.None, 40);

        foreach (var (action0, action1) in ActionReplacements)
        {
            var action0Data = LuminaCache.GetRow<Action>(action0);
            var action1Data = LuminaCache.GetRow<Action>(action1);
            if (action0Data.RowId == 0 || action1Data.RowId == 0) continue;

            var action0Icon = DService.Texture.GetFromGameIcon(new(action0Data.Icon)).GetWrapOrDefault();
            var action1Icon = DService.Texture.GetFromGameIcon(new(action1Data.Icon)).GetWrapOrDefault();
            if (action0Icon == null || action1Icon == null) continue;

            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            ImGuiOm.TextImage(action0Data.Name.ExtractText(), action0Icon.ImGuiHandle, new(ImGui.GetTextLineHeightWithSpacing()));

            ImGui.TableNextColumn();
            ImGui.AlignTextToFramePadding();
            ImGui.Text("→");

            ImGui.TableNextColumn();
            ImGuiOm.TextImage(action1Data.Name.ExtractText(), action1Icon.ImGuiHandle, new(ImGui.GetTextLineHeightWithSpacing()));
        }
    }

    private static uint GetAdjustedActionIDDetour(ActionManager* manager, uint actionID) =>
        !TryGetReplacement(actionID, out var adjustedActionID)
            ? GetAdjustedActionIDHook.Original(manager, actionID)
            : adjustedActionID;

    private static bool TryGetReplacement(uint actionID, out uint adjustedActionID)
    {
        while (true)
        {
            adjustedActionID = 0;
            if (IsActionUnlocked(actionID)) return false;
            if (!ActionReplacements.TryGetValue(actionID, out var info)) return false;

            if (IsActionUnlocked(info))
            {
                adjustedActionID = info;
                return true;
            }

            actionID = info;
        }
    }

    private static uint GetIconIDForSlotDetour(
        RaptureHotbarModule.HotbarSlot* slot, RaptureHotbarModule.HotbarSlotType type, uint actionID)
    {
        if (type != RaptureHotbarModule.HotbarSlotType.Action)
            return GetIconIDForSlotHook.Original(slot, type, actionID);

        return !TryGetReplacement(actionID, out var adjustedActionID)
                   ? GetIconIDForSlotHook.Original(slot, type, actionID)
                   : LuminaCache.GetRow<Action>(adjustedActionID).Icon;
    }

    private static bool IsActionReplaceableDetour(uint actionID) => true;
}
