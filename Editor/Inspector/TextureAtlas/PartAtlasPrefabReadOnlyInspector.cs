#nullable enable
using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace net.rs64.TexTransTool.TextureAtlas.Editor
{
    /// <summary>
    /// Inspection-only view of the transient Prefab contents loaded by the window.
    /// No custom Editors / PropertyDrawers are invoked and no serialized changes are
    /// applied. Only foldout and scroll states are mutable.
    /// </summary>
    internal sealed class PartAtlasPrefabReadOnlyInspector
    {
        private const int MaxVisiblePropertiesPerComponent = 800;
        private readonly Dictionary<int, bool> _componentExpanded = new();
        private readonly Dictionary<string, bool> _propertyExpanded = new(StringComparer.Ordinal);
        private Transform? _selected;
        private Transform? _root;
        private Vector2 _scroll;

        internal void SetSelection(Transform? selected, Transform? root)
        {
            if (_selected == selected && _root == root)
                return;

            _selected = selected;
            _root = root;
            _scroll = Vector2.zero;
            _componentExpanded.Clear();
            _propertyExpanded.Clear();
        }

        internal void Draw(Rect rect)
        {
            GUI.Box(rect, GUIContent.none, EditorStyles.helpBox);
            if (rect.width < 30f || rect.height < 35f)
                return;

            var area = new Rect(rect.x + 5f, rect.y + 5f,
                Mathf.Max(0f, rect.width - 10f), Mathf.Max(0f, rect.height - 10f));
            GUILayout.BeginArea(area);
            try
            {
                EditorGUILayout.LabelField("Inspector（読み取り専用）", EditorStyles.boldLabel);

                using (var scroll = new EditorGUILayout.ScrollViewScope(_scroll))
                {
                    _scroll = scroll.scrollPosition;
                    if (_selected != null)
                        DrawSelectedObject(_selected.gameObject);
                }
            }
            finally
            {
                GUILayout.EndArea();
            }
        }

        private void DrawSelectedObject(GameObject gameObject)
        {
            EditorGUILayout.Space(4f);
            EditorGUILayout.LabelField(gameObject.name, EditorStyles.boldLabel);
            ReadOnlyRow("Path", RelativePath(gameObject.transform));
            ReadOnlyRow("Active Self", gameObject.activeSelf.ToString());
            ReadOnlyRow("Active in Hierarchy", gameObject.activeInHierarchy.ToString());
            ReadOnlyRow("Layer", LayerMask.LayerToName(gameObject.layer)
                + " (" + gameObject.layer + ")");
            ReadOnlyRow("Tag", SafeTag(gameObject));
            ReadOnlyRow("Static", gameObject.isStatic.ToString());

            EditorGUILayout.Space(8f);
            EditorGUILayout.LabelField("Transform", EditorStyles.boldLabel);
            var transform = gameObject.transform;
            ReadOnlyRow("Local Position", FormatVector3(transform.localPosition));
            ReadOnlyRow("Local Rotation", FormatVector3(transform.localEulerAngles));
            ReadOnlyRow("Local Scale", FormatVector3(transform.localScale));
            ReadOnlyRow("Parent", transform.parent == null
                ? "(none)"
                : RelativePath(transform.parent));

            var components = gameObject.GetComponents<Component>();
            for (var index = 0; index < components.Length; index++)
            {
                var component = components[index];
                if (component == null)
                {
                    EditorGUILayout.HelpBox("Missing Script", MessageType.Warning);
                    continue;
                }

                // RectTransform has additional serialized fields beyond the
                // common Transform values displayed above.
                if (component is Transform && component is not RectTransform)
                    continue;

                DrawComponent(component, index);
            }
        }

        private void DrawComponent(Component component, int index)
        {
            EditorGUILayout.Space(6f);

            var title = component.GetType().Name;
            if (component is Behaviour behaviour)
                title += behaviour.enabled ? " (Enabled)" : " (Disabled)";

            var open = _componentExpanded.TryGetValue(index, out var previous) ? previous : true;
            open = EditorGUILayout.Foldout(open, title, true);
            _componentExpanded[index] = open;
            if (!open)
                return;

            // Do not instantiate a custom Editor, draw a PropertyField, or call
            // ApplyModifiedProperties. All values are rendered as plain text.
            try
            {
                var serialized = new SerializedObject(component);
                serialized.UpdateIfRequiredOrScript();
                var iterator = serialized.GetIterator();
                var enterChildren = true;
                var visited = 0;

                while (iterator.NextVisible(enterChildren))
                {
                    if (visited++ >= MaxVisiblePropertiesPerComponent)
                    {
                        EditorGUILayout.LabelField("…（表示項目数の上限）",
                            EditorStyles.miniLabel);
                        break;
                    }

                    var property = iterator.Copy();
                    var isGroup = property.hasVisibleChildren
                        && (property.propertyType == SerializedPropertyType.Generic
                            || property.propertyType == SerializedPropertyType.ManagedReference);

                    // Use serialized property paths for expansion state so
                    // nested objects and list elements remain independently expandable.
                    if (isGroup)
                    {
                        var key = index + ":" + property.propertyPath;
                        var expanded = _propertyExpanded.TryGetValue(key, out var saved) && saved;
                        var label = property.displayName;
                        if (property.isArray)
                            label += " (" + property.arraySize + ")";
                        if (property.propertyType == SerializedPropertyType.ManagedReference)
                            label += " (" + property.managedReferenceFullTypename + ")";

                        using (new EditorGUI.IndentLevelScope(Mathf.Min(property.depth, 5)))
                            expanded = EditorGUILayout.Foldout(expanded, label, true);
                        _propertyExpanded[key] = expanded;
                        enterChildren = expanded;
                    }
                    else
                    {
                        var value = ReadPropertyValue(property);
                        ReadOnlyRow(property.displayName, value, property.tooltip, property.depth);
                        enterChildren = false;
                    }
                }
            }
            catch (Exception exception)
            {
                // A component with malformed/unavailable serialization must not
                // interfere with the source Prefab or the extraction workflow.
                EditorGUILayout.LabelField("シリアライズ情報を読み取れません: "
                    + exception.Message, EditorStyles.wordWrappedMiniLabel);
            }
        }

        private string ReadPropertyValue(SerializedProperty property)
        {
            try
            {
                switch (property.propertyType)
                {
                    case SerializedPropertyType.Integer:
                        return property.longValue.ToString();
                    case SerializedPropertyType.Boolean:
                        return property.boolValue.ToString();
                    case SerializedPropertyType.Float:
                        return property.doubleValue.ToString("G");
                    case SerializedPropertyType.String:
                        return property.stringValue;
                    case SerializedPropertyType.Color:
                        return property.colorValue.ToString();
                    case SerializedPropertyType.ObjectReference:
                        return FormatObjectReference(property.objectReferenceValue);
                    case SerializedPropertyType.ExposedReference:
                        return FormatObjectReference(property.exposedReferenceValue);
                    case SerializedPropertyType.Enum:
                    {
                        var names = property.enumDisplayNames;
                        return property.enumValueIndex >= 0 && property.enumValueIndex < names.Length
                            ? names[property.enumValueIndex]
                            : property.intValue.ToString();
                    }
                    case SerializedPropertyType.Vector2:
                        return property.vector2Value.ToString("F4");
                    case SerializedPropertyType.Vector3:
                        return FormatVector3(property.vector3Value);
                    case SerializedPropertyType.Vector4:
                        return property.vector4Value.ToString("F4");
                    case SerializedPropertyType.Quaternion:
                        return property.quaternionValue.ToString("F4");
                    case SerializedPropertyType.Vector2Int:
                        return property.vector2IntValue.ToString();
                    case SerializedPropertyType.Vector3Int:
                        return property.vector3IntValue.ToString();
                    case SerializedPropertyType.Rect:
                        return property.rectValue.ToString();
                    case SerializedPropertyType.RectInt:
                        return property.rectIntValue.ToString();
                    case SerializedPropertyType.Bounds:
                        return property.boundsValue.ToString();
                    case SerializedPropertyType.BoundsInt:
                        return property.boundsIntValue.ToString();
                    case SerializedPropertyType.LayerMask:
                    case SerializedPropertyType.ArraySize:
                    case SerializedPropertyType.FixedBufferSize:
                        return property.intValue.ToString();
                    case SerializedPropertyType.Character:
                        return ((char)property.intValue).ToString();
                    case SerializedPropertyType.AnimationCurve:
                        return property.animationCurveValue == null
                            ? "(none)"
                            : property.animationCurveValue.length + " keys";
                    case SerializedPropertyType.Gradient:
                        return property.gradientValue == null
                            ? "(none)"
                            : property.gradientValue.colorKeys.Length + " color keys";
                    case SerializedPropertyType.Hash128:
                        return property.hash128Value.ToString();
                    case SerializedPropertyType.ManagedReference:
                        return string.IsNullOrEmpty(property.managedReferenceFullTypename)
                            ? "(null)"
                            : property.managedReferenceFullTypename;
                    default:
                        return "(" + property.type + ")";
                }
            }
            catch (Exception)
            {
                return "(unavailable)";
            }
        }

        private string FormatObjectReference(UnityEngine.Object? reference)
        {
            if (reference == null)
                return "None";

            if (reference is GameObject gameObject)
                return gameObject.name + " [GameObject] " + RelativePath(gameObject.transform);

            if (reference is Component component)
                return component.name + " [" + component.GetType().Name + "] "
                    + RelativePath(component.transform);

            var assetPath = AssetDatabase.GetAssetPath(reference);
            return reference.name + " [" + reference.GetType().Name + "]"
                + (string.IsNullOrEmpty(assetPath) ? "" : " " + assetPath);
        }

        private string RelativePath(Transform transform)
        {
            if (_root == null)
                return transform.name;

            if (transform == _root)
                return _root.name;

            if (!transform.IsChildOf(_root))
                return transform.name + " (outside Prefab)";

            var segments = new List<string>();
            var current = transform;
            while (current != null && current != _root)
            {
                segments.Add(current.name);
                current = current.parent;
            }

            segments.Reverse();
            return _root.name + "/" + string.Join("/", segments);
        }

        private static string SafeTag(GameObject gameObject)
        {
            try { return gameObject.tag; }
            catch (UnityException) { return "(unavailable)"; }
        }

        private static string FormatVector3(Vector3 vector) =>
            "(" + vector.x.ToString("G9") + ", "
                + vector.y.ToString("G9") + ", "
                + vector.z.ToString("G9") + ")";

        private static void ReadOnlyRow(
            string title, string value, string tooltip = "", int depth = 0)
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                // GUILayout labels do not respect EditorGUI.indentLevel.
                // Apply explicit indentation for nested serialized fields.
                var indent = Mathf.Min(depth, 5) * 12f;
                if (indent > 0f)
                    GUILayout.Space(indent);

                GUILayout.Label(new GUIContent(title + ":", tooltip), EditorStyles.miniLabel,
                    GUILayout.Width(Mathf.Max(65f, 120f - indent)));
                GUILayout.Label(string.IsNullOrEmpty(value) ? "(empty)" : value,
                    EditorStyles.wordWrappedMiniLabel, GUILayout.ExpandWidth(true));
            }
        }
    }
}
