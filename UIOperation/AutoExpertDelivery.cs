using System.Collections.Generic;
using System.Numerics;
using ClickLib;
using ClickLib.Clicks;
using DailyRoutines.Abstracts;
using DailyRoutines.Infos;
using DailyRoutines.Infos.Clicks;
using DailyRoutines.Managers;
using DailyRoutines.Windows;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Utility.Raii;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.UI;
using Lumina.Excel.Sheets;
using GrandCompany = FFXIVClientStructs.FFXIV.Client.UI.Agent.GrandCompany;

namespace DailyRoutines.Modules;

public unsafe class AutoExpertDelivery : DailyModuleBase
{
    public override ModuleInfo Info => new()
    {
        Title = GetLoc("AutoExpertDeliveryTitle"),
        Description = GetLoc("AutoExpertDeliveryDescription"),
        Category = ModuleCategories.UIOperation,
    };

    private class Config : ModuleConfiguration
    {
        public bool SkipWhenHQ;
        public bool AutoSwitchWhenOpen = true;
    }

    private static readonly List<InventoryType> ValidInventoryTypes =
    [
        InventoryType.Inventory1, InventoryType.Inventory2, InventoryType.Inventory3,
        InventoryType.Inventory4, InventoryType.ArmoryBody, InventoryType.ArmoryEar, InventoryType.ArmoryFeets,
        InventoryType.ArmoryHands, InventoryType.ArmoryHead, InventoryType.ArmoryLegs, InventoryType.ArmoryRings,
        InventoryType.ArmoryNeck, InventoryType.ArmoryWrist, InventoryType.ArmoryRings, InventoryType.ArmoryMainHand,
        InventoryType.ArmoryOffHand,
    ];

    private static readonly Dictionary<uint, uint> ZoneToEventID = new()
    {
        // 黑涡团
        [128] = 1441793,
        // 双蛇党
        [132] = 1441794,
        // 恒辉队
        [130] = 1441795,
    };

    private static Config ModuleConfig = null!;

    private static HashSet<uint> HQItems = [];

    public override void Init()
    {
        ModuleConfig = LoadConfig<Config>() ?? new();

        TaskHelper ??= new();
        Overlay ??= new Overlay(this);

        DService.AddonLifecycle.RegisterListener(AddonEvent.PostSetup, "GrandCompanySupplyList", OnAddonSupplyList);
        DService.AddonLifecycle.RegisterListener(AddonEvent.PreFinalize, "GrandCompanySupplyList", OnAddonSupplyList);
        DService.AddonLifecycle.RegisterListener(AddonEvent.PostSetup, "GrandCompanySupplyReward", OnAddonSupplyReward);
        DService.AddonLifecycle.RegisterListener(AddonEvent.PostSetup, "SelectYesno", OnAddonYesno);
    }

