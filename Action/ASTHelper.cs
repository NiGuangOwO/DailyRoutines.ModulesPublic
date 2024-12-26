#if DEBUG
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Numerics;
using System.Threading.Tasks;
using System.Web;
using DailyRoutines.Abstracts;
using DailyRoutines.Managers;
using Dalamud.Game.ClientState.Party;
using Dalamud.Interface.Utility.Raii;
using FFXIVClientStructs.FFXIV.Client.Game;
using Newtonsoft.Json;
using Action = Lumina.Excel.Sheets.Action;

namespace DailyRoutines.Modules;

public class ASTHelper : DailyModuleBase
{
    public override ModuleInfo Info => new()
    {
        Author      = ["HaKu"],
        Title       = GetLoc("ASTHelperTitle"),
        Description = GetLoc("ASTHelperDescription"),
        Category    = ModuleCategories.Action
    };

    // module specific config
    private static Config? ModuleConfig;

    // auto play card candidates (melee and range)
    private const  uint UnspecificTargetId = 0xE0000000;
    private static uint MeleeCandidateId   = UnspecificTargetId;
    private static uint RangeCandidateId   = UnspecificTargetId;

    // cache for member objectID for easy access
    private static HashSet<uint> PartyMemberIds;

    // HttpClient for fetching FFLogs (auto play card advance mode)
    private static readonly HttpClient Client    = new();
    private const           string     FFLogsUri = "https://www.fflogs.com/v1";

    public override void Init()
    {
        ModuleConfig = LoadConfig<Config>() ?? new Config();

        // hook before send card & heal
        UseActionManager.Register(OnPreUseAction);

        // task helper for select candidates
        TaskHelper ??= new TaskHelper { TimeLimitMS = 20_000 };

        // fetch team logs when zone changed (for ultimates and current savage)
        DService.ClientState.TerritoryChanged += OnZoneChanged;
    }

    public override void ConfigUI()
    {
        // auto play card
        ImGui.TextColored(LightSkyBlue, GetLoc("ASTHelper-AutoPlayCardTitle"));
        ImGuiOm.HelpMarker(GetLoc("ASTHelper-AutoPlayCardDescription", LuminaCache.GetRow<Action>(17055).Name.ExtractText()));
        
        ImGui.Spacing();

        using (ImRaii.PushIndent())
        {
            if (ImGui.RadioButton($"{GetLoc("Disable")}",
                                  ModuleConfig.AutoPlayCard == AutoPlayCardStatus.Disable))
            {
                ModuleConfig.AutoPlayCard = AutoPlayCardStatus.Disable;
                SaveConfig(ModuleConfig);
            }

            if (ImGui.RadioButton($"{GetLoc("Common")} ({GetLoc("ASTHelper-AutoPlayCard-CommonDescription")})",
                                  ModuleConfig.AutoPlayCard == AutoPlayCardStatus.Default))
            {
                ModuleConfig.AutoPlayCard = AutoPlayCardStatus.Default;
                SaveConfig(ModuleConfig);
            }

            if (ImGui.RadioButton($"{GetLoc("Advance")} ({GetLoc("ASTHelper-AutoPlayCard-AdvanceDescription")})",
                                  ModuleConfig.AutoPlayCard == AutoPlayCardStatus.Advance))
            {
                ModuleConfig.AutoPlayCard = AutoPlayCardStatus.Advance;
                SaveConfig(ModuleConfig);
            }

            // Api Key [v1] for fetching FFLogs records (auto play card advance mode)
            if (ModuleConfig.AutoPlayCard == AutoPlayCardStatus.Advance)
            {
                // api key (v1)
                ImGui.Spacing();

                ImGui.AlignTextToFramePadding();
                ImGui.Text("FFLogs V1 API Key");

                ImGui.Spacing();

                if (ImGui.InputText("##FFLogsAPIKey", ref ModuleConfig.FFLogsAPIKey, 32))
                    SaveConfig(ModuleConfig);

                ImGui.SameLine();
                if (ImGui.Button(GetLoc("Save")))
                {
                    if (string.IsNullOrWhiteSpace(ModuleConfig.FFLogsAPIKey) || ModuleConfig.FFLogsAPIKey.Length != 32)
                    {
                        ModuleConfig.KeyValid = false;
                        SaveConfig(ModuleConfig);
                    }
                    else
                        DService.Framework.RunOnTick(async () => await CheckKeyStatus());
                }

                // key status (valid or invalid)
                ImGui.Spacing();

                ImGui.AlignTextToFramePadding();
                ImGui.Text(GetLoc("ASTHelper-LogsApi-Status"));

                ImGui.SameLine();
                ImGui.Text(ModuleConfig.KeyValid ? GetLoc("Connected") : GetLoc("Disconnected"));
            }
        }

        ImGui.NewLine();
        
        // easy heal
        ImGui.TextColored(LightSkyBlue, GetLoc("ASTHelper-EasyHealTitle"));
        ImGuiOm.HelpMarker(GetLoc("ASTHelper-EasyHealDescription"));
        
        ImGui.Spacing();

        using (ImRaii.PushIndent())
        {
            if (ImGui.RadioButton($"{GetLoc("Disable")}",
                                  ModuleConfig.EasyHeal == EasyHealStatus.Disable))
            {
                ModuleConfig.EasyHeal = EasyHealStatus.Disable;
                SaveConfig(ModuleConfig);
            }

            if (ImGui.RadioButton($"{GetLoc("Enable")} ({GetLoc("ASTHelper-EasyHeal-EnableDescription")})",
                                  ModuleConfig.EasyHeal == EasyHealStatus.Enable))
            {
                ModuleConfig.EasyHeal = EasyHealStatus.Enable;
                SaveConfig(ModuleConfig);
            }

            // heal threshold
            if (ModuleConfig.EasyHeal == EasyHealStatus.Enable)
            {
                ImGui.Spacing();

                ImGui.TextColored(LightGreen, GetLoc("ASTHelper-EasyHeal-HealThreshold"));
                ImGuiOm.HelpMarker(GetLoc("ASTHelper-EasyHeal-HealThresholdHelp"));

                ImGui.Spacing();

                if (ImGui.SliderFloat("##HealThreshold", ref ModuleConfig.NeedHealThreshold, 0.0f, 1.0f, "%.2f"))
                    SaveConfig(ModuleConfig);

                // all time heal warning
                if (ModuleConfig.NeedHealThreshold > 0.92f)
                {
                    ImGui.Spacing();
                    ImGui.TextColored(Orange, GetLoc("ASTHelper-EasyHeal-OverhealWarning"));
                }
            }
        }
        
        ImGui.Spacing();
        
        if (ImGui.Checkbox(GetLoc("SendChat"), ref ModuleConfig.SendChat))
            SaveConfig(ModuleConfig);
        
        ImGui.SameLine();
        if (ImGui.Checkbox(GetLoc("SendNotification"), ref ModuleConfig.SendNotification))
            SaveConfig(ModuleConfig);
    }

