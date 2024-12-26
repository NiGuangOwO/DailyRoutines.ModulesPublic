using System.Collections.Generic;
using System.Linq;
using DailyRoutines.Abstracts;
using DailyRoutines.Infos;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using Lumina.Excel.Sheets;

namespace DailyRoutines.Modules;

public unsafe class AutoCheckItemLevel : DailyModuleBase
{
    public override ModuleInfo Info => new()
    {
        Title = GetLoc("AutoCheckItemLevelTitle"),
        Description = GetLoc("AutoCheckItemLevelDescription"),
        Category = ModuleCategories.Combat,
    };

    private static readonly HashSet<uint> ValidContentJobCategories = [108, 142, 146];
    private static readonly HashSet<uint> HaveOffHandJobCategories = [2, 7, 8, 20];

    private static HudPartyMember? CurrentMember;

    public override void Init()
    {
        TaskHelper ??= new TaskHelper { TimeLimitMS = 20_000 };

        DService.ClientState.TerritoryChanged += OnZoneChanged;
    }

    private void OnZoneChanged(ushort zone)
    {
        CurrentMember = null;

        if (DService.ClientState.IsPvP) return;
        if (!PresetData.TryGetContent(zone, out var content) || content.PvP ||
            !ValidContentJobCategories.Contains(content.AcceptClassJobCategory.RowId)) return;

        var message = new SeStringBuilder().Append($"{GetLoc("AutoCheckItemLevel-ILRequired")}: ")
                                           .AddUiForeground(content.ItemLevelRequired.ToString(), 34).Build();
        Chat(message);

        TaskHelper.Enqueue(() => !BetweenAreas, "WaitForEnteringDuty", null, null, 2);
        TaskHelper.Enqueue(() => CheckMembersItemLevel([DService.ClientState.LocalPlayer.GameObjectId]));
    }

    private bool? CheckMembersItemLevel(HashSet<ulong> checkedMembers)
    {
        if (IsAddonAndNodesReady(CharacterInspect))
            CharacterInspect->Close(true);

        if (DService.PartyList.Length <= 1 || DService.PartyList.All(x => checkedMembers.Contains(x.GameObject?.GameObjectId ?? 0)))
        {
            TaskHelper.Abort();
            return true;
        }

        foreach (var partyMember in DService.PartyList)
        {
            if (partyMember.GameObject == null) continue;
            if (!checkedMembers.Add(partyMember.GameObject.GameObjectId)) continue;

            TaskHelper.Enqueue(() =>
            {
                if (!Throttler.Throttle("AutoCheckItemLevel-WaitExamineUI", 1000)) return false;
                AgentInspect.Instance()->ExamineCharacter(partyMember.GameObject.EntityId);
                return CharacterInspect != null;
            });

            TaskHelper.DelayNext(1_000);
            TaskHelper.Enqueue(() =>
            {
                if (!TryGetInventoryItems([InventoryType.Examine], _ => true, out var list)) return false;

                uint totalIL = 0U, lowestIL = uint.MaxValue;
                var itemSlotAmount = 11;
                for (var i = 0; i < 13; i++)
                {
                    var slot = list[i];
                    var itemID = slot.ItemId;
                    var itemData = LuminaCache.GetRow<Item>(itemID);

                    switch (i)
                    {
                        case 0:
                        {
                            var category = itemData.ClassJobCategory.RowId;
                            if (HaveOffHandJobCategories.Contains(category))
                                itemSlotAmount++;

                            break;
                        }
                        case 1 when itemSlotAmount != 12:
                        case 5: // 腰带
                            continue;
                    }

                    if (itemData.LevelItem.RowId < lowestIL)
                        lowestIL = itemData.LevelItem.RowId;

                    totalIL += itemData.LevelItem.RowId;
                }

                var avgItemLevel = totalIL / itemSlotAmount;

                var content = PresetData.Contents[DService.ClientState.TerritoryType];
                var ssb = new SeStringBuilder();
                ssb.AddUiForeground(25);
                ssb.Add(new PlayerPayload(partyMember.Name.TextValue, partyMember.GameObject.ToBCStruct()->HomeWorld));
                ssb.AddUiForegroundOff();
                ssb.Append($" ({partyMember.ClassJob.ValueNullable?.Name.ExtractText()})");

                ssb.Append($" {GetLoc("Level")}: ").AddUiForeground(
                    partyMember.Level.ToString(), (ushort)(partyMember.Level >= content.ClassJobLevelSync ? 43 : 17));

                ssb.Add(new NewLinePayload());
                ssb.Append($" {GetLoc("AutoCheckItemLevel-ILAverage")}: ")
                   .AddUiForeground(avgItemLevel.ToString(), (ushort)(avgItemLevel > content.ItemLevelSync ? 43 : 17));

                ssb.Append($" {GetLoc("AutoCheckItemLevel-ILMinimum")}: ")
                   .AddUiForeground(lowestIL.ToString(), (ushort)(lowestIL > content.ItemLevelRequired ? 43 : 17));

                ssb.Add(new NewLinePayload());

                Chat(ssb.Build());
                CharacterInspect->Close(true);
                return true;
            });

            TaskHelper.Enqueue(() => CheckMembersItemLevel(checkedMembers));
            return true;
        }

        TaskHelper.Abort();
        return true;
    }

    public override void Uninit()
    {
        DService.ClientState.TerritoryChanged -= OnZoneChanged;

        base.Uninit();
    }
}
