using System;
using System.Collections.Generic;
using System.Linq;
using DailyRoutines.Abstracts;
using DailyRoutines.Helpers;
using DailyRoutines.Infos;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.Gui.ContextMenu;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Memory;
using FFXIVClientStructs.FFXIV.Client.UI.Info;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel.Sheets;

namespace DailyRoutines.Modules;

public unsafe class AutoPlayerCommend : DailyModuleBase
{
    public override ModuleInfo Info => new()
    {
        Title = GetLoc("AutoPlayerCommendTitle"),
        Description = GetLoc("AutoPlayerCommendDescription"),
        Category = ModuleCategories.Combat,
    };

    private static World? SelectedWorld;

    private static readonly AddToBlacklistItem _AddToBlacklistItem = new();
    
    private static string WorldSearchInput = string.Empty;
    private static string PlayerNameInput = string.Empty;
    private static string ContentSearchInput = string.Empty;

    private static bool IsNeedToCommend;

    private static Config ModuleConfig = null!;

    public override void Init()
    {
        ModuleConfig = LoadConfig<Config>() ?? new();

        TaskHelper ??= new TaskHelper { TimeLimitMS = 10_000 };
        
        DService.ContextMenu.OnMenuOpened += OnMenuOpen;
        DService.DutyState.DutyCompleted  += OnDutyComplete;

        DService.AddonLifecycle.RegisterListener(AddonEvent.PostDraw, "VoteMvp", OnAddonList);
        DService.AddonLifecycle.RegisterListener(AddonEvent.PostDraw, "BannerMIP", OnAddonList);
    }

    private static void OnMenuOpen(IMenuOpenedArgs args)
    {
        if (!DService.Condition[ConditionFlag.BoundByDuty]) return;
        if (args.MenuType != ContextMenuType.Default || args.Target is not MenuTargetDefault target ||
            target.TargetCharacter          == null) return;
        
        args.AddMenuItem(_AddToBlacklistItem.Get());
    }

    public override void ConfigUI()
    {
        ImGui.AlignTextToFramePadding();
        ImGui.Text($"{GetLoc("AutoPlayerCommend-BlacklistPlayers")}:");

        ImGui.SameLine();
        ImGui.SetNextItemWidth(300f * GlobalFontScale);
        using (var combo = ImRaii.Combo("###BlacklistPlayerInfoCombo",
                                        GetLoc("AutoPlayerCommend-BlacklistPlayersAmount", ModuleConfig.BlacklistPlayers.Count),
                                        ImGuiComboFlags.HeightLarge))
        {
            if (combo)
            {
                using (ImRaii.Group())
                {
                    ImGui.AlignTextToFramePadding();
                    ImGui.Text($"{GetLoc("AutoPlayerCommend-World")}:");

                    ImGui.SameLine();
                    ImGui.SetNextItemWidth(120f * GlobalFontScale);
                    CNWorldSelectCombo(ref SelectedWorld, ref WorldSearchInput);

                    ImGui.AlignTextToFramePadding();
                    ImGui.Text($"{GetLoc("AutoPlayerCommend-PlayerName")}:");

                    ImGui.SameLine();
                    ImGui.SetNextItemWidth(120f * GlobalFontScale);
                    ImGui.InputText("###PlayerNameInput", ref PlayerNameInput, 100);
                }

                ImGui.SameLine();
                if (ImGuiOm.ButtonIconWithTextVertical(FontAwesomeIcon.Plus, GetLoc("Add")))
                {
                    if (SelectedWorld == null || string.IsNullOrWhiteSpace(PlayerNameInput)) return;
                    var info = new PlayerInfo(PlayerNameInput, SelectedWorld?.RowId ?? 0);
                    if (ModuleConfig.BlacklistPlayers.Add(info))
                        SaveConfig(ModuleConfig);
                }

                ImGui.SameLine();
                if (ImGuiOm.ButtonIconWithTextVertical(FontAwesomeIcon.Sync,
                                                       GetLoc("AutoPlayerCommend-SyncBlacklist")))
                {
                    var blacklist =
                        GetBlacklistInfo(
                            (InfoProxyBlacklist*)InfoModule.Instance()->GetInfoProxyById(InfoProxyId.Blacklist));

                    foreach (var player in blacklist)
                        ModuleConfig.BlacklistPlayers.Add(player);

                    SaveConfig(ModuleConfig);
                }

                ImGuiOm.TooltipHover(GetLoc("AutoPlayerCommend-SyncBlacklistHelp"));

                ImGui.Separator();
                ImGui.Separator();

                foreach (var player in ModuleConfig.BlacklistPlayers)
                {
                    if (!PresetData.Worlds.TryGetValue(player.WorldID, out var world)) continue;
                    ImGui.Selectable($"{world.Name.ExtractText()} / {player.PlayerName}");

                    using var contextMenu =
                        ImRaii.ContextPopupItem($"DeleteBlacklistPlayer_{player.PlayerName}_{player.WorldID}");
                    if (contextMenu)
                    {
                        if (ImGui.Selectable(GetLoc("Delete")))
                        {
                            ModuleConfig.BlacklistPlayers.Remove(player);
                            SaveConfig(ModuleConfig);
                        }
                    }
                }
            }
        }

        ImGui.AlignTextToFramePadding();
        ImGui.Text($"{GetLoc("AutoPlayerCommend-BlacklistContents")}:");

        ImGui.SameLine();
        ImGui.SetNextItemWidth(300f * GlobalFontScale);
        ContentSelectCombo(ref ModuleConfig.BlacklistContentZones, ref ContentSearchInput);
    }

