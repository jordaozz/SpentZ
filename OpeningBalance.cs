using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;

namespace SpentZ
{
    internal sealed class OpeningBalance
    {
        internal DateTime Date { get; set; }
        internal long Cents { get; set; }
    }

    internal sealed partial class LedgerStore
    {
        private void EnsureOpeningSchema()
        {
            Execute(@"CREATE TABLE IF NOT EXISTS opening_balance (
                id INTEGER PRIMARY KEY CHECK(id = 1), date TEXT NOT NULL, cents INTEGER NOT NULL)");
            foreach (string table in new[] { "expenses", "incomes", "reserve_moves" })
                if (!HasColumn(table, "included_in_opening"))
                    Execute("ALTER TABLE " + table + " ADD COLUMN included_in_opening INTEGER NOT NULL DEFAULT 0 CHECK(included_in_opening IN (0, 1))");
        }

        internal OpeningBalance GetOpeningBalance()
        {
            using (Statement query = Prepare("SELECT date, cents FROM opening_balance WHERE id = 1", new object[0]))
                return query.Step() == Row ? new OpeningBalance {
                    Date = DateTime.ParseExact(query.Text(0), "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
                    Cents = query.Integer(1)
                } : null;
        }

        internal bool IsBlankForOpening()
        {
            using (Statement query = Prepare(@"SELECT EXISTS(SELECT 1 FROM expenses) OR EXISTS(SELECT 1 FROM incomes)
                OR EXISTS(SELECT 1 FROM bill_plans) OR EXISTS(SELECT 1 FROM reserve_moves)", new object[0]))
                return query.Step() == Row && query.Integer(0) == 0;
        }

        internal void SetOpeningBalance(DateTime date, long cents)
        {
            Execute("BEGIN IMMEDIATE");
            try
            {
                string on = IsoDate(date);
                Execute("INSERT INTO opening_balance(id, date, cents) VALUES (1, ?, ?)", on, cents);
                foreach (string table in new[] { "expenses", "incomes", "reserve_moves" })
                    Execute("UPDATE " + table + " SET included_in_opening = 1 WHERE date <= ?", on);
                Execute("COMMIT");
            }
            catch
            {
                Execute("ROLLBACK");
                throw;
            }
        }

        private decimal AvailableSinceOpening(OpeningBalance opening, DateTime end)
        {
            string from = IsoDate(opening.Date);
            string until = IsoDate(end);
            using (Statement query = Prepare(@"SELECT
                (SELECT COALESCE(SUM(cents), 0) FROM incomes WHERE date >= ? AND date < ? AND included_in_opening = 0),
                (SELECT COALESCE(SUM(cents), 0) FROM expenses WHERE date >= ? AND date < ? AND included_in_opening = 0),
                (SELECT COALESCE(SUM(cents * direction), 0) FROM reserve_moves WHERE date >= ? AND date < ? AND included_in_opening = 0)",
                new object[] { from, until, from, until, from, until }))
                return query.Step() == Row ? opening.Cents + (decimal)query.Integer(0) - query.Integer(1) - query.Integer(2) : opening.Cents;
        }

        internal decimal ExpensesSinceOpening(OpeningBalance opening, DateTime end)
        {
            using (Statement query = Prepare(@"SELECT COALESCE(SUM(cents), 0) FROM expenses
                WHERE date >= ? AND date < ? AND included_in_opening = 0", new object[] { IsoDate(opening.Date), IsoDate(end) }))
                return query.Step() == Row ? query.Integer(0) : 0;
        }

        internal void CorrectOpeningBalance(OpeningBalance original, long cents)
        {
            Execute("UPDATE opening_balance SET cents = ? WHERE id = 1 AND date = ? AND cents = ?",
                cents, IsoDate(original.Date), original.Cents);
            if (sqlite3_changes(database) == 0) throw new IOException("O saldo inicial mudou. Abra a correção novamente.");
        }
    }

    internal sealed partial class Program
    {
        private const string OpeningPromptSkipped = "opening-prompt-skipped";
        private OpeningBalance openingBalance;
        private decimal availableBalance;

        private void OfferOpeningBalance()
        {
            if (openingBalance == null && store.GetPreference(OpeningPromptSkipped) != "1" && store.IsBlankForOpening())
                ShowOpeningBalance(true);
        }

        private void ShowOpeningBalance(bool firstAccess)
        {
            OpeningBalance original = openingBalance;
            bool correcting = original != null;
            Window dialog = CreateDialog(correcting ? "Corrigir saldo inicial" :
                firstAccess ? "Comece pelo seu saldo de hoje" : "Definir saldo inicial", 440);
            StackPanel body = new StackPanel { Margin = new Thickness(20) };
            body.Children.Add(new TextBlock {
                Text = correcting ? "Qual era o saldo no início?" : "Quanto você tem disponível agora?",
                FontSize = 18, FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 0, 0, 8)
            });
            body.Children.Add(new TextBlock {
                Text = correcting ? "Corrija o valor informado em " + ReportDate(original.Date) +
                    ". A correção recalcula o saldo a partir daquela data e preserva os lançamentos. Para uma diferença de hoje, use Ajustar saldo." :
                    "Informe o valor que você tem hoje na conta e em dinheiro, sem incluir o Cofrinho. " +
                    "O SpentZ usa esse valor como ponto de partida e continua somando entradas e descontando despesas daqui em diante.",
                TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10)
            });
            if (!correcting && !firstAccess && !store.IsBlankForOpening())
                body.Children.Add(new TextBlock {
                    Text = "Os registros anteriores continuam no histórico. O saldo informado já representa as movimentações que você fez até agora.",
                    Foreground = Tone("Muted"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10)
                });
            TextBox amount = new TextBox { MaxLength = 17, Padding = new Thickness(7),
                Text = correcting ? (original.Cents / 100m).ToString("0.00", LedgerEntry.Brazilian) : "" };
            AddField(body, "Saldo disponível (R$)", amount);
            TextBlock feedback = new TextBlock { Foreground = Tone("DangerText"), TextWrapping = TextWrapping.Wrap };
            body.Children.Add(feedback);
            Button save = PrimaryDialogButton(correcting ? "Confirmar correção" : "Definir saldo inicial");
            StackPanel actions = DialogActions(save);
            if (firstAccess) ((Button)actions.Children[0]).Content = "Definir depois";
            body.Children.Add(actions);
            dialog.Content = body;
            dialog.Loaded += delegate { amount.Focus(); if (correcting) amount.SelectAll(); };
            save.Click += delegate
            {
                string value = amount.Text.Trim();
                bool negative = value.StartsWith("-");
                if (negative) value = value.Substring(1);
                long cents;
                if (!LedgerEntry.TryAmount(value, out cents) && !IsZero(value))
                {
                    feedback.Text = "Informe um valor, como 834,00. Use 0 se estiver começando sem saldo.";
                    return;
                }
                Safely(delegate
                {
                    if (correcting) store.CorrectOpeningBalance(original, negative ? -cents : cents);
                    else store.SetOpeningBalance(Today, negative ? -cents : cents);
                    dialog.DialogResult = true;
                });
            };
            if (dialog.ShowDialog() != true)
            {
                if (firstAccess) store.SetPreference(OpeningPromptSkipped, "1");
                return;
            }
            openingBalance = store.GetOpeningBalance();
            RefreshLedgerViews();
            Navigate("Summary", delegate { month = MonthStart(Today); });
        }
    }
}
