using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text.RegularExpressions;
using DailyRoutines.Abstracts;
using DailyRoutines.Helpers;
using DailyRoutines.Managers;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Utility;
using FFXIVClientStructs.FFXIV.Client.Game.MJI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using ImGuiNET;
using Lumina.Excel.Sheets;

namespace DailyRoutines.Modules;

public unsafe class AutoMJIWorkshopImport : DailyModuleBase
{
    public override ModuleInfo Info => new()
    {
        Title = GetLoc("AutoMJIWorkshopImportTitle"),
        Description = GetLoc("AutoMJIWorkshopImportDescription"),
        Category = ModuleCategories.UIOperation,
    };

    public override ModulePermission Permission => new() { CNOnly = true };

    private static Assignments Recommendations = new();
    private static readonly Dictionary<uint, MJICraftworksObject> OriginalCraftItemsSheet;
    private static readonly Dictionary<string, MJICraftworksObject> ItemNameMap;

    private static Config ModuleConfig = null!;

    static AutoMJIWorkshopImport()
    {
        OriginalCraftItemsSheet = LuminaCache.Get<MJICraftworksObject>()
            .Where(x => x.Item.RowId != 0 && x.Item.ValueNullable != null)
            .ToDictionary(x => x.RowId, x => x);
        ItemNameMap = OriginalCraftItemsSheet.Values
            .ToDictionary(
                r => RemoveMJIItemPrefix(r.Item.Value.Name.ExtractText() ?? ""),
                r => r,
                StringComparer.OrdinalIgnoreCase
            );
    }

    public override void Init()
    {
        ModuleConfig = LoadConfig<Config>() ?? new();

        Overlay            ??= new(this);
        Overlay.Flags      &=  ~ImGuiWindowFlags.NoTitleBar;
        Overlay.Flags      &=  ~ImGuiWindowFlags.NoResize;
        Overlay.Flags      &=  ~ImGuiWindowFlags.AlwaysAutoResize;
        Overlay.WindowName =   Lang.Get("AutoMJIWorkshopImportTitle");
        Overlay.SizeConstraints = new() { MinimumSize = new(400f * GlobalFontScale, 300f * GlobalFontScale) };
        
        DService.AddonLifecycle.RegisterListener(AddonEvent.PostSetup, "MJICraftSchedule", OnAddon);
        DService.AddonLifecycle.RegisterListener(AddonEvent.PreFinalize, "MJICraftSchedule", OnAddon);
        if (MJICraftSchedule != null) 
            OnAddon(AddonEvent.PostSetup, null);
    }

    public override void OverlayUI()
    {
        DrawImportSection();
        if (Recommendations.Empty) return;
        DrawBulkApplySection();
        DrawIndividualApplySection();
    }

    private void DrawImportSection()
    {
        ImGui.TextColored(ImGuiColors.DalamudYellow, Lang.Get("AutoMJIWorkshopImport-ImportData"));
        
        ImGui.Separator();
        ImGui.Spacing();

        if (ImGui.Button(Lang.Get("ImportFromClipboard")))
            Recommendations = Assignments.Parse(ImGui.GetClipboardText().Trim());

        ImGui.SameLine();
        ImGui.TextDisabled("|");

        ImGui.SameLine();
        if (ImGui.Button(Lang.Get("AutoMJIWorkshopImport-CNScheduleSet")))
            Util.OpenLink("https://docs.qq.com/doc/DTUNRZkJjTVhvT2Nv");

        ImGui.SameLine();
        if (ImGui.Button(Lang.Get("AutoMJIWorkshopImport-CNScheduleSetNekoMimi")))
            Util.OpenLink("https://docs.qq.com/sheet/DVmxFek1pUUtmYVhl");

        ImGui.SameLine();
        ImGui.TextDisabled("|");
        
        ImGui.SameLine();
        if (ImGui.Button(Lang.Get("AutoMJIWorkshopImport-ClearImportedData"))) 
            Recommendations = new();
        
        ImGui.SameLine();
        ImGui.TextDisabled("|");

        ImGui.SameLine();
        ImGui.SetNextItemWidth(150f * GlobalFontScale);
        if (ImGui.SliderInt(Lang.Get("AutoMJIWorkshopImport-WorkshopAmount"),
                            ref ModuleConfig.WorkshopAmount, 0, 4))
            SaveConfig(ModuleConfig);
    }

