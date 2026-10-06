#nullable enable
using System;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEngine;

namespace net.rs64.TexTransTool.TextureAtlas.Editor
{
    /// <summary>
    /// Reflection-only helper for adding the minimal Modular Avatar attachment metadata
    /// needed when a part is extracted from an avatar's own armature.
    /// </summary>
    internal sealed class ModularAvatarAdapter
    {
        private readonly Type _mergeArmatureType;
        private readonly Type _outfitRootType;
        private readonly Type _avatarObjectReferenceType;
        private readonly Type _armatureLockModeType;
        private readonly Type _boneProxyType;
        private readonly Type _boneProxyAttachmentModeType;

        private readonly FieldInfo _mergeTarget;
        private readonly FieldInfo _mergePrefix;
        private readonly FieldInfo _mergeSuffix;
        private readonly FieldInfo _mergeLockMode;
        private readonly FieldInfo _outfitArmatureRoot;
        private readonly FieldInfo _referencePath;
        private readonly FieldInfo _targetObject;
        private readonly FieldInfo _boneProxyBoneReference;
        private readonly FieldInfo _boneProxySubPath;
        private readonly FieldInfo _boneProxyAttachmentMode;
        private readonly FieldInfo _boneProxyMatchScale;
        private readonly MethodInfo? _resetArmatureLock;

        private ModularAvatarAdapter(
            Type mergeArmatureType,
            Type outfitRootType,
            Type avatarObjectReferenceType,
            Type armatureLockModeType,
            Type boneProxyType,
            Type boneProxyAttachmentModeType)
        {
            _mergeArmatureType = mergeArmatureType;
            _outfitRootType = outfitRootType;
            _avatarObjectReferenceType = avatarObjectReferenceType;
            _armatureLockModeType = armatureLockModeType;
            _boneProxyType = boneProxyType;
            _boneProxyAttachmentModeType = boneProxyAttachmentModeType;

            _mergeTarget = Field(_mergeArmatureType, "mergeTarget", _avatarObjectReferenceType);
            _mergePrefix = Field(_mergeArmatureType, "prefix", typeof(string));
            _mergeSuffix = Field(_mergeArmatureType, "suffix", typeof(string));
            _mergeLockMode = Field(_mergeArmatureType, "LockMode", _armatureLockModeType);
            _outfitArmatureRoot = Field(_outfitRootType, "armatureRoot", typeof(Transform));
            _referencePath = Field(_avatarObjectReferenceType, "referencePath", typeof(string));
            _targetObject = Field(_avatarObjectReferenceType, "targetObject", typeof(GameObject));
            _boneProxyBoneReference = Field(_boneProxyType, "boneReference", typeof(HumanBodyBones));
            _boneProxySubPath = Field(_boneProxyType, "subPath", typeof(string));
            _boneProxyAttachmentMode = Field(
                _boneProxyType,
                "attachmentMode",
                _boneProxyAttachmentModeType
            );
            _boneProxyMatchScale = Field(_boneProxyType, "matchScale", typeof(bool));
            _resetArmatureLock = _mergeArmatureType.GetMethod(
                "ResetArmatureLock",
                BindingFlags.NonPublic | BindingFlags.Instance
            );

            if (_armatureLockModeType.IsEnum is false
                || Enum.GetNames(_armatureLockModeType).Contains("BaseToMerge") is false)
            {
                throw new MissingMemberException(
                    _armatureLockModeType.FullName,
                    "BaseToMerge"
                );
            }

            if (_boneProxyAttachmentModeType.IsEnum is false
                || Enum.GetNames(_boneProxyAttachmentModeType).Contains("AsChildKeepWorldPose") is false)
            {
                throw new MissingMemberException(
                    _boneProxyAttachmentModeType.FullName,
                    "AsChildKeepWorldPose"
                );
            }
        }

        internal static bool TryCreate(out ModularAvatarAdapter? adapter, out string error)
        {
            try
            {
                var merge = TypeOf("nadena.dev.modular_avatar.core.ModularAvatarMergeArmature");
                var outfitRoot = TypeOf("nadena.dev.modular_avatar.core.ModularAvatarOutfitRoot");
                var objRef = TypeOf("nadena.dev.modular_avatar.core.AvatarObjectReference");
                var lockMode = TypeOf("nadena.dev.modular_avatar.core.ArmatureLockMode");
                var boneProxy = TypeOf("nadena.dev.modular_avatar.core.ModularAvatarBoneProxy");
                var attachmentMode = TypeOf("nadena.dev.modular_avatar.core.BoneProxyAttachmentMode");

                adapter = new ModularAvatarAdapter(
                    merge,
                    outfitRoot,
                    objRef,
                    lockMode,
                    boneProxy,
                    attachmentMode
                );
                error = "";
                return true;
            }
            catch (Exception e)
            {
                adapter = null;
                error = e.Message;
                return false;
            }
        }

