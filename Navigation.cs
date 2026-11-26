using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace SpentZ
{
    internal sealed partial class Program
    {
        private readonly Dictionary<string, DateTime> pageMonths = new Dictionary<string, DateTime>();
        private void WireNavigation()
        {
            Click("NavigateBack", GoBack);
            Click("MonthPicker", OpenMonthPicker);
            window.PreviewKeyDown += delegate(object sender, KeyEventArgs args)
            {
                Key key = args.Key == Key.System ? args.SystemKey : args.Key;
                if (key == Key.Left && Keyboard.Modifiers == ModifierKeys.Alt && ParentPage() != null)
                {
                    args.Handled = true;
                    Safely(GoBack);
                }
                else if (key == Key.G && Keyboard.Modifiers == ModifierKeys.Control &&
                    Get<FrameworkElement>("MonthBar").Visibility == Visibility.Visible)
                {
                    args.Handled = true;
                    Safely(OpenMonthPicker);
                }
            };
        }

        private void Navigate(string page, Action configure = null)
        {
            DateTime previousMonth = month;
            string previousPage = MonthContext(currentPage);
            string destination = page == "Closures" || page == "Account" ? "Summary" : page == "ArchivedGoals" ? "Goals" : page;
            if (previousPage != destination)
            {
                pageMonths[previousPage] = month;
                if (!pageMonths.TryGetValue(destination, out month)) month = MonthStart(Today);
            }
            if (configure != null) configure();
            if (month != previousMonth) RefreshMonth();
            ShowPage(page);
        }

        private string MonthContext(string page)
        {
            if (page == "Closures" || page == "Account") return "Summary";
            if (page == "ArchivedGoals") return "Goals";
            return page == "Bills" && showIncomePlans ? "Income" : page;
        }

        private string ParentPage()
        {
            switch (currentPage)
            {
                case "Backup":
                case "Help":
                    return "Settings";
                case "Closures":
                case "Account":
                    return "Summary";
                case "Goals":
                    return "Reserves";
                case "ArchivedGoals":
                    return "Goals";
                case "Bills":
                    return IsBillHistory ? "Bills" : showIncomePlans ? "Income" : null;
                default:
                    return null;
            }
        }

        private void GoBack()
        {
            string parent = ParentPage();
            if (parent == "Bills") OpenAgenda(false, false);
            else if (parent != null) Navigate(parent);
        }

        private Window CreateDialog(string title, double width)
        {
            Window dialog = new Window {
                Owner = window, Title = title, Width = width, SizeToContent = SizeToContent.Height,
                WindowStartupLocation = WindowStartupLocation.CenterOwner, ResizeMode = ResizeMode.NoResize,
                Background = Tone("Surface"), Foreground = Tone("Ink"), FontSize = 14, ShowInTaskbar = false,
                Icon = window.Icon
            };
            dialog.Resources.MergedDictionaries.Add(window.Resources);
            return dialog;
        }

        private Button PrimaryDialogButton(string label)
        {
            return new Button {
                Content = label, IsDefault = true, Margin = new Thickness(8, 0, 0, 0),
                Background = Tone("Accent"), Foreground = Brushes.White
            };
        }

        private static StackPanel DialogActions(Button confirm)
        {
            StackPanel actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            actions.Children.Add(new Button { Content = "Cancelar", IsCancel = true });
            actions.Children.Add(confirm);
            return actions;
        }

        private void OpenMonthPicker()
        {
            Window dialog = CreateDialog("Escolher mês e ano", 355);
            StackPanel body = new StackPanel { Margin = new Thickness(20) };
            ComboBox months = new ComboBox();
            for (int number = 1; number <= 12; number++)
                months.Items.Add(LedgerEntry.Brazilian.DateTimeFormat.GetMonthName(number));
            months.SelectedIndex = month.Month - 1;
            TextBox year = new TextBox { Text = month.Year.ToString(CultureInfo.InvariantCulture), MaxLength = 4, Padding = new Thickness(7) };
            AddField(body, "Mês", months);
            AddField(body, "Ano", year);
            TextBlock feedback = new TextBlock { Foreground = Tone("DangerText"), Margin = new Thickness(0, 0, 0, 6) };
            body.Children.Add(feedback);
            Button view = PrimaryDialogButton("Ir para o mês");
            body.Children.Add(DialogActions(view));
            dialog.Content = body;
            dialog.Loaded += delegate { year.Focus(); year.SelectAll(); };
            DateTime target = month;
            view.Click += delegate
            {
                int number;
                if (!int.TryParse(year.Text.Trim(), out number) || number < 1900 || number > 9998 || months.SelectedIndex < 0)
                {
                    feedback.Text = "Escolha um mês e informe um ano entre 1900 e 9998.";
                    return;
                }
                target = new DateTime(number, months.SelectedIndex + 1, 1);
                dialog.DialogResult = true;
            };
            if (dialog.ShowDialog() != true) return;
            month = target;
            RefreshMonth();
        }
    }
}
