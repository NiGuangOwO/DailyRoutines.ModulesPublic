using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using DailyRoutines.Abstracts;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Hooking;
using Lumina.Excel.Sheets;
using Action = System.Action;
using GameObject = FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject;

namespace DailyRoutines.Modules;

public unsafe class NoAttackWrongMandragoras : DailyModuleBase
{
    public override ModuleInfo Info => new()
    {
        Title = GetLoc("NoAttackWrongMandragorasTitle"),
        Description = GetLoc("NoAttackWrongMandragorasDescription"),
        Category = ModuleCategories.Combat,
    };

    private static readonly CompSig IsTargetableSig = new("0F B6 91 ?? ?? ?? ?? F6 C2 ?? 74 ?? F6 C2 ?? 74");
    private delegate bool IsTargetableDelegate(GameObject* gameObj);
    private static Hook<IsTargetableDelegate>? IsTargetableHook;

    private static          List<uint[]>?    Mandragoras;
    private static readonly List<IBattleNpc> ValidBattleNPCs = [];
    private static readonly HashSet<uint>    ValidZones      = [558, 712, 725, 794, 879, 924, 1000, 1123, 1209];
    private static readonly HashSet<string>  ValidBNPCNames  = ["王后", "queen", "クイーン"];

    public override void Init()
    {
        Mandragoras ??= LuminaCache.Get<BNpcName>()
                                   .Where(x => ValidBNPCNames.Any(
                                              name => x.Singular.ExtractText().Contains(
                                                  name, StringComparison.OrdinalIgnoreCase)))
                                   .Select(queen => Enumerable.Range((int)(queen.RowId - 4), 5).Select(id => (uint)id)
                                                              .ToArray())
                                   .ToList();

        IsTargetableHook ??= DService.Hook.HookFromSignature<IsTargetableDelegate>(IsTargetableSig.Get(), IsTargetableDetour);
        IsTargetableHook.Enable();

        DService.ClientState.TerritoryChanged += OnZoneChanged;
        OnZoneChanged(DService.ClientState.TerritoryType);
    }

    private static void OnZoneChanged(ushort zone) 
        => (ValidZones.Contains(zone) ? (Action)IsTargetableHook.Enable : IsTargetableHook.Disable)();

    private static bool IsTargetableDetour(GameObject* potentialTarget)
    {
        if (!ValidZones.Contains(DService.ClientState.TerritoryType) || Mandragoras == null)
            return IsTargetableHook.Original(potentialTarget);

        if (Throttler.Throttle("NoAttackWrongMandragoras-Update", 100))
        {
            ValidBattleNPCs.Clear();
            ValidBattleNPCs.AddRange(DService.ObjectTable
                                            .OfType<IBattleNpc>()
                                            .Where(obj => obj.IsValid() && !obj.IsDead &&
                                                          Vector3.Distance(
                                                              DService.ClientState.LocalPlayer.Position, obj.Position) <=
                                                          45));
        }

        var objID = potentialTarget->GetNameId();
        return !Mandragoras.Any(mandragoraSeries =>
        {
            var index = Array.IndexOf(mandragoraSeries, objID);
            return index != -1 && ValidBattleNPCs.Any(x => x.IsValid() && !x.IsDead &&
                                                           mandragoraSeries.Take(index).Contains(x.ToStruct()->BaseId));
        }) && IsTargetableHook.Original(potentialTarget);
    }

    public override void Uninit()
    {
        DService.ClientState.TerritoryChanged -= OnZoneChanged;
        base.Uninit();
    }
}