        internal bool ConfigureArmature(
            GameObject partRoot,
            string armaturePath,
            out string error)
        {
            error = "";

            var armature = partRoot.transform.Find(armaturePath);
            if (armature == null)
            {
                error = "抽出後のArmatureが見つかりません: " + armaturePath;
                return false;
            }

            var existingMerge = armature.GetComponent(_mergeArmatureType);
            if (existingMerge != null)
            {
                error =
                    "既存のMA Merge Armature設定と競合するため、自動設定を行えませんでした。";
                return false;
            }

            var merge = armature.gameObject.AddComponent(_mergeArmatureType);
            if (merge == null)
            {
                error = "MA Merge Armatureを追加できませんでした。";
                return false;
            }

            var reference = _mergeTarget.GetValue(merge);
            if (reference == null)
            {
                reference = Activator.CreateInstance(_avatarObjectReferenceType);
                if (reference == null)
                {
                    error = "MA AvatarObjectReferenceを生成できませんでした。";
                    return false;
                }
                _mergeTarget.SetValue(merge, reference);
            }

            _targetObject.SetValue(reference, null);
            _referencePath.SetValue(reference, armaturePath);
            _mergePrefix.SetValue(merge, "");
            _mergeSuffix.SetValue(merge, "");
            _mergeLockMode.SetValue(
                merge,
                Enum.Parse(_armatureLockModeType, "BaseToMerge")
            );

            var outfitRoot = partRoot.GetComponent(_outfitRootType);
            if (outfitRoot == null)
            {
                outfitRoot = partRoot.AddComponent(_outfitRootType);
                if (outfitRoot == null)
                {
                    error = "MA Outfit Rootを追加できませんでした。";
                    UnityEngine.Object.DestroyImmediate(merge);
                    return false;
                }
            }
            else
            {
                var currentArmature = _outfitArmatureRoot.GetValue(outfitRoot) as Transform;
                if (currentArmature != null && currentArmature != armature)
                {
                    error =
                        "既存のMA Outfit Root設定と競合するため、自動設定を行えませんでした。";
                    UnityEngine.Object.DestroyImmediate(merge);
                    return false;
                }
            }

            _outfitArmatureRoot.SetValue(outfitRoot, armature);

            EditorUtility.SetDirty((UnityEngine.Object)merge);
            EditorUtility.SetDirty((UnityEngine.Object)outfitRoot);
            _resetArmatureLock?.Invoke(merge, null);
            return true;
        }

        internal bool ConfigureBoneProxy(
            GameObject partRoot,
            HumanBodyBones boneReference,
            string subPath,
            out bool added,
            out string error)
        {
            error = "";
            var existing = partRoot.GetComponent(_boneProxyType);
            if (existing != null)
            {
                // An existing Bone Proxy is part of the source prefab's authored attachment
                // semantics. Do not overwrite it with a heuristic reconstruction.
                added = false;
                return true;
            }

            added = true;
            var proxy = partRoot.AddComponent(_boneProxyType);
            if (proxy == null)
            {
                error = "MA Bone Proxyを追加できませんでした。";
                return false;
            }

            _boneProxyBoneReference.SetValue(proxy, boneReference);
            _boneProxySubPath.SetValue(proxy, subPath ?? "");
            _boneProxyAttachmentMode.SetValue(
                proxy,
                Enum.Parse(_boneProxyAttachmentModeType, "AsChildKeepWorldPose")
            );
            _boneProxyMatchScale.SetValue(proxy, false);
            EditorUtility.SetDirty((UnityEngine.Object)proxy);

            return true;
        }

        private static Type TypeOf(string fullName)
        {
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                var type = assembly.GetType(fullName, throwOnError: false);
                if (type != null) return type;
            }

            throw new TypeLoadException(fullName);
        }

        private static FieldInfo Field(Type type, string name, Type expectedType)
        {
            var field = type.GetField(
                name,
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance
            ) ?? throw new MissingFieldException(type.FullName, name);

            if (field.FieldType != expectedType)
            {
                throw new InvalidOperationException(
                    type.FullName + "." + name + " type mismatch: " + field.FieldType.FullName
                );
            }

            return field;
        }
    }
}