    // hook before play card & target heal
    private static void OnPreUseAction(
        ref bool  isPrevented, ref ActionType type,     ref uint actionID,
        ref ulong targetID,    ref Vector3    location, ref uint extraParam)
    {
        if (type != ActionType.Action || DService.PartyList.Length == 0) return;
        
        // auto play card
        if (actionID is (37023 or 37026) && ModuleConfig.AutoPlayCard != AutoPlayCardStatus.Disable &&
            !PartyMemberIds.Contains((uint)targetID))
        {
            targetID = actionID switch
            {
                37023 => MeleeCandidateId,
                37026 => RangeCandidateId,
            };
            
            var member = FetchMemberById((uint)targetID);
            if (member != null)
            {
                var name         = member.Name.ExtractText();
                var classJobIcon = member.ClassJob.ValueNullable.ToBitmapFontIcon();
                var classJobName = member.ClassJob.ValueNullable?.Name.ExtractText();
                
                var locKey = actionID switch
                {
                    37023 => "Melee",
                    37026 => "Range",
                };
            
                if (ModuleConfig.SendChat)
                    Chat(GetSLoc($"ASTHelper-AutoPlayCard-Message-{locKey}", name, classJobIcon, classJobName));
                if (ModuleConfig.SendNotification)
                    NotificationInfo(GetLoc($"ASTHelper-AutoPlayCard-Message-{locKey}", name, string.Empty, classJobName));
            }
        }

        // easy heal
        if (ModuleConfig.EasyHeal == EasyHealStatus.Enable)
        {
            if (TargetHealActions.Contains(actionID) && !PartyMemberIds.Contains((uint)targetID))
            {
                // find target with the lowest HP ratio within range and satisfy threshold
                targetID = TargetNeedHeal();
                if (targetID == UnspecificTargetId)
                {
                    isPrevented = true;
                    return;
                }

                var member = FetchMemberById((uint)targetID);
                if (member != null)
                {
                    var name         = member.Name.ExtractText();
                    var classJobIcon = member.ClassJob.ValueNullable.ToBitmapFontIcon();
                    var classJobName = member.ClassJob.ValueNullable?.Name.ExtractText();
                    
                    if (ModuleConfig.SendChat)
                        Chat(GetSLoc("ASTHealer-EasyHeal-Message", name, classJobIcon, classJobName));
                    if (ModuleConfig.SendNotification)
                        NotificationInfo(GetLoc("ASTHealer-EasyHeal-Message", name, string.Empty, classJobName));
                }
            }
        }
    }

