using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;

namespace SpentZ
{
    internal sealed partial class Program
    {
        private void WireBudget()
        {
            Click("CreateCategory", CreateExpenseCategory);
            Click("BudgetSettings", EditCategoryLimit);
            Click("AdjustBalance", EnterBalanceAdjustment);
            Click("HistoryRefund", delegate { EnterRefund(SelectedEntry("HistoryGrid")); });
            Get<DataGrid>("HistoryGrid").SelectionChanged += delegate
            {
                LedgerEntry entry = SelectedEntry("HistoryGrid");
                Get<Button>("HistoryRefund").IsEnabled = entry != null && !entry.IsIncome && entry.Kind == 0 &&
                    store.RemainingRefund(entry) > 0;
            };
        }

        private string lastCreatedCategory;

        private void CreateExpenseCategory()
        {
            Window dialog = CreateDialog("Criar categoria", 410);
            StackPanel fields = new StackPanel { Margin = new Thickness(20) };
            TextBox name = new TextBox { MaxLength = 40, Padding = new Thickness(7) };
            AddField(fields, "Nome da categoria", name);
            fields.Children.Add(new TextBlock {
                Text = "Por exemplo, crie Pet e digite 45 pet no Chat para classificar o gasto.",
                Foreground = Tone("Muted"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10)
            });
            TextBlock feedback = new TextBlock { Foreground = Tone("DangerText"), TextWrapping = TextWrapping.Wrap };
            fields.Children.Add(feedback);
            Button save = PrimaryDialogButton("Criar categoria");
            fields.Children.Add(DialogActions(save));
            dialog.Content = fields;
            dialog.Loaded += delegate { name.Focus(); };
            save.Click += delegate
            {
                try
                {
                    lastCreatedCategory = store.AddExpenseCategory(name.Text);
                    dialog.DialogResult = true;
                }
                catch (IOException error) { feedback.Text = error.Message; }
            };
            if (dialog.ShowDialog() != true) return;
            RefreshHistoryCategories();
            Get<TextBlock>("BudgetStatus").Text = "“" + lastCreatedCategory + "” está disponível no Chat e no seletor de limites.";
        }

        private void EditCategoryLimit()
        {
            DateTime period = month;
            Dictionary<string, long> limits = store.CategoryLimits(period);
            Window dialog = CreateDialog("Definir limite", 430);
            StackPanel fields = new StackPanel { Margin = new Thickness(20) };
            fields.Children.Add(new TextBlock {
                Text = "O limite passa a valer em " +
                    period.ToString("MMMM yyyy", LedgerEntry.Brazilian) + " e se repete nos meses seguintes até ser alterado.",
                TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10)
            });
            ComboBox category = new ComboBox { ItemsSource = store.ExpenseCategories(), Padding = new Thickness(6) };
            TextBox limit = new TextBox { Padding = new Thickness(7), MaxLength = 16 };
            AddField(fields, "Categoria", category);
            AddField(fields, "Limite por mês (R$)", limit);
            fields.Children.Add(new TextBlock {
                Text = "Deixe em branco ou informe 0 para usar a categoria sem limite.",
                Foreground = Tone("Muted"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10)
            });
            category.SelectionChanged += delegate
            {
                long value;
                string selected = category.SelectedItem as string;
                limit.Text = selected != null && limits.TryGetValue(selected, out value) ?
                    (value / 100m).ToString("0.00", LedgerEntry.Brazilian) : "";
            };
            if (lastCreatedCategory != null) category.SelectedItem = lastCreatedCategory;
            TextBlock feedback = new TextBlock { Foreground = Tone("DangerText"), TextWrapping = TextWrapping.Wrap };
            fields.Children.Add(feedback);
            Button save = PrimaryDialogButton("Salvar limite");
            fields.Children.Add(DialogActions(save));
            dialog.Content = fields;
            dialog.Loaded += delegate { category.Focus(); };
            save.Click += delegate
            {
                string selected = category.SelectedItem as string;
                if (selected == null)
                {
                    feedback.Text = "Escolha a categoria que vai receber o limite.";
                    return;
                }
                string value = limit.Text.Trim();
                long cents = 0;
                if (value.Length > 0 && !IsZero(value) && !LedgerEntry.TryAmount(value, out cents))
                {
                    feedback.Text = "Informe um limite válido, como 200,00.";
                    return;
                }
                try { store.SetCategoryLimit(selected, period, cents); }
                catch (IOException error) { feedback.Text = error.Message; return; }
                dialog.DialogResult = true;
            };
            if (dialog.ShowDialog() != true) return;
            Get<TextBlock>("BudgetStatus").Text = "";
            RefreshBudget();
        }

        private static bool IsZero(string text)
        {
            return text == "0" || text == "0,00" || text == "0.00" || text == "0,0" || text == "0.0";
        }

