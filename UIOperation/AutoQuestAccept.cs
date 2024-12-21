using DailyRoutines.Abstracts;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace DailyRoutines.Modules;

public class AutoQuestAccept : DailyModuleBase
{
    public override ModuleInfo Info => new()
    {
        Title = GetLoc("AutoQuestAcceptTitle"),
        Description = GetLoc("AutoQuestAcceptDescription"),
        Category = ModuleCategories.UIOperation,
    };

    public override void Init()
    {
        DService.AddonLifecycle.RegisterListener(AddonEvent.PostSetup, "JournalAccept", OnAddonSetup);
    }

    public override void ConfigUI() { ConflictKeyText(); }

    private unsafe void OnAddonSetup(AddonEvent type, AddonArgs args)
    {
        InterruptByConflictKey(TaskHelper, this);

        var addon = (AtkUnitBase*)args.Addon;
        if (addon == null) return;

        var questID = addon->AtkValues[266].UInt;
        if (questID == 0) return;
        
        Callback(addon, true, 3, questID);
    }

    public override void Uninit()
    {
        DService.AddonLifecycle.UnregisterListener(OnAddonSetup);
    }
}