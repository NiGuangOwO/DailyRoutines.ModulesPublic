using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using DailyRoutines.Abstracts;
using DailyRoutines.Infos;
using DailyRoutines.Managers;
using DailyRoutines.Windows;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Utility;

namespace DailyRoutines.Modules;

public class AutoShowDutyGuide : DailyModuleBase
{
    public override ModuleInfo Info => new()
    {
        Title = GetLoc("AutoShowDutyGuideTitle"),
        Description = GetLoc("AutoShowDutyGuideDescription"),
        Category = ModuleCategories.Combat,
    };

    private const string FF14OrgLinkBase = "https://gh.atmoomen.top/novice-network/master/docs/duty/{0}.md";

    private static CancellationTokenSource? CancelSource;

    private static Config ModuleConfig = null!;

    private static readonly HttpClient client = new();
    private static uint CurrentDuty;
    private static ISharedImmediateTexture? NoviceIcon;

    private static string HintText = string.Empty;
    private static List<string> GuideText = [];

    private static bool IsOnDebug;

    private readonly Dictionary<ushort, Func<bool?>> HintsContent = new()
    {
        { 1036, GetSastashaHint },
    };

    public override void Init()
    {
        ModuleConfig =   LoadConfig<Config>() ?? new();
        CancelSource ??= new();
        NoviceIcon   ??= DService.Texture.GetFromGameIcon(new(61523));
        TaskHelper   ??= new TaskHelper { TimeLimitMS = 60000 };

        Overlay ??= new Overlay(this);
        Overlay.Flags &= ~ImGuiWindowFlags.NoTitleBar;
        Overlay.Flags &= ~ImGuiWindowFlags.AlwaysAutoResize;
        Overlay.Flags |= ImGuiWindowFlags.NoBringToFrontOnFocus | ImGuiWindowFlags.NoNavInputs;
        Overlay.ShowCloseButton = false;

        DService.ClientState.TerritoryChanged += OnZoneChange;
        if (BoundByDuty)
            OnZoneChange(DService.ClientState.TerritoryType);
    }

    public override void ConfigUI()
    {
        ImGui.TextColored(LightSkyBlue, $"{GetLoc("WorkTheory")}:");
        ImGuiOm.HelpMarker(GetLoc("AutoShowDutyGuide-TheoryHelp"), 30f);

        ImGui.SetNextItemWidth(80f * GlobalFontScale);
        ImGui.InputFloat(GetLoc("FontScale"), ref ModuleConfig.FontScale);
        if (ImGui.IsItemDeactivatedAfterEdit())
            SaveConfig(ModuleConfig);

        using (ImRaii.Disabled(BoundByDuty))
        {
            if (ImGui.Checkbox(GetLoc("AutoShowDutyGuide-DebugMode"), ref IsOnDebug))
            {
                if (IsOnDebug) OnZoneChange(172);
                else
                {
                    HintText = string.Empty;
                    GuideText.Clear();
                    CurrentDuty = 0;
                }
            }
        }
        
        ImGuiOm.TooltipHover(GetLoc("AutoShowDutyGuide-DebugModeHelp"));
    }

    public override void OverlayOnOpen() => ImGui.SetScrollHereY();

    public override void OverlayPreDraw()
    {
        if (!IsOnDebug && (!BoundByDuty || GuideText.Count <= 0))
        {
            Overlay.IsOpen = false;
            GuideText.Clear();
            HintText = string.Empty;
            return;
        }

        if (GuideText.Count > 0)
            Overlay.WindowName = $"{GuideText[0]}###AutoShowDutyGuide-GuideWindow";
    }

    public override void OverlayUI()
    {
        using (FontManager.GetUIFont(ModuleConfig.FontScale).Push())
        {
            if (ImGuiOm.SelectableImageWithText(NoviceIcon.GetWrapOrEmpty().ImGuiHandle, ScaledVector2(24f),
                                                GetLoc("AutoShowDutyGuide-Source"), false))
                Util.OpenLink($"https://ff14.org/duty/{CurrentDuty}.htm");

            ImGui.Separator();

            using (ImRaii.TextWrapPos(ImGui.GetWindowWidth()))
            {
                if (!string.IsNullOrWhiteSpace(HintText))
                {
                    using (FontManager.GetUIFont(ModuleConfig.FontScale * 0.8f).Push())
                        ImGui.Text($"{GetLoc("AutoShowDutyGuide-DutyExtraGuide")}:");
                    ImGui.Text($"{HintText}");
                    ImGui.Separator();
                }

                for (var i = 1; i < GuideText.Count; i++)
                {
                    var       text = GuideText[i];
                    using var id   = ImRaii.PushId($"DutyGuideLine-{i}");
                    
                    ImGui.Text(text);
                    
                    if (ImGui.IsItemHovered())
                        ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
                    
                    if (ImGui.IsItemClicked(ImGuiMouseButton.Right))
                    {
                        ImGui.SetClipboardText(text);
                        NotificationSuccess(GetLoc("AutoShowDutyGuide-CopyNotice"));
                    }
                    
                    ImGui.NewLine();
                }
            }
        }
    }

    private void OnZoneChange(ushort territory)
    {
        if (!PresetData.Contents.TryGetValue(territory, out var content))
        {
            CurrentDuty = 0;
            HintText = string.Empty;
            GuideText.Clear();
            Overlay.IsOpen = false;
            return;
        }

        if (HintsContent.TryGetValue(territory, out var func))
        {
            TaskHelper.DelayNext(500);
            TaskHelper.Enqueue(func);
        }

        Task.Run(async () => await GetDutyGuide(content.RowId), CancelSource.Token);
    }

    private static bool? GetSastashaHint()
    {
        if (BetweenAreas) return false;
        if (!BoundByDuty) return true;

        var blueObj =
            DService.ObjectTable.FirstOrDefault(x => x.IsValid() && x.IsTargetable && x.DataId == (uint)Sastasha.蓝珊瑚);

        var redObj = DService.ObjectTable.FirstOrDefault(
            x => x.IsValid() && x.IsTargetable && x.DataId == (uint)Sastasha.红珊瑚);

        var greenObj =
            DService.ObjectTable.FirstOrDefault(x => x.IsValid() && x.IsTargetable && x.DataId == (uint)Sastasha.绿珊瑚);

        if (blueObj == null && redObj == null && greenObj == null) return false;

        if (blueObj != null) HintText = $"正确机关: {Sastasha.蓝珊瑚}";
        if (redObj != null) HintText = $"正确机关: {Sastasha.红珊瑚}";
        if (greenObj != null) HintText = $"正确机关: {Sastasha.绿珊瑚}";

        return true;
    }

    private async Task GetDutyGuide(uint dutyID)
    {
        try
        {
            CurrentDuty = dutyID;
            var originalText = await client.GetStringAsync(string.Format(FF14OrgLinkBase, dutyID));

            var plainText = MarkdownToPlainText(originalText);
            if (!string.IsNullOrWhiteSpace(plainText))
            {
                GuideText      = [.. plainText.Split('\n')];
                Overlay.IsOpen = true;
            }
        }
        catch (Exception)
        {
            // ignored
        }
    }

    public override void Uninit()
    {
        DService.ClientState.TerritoryChanged -= OnZoneChange;
        
        CancelSource?.Cancel();
        CancelSource?.Dispose();
        CancelSource = null;
        
        base.Uninit();
    }

    private enum Sastasha
    {
        蓝珊瑚 = 2000212,
        红珊瑚 = 2001548,
        绿珊瑚 = 2001549,
    }

    private class Config : ModuleConfiguration
    {
        public float FontScale = 1f;
    }
}