    public override void OverlayUI()
    {
        var addon = GrandCompanySupplyList;
        if (!IsAddonAndNodesReady(addon)) return;

        using var font = FontManager.UIFont80.Push();
        
        var pos = new Vector2(addon->GetX(), addon->GetY() - ImGui.GetTextLineHeightWithSpacing());
        ImGui.SetWindowPos(pos);

        ImGui.TextColored(ImGuiColors.DalamudYellow, GetLoc("AutoExpertDeliveryTitle"));

        var playerState = PlayerState.Instance();
        var rank        = playerState->GetGrandCompanyRank();
        var rankText = (GrandCompany)playerState->GrandCompany switch
        {
            GrandCompany.Maelstrom      => LuminaCache.GetRow<GCRankLimsaMaleText>(rank).Singular.ExtractText(),
            GrandCompany.TwinAdder      => LuminaCache.GetRow<GCRankGridaniaMaleText>(rank).Singular.ExtractText(),
            GrandCompany.ImmortalFlames => LuminaCache.GetRow<GCRankUldahMaleText>(rank).Singular.ExtractText(),
            _                           => string.Empty,
        };
        if (string.IsNullOrEmpty(rankText)) return;

        var rankData = LuminaCache.GetRow<GrandCompanyRank>(rank);
        var iconID = (GrandCompany)playerState->GrandCompany switch
        {
            GrandCompany.Maelstrom      => rankData.IconMaelstrom,
            GrandCompany.TwinAdder      => rankData.IconSerpents,
            GrandCompany.ImmortalFlames => rankData.IconFlames,
            _                           => 0,
        };
        if (iconID == 0) return;

        var icon = DService.Texture.GetFromGameIcon(new((uint)iconID)).GetWrapOrDefault();
        if (icon == null) return;

        ImGui.SameLine();
        using (ImRaii.Group())
        {
            ImGui.Image(icon.ImGuiHandle, new(ImGui.GetTextLineHeightWithSpacing()));
            
            ImGui.SameLine();
            ImGui.Text(rankText);
        }
        
        ImGuiOm.HelpMarker($"{Lang.Get("ConflictKey")}: {Service.Config.ConflictKey}");
        
        ImGui.Separator();
        ImGui.Spacing();

        using (ImRaii.Group())
        {
            if (ImGui.Checkbox(GetLoc("AutoExpertDelivery-SkipHQ"), ref ModuleConfig.SkipWhenHQ))
                SaveConfig(ModuleConfig);
            
            ImGui.SameLine();
            if (ImGui.Checkbox(GetLoc("AutoExpertDelivery-AutoSwitch"), ref ModuleConfig.AutoSwitchWhenOpen))
            {
                if (ModuleConfig.AutoSwitchWhenOpen)
                    ClickGrandCompanySupplyList.Using((nint)addon).ExpertDelivery();
                SaveConfig(ModuleConfig);
            }
        }

        using (ImRaii.Disabled(TaskHelper.IsBusy))
        {
            if (ImGui.Button(GetLoc("Start")))
                EnqueueARound();
        }

        ImGui.SameLine();
        using (ImRaii.Disabled(!TaskHelper.IsBusy))
        {
            if (ImGui.Button(GetLoc("Stop")))
                TaskHelper.Abort();
        }
        
        ImGui.SameLine();
        using (ImRaii.Disabled(TaskHelper.IsBusy))
        {
            if (ImGui.Button(LuminaCache.GetRow<Addon>(3280).Text.ExtractText()))
            {
                if (!ZoneToEventID.TryGetValue(DService.ClientState.TerritoryType, out var eventID)) return;
                
                TaskHelper.Enqueue(() =>
                {
                    if (!OccupiedInEvent) return true;
                
                    if (IsAddonAndNodesReady(GrandCompanySupplyList))
                        GrandCompanySupplyList->Close(true);
                
                    if (IsAddonAndNodesReady(SelectString))
                        SelectString->Close(true);

                    return false;
                });

                TaskHelper.Enqueue(
                    () => GamePacketManager.SendPackt(
                        new EventStartPackt(DService.ClientState.LocalPlayer.GameObjectId, eventID)));
            }
        }
    }

    private void EnqueueARound()
    {
        TaskHelper.Enqueue(() =>
        {
            InterruptByConflictKey(TaskHelper, this);
            return CheckIfToReachCap();
        });

        TaskHelper.Enqueue(() =>
        {
            InterruptByConflictKey(TaskHelper, this);
            ClickListUI();
        });

        TaskHelper.Enqueue(() => !IsAddonAndNodesReady(GrandCompanySupplyList));

        TaskHelper.DelayNext(500);
        TaskHelper.Enqueue(() =>
        {
            InterruptByConflictKey(TaskHelper, this);
            EnqueueARound();
        });
    }

