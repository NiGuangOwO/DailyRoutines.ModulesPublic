using System.Collections.Generic;
using DailyRoutines.Abstracts;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Interface.Utility.Raii;
using FFXIVClientStructs.FFXIV.Client.Game;
using Lumina.Excel.Sheets;

namespace DailyRoutines.Modules;

public class AutoSortItems : DailyModuleBase
{
    private static readonly string[] sortOptions        = [GetLoc("Descending"), GetLoc("Ascending")];
    private static readonly string[] tabOptions         = [GetLoc("AutoSortItems-Splited"), GetLoc("AutoSortItems-Merged")];
    private static readonly string[] sortOptionsCommand = ["des", "asc"];

    private static readonly HashSet<uint> InvalidContentTypes = [16, 17, 18, 19, 31, 32, 34, 35];

    private static Config ModuleConfig = null!;

    public override ModuleInfo Info => new()
    {
        Author = ["那年雪落"],
        Title = GetLoc("AutoSortItemsTitle"),
        Description = GetLoc("AutoSortItemsDescription"),
        Category = ModuleCategories.General,
    };

    public override void Init()
    {
        ModuleConfig =   LoadConfig<Config>() ?? new();
        TaskHelper   ??= new TaskHelper { TimeLimitMS = 30_000 };
        
        DService.ClientState.TerritoryChanged += OnZoneChanged;
    }
    
    public override void ConfigUI()
    {
        if (ImGui.Checkbox(GetLoc("SendChat"), ref ModuleConfig.SendChat))
            SaveConfig(ModuleConfig);
        
        ImGui.SameLine();
        if (ImGui.Checkbox(GetLoc("SendNotification"), ref ModuleConfig.SendNotification))
            SaveConfig(ModuleConfig);
        
        ImGui.Spacing();
        
        var       tableSize = (ImGui.GetContentRegionAvail() * 0.75f) with { Y = 0 };
        using var table = ImRaii.Table(GetLoc("Sort"), 2, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg, tableSize);
        if (!table) return;

        ImGui.TableSetupColumn("名称",   ImGuiTableColumnFlags.WidthStretch, 30);
        ImGui.TableSetupColumn("方法", ImGuiTableColumnFlags.WidthStretch, 30);
        
        ImGui.TableNextRow(ImGuiTableRowFlags.Headers);
        ImGui.TableNextColumn();
        ImGui.Text(LuminaCache.GetRow<Addon>(12210).Text.ExtractText());

        var typeText = LuminaCache.GetRow<Addon>(9448).Text.ExtractText();
        
        DrawTableRow("兵装库 ID", "ID", ref ModuleConfig.ArmouryChestId, sortOptions);
        DrawTableRow("兵装库等级", GetLoc("Level"), ref ModuleConfig.ArmouryItemLevel, sortOptions);
        DrawTableRow("兵装库类型", typeText, ref ModuleConfig.ArmouryCategory, sortOptions, GetLoc("AutoSortItems-ArmouryCategoryDesc"));
        
        ImGui.TableNextRow(ImGuiTableRowFlags.Headers);
        ImGui.TableNextColumn();
        ImGui.Text(LuminaCache.GetRow<Addon>(12209).Text.ExtractText());
        
        DrawTableRow("背包 HQ", "HQ", ref ModuleConfig.InventoryHq, sortOptions);
        DrawTableRow("背包 ID", "ID", ref ModuleConfig.InventoryId, sortOptions);
        DrawTableRow("背包等级", GetLoc("Level"), ref ModuleConfig.InventoryItemLevel, sortOptions);
        DrawTableRow("背包类型", typeText, ref ModuleConfig.InventoryCategory, sortOptions, GetLoc("AutoSortItems-InventoryCategoryDesc"));
        DrawTableRow("背包分栏", GetLoc("AutoSortItems-Splited"), ref ModuleConfig.InventoryTab, tabOptions, GetLoc("AutoSortItems-InventoryTabDesc"));
    }

