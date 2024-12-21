using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using DailyRoutines.Abstracts;
using DailyRoutines.Helpers;
using DailyRoutines.Infos;
using DailyRoutines.Managers;
using DailyRoutines.Windows;
using Dalamud.Game.Gui.ContextMenu;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using ImGuiNET;
using Lumina.Excel.GeneratedSheets;

namespace DailyRoutines.Modules;

public unsafe class AutoSplitStacks : DailyModuleBase
{
    public override ModuleInfo Info => new()
    {
        Title = GetLoc("AutoSplitStacksTitle"),
        Description = GetLoc("AutoSplitStacksDescription"),
        Category = ModuleCategories.General,
    };

    private static readonly InventoryType[] InventoryTypes =
    [
        InventoryType.Inventory1, InventoryType.Inventory2, InventoryType.Inventory3,
        InventoryType.Inventory4,
    ];

    private const string Command = "/pdrsplit";
    private static readonly Vector2 CheckboxSize = ScaledVector2(20f);

    private static Config ModuleConfig = null!;

    private static LuminaSearcher<Item>? ItemSearcher;
    
    private static Item? SelectedItem;
    private static string ItemSearchInput = string.Empty;
    private static int SplitAmountInput = 1;

    private static          Overlay?           SplitOverlay;
    private static readonly FastSplitItemStack _FastSplitItemStack = new();

    private static uint FastSplitItemID;

    public override void Init()
    {
        ModuleConfig = LoadConfig<Config>() ?? new();

        ItemSearcher ??= new(LuminaCache.Get<Item>()
                                        .Where(x => x.FilterGroup != 16 &&
                                                    x.StackSize   > 1   &&
                                                    !string.IsNullOrEmpty(x.Name.ExtractText()))
                                        .GroupBy(x => x.Name.ExtractText())
                                        .Select(x => x.First()),
                             [x => x.Name.ExtractText(), x => x.RowId.ToString()], x => x.Name.ExtractText());

        TaskHelper         ??= new() { AbortOnTimeout = true };
        SplitOverlay       ??= new(this);
        SplitOverlay.Flags |=  ImGuiWindowFlags.NoBackground;

        CommandManager.AddCommand(Command, new(OnCommand)
        {
            HelpMessage = Lang.Get("AutoSplitStacks-CommandHelp"),
        });

        DService.ContextMenu.OnMenuOpened += OnMenuOpened;
    }

