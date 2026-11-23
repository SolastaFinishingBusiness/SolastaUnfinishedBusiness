using System.Diagnostics.CodeAnalysis;
#if FB_DEVELOPMENT
using System.Globalization;
using System.IO;
#endif
using HarmonyLib;
using JetBrains.Annotations;
using SolastaUnfinishedBusiness.Api.GameExtensions;
using SolastaUnfinishedBusiness.Models;
using UnityEngine;

namespace SolastaUnfinishedBusiness.Patches;

[UsedImplicitly]
public static class GraphicsCharacterFactoryManagerPatcher
{
    [HarmonyPatch(typeof(GraphicsCharacterFactoryManager),
        nameof(GraphicsCharacterFactoryManager.InstantiateWieldedItemAsNeeded))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class InstantiateWieldedItemAsNeeded_Patch
    {
        [UsedImplicitly]
        public static void Postfix(GraphicsCharacter graphicsCharacter,
            RulesetItem rulesetItem,
            string slotName)
        {
            //PATCH: Support for custom scaling of equipped items.
            //Used to scale reach weapons and hand crossbows
            var feature = rulesetItem.itemDefinition?.GetFirstSubFeatureOfType<CustomScale>()
                          ?? rulesetItem.itemDefinition?.WeaponDescription?.WeaponTypeDefinition
                              .GetFirstSubFeatureOfType<CustomScale>();

            if (feature == null)
            {
                return;
            }

            var flag = rulesetItem.ItemDefinition.IsArmor &&
                       rulesetItem.ItemDefinition.ArmorDescription.ArmorType == "ShieldType";

            AnimationDefinitions.BoneType boneType;

            if (rulesetItem.ItemDefinition.IsWeapon)
            {
                boneType = slotName != EquipmentDefinitions.SlotTypeOffHand
                    ? rulesetItem.ItemDefinition.WeaponDescription.WeaponTypeDefinition.IsAttachedToBone
                    : AnimationDefinitions.BoneType.Prop2;
            }
            else if (flag)
            {
                boneType = AnimationDefinitions.BoneType.Shield;
            }
            else if (slotName == EquipmentDefinitions.SlotTypeMainHand)
            {
                boneType = AnimationDefinitions.BoneType.Prop1;
            }
            else if (slotName == EquipmentDefinitions.SlotTypeOffHand)
            {
                boneType = AnimationDefinitions.BoneType.Prop2;
            }
            else
            {
                return;
            }


            var boneTransform = graphicsCharacter.GetBoneTransform(boneType);

            if (!boneTransform)
            {
                return;
            }

            var transform = boneTransform.Find(rulesetItem.Name);

            if (!transform)
            {
                return;
            }

            var scale = transform.localScale;

            scale.x *= feature.X;
            scale.y *= feature.Y;
            scale.z *= feature.Z;
            transform.localScale = scale;

            if (feature.GripAlongLength is { } grip)
            {
                MoveGripAlongLength(transform, GripForTuning(grip));
            }
        }

        /// <summary>
        ///     Slides a wielded model along its local z axis (its length) so that the hand, which holds the model at its
        ///     origin, ends up at <paramref name="grip" /> of the model's length: 0 is the end nearest the original
        ///     origin, 1 the far end. The length comes from the bounds of every mesh under the model, read in the
        ///     model's unscaled local space, so it fits whichever model the item shows. Runs once per instantiated
        ///     model: the game makes a new one each time the item is put in a hand.
        /// </summary>
        private static void MoveGripAlongLength(Transform item, float grip)
        {
            var near = float.PositiveInfinity;
            var far = float.NegativeInfinity;

            foreach (var filter in item.GetComponentsInChildren<MeshFilter>(true))
            {
                AddMeshExtent(item, filter.transform, filter.sharedMesh, ref near, ref far);
            }

            foreach (var skinned in item.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                AddMeshExtent(item, skinned.transform, skinned.sharedMesh, ref near, ref far);
            }

            // no mesh found: leave the model where the game put it
            if (near > far)
            {
                return;
            }

            // near and far hold min and max z here; the end closer to the origin is the one the hand held
            if (Mathf.Abs(far) < Mathf.Abs(near))
            {
                (near, far) = (far, near);
            }

            var target = near + (grip * (far - near));

            // the point at z = target (unscaled) is moved to where the model's origin was, i.e. into the hand
            item.localPosition -= item.localRotation * Vector3.Scale(item.localScale, new Vector3(0f, 0f, target));
        }

        /// <summary>Widens [min, max] to the z extent of a mesh's bounds, expressed in the model's local space.</summary>
        private static void AddMeshExtent(Transform item, Transform owner, Mesh mesh, ref float min, ref float max)
        {
            if (!mesh)
            {
                return;
            }

            var bounds = mesh.bounds;

            for (var corner = 0; corner < 8; corner++)
            {
                var point = new Vector3(
                    (corner & 1) == 0 ? bounds.min.x : bounds.max.x,
                    (corner & 2) == 0 ? bounds.min.y : bounds.max.y,
                    (corner & 4) == 0 ? bounds.min.z : bounds.max.z);
                var z = item.InverseTransformPoint(owner.TransformPoint(point)).z;

                min = Mathf.Min(min, z);
                max = Mathf.Max(max, z);
            }
        }

        /// <summary>
        ///     Development builds only: a number in <c>WieldedGripOverride.txt</c> in the mod's folder replaces every
        ///     grip fraction, read each time a model is put in a hand, so the grip can be tuned in game by editing the
        ///     file and re-equipping the weapon. Release builds always use the item's own value.
        /// </summary>
        private static float GripForTuning(float grip)
        {
#if FB_DEVELOPMENT
            try
            {
                var path = Path.Combine(Main.ModFolder, "WieldedGripOverride.txt");

                if (File.Exists(path) &&
                    float.TryParse(File.ReadAllText(path).Trim(), NumberStyles.Float, CultureInfo.InvariantCulture,
                        out var tuned))
                {
                    return tuned;
                }
            }
            catch (IOException)
            {
                // a file being written while read: keep the item's value
            }
#endif
            return grip;
        }
    }

#if false
    [HarmonyPatch(typeof(GraphicsCharacterFactoryManager),
        nameof(GraphicsCharacterFactoryManager.CollectBodyPartsToLoadWherePossible_Morphotypes))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class CollectBodyPartsToLoadWherePossible_Morphotypes_Patch
    {
        [UsedImplicitly]
        public static void Postfix(GraphicsCharacterFactoryManager __instance)
        {
            //PATCH: support for horns on all races
            var searchTermFemale = "_Female_" + MorphotypeElementDefinition.ElementCategory.Horns;
            var searchTermMale = "_Male_" + MorphotypeElementDefinition.ElementCategory.Horns;

            for (var i = 0; i < __instance.shapePartsToLoad.Length; i++)
            {
                var pos = __instance.shapePartsToLoad[i].IndexOf(searchTermFemale, StringComparison.InvariantCulture);

                if (pos > 0)
                {
                    var raceName = __instance.shapePartsToLoad[i].Substring(0, pos);
                    var newPartName = __instance.shapePartsToLoad[i].Replace(raceName, "Dragonborn");

                    __instance.shapePartsToLoad[i] = newPartName;
                }

                pos = __instance.shapePartsToLoad[i].IndexOf(searchTermMale, StringComparison.InvariantCulture);

                // ReSharper disable once InvertIf
                if (pos > 0)
                {
                    var raceName = __instance.shapePartsToLoad[i].Substring(0, pos);
                    var newPartName = __instance.shapePartsToLoad[i].Replace(raceName, "Dragonborn");

                    __instance.shapePartsToLoad[i] = newPartName;
                }
            }
        }
    }
#endif
}