    private static void DrawBulkApplySection()
    {
        ImGui.Dummy(new Vector2(12, 12));
        ImGui.TextColored(ImGuiColors.DalamudYellow, Lang.Get("AutoMJIWorkshopImport-BulkApply"));
        ImGui.Separator();

        if (ImGui.Button(Lang.Get("AutoMJIWorkshopImport-ThisWeek"))) 
            ApplyRecommendations(false);
        
        ImGui.SameLine();
        if (ImGui.Button(Lang.Get("AutoMJIWorkshopImport-NextWeek"))) 
            ApplyRecommendations(true);
        
        ImGui.SameLine();
        ImGui.Checkbox(Lang.Get("AutoMJIWorkshopImport-IgnoreFourthWorkshop"),
                       ref ModuleConfig.IgnoreFourthWorkshop);
    }

    private static void DrawIndividualApplySection()
    {
        ImGui.Dummy(new Vector2(12, 12));
        ImGui.TextColored(ImGuiColors.DalamudYellow, Lang.Get("AutoMJIWorkshopImport-IndividualApply"));
        ImGui.Separator();

        using var scrollSection = ImRaii.Child("ScrollableSection");
        foreach (var (cycle, rec) in Recommendations.Enumerate())
        {
            ImGui.AlignTextToFramePadding();
            ImGui.Text($"{Lang.Get("AutoMJIWorkshopImport-DayCycleDisplay", cycle)}:");
            
            ImGui.SameLine();
            if (ImGui.SmallButton($"{Lang.Get("Apply")}##{cycle}"))
                ApplyRecommendationToCurrentCycle(rec);

            DrawWorkshopTable(cycle, rec);
        }
    }

    private static void DrawWorkshopTable(int cycle, DayAssignment rec)
    {
        const ImGuiTableFlags tableFlags = ImGuiTableFlags.RowBg | ImGuiTableFlags.NoKeepColumnsVisible;

        using var outerTable = ImRaii.Table($"table_{cycle}", rec.Workshops.Count, tableFlags);
        if (!outerTable) return;

        SetupTableColumns(rec.Workshops.Count);
        ImGui.TableHeadersRow();

        ImGui.TableNextRow();
        var workshopLimit = CalculateWorkshopLimit(rec.Workshops.Count);
        for (var i = 0; i < workshopLimit; ++i)
        {
            ImGui.TableNextColumn();
            DrawWorkshopContent(rec.Workshops[i]);
        }
    }

    private static void SetupTableColumns(int workshopCount)
    {
        for (var i = 0; i < workshopCount; ++i)
            ImGui.TableSetupColumn($"{Lang.Get("AutoMJIWorkshopImport-Workshop")} {i + 1}");
    }

    private static int CalculateWorkshopLimit(int workshopCount) 
        => workshopCount - (ModuleConfig.IgnoreFourthWorkshop && workshopCount > 1 ? 1 : 0);

    private static void DrawWorkshopContent(WorkshopAssignment workshop)
    {
        if (workshop.IsRest)
        {
            ImGui.TextColored(ImGuiColors.TankBlue, Lang.Get("AutoMJIWorkshopImport-RestCycle"));
            return;
        }

        using var innerTable = ImRaii.Table("inner_table", 2, ImGuiTableFlags.RowBg | ImGuiTableFlags.NoKeepColumnsVisible);
        if (!innerTable) return;

        ImGui.TableSetupColumn("", ImGuiTableColumnFlags.WidthFixed);
        foreach (var slot in workshop.Slots)
        {
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            DrawItemIcon(slot.CraftObjectId);
            ImGui.TableNextColumn();
            DrawItemName(slot.CraftObjectId);
        }
    }