    public override void ConfigUI()
    {
        ImGui.TextColored(LightBlue, $"{GetLoc("Command")}:");

        ImGui.SameLine();
        ImGui.Text($"{Command} → {GetLoc("AutoSplitStacks-CommandHelp")}");

        ImGui.Spacing();

        using var table = ImRaii.Table("SplitItem", 4);
        if (!table) return;
        ImGui.TableSetupColumn("勾选框", ImGuiTableColumnFlags.WidthFixed, CheckboxSize.X);
        ImGui.TableSetupColumn("名称",  ImGuiTableColumnFlags.None,       30);
        ImGui.TableSetupColumn("数量",  ImGuiTableColumnFlags.WidthFixed, ImGui.CalcTextSize("四个汉字").X);
        ImGui.TableSetupColumn("操作",  ImGuiTableColumnFlags.None,       10);

        ImGui.TableNextRow(ImGuiTableRowFlags.Headers);
        ImGui.TableNextColumn();
        if (ImGuiOm.SelectableIconCentered("AddNewGroup", FontAwesomeIcon.Plus))
            ImGui.OpenPopup("AddNewGroupPopup");

        using (var popup = ImRaii.Popup("AddNewGroupPopup"))
        {
            if (popup)
            {
                using (ImRaii.Group())
                {
                    ImGui.AlignTextToFramePadding();
                    ImGui.TextColored(LightSkyBlue, $"{Lang.Get("Item")}:");

                    ImGui.SameLine();
                    ImGui.SetNextItemWidth(250f * GlobalFontScale);
                    using (var combo = ImRaii.Combo("###ItemSelectCombo",
                                                    SelectedItem == null ? "" : SelectedItem.Name.RawString,
                               ImGuiComboFlags.HeightLarge))
                    {
                        if (combo)
                        {
                            ImGui.SetNextItemWidth(-1f);
                            ImGui.InputTextWithHint("###ItemSearchInput", Lang.Get("PleaseSearch"),
                                                    ref ItemSearchInput, 100);
                            if (ImGui.IsItemDeactivatedAfterEdit())
                                ItemSearcher.Search(ItemSearchInput);

                            foreach (var item in ItemSearcher.SearchResult)
                            {
                                var icon = ImageHelper.GetIcon(item.Icon).ImGuiHandle;
                                if (ImGuiOm.SelectableImageWithText(icon, new(ImGui.GetTextLineHeightWithSpacing()),
                                                                    item.Name.ExtractText(), item == SelectedItem))
                                    SelectedItem = item;
                            }
                        }
                    }

                    ImGui.AlignTextToFramePadding();
                    ImGui.TextColored(LightSkyBlue, $"{Lang.Get("Amount")}:");

                    ImGui.SameLine();
                    ImGui.SetNextItemWidth(250f * GlobalFontScale);
                    if (ImGui.InputInt("###SplitAmountInput", ref SplitAmountInput, 0, 0))
                        SplitAmountInput = Math.Clamp(SplitAmountInput, 1, 998);
                }

                var itemSize = ImGui.GetItemRectSize();

                ImGui.SameLine();
                using (ImRaii.Disabled(SelectedItem == null))
                {
                    if (ImGuiOm.ButtonIconWithTextVertical(FontAwesomeIcon.Plus, Lang.Get("Add"),
                                                           buttonSize: new(ImGui.CalcTextSize("三个字").X, itemSize.Y)))
                    {
                        var newGroup = new SplitGroup(SelectedItem.RowId, SplitAmountInput);
                        if (!ModuleConfig.SplitGroups.Contains(newGroup))
                        {
                            ModuleConfig.SplitGroups.Add(newGroup);
                            SaveConfig(ModuleConfig);
                        }
                    }
                }
            }
        }

        ImGui.TableNextColumn();
        ImGuiOm.Text(Lang.Get("Item"));

        ImGui.TableNextColumn();
        ImGuiOm.Text(Lang.Get("AutoSplitStacks-SplitAmount"));

        foreach (var group in ModuleConfig.SplitGroups.ToList())
        {
            ImGui.TableNextRow();

            ImGui.TableNextColumn();
            var isEnabled = group.IsEnabled;
            if (ImGui.Checkbox($"###IsEnabled_{group.ItemID}", ref isEnabled))
            {
                var index = ModuleConfig.SplitGroups.IndexOf(group);
                ModuleConfig.SplitGroups[index].IsEnabled = isEnabled;
                SaveConfig(ModuleConfig);
            }

            ImGui.TableNextColumn();
            var icon = ImageHelper.GetIcon(LuminaCache.GetRow<Item>(group.ItemID).Icon);
            var name = LuminaCache.GetRow<Item>(group.ItemID).Name.RawString;
            ImGuiOm.TextImage(name, icon.ImGuiHandle, ScaledVector2(24f));

            ImGui.TableNextColumn();
            ImGuiOm.Selectable(group.Amount.ToString());

            if (ImGui.BeginPopupContextItem($"{group.ItemID}_AmountEdit"))
            {
                if (ImGui.IsWindowAppearing())
                    SplitAmountInput = group.Amount;

                ImGui.TextColored(LightSkyBlue, $"{Lang.Get("Amount")}:");

                ImGui.SameLine();
                ImGui.SetNextItemWidth(150f * GlobalFontScale);
                ImGui.InputInt($"###{group.ItemID}AmountEdit", ref SplitAmountInput, 0, 0);
                if (ImGui.IsItemDeactivatedAfterEdit())
                {
                    var index = ModuleConfig.SplitGroups.IndexOf(group);
                    ModuleConfig.SplitGroups[index].Amount = SplitAmountInput;
                    SaveConfig(ModuleConfig);
                }

                ImGui.EndPopup();
            }

            ImGui.TableNextColumn();
            if (ImGuiOm.ButtonIcon($"{group.ItemID}_Enqueue", FontAwesomeIcon.Play,
                                   Lang.Get("Execute")))
                EnqueueSplit(group);

            ImGui.SameLine();
            if (ImGuiOm.ButtonIcon($"{group.ItemID}_Delete", FontAwesomeIcon.TrashAlt,
                                   Lang.Get("AutoSplitStacks-HoldCtrlToDelete")))
            {
                if (ImGui.IsKeyDown(ImGuiKey.LeftCtrl))
                {
                    ModuleConfig.SplitGroups.Remove(group);
                    SaveConfig(ModuleConfig);
                }
            }
        }
    }