    private bool CheckIfToReachCap()
    {
        if (!IsAddonAndNodesReady(GrandCompanySupplyList)) return false;

        var addon = (AddonGrandCompanySupplyList*)GrandCompanySupplyList;
        if (addon == null) return false;

        if (addon->ExpertDeliveryList->ListLength == 0)
        {
            TaskHelper.Abort();
            MakeSureAddonsClosed();
            return true;
        }

        var grandCompany = PlayerState.Instance()->GrandCompany;
        if ((GrandCompany)grandCompany == GrandCompany.None)
        {
            TaskHelper.Abort();
            MakeSureAddonsClosed();
            return true;
        }
        var companySeals = InventoryManager.Instance()->GetCompanySeals(grandCompany);
        var capAmount = LuminaCache.GetRow<GrandCompanyRank>(PlayerState.Instance()->GetGrandCompanyRank()).MaxSeals;

        var firstItemAmount = GrandCompanySupplyList->AtkValues[265].UInt;
        if (firstItemAmount + companySeals > capAmount)
        {
            TaskHelper.Abort();
            MakeSureAddonsClosed();
            return true;
        }

        return true;
    }

    private void ClickListUI()
    {
        if (!IsAddonAndNodesReady(GrandCompanySupplyList)) return;

        var addon = (AddonGrandCompanySupplyList*)GrandCompanySupplyList;
        if (addon == null) return;

        if (addon->ExpertDeliveryList->ListLength == 0)
        {
            TaskHelper.Abort();
            MakeSureAddonsClosed();
            return;
        }

        if (ModuleConfig.SkipWhenHQ)
        {
            HQItems = InventoryScanner(ValidInventoryTypes);

            var onlyHQLeft = true;
            for (var i = 0; i < addon->ExpertDeliveryList->ListLength; i++)
            {
                var itemID = addon->AtkUnitBase.AtkValues[i + 425].UInt;
                var isHQItem = HQItems.Contains(itemID);
                if (isHQItem) continue;

                ClickGrandCompanySupplyList.Using((nint)addon).ItemEntry(i);
                onlyHQLeft = false;
                break;
            }

            if (onlyHQLeft) TaskHelper.Abort();
        }
        else
            ClickGrandCompanySupplyList.Using((nint)addon).ItemEntry(0);
    }

    private static void MakeSureAddonsClosed()
    {
        if (GrandCompanySupplyReward != null)
            GrandCompanySupplyReward->Close(true);

        if (SelectYesno != null)
            SelectYesno->Close(true);
    }

    public static HashSet<uint> InventoryScanner(IEnumerable<InventoryType> inventories)
    {
        var inventoryManager = InventoryManager.Instance();

        var list = new HashSet<uint>();
        if (inventoryManager == null) return list;

        foreach (var inventory in inventories)
        {
            var container = inventoryManager->GetInventoryContainer(inventory);
            if (container == null) continue;

            for (var i = 0; i < container->Size; i++)
            {
                var slot = container->GetInventorySlot(i);
                if (slot == null) continue;

                var item = slot->ItemId;
                if (item == 0) continue;

                if (!slot->Flags.HasFlag(InventoryItem.ItemFlags.HighQuality)) continue;

                list.Add(item);
            }
        }

        return list;
    }

    private void OnAddonSupplyList(AddonEvent type, AddonArgs args)
    {
        Overlay.IsOpen = type switch
        {
            AddonEvent.PostSetup => true,
            AddonEvent.PreFinalize => false,
            _ => Overlay.IsOpen,
        };

        if (ModuleConfig.AutoSwitchWhenOpen && type == AddonEvent.PostSetup)
            ClickGrandCompanySupplyList.Using(args.Addon).ExpertDelivery();
    }

    private static void OnAddonSupplyReward(AddonEvent type, AddonArgs args)
    {
        if (GrandCompanySupplyList != null)
            GrandCompanySupplyList->Close(false);

        ClickGrandCompanySupplyReward.Using(args.Addon).Deliver();
    }

    private void OnAddonYesno(AddonEvent type, AddonArgs args)
    {
        if (!TaskHelper.IsBusy) return;
        Click.SendClick(ModuleConfig.SkipWhenHQ ? "select_no" : "select_yes");
    }

    public override void Uninit()
    {
        DService.AddonLifecycle.UnregisterListener(OnAddonSupplyList);
        DService.AddonLifecycle.UnregisterListener(OnAddonSupplyReward);
        DService.AddonLifecycle.UnregisterListener(OnAddonYesno);

        base.Uninit();
    }
}