    private static void DrawItemIcon(uint craftObjectId)
    {
        if (!OriginalCraftItemsSheet.TryGetValue(craftObjectId, out var row) || row.Item.ValueNullable == null) return;
        var iconSize = ImGui.GetTextLineHeight() * 1.5f;
        var iconSizeVec = new Vector2(iconSize, iconSize);
        var craftworkItemIcon = row.Item.Value.Icon;
        ImGui.Image(DService.Texture.GetFromGameIcon(new(craftworkItemIcon)).GetWrapOrEmpty().ImGuiHandle,
            iconSizeVec, Vector2.Zero, Vector2.One);
    }

    private static void DrawItemName(uint craftObjectId)
    {
        if (!OriginalCraftItemsSheet.TryGetValue(craftObjectId, out var row) || row.Item.ValueNullable == null) return;
        ImGui.TextUnformatted(row.Item.Value.Name.ExtractText());
    }

    private static string RemoveMJIItemPrefix(string name) =>
        name switch
        {
            var n when n.StartsWith("海岛") => n[2..],
            var n when n.StartsWith("开拓工房") => n[4..],
            var n when n.StartsWith("Isleworks ") => n[10..],
            var n when n.StartsWith("Isleberry ") => n[10..],
            var n when n.StartsWith("Islefish ") => n[9..],
            var n when n.StartsWith("Island ") => n[7..],
            _ => name
        };

    private static void ApplyRecommendations(bool nextWeek)
    {
        try
        {
            var agentData = AgentMJICraftSchedule.Instance()->Data;
            if (Recommendations.Schedules.Count > 7)
                throw new Exception(Lang.Get("AutoMJIWorkshopImport-Exception-AboveSevenDays",
                                                         Recommendations.Schedules.Count));

            var forbiddenCycles = nextWeek ? 0 : (1u << (agentData->CycleInProgress + 1)) - 1;
            var currentRestCycles = nextWeek ? agentData->RestCycles >> 7 : agentData->RestCycles & 0x7F;
            
            HandleRestCycles(currentRestCycles, forbiddenCycles, nextWeek);

            foreach (var (c, r) in Recommendations.Enumerate())
                ApplyRecommendation(c - 1 + (nextWeek ? 7 : 0), r);

            ResetCurrentCycleToRefreshUI();
        }
        catch (Exception ex)
        {
            NotificationError($"{Lang.Get("Error")}: {ex.Message}");
        }
    }

    private static void HandleRestCycles(uint currentRestCycles, uint forbiddenCycles, bool nextWeek)
    {
        if ((currentRestCycles & Recommendations.CyclesMask) == 0) return;

        var freeCycles = ~Recommendations.CyclesMask & 0x7F;
        var rest = (1u << (31 - BitOperations.LeadingZeroCount(freeCycles))) | 1;

        if (BitOperations.PopCount(rest) != 2)
            throw new Exception(Lang.Get("AutoMJIWorkshopImport-Exception-FailToObtainRestDays"));

        var changedRest = rest ^ currentRestCycles;
        if ((changedRest & forbiddenCycles) != 0)
            throw new Exception(Lang.Get("AutoMJIWorkshopImport-Exception-RestDaysFinished"));

        var newRest = nextWeek
            ? (rest << 7) | (AgentMJICraftSchedule.Instance()->Data->RestCycles & 0x7F)
            : (AgentMJICraftSchedule.Instance()->Data->RestCycles & 0x3F80) | rest;
        SetRestCycles(newRest);
    }

    private static void ApplyRecommendation(int cycle, DayAssignment assignment)
    {
        var maxWorkshops = ModuleConfig.WorkshopAmount;
        for (var workshop = 0; workshop < maxWorkshops; workshop++)
        {
            if (ModuleConfig.IgnoreFourthWorkshop && workshop == maxWorkshops - 1) continue;

            var workshopRec = workshop < assignment.Workshops.Count 
                ? assignment.Workshops[workshop] 
                : assignment.Workshops[0];

            foreach (var slotRec in workshopRec.Slots)
                ScheduleItemToWorkshop(slotRec.CraftObjectId, slotRec.Slot, cycle, workshop);
        }
    }