    public override void OverlayUI()
    {
        if (ImGui.IsWindowAppearing())
            ImGui.OpenPopup($"{Lang.Get("AutoSplitStacks-FastSplit")}###FastSplitPopup");

        var isOpen = true;
        if (ImGui.BeginPopupModal($"{Lang.Get("AutoSplitStacks-FastSplit")}###FastSplitPopup", 
                                  ref isOpen, ImGuiWindowFlags.AlwaysAutoResize))
        {
            ImGui.Text($"{Lang.Get("AutoSplitStacks-PleaseInputSplitAmount")}:");

            ImGui.SetNextItemWidth(150f * GlobalFontScale);
            if (ImGui.InputInt("###FastSplitAmountInput", ref SplitAmountInput, 0, 0))
                SplitAmountInput = Math.Clamp(SplitAmountInput, 1, 998);

            ImGui.SameLine();
            if (ImGui.Button(Lang.Get("Confirm")))
            {
                EnqueueSplit(FastSplitItemID, SplitAmountInput);

                ImGui.CloseCurrentPopup();
                SplitOverlay.IsOpen = false;
            }

            ImGui.SameLine();
            if (ImGui.Button(Lang.Get("Cancel")))
            {
                ImGui.CloseCurrentPopup();
                SplitOverlay.IsOpen = false;
            }

            ImGui.EndPopup();
        }
    }

    public override void OverlayOnClose() => FastSplitItemID = 0;

    private void OnCommand(string command, string args)
    {
        args = args.Trim();
        if (string.IsNullOrWhiteSpace(args)) return;

        if (int.TryParse(args, out var itemID))
        {
            var group = ModuleConfig.SplitGroups.FirstOrDefault(x => x.ItemID == itemID);
            if (group == null) return;

            EnqueueSplit(group);
            return;
        }

        var item = LuminaCache.Get<Item>()
                              .Where(x => x.Name.ExtractText().Contains(args, StringComparison.OrdinalIgnoreCase))
                              .OrderBy(x => x.Name.ExtractText().Length)
                              .FirstOrDefault();
        if (item != null)
        {
            var group = ModuleConfig.SplitGroups.FirstOrDefault(x => x.ItemID == item.RowId);
            if (group == null) return;

            EnqueueSplit(group);
        }
    }

    private static void OnMenuOpened(IMenuOpenedArgs args)
    {
        if (args.Target is not MenuTargetInventory { TargetItem: not null } iTarget) return;
        if (iTarget.TargetItem.Value.Quantity <= 1) return;

        args.AddMenuItem(_FastSplitItemStack.Get());
    }

    private void EnqueueSplit(SplitGroup group)
        => EnqueueSplit(group.ItemID, group.Amount);

    private void EnqueueSplit(uint itemID, int amount)
    {
        TaskHelper.Enqueue(() => ClickItemToSplit(itemID, amount));
        TaskHelper.DelayNext(100, $"SplitRound_{itemID}_{amount}");
        TaskHelper.Enqueue(() => EnqueueSplit(itemID, amount));
    }