    // fetch member information by id
    private static IPartyMember? FetchMemberById(uint id) 
        => DService.PartyList.FirstOrDefault(m => m.ObjectId == id);

    // update candidates when zone changed
    private void OnZoneChanged(ushort zone)
    {
        // disable auto play card or no party member
        if (ModuleConfig.AutoPlayCard == AutoPlayCardStatus.Disable) return;
        TaskHelper.Enqueue(() => !BetweenAreas, "##WaitForEnterDuty", null, null, 2);
        TaskHelper.Enqueue(() => SelectCandidates(zone));
    }

    private bool? SelectCandidates(ushort zone)
    {
        // reset candidates when zone changed
        MeleeCandidateId = UnspecificTargetId;
        RangeCandidateId = UnspecificTargetId;

        // find card candidates
        var partyList = DService.PartyList; // role [0 tank, 2 melee, 3 range, 4 healer]
        if (partyList.Length == 0)
        {
            TaskHelper.Abort();
            return true;
        }

        // cache member objectID for easy access
        PartyMemberIds = partyList.Select(m => m.ObjectId).ToHashSet();
        
        // advance fallback when no valid zone id or invalid key
        if (!Dal2LogsZoneMap.ContainsKey(zone) || ModuleConfig.AutoPlayCard == AutoPlayCardStatus.Advance)
        {
            if (!ModuleConfig.KeyValid)
            {
                Chat(GetLoc("ASTHealer-AutoPlayCard-AdvanceFallback"));
                ModuleConfig.AutoPlayCard = AutoPlayCardStatus.Default;
                SaveConfig(ModuleConfig);
            }
        }

        // select the best candidate for melee and range according to rdps
        if (ModuleConfig.AutoPlayCard == AutoPlayCardStatus.Advance)
        {
            var tmpBestMeleeDps = -1.0;
            var tmpBestRangeDps = -1.0;
            foreach (var member in partyList)
            {
                // fetch highest rDPS record in current region, job and patch
                var bestRecord = FetchBestLogsRecord(zone, member).GetAwaiter().GetResult();
                if (bestRecord == null) continue;

                switch (member.ClassJob.ValueNullable?.Role)
                {
                    case 2:
                        // better melee candidate?
                        if (bestRecord.DPS > tmpBestMeleeDps)
                        {
                            MeleeCandidateId = member.ObjectId;
                            tmpBestMeleeDps  = bestRecord.DPS;
                        }

                        break;
                    case 3:
                        // better range candidate?
                        if (bestRecord.DPS > tmpBestRangeDps)
                        {
                            RangeCandidateId = member.ObjectId;
                            tmpBestRangeDps  = bestRecord.DPS;
                        }

                        break;
                }
            }
        }

        // select candidates according to default order
        else if (ModuleConfig.AutoPlayCard == AutoPlayCardStatus.Default)
        {
            // melee with higher order
            foreach (var meleeJob in MeleeOrder)
            {
                var meleeMember = partyList.FirstOrDefault(m => m.ClassJob.ValueNullable?.NameEnglish == meleeJob);
                if (meleeMember != null)
                {
                    MeleeCandidateId = meleeMember.ObjectId;
                    break;
                }
            }

            // range with higher order
            foreach (var rangeJob in RangeOrder)
            {
                var rangeMember = partyList.FirstOrDefault(m => m.ClassJob.ValueNullable?.NameEnglish == rangeJob);
                if (rangeMember != null)
                {
                    RangeCandidateId = rangeMember.ObjectId;
                    break;
                }
            }
        }

        // melee fallback: if no candidate found, select the first melee or first party member.
        if (MeleeCandidateId == UnspecificTargetId)
        {
            MeleeCandidateId = partyList.FirstOrDefault(m => m.ClassJob.ValueNullable?.Role == 2, partyList.First()).ObjectId;
        }

        // range fallback: if no candidate found, select the last range or last party member.
        if (RangeCandidateId == UnspecificTargetId)
        {
            RangeCandidateId = partyList.LastOrDefault(m => m.ClassJob.ValueNullable?.Role == 3, partyList.Last()).ObjectId;
        }

        TaskHelper.Abort();
        return true;
    }

    private static uint TargetNeedHeal()
    {
        var partyList  = DService.PartyList;
        var lowRatio   = 2f;
        var needHealId = UnspecificTargetId;
        foreach (var member in partyList)
        {
            var ratio = member.CurrentHP / (float)member.MaxHP;
            if (ratio < lowRatio && ratio <= ModuleConfig.NeedHealThreshold)
            {
                lowRatio   = ratio;
                needHealId = member.ObjectId;
            }
        }

        return needHealId;
    }