    private static void ApplyRecommendationToCurrentCycle(DayAssignment rec)
    {
        var cycle = AgentMJICraftSchedule.Instance()->Data->CycleDisplayed;
        ApplyRecommendation(cycle, rec);
        ResetCurrentCycleToRefreshUI();
    }

    public static void ScheduleItemToWorkshop(uint objId, int startingHour, int cycle, int workshop)
        => MJIManager.Instance()->ScheduleCraft((ushort)objId, (byte)((startingHour + 17) % 24), (byte)cycle,
                                                (byte)workshop);

    public static void SetRestCycles(uint mask)
    {
        var agent = AgentMJICraftSchedule.Instance();
        agent->Data->NewRestCycles = mask;
        SendEvent(&agent->AgentInterface, 5, 0);
    }

    public static void ResetCurrentCycleToRefreshUI()
    {
        var agent = AgentMJICraftSchedule.Instance();
        agent->SetDisplayedCycle(agent->Data->CycleDisplayed);
        agent->Data->Flags1 |= AgentMJICraftSchedule.DataFlags1.MaterialsUpdated;
    }
    
    private void OnAddon(AddonEvent type, AddonArgs? args)
    {
        Overlay.IsOpen = type switch
        {
            AddonEvent.PostSetup   => true,
            AddonEvent.PreFinalize => false,
            _                      => Overlay.IsOpen
        };
    }

    public override void Uninit()
    {
        base.Uninit();

        SaveConfig(ModuleConfig);
        Recommendations = new();
    }

    private class Config : ModuleConfiguration
    {
        public bool IgnoreFourthWorkshop;
        public int  WorkshopAmount = 4;
    }

    public class Assignments
    {
        private readonly List<DayAssignment> _schedules = [];
        public uint CyclesMask { get; private set; }
        public IReadOnlyList<DayAssignment> Schedules => _schedules;

        public bool Empty => Schedules.Count == 0;

        public void Add(int cycle, DayAssignment schedule)
        {
            if (schedule.Empty) return;

            if (cycle is < 1 or > 7)
                throw new ArgumentOutOfRangeException($"无效的天数指定: {cycle}");

            var mask = 1u << (cycle - 1);
            if ((CyclesMask & mask) != 0)
            {
                // 如果已经有安排，则更新现有的安排
                var existingSchedule = _schedules.FirstOrDefault(s => s.Cycle == cycle);
                if (existingSchedule != null)
                {
                    existingSchedule.MergeWith(schedule);
                    return;
                }
            }

            if ((CyclesMask & ~(mask - 1)) != 0)
                throw new InvalidOperationException($"无效的天内安排: {cycle}");

            schedule.Cycle = cycle;
            _schedules.Add(schedule);
            CyclesMask |= mask;
        }

        public IEnumerable<(int cycle, DayAssignment rec)> Enumerate() => _schedules.Select(rec => (rec.Cycle, rec));

        public void Clear()
        {
            _schedules.Clear();
            CyclesMask = 0;
        }

        public static Assignments Parse(string input)
        {
            var result = new Assignments();
            var lines = input.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            var currentCycle = 0;
            var currentDayRec = new DayAssignment();

            foreach (var line in lines)
                try
                {
                    var (cycle, prefix, tasks) = ParseLine(line);

                    if (cycle != currentCycle)
                    {
                        if (currentCycle > 0) result.Add(currentCycle, currentDayRec);
                        currentCycle = cycle;
                        currentDayRec = new DayAssignment();
                    }

                    if (tasks == "休息")
                        currentDayRec.SetRest();
                    else
                        currentDayRec.AddWorkshops(prefix, tasks);
                }
                catch (Exception ex)
                {
                    NotificationError(ex.Message, "解析时发生错误");
                    Error("解析时发生错误:", ex);
                }

            if (currentCycle > 0) result.Add(currentCycle, currentDayRec);

            return result;
        }

