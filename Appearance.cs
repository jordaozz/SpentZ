using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace SpentZ
{
    internal sealed partial class Program
    {
        private sealed class AccentChoice
        {
            internal readonly string Name;
            internal readonly string Tone;
            internal AccentChoice(string name, string tone) { Name = name; Tone = tone; }
        }

        private static readonly AccentChoice[] AccentChoices = {
            new AccentChoice("Verde", "#197459"),
            new AccentChoice("Azul", "#2663C4"),
            new AccentChoice("Vermelho", "#AF3549"),
            new AccentChoice("Laranja", "#AA5318"),
            new AccentChoice("Rosa", "#A6417B"),
            new AccentChoice("Roxo", "#704CB5"),
            new AccentChoice("Turquesa", "#117782")
        };

        private static readonly int[] ReminderChoices = { 0, 3, 7, 15, 30 };
        private readonly Dictionary<string, RadioButton> paletteButtons = new Dictionary<string, RadioButton>();
        private bool loadingSettings;
        private bool dark;
        private AccentChoice accent;
        private int reminderDays = 7;

        private void WireSettings()
        {
            Click("ThemeToggle", delegate { SetDark(!dark); });
            Click("ResetRecords", ConfirmResetRecords);
            Get<CheckBox>("SettingsDark").Checked += delegate { if (!loadingSettings) Safely(delegate { SetDark(true); }); };
            Get<CheckBox>("SettingsDark").Unchecked += delegate { if (!loadingSettings) Safely(delegate { SetDark(false); }); };
            WrapPanel choices = Get<WrapPanel>("PaletteChoices");
            foreach (AccentChoice choice in AccentChoices)
            {
                StackPanel label = new StackPanel { Orientation = Orientation.Horizontal };
                label.Children.Add(new Ellipse {
                    Width = 13, Height = 13, Fill = Solid(ToColor(choice.Tone)),
                    Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center
                });
                label.Children.Add(new TextBlock { Text = choice.Name });
                RadioButton button = new RadioButton {
                    Content = label, GroupName = "AccentColor", Margin = new Thickness(0, 0, 16, 10)
                };
                button.SetResourceReference(Control.ForegroundProperty, "Ink");
                button.Checked += delegate
                {
                    if (loadingSettings) return;
                    Safely(delegate {
                        store.SetPreference("accent", choice.Name);
                        accent = choice;
                        ApplyTheme();
                    });
                };
                paletteButtons.Add(choice.Name, button);
                choices.Children.Add(button);
            }
            ComboBox days = Get<ComboBox>("ReminderDays");
            foreach (int threshold in ReminderChoices)
                days.Items.Add(threshold == 0 ? "No dia da cobrança" : threshold + " dias antes");
            days.SelectionChanged += delegate
            {
                if (loadingSettings || days.SelectedIndex < 0) return;
                Safely(delegate {
                    reminderDays = ReminderChoices[days.SelectedIndex];
                    store.SetPreference("reminder", reminderDays.ToString());
                    RefreshBillViews();
                });
            };
        }

        private void LoadSettings()
        {
            dark = store.GetPreference("dark") == "1";
            string name = store.GetPreference("accent");
            accent = Array.Find(AccentChoices, item => item.Name == name) ?? AccentChoices[0];
            int savedDays;
            reminderDays = int.TryParse(store.GetPreference("reminder"), out savedDays) &&
                Array.IndexOf(ReminderChoices, savedDays) >= 0 ? savedDays : 7;
            loadingSettings = true;
            Get<CheckBox>("SettingsDark").IsChecked = dark;
            paletteButtons[accent.Name].IsChecked = true;
            Get<ComboBox>("ReminderDays").SelectedIndex = Array.IndexOf(ReminderChoices, reminderDays);
            loadingSettings = false;
            ApplyTheme();
        }

        private void SetDark(bool value)
        {
            store.SetPreference("dark", value ? "1" : "0");
            dark = value;
            loadingSettings = true;
            Get<CheckBox>("SettingsDark").IsChecked = dark;
            loadingSettings = false;
            ApplyTheme();
        }

        private void ApplyTheme()
        {
            Color seed = ToColor(accent.Tone);
            Color surface = ToColor(dark ? "#202631" : "#FFFFFF");
            Color canvas = ToColor(dark ? "#141922" : "#F5F6FA");
            Color ink = ToColor(dark ? "#E9EDF4" : "#18263A");
            Color muted = ToColor(dark ? "#B7C2D1" : "#536278");
            Color border = ToColor(dark ? "#3A4556" : "#E1E6ED");
            Color strong = Mix(seed, Colors.Black, .22);
            Color soft = Mix(surface, seed, dark ? .20 : .11);
            Color accentText = Mix(seed, dark ? Colors.White : Colors.Black, dark ? .67 : .35);
            SetTone("Canvas", canvas);
            SetTone("Surface", surface);
            SetTone("Ink", ink);
            SetTone("Muted", muted);
            SetTone("Outline", border);
            SetTone("InputOutline", ToColor(dark ? "#596780" : "#B6C3D4"));
            SetTone("Alternate", Mix(surface, canvas, .75));
            SetTone("Accent", strong);
            SetTone("AccentSoft", soft);
            SetTone("AccentSelection", Mix(surface, seed, dark ? .42 : .24));
            SetTone("AccentText", accentText);
            SetTone("AccentBar", Mix(seed, dark ? Colors.White : Colors.Black, dark ? .34 : .05));
            SetTone("AccentBarLight", Mix(seed, Colors.White, .31));
            SetTone("OnAccentMuted", ToColor("#F2F5FA"));
            SetTone("NavText", ToColor("#D4DFEA"));
            SetTone("NavSelected", Mix(ToColor("#202A3C"), seed, .47));
            SetTone("NavDot", Mix(seed, Colors.White, .57));
            SetTone("FocusOutline", ToColor(dark ? "#F3C86B" : "#965807"));
            SetTone("DangerText", ToColor(dark ? "#FFB9BE" : "#852C2B"));
            SetTone("DangerSurface", ToColor(dark ? "#462A35" : "#FBE9E8"));
            SetTone("IncomeInk", ToColor(dark ? "#83E4BA" : "#126346"));
            SetTone("ExpenseInk", ToColor(dark ? "#FFB0B1" : "#A22635"));
            SetTone("ResetSurface", ToColor(accent.Name == "Vermelho" ? "#8C4600" : "#B4232B"));
            SetTone("WarningText", ToColor(dark ? "#FFE1A3" : "#633A10"));
            SetTone("WarningSurface", ToColor(dark ? "#483722" : "#FFF1D7"));
            SetTone(SystemColors.WindowBrushKey, surface);
            SetTone(SystemColors.WindowTextBrushKey, ink);
            SetTone(SystemColors.ControlBrushKey, surface);
            SetTone(SystemColors.ControlTextBrushKey, ink);
            SetTone(SystemColors.HighlightBrushKey, Mix(surface, seed, dark ? .42 : .24));
            SetTone(SystemColors.HighlightTextBrushKey, ink);
            window.Resources["Hero"] = new LinearGradientBrush(Mix(seed, Colors.Black, .34), strong, 22);
            Color side = ToColor(dark ? "#151A24" : "#172638");
            window.Resources["Sidebar"] = new LinearGradientBrush(side, Mix(side, seed, .18), 65);
            Get<Button>("ThemeToggle").Content = dark ? "Modo claro" : "Modo noturno";
            QueueChatHighlight();
            if (currentPage == "Summary") RefreshBudget();
        }

        private void SetTone(object key, Color color)
        {
            window.Resources[key] = Solid(color);
        }

        private SolidColorBrush Tone(string key)
        {
            return (SolidColorBrush)window.Resources[key];
        }

        private static T Paint<T>(T control, DependencyProperty property, string key) where T : FrameworkElement
        {
            control.SetResourceReference(property, key);
            return control;
        }

        private static SolidColorBrush Solid(Color color)
        {
            return new SolidColorBrush(color);
        }

        private static Color ToColor(string hex)
        {
            return (Color)ColorConverter.ConvertFromString(hex);
        }

        private static Color Mix(Color left, Color right, double weight)
        {
            return Color.FromRgb(
                (byte)Math.Round(left.R * (1 - weight) + right.R * weight),
                (byte)Math.Round(left.G * (1 - weight) + right.G * weight),
                (byte)Math.Round(left.B * (1 - weight) + right.B * weight));
        }
    }
}
