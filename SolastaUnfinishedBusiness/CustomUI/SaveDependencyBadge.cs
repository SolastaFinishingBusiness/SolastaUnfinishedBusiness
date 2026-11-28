using System;
using SolastaUnfinishedBusiness.Models;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.UI;
using Resources = SolastaUnfinishedBusiness.Properties.Resources;

namespace SolastaUnfinishedBusiness.CustomUI;

/// <summary>
///     Shows, on each line of the Save and Load screens, whether that save depends on the mod, as read from the
///     classification stamped in the save file's gzip header (<see cref="SaveStampContext.Read" />). Lines are pooled
///     and rebound by the game, so every bind sets the badge's state afresh.
/// </summary>
internal sealed class SaveDependencyBadge : MonoBehaviour
{
    private const string BadgeName = "FbSaveDependencyImage";
    internal const string RequiredTooltip = "UI/&SaveDependencyRequiredDescription";
    internal const string ClearTooltip = "UI/&SaveDependencyClearDescription";

    // The game's own Solasta icon (Addressables asset Gui/Bitmaps/Logos/solasta_icon, a 64 x 64 sprite of the
    // logo's ring around an "S"), loaded by its asset GUID from the game's catalog.
    private const string SolastaIconGuid = "cd6ad5bcd8e9be74bbb4eaeea9492cb0";

    // Loaded once and kept for the session: every badge shares it, so no line releases it on rebind.
    private static Sprite _solastaIcon;

    // Set after a failed load, so a missing asset is logged once rather than on every line bind.
    private static bool _solastaIconFailed;

    // Both 64 px images are drawn at this size and offset from the thumbnail's bottom-left corner.
    // SaveDependencyRequired.png is the mod's medallion scaled and centred inside its 64 px frame to lie over the
    // Solasta icon's circle, so the two badges show the same size and place without per-image positioning.
    private const float BadgeSize = 36f;
    private const float BadgeInset = 4f;

    private Image _image;
    private GuiTooltip _tooltip;

    // The text this component last put on the line's background tooltip, so the next bind can take it back off
    // without touching anything the game itself wrote there.
    private string _backgroundContent;

    internal static void Bind(LoadSaveLine line)
    {
        if (!line || line.SaveDescriptor == null)
        {
            return;
        }

        // Unity objects overload ==, so the explicit check is used rather than ??.
        var badge = line.GetComponent<SaveDependencyBadge>();

        if (!badge)
        {
            badge = line.gameObject.AddComponent<SaveDependencyBadge>();
        }

        badge.Refresh(line);
    }

    // Removes this component's text from the background tooltip before the game rebinds the line, as the game
    // only writes that tooltip when it has a warning to show and otherwise keeps whatever was there.
    internal static void Unbind(LoadSaveLine line)
    {
        var badge = line ? line.GetComponent<SaveDependencyBadge>() : null;

        if (badge)
        {
            badge.ReleaseBackgroundTooltip(line);
        }
    }

    private void Refresh(LoadSaveLine line)
    {
        ReleaseBackgroundTooltip(line);

        var service = ServiceRepository.GetService<IGameSerializationService>();
        var path = service?.BuildFilenameWithPathAndExtension(line.SaveDescriptor.Filename);
        var dependency = string.IsNullOrEmpty(path)
            ? SaveDependency.Unknown
            : SaveStampContext.Read(path);

        if (dependency == SaveDependency.Unknown)
        {
            // No icon and no tooltip when the save carries no classification (older builds, vanilla re-saves).
            if (_image)
            {
                _image.gameObject.SetActive(false);
            }

            return;
        }

        if (!_image && !CreateImage(line))
        {
            return;
        }

        var required = dependency == SaveDependency.Required;
        var content = required ? RequiredTooltip : ClearTooltip;

        var sprite = required
            ? Sprites.GetOrCreateSprite("SaveDependencyRequired", Resources.SaveDependencyRequired, 64)
            : SolastaIcon();

        if (!sprite)
        {
            // Without its icon the badge would show the copied Iron Man image, so it stays hidden instead.
            _image.gameObject.SetActive(false);

            return;
        }

        _image.sprite = sprite;
        _image.color = Color.white;
        _tooltip.Content = content;
        _image.gameObject.SetActive(true);

        // A gamepad moves the focus from line to line instead of hovering the small badge, and the line's
        // background tooltip is the one shown for the focused line. It is borrowed only while it is empty, so the
        // game's own warnings (missing DLC, save version, import recommendation) keep priority.
        var background = line.backgroundTooltip;

        if (Gui.GamepadActive && background && string.IsNullOrEmpty(background.Content))
        {
            background.Content = content;
            _backgroundContent = content;
        }
    }

    // The game's synchronous Addressables load throws when the key is missing or the load fails; that is caught
    // here so a missing icon only hides the "no known dependency" badge instead of breaking the line's bind.
    private static Sprite SolastaIcon()
    {
        if (_solastaIcon || _solastaIconFailed)
        {
            return _solastaIcon;
        }

        try
        {
            _solastaIcon = Gui.LoadAssetSync<Sprite>(new AssetReferenceSprite(SolastaIconGuid));
        }
        catch (Exception e)
        {
            _solastaIconFailed = true;
            Main.Error($"Save badge: the game's Solasta icon could not be loaded: {e.Message}");
        }

        return _solastaIcon;
    }

    private void ReleaseBackgroundTooltip(LoadSaveLine line)
    {
        var background = line.backgroundTooltip;

        if (_backgroundContent != null && background && background.Content == _backgroundContent)
        {
            background.Content = string.Empty;
        }

        _backgroundContent = null;
    }

    // Copies the line's Iron Man badge, which already has an image and a tooltip inside the thumbnail's mask, and
    // moves the copy to the thumbnail's bottom-left corner. The Iron Man badge keeps the bottom-right corner.
    private bool CreateImage(LoadSaveLine line)
    {
        var ironMan = line.ironManModeImage;

        if (!ironMan)
        {
            return false;
        }

        var parent = ironMan.transform.parent;
        var existing = parent.Find(BadgeName);
        var copy = existing ? existing.gameObject : Instantiate(ironMan.gameObject, parent, false);

        copy.name = BadgeName;

        var rect = (RectTransform)copy.transform;

        rect.anchorMin = Vector2.zero;
        rect.anchorMax = Vector2.zero;
        rect.pivot = Vector2.zero;
        rect.anchoredPosition = new Vector2(BadgeInset, BadgeInset);
        rect.sizeDelta = new Vector2(BadgeSize, BadgeSize);

        _image = copy.GetComponent<Image>();
        _tooltip = copy.GetComponent<GuiTooltip>();

        if (!_tooltip)
        {
            _tooltip = copy.AddComponent<GuiTooltip>();
        }

        // Keeps the badge's tooltip reachable by the mouse even where the badge overlaps the thumbnail.
        _image.raycastTarget = true;
        _image.preserveAspect = true;

        return _image;
    }
}
