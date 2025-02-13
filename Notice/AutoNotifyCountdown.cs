using System.Collections.Generic;
using System.Linq;
using DailyRoutines.Abstracts;
using DailyRoutines.Helpers;
using DailyRoutines.Managers;
using Dalamud.Game.Text;
using Dalamud.Game.Text.SeStringHandling;
using FFXIVClientStructs.FFXIV.Client.System.Framework;
using ImGuiNET;
using Lumina.Excel.GeneratedSheets;
using PayloadType = Lumina.Text.Payloads.PayloadType;

namespace DailyRoutines.Modules;

public class AutoNotifyCountdown : DailyModuleBase
{
    public override ModuleInfo Info => new()
    {
        Title = GetLoc("AutoNotifyCountdownTitle"),
        Description = GetLoc("AutoNotifyCountdownDescription"),
        Category = ModuleCategories.Notice,
        Author = ["HSS"]
    };

    private static bool ConfigOnlyNotifyWhenBackground;
    private static List<string>? Countdown;

    public override void Init()
    {
        AddConfig("OnlyNotifyWhenBackground", true);
        ConfigOnlyNotifyWhenBackground = GetConfig<bool>("OnlyNotifyWhenBackground");

        Countdown ??= LuminaCache.GetRow<LogMessage>(5255).Text.Payloads
                                 .Where(x => x.PayloadType == PayloadType.Text)
                                 .Select(text => text.RawString).ToList();

        DService.Chat.ChatMessage += OnChatMessage;
    }

    public override void ConfigUI()
    {
        if (ImGui.Checkbox(Lang.Get("OnlyNotifyWhenBackground"),
                           ref ConfigOnlyNotifyWhenBackground))
            UpdateConfig("OnlyNotifyWhenBackground", ConfigOnlyNotifyWhenBackground);
    }

    private static unsafe void OnChatMessage(XivChatType type, int timestamp, ref SeString sender, ref SeString message, ref bool ishandled)
    {
        if (ConfigOnlyNotifyWhenBackground && !Framework.Instance()->WindowInactive) return;

        var uintType = (uint)type;
        if (uintType != 185) return;

        var msg = message.TextValue;
        if (Countdown.All(msg.Contains))
        {
            NotificationInfo(message.TextValue, Lang.Get("AutoNotifyCountdown-NotificationTitle"));
            Speak(message.TextValue);
        }
    }

    public override void Uninit()
    {
        DService.Chat.ChatMessage -= OnChatMessage;
    }
}