        private static (int cycle, int prefix, string tasks) ParseLine(string line)
        {
            var restMatch = Regex.Match(line, @"D(\d+)[:：]\s*(休息)");
            if (restMatch.Success) return (int.Parse(restMatch.Groups[1].Value), 4, "休息");

            var normalMatch = Regex.Match(line, @"D(\d+)[:：]\s*(\d+)×(.+)");
            if (normalMatch.Success)
                return (int.Parse(normalMatch.Groups[1].Value), int.Parse(normalMatch.Groups[2].Value),
                           normalMatch.Groups[3].Value.Trim());

            throw new FormatException($"无效的行格式: {line}");
        }
    }

    public class DayAssignment
    {
        private readonly List<WorkshopAssignment> _workshops = [];
        public IReadOnlyList<WorkshopAssignment> Workshops => _workshops;
        public bool Empty => _workshops.Count == 0;
        public int Cycle { get; set; }
        public bool IsRest { get; private set; }

        public void SetRest()
        {
            IsRest = true;
            _workshops.Clear();
            for (var i = 0; i < 4; i++)
                _workshops.Add(WorkshopAssignment.CreateRest());
        }

        public void AddWorkshops(int prefix, string tasks)
        {
            if (IsRest) throw new InvalidOperationException("无法将工房安排添加至休息日");

            var workshop = WorkshopAssignment.Create(tasks);

            if (_workshops.Count == 0)
            {
                // 第一行
                for (var i = 0; i < prefix; i++)
                    _workshops.Add(workshop);
            }
            else
            {
                // 第二行
                var remainingWorkshops = 4 - _workshops.Count;
                for (var i = 0; i < remainingWorkshops; i++)
                    _workshops.Add(workshop);
            }
        }

        public void MergeWith(DayAssignment other)
        {
            // 合并两天安排
            _workshops.AddRange(other.Workshops);

            // 确保工房总数不超过 4
            while (_workshops.Count > 4) _workshops.RemoveAt(_workshops.Count - 1);
        }

        public IEnumerable<(int workshop, WorkshopAssignment rec)> Enumerate(int maxWorkshops)
        {
            if (Empty) yield break;

            for (var i = 0; i < maxWorkshops; i++)
                if (i < _workshops.Count)
                    yield return (i, _workshops[i]);
                else
                {
                    // 工房数量不足 -> 使用最后一个工房的安排
                    yield return (i, _workshops[^1]);
                }
        }
    }

    public class WorkshopAssignment
    {
        public List<SlotRec> Slots { get; } = [];
        public bool IsRest { get; private set; }

        public void Add(int slot, uint craftObjectId)
        {
            Slots.Add(new SlotRec(slot, craftObjectId));
        }

        public static WorkshopAssignment Create(string tasks)
        {
            var workshop = new WorkshopAssignment();

            if (tasks == "休息")
            {
                workshop.IsRest = true;
                return workshop;
            }

            var items = Regex.Split(tasks, @",\s*|、");
            var slot = 0;

            foreach (var item in items)
            {
                var craftObject = TryParseItem(item);
                if (craftObject != null)
                {
                    workshop.Add(slot, craftObject.Value.RowId);
                    slot += craftObject.Value.CraftingTime;
                }
                else
                    NotificationWarning($"无法找到物品数据: {item}");
            }

            return workshop;
        }

        public static WorkshopAssignment CreateRest() => new() { IsRest = true };

        private static MJICraftworksObject? TryParseItem(string itemName)
        {
            var matchingItems = ItemNameMap.Where(kvp => kvp.Key.Contains(itemName, StringComparison.OrdinalIgnoreCase))
                                           .OrderBy(kvp => kvp.Key.Length)
                                           .ToList();

            return matchingItems.FirstOrDefault().Value;
        }
    }

    public readonly record struct SlotRec(int Slot, uint CraftObjectId);
}