    public override void Uninit()
    {
        DService.ClientState.TerritoryChanged -= OnZoneChanged;
        TaskHelper?.Abort();
        base.Uninit();
    }
    
    private void DrawTableRow(string id, string label, ref int value, string[] options, string note = "")
    {
        using var idPush = ImRaii.PushId($"{label}_{id}");
        
        ImGui.TableNextRow();
        
        ImGui.TableNextColumn();
        ImGui.Text(label);

        if (!string.IsNullOrWhiteSpace(note))
            ImGuiOm.HelpMarker(note);

        var oldValue = value;
        ImGui.TableNextColumn();
        ImGui.SetNextItemWidth(-1);
        if (ImGui.Combo($"##{label}", ref value, options, options.Length) && value != oldValue)
            SaveConfig(ModuleConfig);
    }
    
    private void OnZoneChanged(ushort zone)
    {
        if (zone <= 0) return;
        TaskHelper.Abort();
        TaskHelper.Enqueue(CheckCanSort);
    }

    private bool? CheckCanSort()
    {
        if (BetweenAreas || !IsScreenReady() || OccupiedInEvent) return false;
        var isInNormalConditions = DService.Condition[ConditionFlag.NormalConditions] || DService.Condition[ConditionFlag.Mounted];
        if (!isInNormalConditions || !IsInNormalMap())
        {
            TaskHelper.Abort();
            return true;
        }

        TaskHelper.Enqueue(SendSortCommand, "SendSortCommand");
        return true;
    }

    private static unsafe bool IsInNormalMap()
    {
        var currentMapData = LuminaCache.GetRow<Map>(DService.ClientState.MapId);
        if (currentMapData.RowId == 0) return false;
        if (currentMapData.TerritoryType.RowId == 0 ||
            currentMapData.TerritoryType.Value.ContentFinderCondition.RowId != 0) return false;

        var isPVP = GameMain.IsInPvPArea() || GameMain.IsInPvPInstance();
        var contentData =
            LuminaCache.GetRow<ContentFinderCondition>(GameMain.Instance()->CurrentContentFinderConditionId);

        return !isPVP && (contentData.RowId == 0 || !InvalidContentTypes.Contains(contentData.ContentType.RowId));
    }

    private void SendSortCommand()
    {
        SendSortCondition("armourychest", "id", ModuleConfig.ArmouryChestId);
        SendSortCondition("armourychest", "itemlevel", ModuleConfig.ArmouryItemLevel);
        SendSortCondition("armourychest", "category", ModuleConfig.ArmouryCategory);
        ChatHelper.Instance.SendMessage("/itemsort execute armourychest");

        SendSortCondition("inventory", "hq", ModuleConfig.InventoryHq);
        SendSortCondition("inventory", "id", ModuleConfig.InventoryId);
        SendSortCondition("inventory", "itemlevel", ModuleConfig.InventoryItemLevel);
        SendSortCondition("inventory", "category", ModuleConfig.InventoryCategory);

        if (ModuleConfig.InventoryTab == 0)
            ChatHelper.Instance.SendMessage("/itemsort condition inventory tab");

        ChatHelper.Instance.SendMessage("/itemsort execute inventory");

        if (ModuleConfig.SendNotification)
            NotificationInfo(GetLoc("AutoSortItems-SortMessage"));
        if (ModuleConfig.SendChat)
            Chat(GetLoc("AutoSortItems-SortMessage"));

        return;

        void SendSortCondition(string target, string condition, int setting)
            => ChatHelper.Instance.SendMessage($"/itemsort condition {target} {condition} {sortOptionsCommand[setting]}");
    }

    public class Config : ModuleConfiguration
    {
        public int ArmouryChestId;
        public int ArmouryItemLevel;
        public int ArmouryCategory;
        public int InventoryHq;
        public int InventoryId;
        public int InventoryItemLevel;
        public int InventoryCategory;
        public int InventoryTab;
        
        public bool SendChat;
        public bool SendNotification = true;
    }
}
