using System;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace Game.Framework.Editor
{
    /// <summary>SSFramework EditorWindow 共用的中性色板与少量 UI Toolkit 视觉原语。</summary>
    /// <remarks>
    /// 这里只收口审计与构建证据窗口已经重复的卡片、按钮、标题和响应式指标；业务状态、
    /// Foldout 结构与窗口生命周期仍由各自 Module 拥有，避免演变成参数化的万能窗口构建器。
    /// </remarks>
    internal static class FrameworkEditorVisuals
    {
        internal const float CompactWidth = 620f;

        internal enum Tone
        {
            Neutral,
            Active,
            Healthy,
            Warning,
            Error,
        }

        internal static void ApplyWindowSurface(VisualElement root)
        {
            if (root == null) throw new ArgumentNullException(nameof(root));
            root.style.flexDirection = FlexDirection.Column;
            root.style.backgroundColor = WindowBackground;
        }

        internal static VisualElement CreateHero(
            string name,
            string eyebrow,
            string title,
            string description,
            Tone tone = Tone.Active)
        {
            var hero = new VisualElement
            {
                name = name,
                style =
                {
                    flexShrink = 0,
                    paddingLeft = 14,
                    paddingRight = 14,
                    paddingTop = 11,
                    paddingBottom = 10,
                    backgroundColor = HeroBackground,
                    borderBottomWidth = 1,
                    borderBottomColor = ToneColor(tone),
                },
            };

            if (!string.IsNullOrWhiteSpace(eyebrow))
            {
                var eyebrowLabel = Wrap(new Label(eyebrow));
                eyebrowLabel.style.fontSize = 10;
                eyebrowLabel.style.letterSpacing = 0.6f;
                eyebrowLabel.style.unityFontStyleAndWeight = FontStyle.Bold;
                eyebrowLabel.style.color = ToneTextColor(tone);
                eyebrowLabel.style.marginBottom = 3;
                hero.Add(eyebrowLabel);
            }

            var titleLabel = Wrap(new Label(title));
            titleLabel.style.fontSize = 20;
            titleLabel.style.unityFontStyleAndWeight = FontStyle.Bold;
            hero.Add(titleLabel);

            if (!string.IsNullOrWhiteSpace(description))
            {
                var descriptionLabel = Wrap(new Label(description));
                descriptionLabel.style.marginTop = 4;
                descriptionLabel.style.color = MutedTextColor;
                hero.Add(descriptionLabel);
            }
            return hero;
        }

        internal static VisualElement CreateCard(string name, Tone tone = Tone.Neutral)
        {
            var card = new VisualElement
            {
                name = name,
                style =
                {
                    flexShrink = 0,
                    marginTop = 3,
                    marginBottom = 5,
                    paddingLeft = 10,
                    paddingRight = 10,
                    paddingTop = 9,
                    paddingBottom = 9,
                    backgroundColor = CardBackground,
                    borderLeftWidth = tone == Tone.Neutral ? 1 : 4,
                    borderRightWidth = 1,
                    borderTopWidth = 1,
                    borderBottomWidth = 1,
                    borderLeftColor = tone == Tone.Neutral ? BorderColor : ToneColor(tone),
                    borderRightColor = BorderColor,
                    borderTopColor = BorderColor,
                    borderBottomColor = BorderColor,
                    borderTopLeftRadius = 6,
                    borderTopRightRadius = 6,
                    borderBottomLeftRadius = 6,
                    borderBottomRightRadius = 6,
                },
            };
            return card;
        }

        internal static Label CreateSectionTitle(string text)
        {
            var label = Wrap(new Label(text));
            label.style.fontSize = 15;
            label.style.unityFontStyleAndWeight = FontStyle.Bold;
            label.style.marginTop = 11;
            label.style.marginBottom = 5;
            label.style.paddingLeft = 7;
            label.style.borderLeftWidth = 3;
            label.style.borderLeftColor = ActiveColor;
            return label;
        }

        internal static Label CreateCardTitle(string text)
        {
            var label = Wrap(new Label(text));
            label.style.unityFontStyleAndWeight = FontStyle.Bold;
            label.style.marginBottom = 3;
            return label;
        }

        internal static Label CreateBullet(string text)
        {
            var label = Wrap(new Label("• " + text));
            label.style.marginTop = 3;
            label.style.marginBottom = 3;
            return label;
        }

        internal static Label CreateMutedLabel(string text)
        {
            var label = Wrap(new Label(text));
            label.style.color = MutedTextColor;
            label.style.marginTop = 4;
            label.style.marginBottom = 4;
            return label;
        }

        internal static Button CreateActionButton(
            string text,
            Action action,
            string tooltip,
            string name = null,
            bool primary = false)
        {
            var button = new Button(action)
            {
                text = text,
                tooltip = tooltip,
                name = name,
                style =
                {
                    flexGrow = 1,
                    flexBasis = 0,
                    minHeight = 28,
                    marginLeft = 2,
                    marginRight = 2,
                    marginTop = 2,
                    marginBottom = 2,
                    unityFontStyleAndWeight = primary ? FontStyle.Bold : FontStyle.Normal,
                },
            };
            if (primary)
            {
                button.style.backgroundColor = PrimaryButtonBackground;
                button.style.color = Color.white;
                button.style.borderLeftColor = ActiveColor;
                button.style.borderRightColor = ActiveColor;
                button.style.borderTopColor = ActiveColor;
                button.style.borderBottomColor = ActiveColor;
            }
            return button;
        }

        internal static VisualElement CreateMetric(string name, string caption, string value, string note)
        {
            var metric = new VisualElement
            {
                name = name,
                style =
                {
                    flexGrow = 1,
                    flexBasis = 0,
                    minWidth = 0,
                    marginLeft = 2,
                    marginRight = 2,
                    marginTop = 2,
                    marginBottom = 2,
                    paddingLeft = 8,
                    paddingRight = 8,
                    paddingTop = 6,
                    paddingBottom = 6,
                    backgroundColor = DetailBackground,
                    borderTopLeftRadius = 4,
                    borderTopRightRadius = 4,
                    borderBottomLeftRadius = 4,
                    borderBottomRightRadius = 4,
                },
            };
            var captionLabel = Wrap(new Label(caption));
            captionLabel.style.fontSize = 11;
            captionLabel.style.color = MutedTextColor;
            metric.Add(captionLabel);

            var valueLabel = Wrap(new Label(value));
            valueLabel.style.fontSize = 15;
            valueLabel.style.unityFontStyleAndWeight = FontStyle.Bold;
            valueLabel.style.marginTop = 1;
            metric.Add(valueLabel);

            var noteLabel = Wrap(new Label(note));
            noteLabel.style.fontSize = 10;
            noteLabel.style.color = MutedTextColor;
            metric.Add(noteLabel);
            return metric;
        }

        internal static Label Wrap(Label label)
        {
            label.style.whiteSpace = WhiteSpace.Normal;
            label.style.flexShrink = 1;
            return label;
        }

        internal static void ApplyResponsiveChildren(VisualElement parent, bool compact)
        {
            if (parent == null) return;
            foreach (VisualElement child in parent.Children())
            {
                child.style.flexBasis = compact ? StyleKeyword.Auto : 0;
                child.style.flexGrow = compact ? 0 : 1;
            }
        }

        internal static Color ToneColor(Tone tone) => tone switch
        {
            Tone.Active => ActiveColor,
            Tone.Healthy => HealthyColor,
            Tone.Warning => WarningColor,
            Tone.Error => ErrorColor,
            _ => BorderColor,
        };

        internal static Color ToneTextColor(Tone tone) => tone switch
        {
            Tone.Active => ActiveTextColor,
            Tone.Healthy => HealthyTextColor,
            Tone.Warning => WarningTextColor,
            Tone.Error => ErrorTextColor,
            _ => MutedTextColor,
        };

        internal static Color WindowBackground => EditorGUIUtility.isProSkin
            ? new Color(0.105f, 0.11f, 0.12f, 1f)
            : new Color(0.86f, 0.87f, 0.89f, 1f);

        internal static Color HeroBackground => EditorGUIUtility.isProSkin
            ? new Color(0.12f, 0.145f, 0.18f, 1f)
            : new Color(0.91f, 0.94f, 0.98f, 1f);

        internal static Color CardBackground => EditorGUIUtility.isProSkin
            ? new Color(0.155f, 0.165f, 0.18f, 1f)
            : new Color(0.97f, 0.975f, 0.985f, 1f);

        internal static Color DetailBackground => EditorGUIUtility.isProSkin
            ? new Color(0.115f, 0.12f, 0.135f, 1f)
            : new Color(0.90f, 0.915f, 0.94f, 1f);

        internal static Color BorderColor => EditorGUIUtility.isProSkin
            ? new Color(0.29f, 0.31f, 0.34f, 1f)
            : new Color(0.64f, 0.67f, 0.72f, 1f);

        internal static Color MutedTextColor => EditorGUIUtility.isProSkin
            ? new Color(0.72f, 0.74f, 0.78f, 1f)
            : new Color(0.24f, 0.27f, 0.31f, 1f);

        internal static Color ActiveColor => EditorGUIUtility.isProSkin
            ? new Color(0.18f, 0.55f, 0.88f, 1f)
            : new Color(0.06f, 0.38f, 0.72f, 1f);

        internal static Color ActiveTextColor => EditorGUIUtility.isProSkin
            ? new Color(0.48f, 0.78f, 1f, 1f)
            : new Color(0.03f, 0.30f, 0.64f, 1f);

        internal static Color HealthyColor => EditorGUIUtility.isProSkin
            ? new Color(0.18f, 0.56f, 0.32f, 1f)
            : new Color(0.10f, 0.46f, 0.22f, 1f);

        internal static Color HealthyTextColor => EditorGUIUtility.isProSkin
            ? new Color(0.42f, 0.88f, 0.58f, 1f)
            : new Color(0.04f, 0.34f, 0.14f, 1f);

        internal static Color WarningColor => EditorGUIUtility.isProSkin
            ? new Color(0.86f, 0.52f, 0.15f, 1f)
            : new Color(0.72f, 0.36f, 0.04f, 1f);

        internal static Color WarningTextColor => EditorGUIUtility.isProSkin
            ? new Color(1f, 0.70f, 0.30f, 1f)
            : new Color(0.58f, 0.23f, 0.01f, 1f);

        internal static Color ErrorColor => EditorGUIUtility.isProSkin
            ? new Color(0.78f, 0.27f, 0.25f, 1f)
            : new Color(0.72f, 0.12f, 0.10f, 1f);

        internal static Color ErrorTextColor => EditorGUIUtility.isProSkin
            ? new Color(1f, 0.50f, 0.46f, 1f)
            : new Color(0.62f, 0.05f, 0.05f, 1f);

        private static Color PrimaryButtonBackground => EditorGUIUtility.isProSkin
            ? new Color(0.10f, 0.36f, 0.62f, 1f)
            : new Color(0.08f, 0.42f, 0.76f, 1f);
    }
}