        private void RefreshBudget()
        {
            StackPanel panel = Get<StackPanel>("CategoryLimits");
            panel.Children.Clear();
            List<CategoryProgress> limits = store.CategoryProgressFor(month, monthlyExpenses);
            if (limits.Count == 0)
            {
                panel.Children.Add(new TextBlock { Text = "Defina um limite para acompanhar quanto pode gastar por categoria.",
                    Foreground = Tone("Muted") });
                return;
            }
            foreach (CategoryProgress limit in limits)
            {
                panel.Children.Add(new TextBlock {
                    Text = limit.Display, Foreground = Tone(limit.Remaining < 0 ? "DangerText" : "Ink"), TextWrapping = TextWrapping.Wrap
                });
                panel.Children.Add(new ProgressBar { Height = 6, Maximum = 1, Value = Math.Min(1, (double)(limit.Used / limit.Limit)),
                    Background = Tone("AccentSoft"), Foreground = Tone(limit.Remaining < 0 ? "DangerText" : "AccentBar"),
                    Margin = new Thickness(0, 4, 0, 8) });
            }
        }

        private void EnterBalanceAdjustment()
        {
            if (month > MonthStart(Today)) return;
            DateTime period = month;
            DateTime on = period == MonthStart(Today) ? Today : period.AddMonths(1).AddDays(-1);
            Window dialog = CreateDialog("Ajustar saldo do mês", 430);
            StackPanel fields = new StackPanel { Margin = new Thickness(20) };
            fields.Children.Add(new TextBlock {
                Text = "Disponível em " + period.ToString("MMMM yyyy", LedgerEntry.Brazilian) + " · " + LedgerEntry.Money(availableBalance),
                FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 8)
            });
            fields.Children.Add(new TextBlock {
                Text = "Informe quanto você tem disponível, sem incluir o dinheiro guardado no Cofrinho. " +
                    "A diferença será registrada em " + ReportDate(on) + " como ajuste de saldo, com origem não informada.",
                TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12)
            });
            TextBox target = new TextBox { Padding = new Thickness(7), MaxLength = 17 };
            AddField(fields, "Saldo disponível correto (R$)", target);
            TextBlock preview = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) };
            fields.Children.Add(preview);
            Button save = PrimaryDialogButton("Registrar ajuste");
            save.IsEnabled = false;
            fields.Children.Add(DialogActions(save));
            dialog.Content = fields;
            long desired = 0;
            target.TextChanged += delegate
            {
                string text = target.Text.Trim();
                bool negative = text.StartsWith("-");
                string digits = negative ? text.Substring(1) : text;
                bool valid = LedgerEntry.TryAmount(digits, out desired) || IsZero(digits);
                if (negative) desired = -desired;
                decimal difference = desired - availableBalance;
                preview.Text = !valid ? "" : difference == 0 ? "O saldo já confere." :
                    (difference > 0 ? "Entrada sem origem · " : "Gasto não informado · ") + LedgerEntry.Money(Math.Abs(difference));
                save.IsEnabled = valid && difference != 0;
            };
            dialog.Loaded += delegate { target.Focus(); };
            save.Click += delegate
            {
                if (save.IsEnabled) dialog.DialogResult = true;
            };
            if (dialog.ShowDialog() != true) return;
            store.AdjustAvailable(on, desired);
            RefreshLedgerViews();
        }

        private void EnterRefund(LedgerEntry expense)
        {
            if (expense == null || expense.IsIncome || expense.Kind != 0) return;
            decimal remaining = store.RemainingRefund(expense);
            if (remaining <= 0) return;
            Window dialog = CreateDialog("Registrar estorno", 410);
            StackPanel fields = new StackPanel { Margin = new Thickness(20) };
            fields.Children.Add(new TextBlock { Text = expense.Description + " · até " + LedgerEntry.Money(remaining),
                FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12) });
            TextBox on = new TextBox { Text = ReportDate(Today), MaxLength = 10, Padding = new Thickness(7) };
            TextBox amount = new TextBox { Text = (remaining / 100m).ToString("0.00", LedgerEntry.Brazilian), Padding = new Thickness(7) };
            AddField(fields, "Data em que recebeu (dia/mês/ano)", on);
            AddField(fields, "Valor devolvido (R$)", amount);
            TextBlock feedback = new TextBlock { Foreground = Tone("DangerText"), TextWrapping = TextWrapping.Wrap };
            fields.Children.Add(feedback);
            Button save = PrimaryDialogButton("Registrar estorno");
            fields.Children.Add(DialogActions(save));
            dialog.Content = fields;
            DateTime date = Today;
            save.Click += delegate
            {
                long cents;
                if (!TryEntryDate(on.Text, out date) || date > Today || date < expense.Date ||
                    !LedgerEntry.TryAmount(amount.Text, out cents) || cents > remaining)
                {
                    feedback.Text = "Confira a data em que o dinheiro voltou e o valor que ainda pode ser estornado.";
                    return;
                }
                try { store.SaveRefund(expense, date, cents); }
                catch (IOException error) { feedback.Text = error.Message; return; }
                dialog.DialogResult = true;
            };
            if (dialog.ShowDialog() != true) return;
            month = MonthStart(date);
            Get<ComboBox>("HistoryType").SelectedIndex = 0;
            Get<TextBox>("HistorySearch").Clear();
            Get<ComboBox>("HistoryCategory").SelectedIndex = 0;
            RefreshLedgerViews();
        }

        private void RefreshLedgerViews()
        {
            ReloadBills();
            UpdateClosures();
            RefreshMonth();
            RefreshChat();
        }
    }
}