    private async Task CheckKeyStatus()
    {
        try
        {
            var uri      = $"{FFLogsUri}/classes?api_key={ModuleConfig.FFLogsAPIKey}";
            var response = await Client.GetStringAsync(uri);
            if (!string.IsNullOrWhiteSpace(response))
            {
                ModuleConfig.KeyValid = true;
                SaveConfig(ModuleConfig);
            }
            else
            {
                ModuleConfig.KeyValid = false;
                SaveConfig(ModuleConfig);
            }
        }
        catch (Exception)
        {
            ModuleConfig.KeyValid = false;
            SaveConfig(ModuleConfig);
        }
    }

    private static string GetRegion()
    {
        return DService.ClientState.LocalPlayer.CurrentWorld.ValueNullable?.DataCenter.Value.Region switch
        {
            1 => "JP",
            2 => "NA",
            3 => "EU",
            4 => "OC",
            5 => "CN",
            _ => string.Empty
        };
    }

    private static async Task<LogsRecord?> FetchBestLogsRecord(ushort zone, IPartyMember member)
    {
        // get character info
        var charaName  = member.Name;
        var serverSlug = member.World.ValueNullable?.Name;
        var region     = GetRegion();
        var job        = member.ClassJob.ValueNullable?.NameEnglish;

        // fetch record
        try
        {
            var uri   = $"{FFLogsUri}/parses/character/{charaName}/{serverSlug}/{region}";
            var query = HttpUtility.ParseQueryString(string.Empty);
            query["api_key"]   = ModuleConfig.FFLogsAPIKey;
            query["metric"]    = "rdps";
            query["encounter"] = Dal2LogsZoneMap[zone].ToString();

            // contains all ultimates and current savage in current patch
            var response = await Client.GetStringAsync($"{uri}?{query}");
            var records  = JsonConvert.DeserializeObject<LogsRecord[]>(response);
            if (records == null || records.Length == 0) return null;

            // find best record
            var bestRecord = records.Where(r => r.JobName == job)
                                    .OrderByDescending(r => r.Difficulty)
                                    .ThenByDescending(r => r.DPS)
                                    .FirstOrDefault();
            return bestRecord;
        }
        catch (Exception)
        {
            return null;
        }
    }

    public override void Uninit()
    {
        UseActionManager.Unregister(OnPreUseAction);
        DService.ClientState.TerritoryChanged -= OnZoneChanged;

        base.Uninit();
    }

    private class Config : ModuleConfiguration
    {
        // auto play card
        public AutoPlayCardStatus AutoPlayCard = AutoPlayCardStatus.Default;

        // FFLogs API Key v1 for fetching records (auto play card advance mode)
        public string FFLogsAPIKey = string.Empty;
        public bool   KeyValid;

        // easy heal
        public EasyHealStatus EasyHeal          = EasyHealStatus.Enable;
        public float          NeedHealThreshold = 0.8f;

        public bool SendChat;
        public bool SendNotification = true;
    }

    // Dalamud-FFLogs zone match map (ultimates and current savage)
    public static readonly Dictionary<uint, uint> Dal2LogsZoneMap = new()
    {
        // ultimates
        [733]  = 1073, // Bahamut f1bz
        [777]  = 1074, // Weapon w1fz
        [887]  = 1075, // Alexander d2az
        [968]  = 1076, // Dragonsong r1fz
        [1122] = 1077, // Omega z3oz
        // m1-4s
        [1226] = 93,
        [1228] = 94,
        [1230] = 95,
        [1232] = 96,
    };

    public class LogsRecord
    {
        // job english name
        [JsonProperty("spec")]
        public string JobName { get; set; }

        // record difficulty
        [JsonProperty("difficulty")]
        public int Difficulty { get; set; }

        // rDPS
        [JsonProperty("total")]
        public double DPS { get; set; }
    }

    public enum AutoPlayCardStatus
    {
        Disable, // disable auto play card
        Default, // select target based on predefined order when no target selected
        Advance  // select target based on FFLogs rDPS records when no target selected
    }

    public enum EasyHealStatus
    {
        Disable, // disable easy heal
        Enable   // select target with the lowest HP ratio within range when no target selected
    }

    // predefined card priority, arranged based on FFLogs statistics current order.
    // https://www.fflogs.com/zone/statistics/62
    public static readonly string[] MeleeOrder = ["Viper", "Dragon", "Ninja", "Monk", "Reaper", "Samurai"];

    public static readonly string[] RangeOrder =
        ["Black Mage", "Pictomancer", "Red Mage", "Machinist", "Summoner", "Bard", "Dancer"];

    // list of target healing actions
    public static readonly uint[] TargetHealActions =
    [
        3594,  // benefic 1
        3610,  // benefic 2
        3614,  // essential dignity
        3595,  // aspected benefic
        37024, // arrow card (+10% recovery)
        37025, // spire card (400 barrier)
        37027, // bole card (-10% damage)
        37028, // ewer card (200 hot 15s)
        16556, // celestial intersection
        25873  // exaltation
    ];
}

#endif
