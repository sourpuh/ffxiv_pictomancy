using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;
using System.Drawing;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Text;

namespace Pictomancy.DXDraw;

/**
 * Automatically clip FFXIV native UI elements so they "appear" to be in front of Pictomancy's overlay.
 */
internal class AddonClipper
{
    private readonly static string[] _actionBarAddonNames = { "_ActionBar", "_ActionBar01", "_ActionBar02", "_ActionBar03", "_ActionBar04", "_ActionBar05", "_ActionBar06", "_ActionBar07", "_ActionBar08", "_ActionBar09", "_ActionBarEx" };
    private readonly static string[] _statusAddonNames = { "_StatusCustom0", "_StatusCustom1", "_StatusCustom2" };
    private readonly static List<string> _ignoredAddonNames = ["_FocusTargetInfo"];
    private DXRenderer? _renderer;

    public void Clip(DXRenderer renderer)
    {
        _renderer = renderer;
        ClipWindows();
        ClipMainTargetInfo();
        ClipTargetInfoCastBar();
        ClipActionBars();
        // Visibility for this addon doesn't seem to work. Disabled until there is a way to hide it.
        // ClipActionCross();
        ClipPartyList();
        ClipChatBubbles();
        ClipEnemyList();
        ClipStatuses();
        ClipParameterWidget();
        _renderer = null;
    }

    private unsafe void ClipPartyList()
    {
        AtkUnitBase* addon = (AtkUnitBase*)PictoService.GameGui.GetAddonByName("_PartyList", 1);
        if (addon == null || !addon->IsVisible) { return; }

        if (addon->UldManager.NodeListCount < 2) { return; }

        AtkResNode* baseNode = addon->UldManager.NodeList[0];

        for (int i = 6; i < 23; i++)
        {
            AtkResNode* slotNode = addon->UldManager.NodeList[i];
            if (slotNode is null) continue;

            if (slotNode->IsVisible())
            {
                Vector2 pos = new Vector2(
                    slotNode->ScreenX + (18f * addon->Scale),
                    slotNode->ScreenY
                    );
                Vector2 size = new Vector2(
                    slotNode->Width * addon->Scale - 25f * addon->Scale,
                    slotNode->Height * addon->Scale - 5f * addon->Scale
                    );

                _renderer!.AddClipZone(Rectangle(pos, size));
            }
        }
    }

    private unsafe void ClipEnemyList()
    {
        AtkUnitBase* addon = (AtkUnitBase*)PictoService.GameGui.GetAddonByName("_EnemyList");
        if (addon == null || !addon->IsVisible || addon->UldManager.NodeList == null) return;

        for (int i = 4; i <= 11; i++)
        {
            ClipAtkNode(addon, addon->UldManager.NodeList[i]);
        }
    }

    public unsafe void ClipWindows()
    {
        AtkStage* stage = AtkStage.Instance();
        if (stage == null) { return; }

        RaptureAtkUnitManager* manager = stage->RaptureAtkUnitManager;
        if (manager == null) { return; }

        AtkUnitList* loadedUnitsList = &manager->AtkUnitManager.AllLoadedUnitsList;
        if (loadedUnitsList == null) { return; }

        for (int i = 0; i < loadedUnitsList->Count; i++)
        {
            try
            {
                AtkUnitBase* addon = *(AtkUnitBase**)Unsafe.AsPointer(ref loadedUnitsList->Entries[i]);
                if (addon == null || !addon->IsVisible || addon->WindowNode == null || addon->Scale == 0)
                {
                    continue;
                }

                string? name = Encoding.UTF8.GetString(addon->Name);
                if (name != null && _ignoredAddonNames.Contains(name))
                {
                    continue;
                }

                float margin = 5 * addon->Scale;
                float bottomMargin = 13 * addon->Scale;

                Vector2 pos = new Vector2(addon->X + margin, addon->Y + margin);
                Vector2 size = new Vector2(
                    addon->WindowNode->AtkResNode.Width * addon->Scale - margin,
                    addon->WindowNode->AtkResNode.Height * addon->Scale - bottomMargin
                );

                // special case for duty finder
                if (name == "ContentsFinder")
                {
                    size.X += size.X + (16 * addon->Scale);
                    size.Y += (30 * addon->Scale);
                }

                _renderer!.AddClipZone(Rectangle(pos, size));
            }
            catch { }
        }
    }

    private unsafe void ClipMainTargetInfo()
    {
        AtkUnitBase* addon = (AtkUnitBase*)PictoService.GameGui.GetAddonByName("_TargetInfoMainTarget", 1);
        if (addon == null || !addon->IsVisible || addon->UldManager.NodeListCount < 5) return;
        var gaugeBar = addon->UldManager.NodeList[5];
        if (gaugeBar == null || !gaugeBar->IsVisible()) return;
        ClipAtkNode(addon, gaugeBar->GetAsAtkComponentNode()->Component->UldManager.NodeList[0]);
    }

