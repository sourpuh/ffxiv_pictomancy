using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;
using System.Drawing;
using System.Numerics;
using System.Runtime.CompilerServices;

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
    const int numCircleSegments = 16;
    private Vector2[] circleOffsets;

    public AddonClipper()
    {
        circleOffsets = new Vector2[numCircleSegments + 1];
        float angleStep = MathF.PI * 2 / numCircleSegments;

        for (int step = 0; step <= numCircleSegments; step++)
        {
            float angle = MathF.PI / 2 + step * angleStep;
            circleOffsets[step] = new(MathF.Cos(angle), MathF.Sin(angle));
        }
    }

    public void Clip(DXRenderer renderer)
    {
        _renderer = renderer;
        ClipWindows();
        ClipCastBar();
        ClipMainTargetInfo();
        ClipTargetInfoCastBar();
        ClipTargetInfoStatus();
        ClipFocusTarget();
        ClipActionBars();
        ClipActionCross();
        ClipPartyList();
        ClipChatBubbles();
        ClipEnemyList();
        ClipStatuses();
        ClipParameterWidget();
        ClipLimitBreak();
        ClipMinimap();
        ClipMainCommand();
        // ClipChat();
        _renderer = null;
    }

    private unsafe void ClipPartyList()
    {
        AtkUnitBase* addon = (AtkUnitBase*)PictoService.GameGui.GetAddonByName("_PartyList", 1);
        if (addon == null || !addon->IsVisible || addon->UldManager.NodeListCount < 23) return;

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

                _renderer!.AddClipRect(Rectangle(pos, size));
            }
        }
    }

    private unsafe void ClipEnemyList()
    {
        AtkUnitBase* addon = (AtkUnitBase*)PictoService.GameGui.GetAddonByName("_EnemyList");
        if (addon == null || !addon->IsVisible || addon->UldManager.NodeList == null) return;

        for (int i = 4; i <= 11; i++)
        {
            ClipAtkNodeRectangle(addon, addon->UldManager.NodeList[i]);
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

                string name = addon->NameString;
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

                _renderer!.AddClipRect(Rectangle(pos, size));
            }
            catch { }
        }
    }
    private unsafe void ClipCastBar()
    {
        AtkUnitBase* addon = (AtkUnitBase*)PictoService.GameGui.GetAddonByName("_CastBar", 1);
        if (addon == null || !addon->IsVisible || addon->VisibilityFlags != 0 || addon->UldManager.NodeListCount < 2) return;
        ClipAtkNodeRectangle(addon, addon->UldManager.NodeList[3]);
        ClipAtkNodeRectangle(addon, addon->UldManager.NodeList[8]);
        ClipAtkNodeRectangle(addon, addon->UldManager.NodeList[9]);
        ClipAtkNodeRectangle(addon, addon->UldManager.NodeList[10]);
    }
    private unsafe void ClipMainTargetInfo()
    {
        AtkUnitBase* addon = (AtkUnitBase*)PictoService.GameGui.GetAddonByName("_TargetInfoMainTarget", 1);
        if (addon == null || !addon->IsVisible || addon->UldManager.NodeListCount < 5) return;
        var gaugeBar = addon->UldManager.NodeList[5];
        if (gaugeBar == null || !gaugeBar->IsVisible()) return;
        ClipAtkNodeRectangle(addon, gaugeBar->GetAsAtkComponentNode()->Component->UldManager.NodeList[0]);
        if (addon->UldManager.NodeList[9]->IsVisible())
            ClipAtkNodeRectangle(addon, addon->UldManager.NodeList[10]);
    }

    private unsafe void ClipTargetInfoCastBar()
    {
        AtkUnitBase* addon = (AtkUnitBase*)PictoService.GameGui.GetAddonByName("_TargetInfoCastBar", 1);
        if (addon == null || !addon->IsVisible || addon->VisibilityFlags != 0 || addon->UldManager.NodeListCount < 2) return;
        ClipAtkNodeRectangle(addon, addon->UldManager.NodeList[2]);
    }

    private unsafe void ClipTargetInfoStatus()
    {
        AtkUnitBase* addon = (AtkUnitBase*)PictoService.GameGui.GetAddonByName("_TargetInfoBuffDebuff", 1);
        if (addon == null || !addon->IsVisible || addon->UldManager.NodeListCount < 31) return;
        for (int i = 2; i <= 31; i++)
        {
            var status = addon->UldManager.NodeList[i];
            if (status == null || !status->IsVisible()) continue;
            ClipAtkNodeRectangle(addon, status->GetAsAtkComponentNode()->Component->UldManager.NodeList[1]);
            ClipAtkNodeRectangle(addon, status->GetAsAtkComponentNode()->Component->UldManager.NodeList[2]);
        }
    }
    private unsafe void ClipFocusTarget()
    {
        AtkUnitBase* addon = (AtkUnitBase*)PictoService.GameGui.GetAddonByName("_FocusTargetInfo", 1);
        if (addon == null || !addon->IsVisible || addon->UldManager.NodeListCount < 16) return;
        ClipAtkNodeRectangle(addon, addon->UldManager.NodeList[2]);
        ClipAtkNodeRectangle(addon, addon->UldManager.NodeList[16]);
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
                ClipAtkNodeRectangle(addon, hotbarBtn->GetAsAtkComponentNode()->Component->UldManager.NodeList[0]);
            }
        }
    }

    private unsafe void ClipActionCross()
    {
        // Standard 'IsVisible' for these addons doesn't seem to work.
        // 'VisibilityFlags' is the only thing that changes when toggling the addons.
        // 'ActionCross' uses 'VisibilityFlags' 0 as visible and 1 as hidden.
        // The 'ActionDoubleCross' bars seem to use the visibility flag from 'ActionCross'.
        {
            AtkUnitBase* addon = (AtkUnitBase*)PictoService.GameGui.GetAddonByName("_ActionCross", 1);
            if (addon == null || !addon->IsVisible || addon->VisibilityFlags != 0 || addon->UldManager.NodeListCount < 11) return;
            for (int i = 8; i <= 11; i++)
            {
                ClipCrossButtonGroup(addon, addon->UldManager.NodeList[i]);
            }
        }

        {
            AtkUnitBase* addon = (AtkUnitBase*)PictoService.GameGui.GetAddonByName("_ActionDoubleCrossL", 1);
            if (addon == null || !addon->IsVisible || addon->VisibilityFlags != 0 || addon->UldManager.NodeListCount < 7) return;
            ClipCrossButtonGroup(addon, addon->UldManager.NodeList[5]);
            ClipCrossButtonGroup(addon, addon->UldManager.NodeList[6]);
        }

        {
            AtkUnitBase* addon = (AtkUnitBase*)PictoService.GameGui.GetAddonByName("_ActionDoubleCrossR", 1);
            if (addon == null || !addon->IsVisible || addon->VisibilityFlags != 0 || addon->UldManager.NodeListCount < 7) return;
            ClipCrossButtonGroup(addon, addon->UldManager.NodeList[5]);
            ClipCrossButtonGroup(addon, addon->UldManager.NodeList[6]);
        }
    }

    private unsafe void ClipCrossButtonGroup(AtkUnitBase* addon, AtkResNode* buttonGroup)
    {
        if (buttonGroup == null || !buttonGroup->IsVisible()) return;
        for (int j = 0; j <= 3; j++)
        {
            ClipAtkNodeRectangle(addon, buttonGroup->GetAsAtkComponentNode()->Component->UldManager.NodeList[j], buttonGroup);
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
                ClipAtkNodeRectangle(addon, status->GetAsAtkComponentNode()->Component->UldManager.NodeList[1]);
                ClipAtkNodeRectangle(addon, status->GetAsAtkComponentNode()->Component->UldManager.NodeList[2]);
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
            ClipAtkNodeRectangle(addon, component->Component->UldManager.NodeList[1]);
        }
    }

    private unsafe void ClipParameterWidget()
    {
        AtkUnitBase* addon = (AtkUnitBase*)PictoService.GameGui.GetAddonByName("_ParameterWidget", 1);
        if (addon == null || !addon->IsVisible || addon->UldManager.NodeListCount < 2) return;
        // HP
        ClipAtkNodeRectangle(addon, addon->UldManager.NodeList[2]);
        // MP
        ClipAtkNodeRectangle(addon, addon->UldManager.NodeList[1]);
    }
    private unsafe void ClipLimitBreak()
    {
        AtkUnitBase* addon = (AtkUnitBase*)PictoService.GameGui.GetAddonByName("_LimitBreak", 1);
        if (addon == null || !addon->IsVisible || addon->UldManager.NodeListCount < 5) return;
        ClipAtkNodeRectangle(addon, addon->UldManager.NodeList[2]);
        ClipAtkNodeRectangle(addon, addon->UldManager.NodeList[3]);
        ClipAtkNodeRectangle(addon, addon->UldManager.NodeList[4]);
    }
    private unsafe void ClipChat()
    {
        AtkUnitBase* addon = (AtkUnitBase*)PictoService.GameGui.GetAddonByName("ChatLog", 1);
        if (addon == null || !addon->IsVisible || addon->UldManager.NodeListCount < 5) return;
        ClipAtkNodeRectangle(addon, addon->UldManager.NodeList[3], null, 0.5f, true);
    }
    private unsafe void ClipMinimap()
    {
        AtkUnitBase* addon = (AtkUnitBase*)PictoService.GameGui.GetAddonByName("_NaviMap", 1);
        if (addon == null || !addon->IsVisible || addon->UldManager.NodeListCount < 19) return;
        ClipAtkNodeCircle(addon, addon->UldManager.NodeList[6]);
        ClipAtkNodeCircle(addon, addon->UldManager.NodeList[8]);
        ClipAtkNodeCircle(addon, addon->UldManager.NodeList[15]);
    }
    private unsafe void ClipMainCommand()
    {
        AtkUnitBase* addon = (AtkUnitBase*)PictoService.GameGui.GetAddonByName("_MainCommand", 1);
        if (addon == null || !addon->IsVisible || addon->UldManager.NodeListCount < 8) return;
        for (int i = 1; i <= 7; i++)
        {
            ClipAtkNodeCircle(addon, addon->UldManager.NodeList[i]);
        }
    }
    private static Rectangle Rectangle(Vector2 min, Vector2 size)
    {
        return new((int)min.X, (int)min.Y, (int)size.X, (int)size.Y);
    }

    private unsafe void ClipAtkNodeRectangle(AtkUnitBase* addon, AtkResNode* node, AtkResNode* parent = null, float alpha = 0, bool overrideVisibility = false)
    {
        if (node == null || !overrideVisibility && !node->IsVisible()) return;
        int posX = (int)node->ScreenX;
        int posY = (int)node->ScreenY;

        int width = (int)(node->Width * addon->Scale * node->ScaleX * (parent == null ? 1 : parent->ScaleX));
        int height = (int)(node->Height * addon->Scale * node->ScaleY * (parent == null ? 1 : parent->ScaleY));
        _renderer!.AddClipRect(new(posX, posY, width, height), alpha);
    }
    private unsafe void ClipAtkNodeCircle(AtkUnitBase* addon, AtkResNode* node, AtkResNode* parent = null, float alpha = 0, bool overrideVisibility = false)
    {
        if (node == null || !overrideVisibility && !node->IsVisible()) return;
        float xRadius = 0.5f * node->Width * addon->Scale * node->ScaleX * (parent == null ? 1 : parent->ScaleX);
        float yRadius = 0.5f * node->Height * addon->Scale * node->ScaleY * (parent == null ? 1 : parent->ScaleY);
        Vector2 radius = new(xRadius, yRadius);
        Vector2 center = new Vector2(node->ScreenX, node->ScreenY) + radius;

        for (int i = 0; i < numCircleSegments; i++)
        {
            _renderer!.AddClipTri(center, center + circleOffsets[i] * radius, center + circleOffsets[i + 1] * radius, alpha);
        }
    }
}
