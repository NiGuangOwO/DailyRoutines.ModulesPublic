using DailyRoutines.Abstracts;
using DailyRoutines.Infos;
using DailyRoutines.Managers;
using Dalamud.Hooking;
using Dalamud.Utility;
using FFXIVClientStructs.FFXIV.Client.Game;
using Lumina.Excel.GeneratedSheets;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;

namespace DailyRoutines.Modules;

public unsafe class AutoMovePetCenter : DailyModuleBase
{
    public override ModuleInfo Info => new()
    {
        Author = ["逆光"],
        Title = GetLoc("AutoMovePetCenterTitle"),
        Description = GetLoc("AutoMovePetCenterDescription"),
        Category = ModuleCategories.Combat,
        ModulesConflict = ["AutoMovePetPosition"]
    };

    private static readonly CompSig NpcSpawnSig = new("48 89 5C 24 ?? 57 48 81 EC ?? ?? ?? ?? 48 8B DA 8B F9 E8 ?? ?? ?? ?? 3C ?? 75 ?? E8 ?? ?? ?? ?? 3C ?? 75 ?? 80 BB ?? ?? ?? ?? ?? 75 ?? 8B 05 ?? ?? ?? ?? 39 43 ?? 0F 85 ?? ?? ?? ?? 0F B6 53 ?? 48 8D 0D ?? ?? ?? ?? E8 ?? ?? ?? ?? 0F B6 53 ?? 48 8D 0D ?? ?? ?? ?? E8 ?? ?? ?? ?? 48 8D 54 24 ?? C7 44 24 ?? ?? ?? ?? ?? B8 ?? ?? ?? ?? 66 90 48 8D 92 ?? ?? ?? ?? 0F 10 03 0F 10 4B ?? 48 8D 9B ?? ?? ?? ?? 0F 11 42 ?? 0F 10 43 ?? 0F 11 4A ?? 0F 10 4B ?? 0F 11 42 ?? 0F 10 43 ?? 0F 11 4A ?? 0F 10 4B ?? 0F 11 42 ?? 0F 10 43 ?? 0F 11 4A ?? 0F 10 4B ?? 0F 11 42 ?? 0F 11 4A ?? 48 83 E8 ?? 75 ?? 48 8B 03");
    private delegate void NpcSpawnDelegate(nint a1, nint packetData);
    private static Hook<NpcSpawnDelegate>? NpcSpawnHook;

    private static readonly Dictionary<string, List<MapInfo>> MMapInfoDict = [];

    public override void Init()
    {
        NpcSpawnHook ??= NpcSpawnSig.GetHook<NpcSpawnDelegate>(ReviceNpcSpawn);
        NpcSpawnHook.Enable();

        LoadZoneAndMapInfo();
        base.Init();
    }

    private static void LoadZoneAndMapInfo()
    {
        MMapInfoDict.Clear();
        foreach (var map in DService.Data.GetExcelSheet<Map>()!)
        {
            var mapZoneKey = map.Id.RawString.Split('/')[0];
            if (!MMapInfoDict.TryGetValue(mapZoneKey, out var value))
            {
                value = [];
                MMapInfoDict[mapZoneKey] = value;
            }
            value.Add(new MapInfo(map.Id, map.SizeFactor, map.OffsetX, map.OffsetY, map.PlaceNameSub.Value!.Name.RawString));
        }
    }

    private void ReviceNpcSpawn(nint a1, nint packetData)
    {
        NpcSpawnHook.Original(a1, packetData);
        try
        {
            var sourceCharacter = Marshal.PtrToStructure<NpcSpawn>(packetData);
            if (DService.ClientState.LocalPlayer == null || GameMain.Instance()->CurrentContentFinderConditionId == 0)
                return;

            if (sourceCharacter.spawnerId == DService.ClientState.LocalPlayer.GameObjectId)
            {
                var pos = GetMapInfoFromTerritoryTypeID(GameMain.Instance()->CurrentTerritoryTypeId).GetMapCoordinates(new Vector2(1024f, 1024f));
                var center = new Vector3(pos.X, DService.ClientState.LocalPlayer.Position.Y, pos.Y);
                DService.Log.Debug($"自动将召唤兽移动至: {center}");
                ExecuteCommandManager.ExecuteCommandComplexLocation(ExecuteCommandComplexFlag.PetAction, center, 3);

            }
        }
        catch (Exception e)
        {
            DService.Log.Error($"{e}");
        }
    }

    private static MapInfo GetMapInfoFromTerritoryTypeID(uint TerritoryType)
    {
        var mapBaseName = DService.Data.GetExcelSheet<TerritoryType>()?.GetRow(TerritoryType)?.Map.Value?.Id.RawString.Split('/')[0];
        if (mapBaseName.IsNullOrEmpty())
            return MapInfo.Unknown;

        return MMapInfoDict.TryGetValue(mapBaseName!, out var value) ?
            value.MinBy(item => Vector2.Distance(item.GetMapCoordinates(new Vector2(1024f, 1024f)), new Vector2(DService.ClientState.LocalPlayer!.Position.X, DService.ClientState.LocalPlayer!.Position.Z)))
            : MapInfo.Unknown;
    }

    private struct MapInfo(string mapID, ushort sizeFactor, short offsetX, short offsetY, string placeNameSub)
    {
        public string MapID { readonly get; set; } = mapID;
        public ushort SizeFactor { readonly get; set; } = sizeFactor;
        public Vector2 Offset { readonly get; set; } = new Vector2(offsetX, offsetY);
        public string PlaceNameSub { readonly get; set; } = placeNameSub;

        public readonly Vector2 GetMapCoordinates(Vector2 pixelCoordinates)
        {
            return ((pixelCoordinates - new Vector2(1024f)) / SizeFactor * 100f) - Offset;
        }

        public static readonly MapInfo Unknown = new("default/00", 100, 0, 0, "");
    }

    [StructLayout(LayoutKind.Explicit, Size = 672)]
    private struct NpcSpawn
    {
        [FieldOffset(84)]
        public uint spawnerId;
    }
}

