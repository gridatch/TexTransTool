#nullable enable
using System;
using System.Linq;
using System.Reflection;
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

        private readonly FieldInfo _mergeTarget;
        private readonly FieldInfo _mergePrefix;
        private readonly FieldInfo _mergeSuffix;
        private readonly FieldInfo _mergeLockMode;
        private readonly FieldInfo _outfitArmatureRoot;
        private readonly FieldInfo _referencePath;
        private readonly FieldInfo _targetObject;

        private ModularAvatarAdapter(
            Type mergeArmatureType,
            Type outfitRootType,
            Type avatarObjectReferenceType,
            Type armatureLockModeType)
        {
            _mergeArmatureType = mergeArmatureType;
            _outfitRootType = outfitRootType;
            _avatarObjectReferenceType = avatarObjectReferenceType;
            _armatureLockModeType = armatureLockModeType;

            _mergeTarget = Field(_mergeArmatureType, "mergeTarget", _avatarObjectReferenceType);
            _mergePrefix = Field(_mergeArmatureType, "prefix", typeof(string));
            _mergeSuffix = Field(_mergeArmatureType, "suffix", typeof(string));
            _mergeLockMode = Field(_mergeArmatureType, "LockMode", _armatureLockModeType);
            _outfitArmatureRoot = Field(_outfitRootType, "armatureRoot", typeof(Transform));
            _referencePath = Field(_avatarObjectReferenceType, "referencePath", typeof(string));
            _targetObject = Field(_avatarObjectReferenceType, "targetObject", typeof(GameObject));

            if (_armatureLockModeType.IsEnum is false
                || Enum.GetNames(_armatureLockModeType).Contains("BaseToMerge") is false)
            {
                throw new MissingMemberException(
                    _armatureLockModeType.FullName,
                    "BaseToMerge"
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

                adapter = new ModularAvatarAdapter(merge, outfitRoot, objRef, lockMode);
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

            var merge = armature.GetComponent(_mergeArmatureType)
                ?? armature.gameObject.AddComponent(_mergeArmatureType);

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

            var outfitRoot = partRoot.GetComponent(_outfitRootType)
                ?? partRoot.AddComponent(_outfitRootType);
            _outfitArmatureRoot.SetValue(outfitRoot, armature);

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