    private void OnDutyComplete(object? sender, ushort dutyZoneID)
    {
        IsNeedToCommend = false;
        if (InterruptByConflictKey(TaskHelper, this)) return;
        if (ModuleConfig.BlacklistContentZones.Contains(dutyZoneID)) return;

        TaskHelper.Enqueue(OpenCommendWindow);
    }

    private static bool? OpenCommendWindow()
    {
        var notification    = GetAddonByName("_Notification");
        var notificationMvp = GetAddonByName("_NotificationIcMvp");
        if (notification == null && notificationMvp == null) return true;

        IsNeedToCommend = true;
        Callback(notification, true, 0, 11);
        return true;
    }

    private void ProcessCommendation(string addonName, int voteOffset, int nameOffset, int callbackIndex)
    {
        TaskHelper.Abort();

        var localPlayer = DService.ClientState.LocalPlayer;
        var localPlayerInfo = new PlayerInfo(localPlayer.Name.ExtractText(), localPlayer.HomeWorld.Value.RowId)
        {
            JobID = localPlayer.ClassJob.Value.RowId,
            Role = GetCharacterJobRole(localPlayer.ClassJob.Value.Role),
        };

        var allies = DService.PartyList.Select(x => new PlayerInfo(x.Name.ExtractText(), x.World.Value.RowId)
                            {
                                Role  = GetCharacterJobRole(x.ClassJob.Value.Role),
                                JobID = x.ClassJob.Value.RowId,
                            })
                            .Where(x => x != localPlayerInfo && !ModuleConfig.BlacklistPlayers.Contains(x)).ToList();

        if (allies.Count == 0) return;
        var playersToCommend = allies
                               .OrderByDescending(player => localPlayer.ClassJob.RowId == player.JobID || 
                                                            player.Role == localPlayerInfo.Role)
                               .ThenByDescending(player => localPlayerInfo.Role switch
                               {
                                   PlayerRole.Tank or PlayerRole.Healer 
                                       => player.Role is PlayerRole.Tank or PlayerRole.Healer ? 1 : 0,
                                   PlayerRole.DPS => player.Role switch
                                   {
                                       PlayerRole.DPS    => 3,
                                       PlayerRole.Healer => 2,
                                       PlayerRole.Tank   => 1,
                                       _                 => 0,
                                   },
                                   _ => 0,
                               });

        if (TryGetAddonByName<AtkUnitBase>(addonName, out var addon) && IsAddonAndNodesReady(addon))
        {
            foreach (var player in playersToCommend)
                for (var i = 0; i < allies.Count; i++)
                    if (addon->AtkValues[i + voteOffset].Bool)
                    {
                        var playerNameInAddon =
                            MemoryHelper.ReadStringNullTerminated((nint)addon->AtkValues[i + nameOffset].String);

                        if (playerNameInAddon == player.PlayerName)
                        {
                            Callback(addon, true, callbackIndex, i);

                            var job = LuminaCache.GetRow<ClassJob>(player.JobID);
                            var message = GetSLoc("AutoPlayerCommend-NoticeMessage",
                                                  ((ClassJob?)job).ToBitmapFontIcon(), job.Name.ExtractText(),
                                                  player.PlayerName);
                            Chat(message);
                            return;
                        }
                    }
        }
    }