    private unsafe void ClipTargetInfoCastBar()
    {
        AtkUnitBase* addon = (AtkUnitBase*)PictoService.GameGui.GetAddonByName("_TargetInfoCastBar", 1);
        if (addon == null || !addon->IsVisible || addon->UldManager.NodeListCount < 2) return;
        ClipAtkNode(addon, addon->UldManager.NodeList[2]);
    }

    private unsafe void ClipActionBars()
    {
        foreach (string addonName in _actionBarAddonNames)
        {
            AtkUnitBase* addon = (AtkUnitBase*)PictoService.GameGui.GetAddonByName(addonName, 1);
            if (addon == null || !addon->IsVisible || addon->UldManager.NodeListCount < 20) continue;
            for (int i = 9; i <= 20; i++)
            {
                var hotbarBtn = addon->UldManager.NodeList[i];
                if (hotbarBtn == null || !hotbarBtn->IsVisible()) continue;
                ClipAtkNode(addon, hotbarBtn->GetAsAtkComponentNode()->Component->UldManager.NodeList[0]);
            }
        }
    }

    private unsafe void ClipActionCross()
    {
        AtkUnitBase* addon = (AtkUnitBase*)PictoService.GameGui.GetAddonByName("_ActionCross", 1);
        if (addon == null || !addon->IsVisible || addon->UldManager.NodeListCount < 11) return;
        for (int i = 8; i <= 11; i++)
        {
            var buttonGroup = addon->UldManager.NodeList[i];
            if (buttonGroup == null || !buttonGroup->IsVisible()) continue;
            for (int j = 0; j <= 3; j++)
            {
                ClipAtkNode(addon, buttonGroup->GetAsAtkComponentNode()->Component->UldManager.NodeList[j], buttonGroup);
            }
        }
    }

    private unsafe void ClipStatuses()
    {
        foreach (string addonName in _statusAddonNames)
        {
            AtkUnitBase* addon = (AtkUnitBase*)PictoService.GameGui.GetAddonByName(addonName, 1);
            if (addon == null || !addon->IsVisible || addon->UldManager.NodeListCount < 24) continue;

            for (int i = 5; i <= 24; i++)
            {
                var status = addon->UldManager.NodeList[i];
                if (status == null || !status->IsVisible()) continue;
                ClipAtkNode(addon, status->GetAsAtkComponentNode()->Component->UldManager.NodeList[1]);
                ClipAtkNode(addon, status->GetAsAtkComponentNode()->Component->UldManager.NodeList[2]);
            }
        }
    }

    private unsafe void ClipChatBubbles()
    {
        AtkUnitBase* addon = (AtkUnitBase*)PictoService.GameGui.GetAddonByName("_MiniTalk", 1);
        if (addon == null || !addon->IsVisible || addon->UldManager.NodeListCount < 10) return;
        for (int i = 1; i <= 10; i++)
        {
            AtkResNode* node = addon->UldManager.NodeList[i];
            if (node == null || !node->IsVisible()) continue;

            AtkComponentNode* component = node->GetAsAtkComponentNode();
            if (component == null || component->Component->UldManager.NodeListCount < 1) continue;
            ClipAtkNode(addon, component->Component->UldManager.NodeList[1]);
        }
    }

    private unsafe void ClipParameterWidget()
    {
        AtkUnitBase* addon = (AtkUnitBase*)PictoService.GameGui.GetAddonByName("_ParameterWidget", 1);
        if (addon == null || !addon->IsVisible || addon->UldManager.NodeListCount < 2) return;
        // HP
        ClipAtkNode(addon, addon->UldManager.NodeList[2]);
        // MP
        ClipAtkNode(addon, addon->UldManager.NodeList[1]);
    }

    private static Rectangle Rectangle(Vector2 min, Vector2 size)
    {
        return new((int)min.X, (int)min.Y, (int)size.X, (int)size.Y);
    }

    private unsafe void ClipAtkNode(AtkUnitBase* addon, AtkResNode* node, AtkResNode* parent = null)
    {
        if (node == null || !node->IsVisible()) return;
        int posX = (int)node->ScreenX;
        int posY = (int)node->ScreenY;

        int width = (int)(node->Width * addon->Scale * node->ScaleX * (parent == null ? 1 : parent->ScaleX));
        int height = (int)(node->Height * addon->Scale * node->ScaleY * (parent == null ? 1 : parent->ScaleY));
        _renderer!.AddClipZone(new(posX, posY, width, height));
    }
}
