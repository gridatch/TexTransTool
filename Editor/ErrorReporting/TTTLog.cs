using System;
using System.Collections.Generic;
using UnityEditor;
using System.Linq;
using net.rs64.TexTransCore;
using UnityEngine;


#if CONTAINS_NDMF
using nadena.dev.ndmf;
using UnityEngine.UIElements;
#endif

namespace net.rs64.TexTransTool
{

    internal static class TTTLog
    {
        [InitializeOnLoadMethod]
        static void RegisterCall()
        {
            TTLog.LogCall += Info;
            TTLog.WarningCall += Warning;
            TTLog.ErrorCall += Error;
            TTLog.ExceptionCall += Exception;
        }


        public static void Info(string code, params object[] objects)
        {
            objects = LocalizeStructuredArguments(objects);
#if CONTAINS_NDMF
            ErrorReport.ReportError(NDMFLocalizer, ErrorSeverity.Information, code, objects);
#else
            Debug.Log("[info]" + code + ":" + string.Join('-', objects.Select(i => i.ToString())));
#endif
        }

        internal static void CopyableInfo(
            string title,
            string message,
            string copyText,
            string buttonLabel)
        {
#if CONTAINS_NDMF
            ErrorReport.ReportError(new CopyableInfoError(
                title, message, copyText, buttonLabel));
#else
            Debug.Log("[info]" + title + "\n" + message);
#endif
        }
        internal static string FormatLocalizedLines(
            IEnumerable<FormattableString> localizedLines)
        {
            return string.Join("\n", localizedLines.Select(line =>
                string.Format(line.Format.GetLocalize(), line.GetArguments())));
        }

        private static object[] LocalizeStructuredArguments(object[] objects)
        {
            return objects.Select(obj =>
            {
                if (obj is IEnumerable<FormattableString> localizedLines)
                {
                    return (object)FormatLocalizedLines(localizedLines);
                }

                return obj;
            }).ToArray();
        }

        public static void Warning(string code, params object[] objects)
        {
#if CONTAINS_NDMF
            ErrorReport.ReportError(NDMFLocalizer, ErrorSeverity.NonFatal, code, objects);
#else
            Debug.LogWarning("[Warning]" + code + ":" + string.Join('-', objects.Select(i => i.ToString())));
#endif
        }
        public static void Error(string code, params object[] objects)
        {
#if CONTAINS_NDMF
            ErrorReport.ReportError(NDMFLocalizer, ErrorSeverity.Error, code, objects);
#else
            Debug.LogError("[Error]" + code + ":" + string.Join('-', objects.Select(i => i.ToString())));
#endif
        }
        public static void Exception(Exception e, string additionalStackTrace = "")
        {
#if CONTAINS_NDMF
            ErrorReport.ReportException(e, additionalStackTrace);
#else
            Debug.LogException(e);
            Debug.LogError(e.Message + "-AdditionalStackTrace:" + additionalStackTrace);
#endif
        }

        public static void ReportingObject(UnityEngine.Object obj, Action action)
        {
#if CONTAINS_NDMF
            ErrorReport.WithContextObject(obj, action);
#else
            try
            {
                    action.Invoke();
            }
            catch (Exception e)
            {
                    Debug.LogException(e);
                    throw e;
            }
#endif
        }

#if CONTAINS_NDMF
        private sealed class CopyableInfoError : SimpleError
        {
            private readonly string _title;
            private readonly string _message;
            private readonly string _copyText;
            private readonly string _buttonLabel;

            internal CopyableInfoError(
                string title,
                string message,
                string copyText,
                string buttonLabel)
            {
                _title = title;
                _message = message;
                _copyText = copyText;
                _buttonLabel = buttonLabel;
            }

            public override nadena.dev.ndmf.localization.Localizer Localizer =>
                NDMFLocalizer;
            public override string TitleKey => string.Empty;
            public override ErrorSeverity Severity => ErrorSeverity.Information;

            public override string FormatTitle() => _title;
            public override string FormatDetails() => _message;
            public override string FormatHint() => null;

            public override VisualElement CreateVisualElement(
                ErrorReport report)
            {
                var element = base.CreateVisualElement(report);
                var button = new Button(() =>
                    GUIUtility.systemCopyBuffer = _copyText)
                {
                    text = _buttonLabel,
                };

                var buttons = element.Q<VisualElement>(
                    "error-list-buttons");
                if (buttons != null)
                {
                    buttons.style.display = DisplayStyle.Flex;
                    buttons.Add(button);
                }
                else
                {
                    element.Add(button);
                }

                return element;
            }
        }

        private static nadena.dev.ndmf.localization.Localizer _ndmfLocalizer;
        public static nadena.dev.ndmf.localization.Localizer NDMFLocalizer
        {
            get
            {
                if (_ndmfLocalizer == null)
                {
                    Localize.LoadLocalize();
                    _ndmfLocalizer = new nadena.dev.ndmf.localization.Localizer("en-US", () => Localize.LocalizationAssets.Values.ToList());
                }
                return _ndmfLocalizer;
            }
        }
#endif
    }



}