    private void OnAddonList(AddonEvent type, AddonArgs args)
    {
        if (!IsNeedToCommend) return;

        switch (args.AddonName)
        {
            case "VoteMvp":
                ProcessCommendation("VoteMvp", 16, 9, 0);
                break;
            case "BannerMIP":
                ProcessCommendation("BannerMIP", 29, 22, 12);
                break;
        }

        IsNeedToCommend = false;
    }

    private static PlayerRole GetCharacterJobRole(byte rawRole) =>
        rawRole switch
        {
            1 => PlayerRole.Tank,
            2 or 3 => PlayerRole.DPS,
            4 => PlayerRole.Healer,
            _ => PlayerRole.None,
        };

    private static List<PlayerInfo> GetBlacklistInfo(InfoProxyBlacklist* blacklist)
    {
        var list = new List<PlayerInfo>();
        var stringArray = (nint*)AtkStage.Instance()->GetStringArrayData()[14]->StringArray;
        
        for (var num = 0u; num < blacklist->InfoProxyPageInterface.InfoProxyInterface.EntryCount; num++)
        {
            var playerName = MemoryHelper.ReadStringNullTerminated(stringArray[num]);
            var worldName = MemoryHelper.ReadStringNullTerminated(stringArray[200 + num]);
            var world = PresetData.Worlds.Values.FirstOrDefault(
                x => x.Name.ExtractText().Contains(worldName, StringComparison.OrdinalIgnoreCase));
            if (world.RowId == 0 || string.IsNullOrWhiteSpace(playerName)) continue;

            var player = new PlayerInfo(playerName, world.RowId);
            list.Add(player);
        }

        return list;
    }

    public override void Uninit()
    {
        DService.AddonLifecycle.UnregisterListener(OnAddonList);
        
        DService.ContextMenu.OnMenuOpened -= OnMenuOpen;
        DService.DutyState.DutyCompleted  -= OnDutyComplete;

        SaveConfig(ModuleConfig);
        base.Uninit();
    }

    private enum PlayerRole
    {
        Tank,
        Healer,
        DPS,
        None,
    }

    private class PlayerInfo : IEquatable<PlayerInfo>
    {
        public PlayerInfo() { }

        public PlayerInfo(string name, uint world)
        {
            PlayerName = name;
            WorldID = world;
        }

        public string      PlayerName { get; set; } = string.Empty;
        public uint        WorldID    { get; set; }
        public PlayerRole? Role       { get; set; } = PlayerRole.None;
        public uint        JobID      { get; set; }

        public bool Equals(PlayerInfo? other)
        {
            if (other is null || GetType() != other.GetType())
                return false;

            return PlayerName == other.PlayerName && WorldID == other.WorldID;
        }

        public override bool Equals(object? obj) { return Equals(obj as PlayerInfo); }

        public override int GetHashCode() { return HashCode.Combine(PlayerName, WorldID); }

        public static bool operator ==(PlayerInfo? lhs, PlayerInfo? rhs)
        {
            if (lhs is null) return rhs is null;
            return lhs.Equals(rhs);
        }

        public static bool operator !=(PlayerInfo lhs, PlayerInfo rhs) { return !(lhs == rhs); }
    }

    private class Config : ModuleConfiguration
    {
        public HashSet<uint> BlacklistContentZones = [];
        public HashSet<PlayerInfo> BlacklistPlayers = [];
    }
    
    private class AddToBlacklistItem : MenuItemBase
    {
        public override string Name { get; protected set; } = GetLoc("AutoPlayerCommend-AddToBlacklistItem");
        
        protected override void OnClicked(IMenuItemClickedArgs args)
        {
            if (args.Target is not MenuTargetDefault target) return;
            if (target.TargetCharacter          == null && string.IsNullOrWhiteSpace(target.TargetName) &&
                target.TargetHomeWorld.ValueNullable == null) return;
            
            var playerName  = target.TargetCharacter != null ? target.TargetCharacter.Name : target.TargetName;
            var playerWorld = target.TargetCharacter?.HomeWorld ?? target.TargetHomeWorld;
            
            var info   = new PlayerInfo(playerName, playerWorld.RowId);
            NotificationInfo(
                ModuleConfig.BlacklistPlayers.Add(info)
                    ? GetLoc("AutoPlayerCommend-AddToBlacklistSuccess", 
                                           playerName, playerWorld.Value.Name.ExtractText())
                    : GetLoc("AutoPlayerCommend-AddToBlacklistFail", 
                                           playerName, playerWorld.Value.Name.ExtractText()));
        }
    }
}