    private bool? ClickItemToSplit(uint itemID, int amount)
    {
        if (InputNumeric != null || itemID == 0 || amount == 0)
        {
            TaskHelper.Abort();
            return true;
        }

        var agent = AgentInventoryContext.Instance();
        var manager = InventoryManager.Instance();
        var agentInventory = AgentModule.Instance()->GetAgentByInternalId(AgentId.Inventory);
        var addon = RaptureAtkUnitManager.Instance()->GetAddonById((ushort)agentInventory->AddonId);

        if (agent == null || manager == null || agentInventory == null || addon == null || !addon->IsVisible)
        {
            addon->Open(1);
            return false;
        }

        if (IsInventoryFull(InventoryTypes))
        {
            TaskHelper.Abort();
            NotificationWarning(Lang.Get("AutoSplitStacks-Notification-FullInventory"));
            return true;
        }

        var foundTypes
            = InventoryTypes.Where(type => manager->GetInventoryContainer(type) != null && 
                                                    manager->GetInventoryContainer(type)->Loaded != 0 &&
                                                    manager->GetItemCountInContainer(itemID, type) + 
                                                    manager->GetItemCountInContainer(itemID, type, true) > amount)
                            .ToList();
        if (foundTypes.Count <= 0)
        {
            TaskHelper.Abort();
            NotificationWarning(Lang.Get("AutoSplitStacks-Notification-ItemNoFound"));
            return true;
        }

        foreach (var type in foundTypes)
        {
            var container = manager->GetInventoryContainer(type);
            int? foundSlot = null;
            for (var i = 0; i < container->Size; i++)
            {
                var slot = container->GetInventorySlot(i);
                if (slot->ItemId == itemID)
                {
                    if (slot->GetQuantity() > amount)
                    {
                        foundSlot = i;
                        break;
                    }
                }
            }

            if (foundSlot == null) continue;

            agent->OpenForItemSlot(type, (int)foundSlot, agentInventory->AddonId);
            EnqueueOperations(itemID, type, (int)foundSlot, amount);
            return true;
        }

        TaskHelper.Abort();
        NotificationWarning(Lang.Get("AutoSplitStacks-Notification-ItemNoFound"));
        return true;
    }

    private void EnqueueOperations(uint itemID, InventoryType foundType, int foundSlot, int amount)
    {
        TaskHelper.DelayNext(20, $"ContextMenu_{itemID}_{foundType}_{foundSlot}", false, 2);
        TaskHelper.Enqueue(() =>
        {
            ClickContextMenu(LuminaCache.GetRow<Addon>(92).Text.RawString);
            return true;
        }, null, null, null, 2);

        TaskHelper.DelayNext(20, $"InputNumeric_{itemID}_{foundType}_{foundSlot}", false, 2);
        TaskHelper.Enqueue(() =>
        {
            if (InputNumeric == null || !IsAddonAndNodesReady(InputNumeric)) return false;

            Callback(InputNumeric, true, amount);
            return true;
        }, null, null, null, 2);
    }

    public override void Uninit()
    {
        CommandManager.RemoveCommand(Command);
        DService.ContextMenu.OnMenuOpened -= OnMenuOpened;

        base.Uninit();
    }

    private class Config : ModuleConfiguration
    {
        public List<SplitGroup> SplitGroups = [];
    }

    private class SplitGroup : IEquatable<SplitGroup>
    {
        public bool IsEnabled { get; set; } = true;
        public uint ItemID    { get; set; }
        public int  Amount    { get; set; }

        public SplitGroup() { }

        public SplitGroup(uint itemID, int amount)
        {
            ItemID = itemID;
            Amount = amount;
        }

        public bool Equals(SplitGroup? other)
        {
            if (other is null) return false;
            if (ReferenceEquals(this, other)) return true;

            return ItemID == other.ItemID;
        }

        public override bool Equals(object? obj)
        {
            if (obj is null) return false;
            if (ReferenceEquals(this, obj)) return true;

            return obj.GetType() == GetType() && Equals((SplitGroup)obj);
        }

        public override int GetHashCode() => (int)ItemID;
    }

    private class FastSplitItemStack : MenuItemBase
    {
        public override    string Name { get; protected set; } = Lang.Get("AutoSplitStacks-FastSplit");
        protected override bool   WithDRPrefix { get; set; } = true;

        protected override void   OnClicked(IMenuItemClickedArgs args)
        {
            if (args.Target is not MenuTargetInventory { TargetItem: not null } iTarget) return;
            if (iTarget.TargetItem.Value.Quantity <= 1) return;

            FastSplitItemID     = iTarget.TargetItem.Value.ItemId;
            SplitOverlay.IsOpen = true;
        }
    }
}