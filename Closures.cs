using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;

namespace SpentZ
{
    public sealed class MonthClosure
    {
        public DateTime Month { get; set; }
        public long Income { get; set; }
        public long Expenses { get; set; }
        public long Entries { get; set; }
        public bool IsPartial { get; set; }
        public long Balance { get { return Income - Expenses; } }
        public string MonthDisplay { get { return Month.ToString("MMMM yyyy", LedgerEntry.Brazilian) + (IsPartial ? " · parcial" : ""); } }
        public string BalanceDisplay { get { return LedgerEntry.Money(Balance); } }
    }

    internal sealed partial class LedgerStore
    {
        private void EnsureClosureSchema()
        {
            Execute(@"CREATE TABLE IF NOT EXISTS month_closures (
                month TEXT PRIMARY KEY, income INTEGER NOT NULL CHECK(income >= 0),
                expenses INTEGER NOT NULL CHECK(expenses >= 0), entries INTEGER NOT NULL CHECK(entries >= 0))");
        }

        internal void UpdateClosures(DateTime today)
        {
            string cutoff = IsoDate(new DateTime(today.Year, today.Month, 1));
            Execute(@"WITH RECURSIVE reserve_activity(month, delta, moves) AS (
                    SELECT substr(date, 1, 7) || '-01', SUM(cents * direction), COUNT(*)
                    FROM reserve_moves WHERE date < ? GROUP BY substr(date, 1, 7)
                ), reserve_calendar(month, balance, moves) AS (
                    SELECT month, delta, moves FROM reserve_activity WHERE month = (SELECT MIN(month) FROM reserve_activity)
                    UNION ALL
                    SELECT date(calendar.month, '+1 month'), calendar.balance + COALESCE(activity.delta, 0), COALESCE(activity.moves, 0)
                    FROM reserve_calendar calendar
                    LEFT JOIN reserve_activity activity ON activity.month = date(calendar.month, '+1 month')
                    WHERE date(calendar.month, '+1 month') < ?
                )
                INSERT INTO month_closures(month, income, expenses, entries)
                SELECT month, SUM(income), SUM(expenses), SUM(entries)
                FROM (
                    SELECT substr(date, 1, 7) || '-01' AS month, 0 AS income, cents AS expenses, 1 AS entries FROM expenses WHERE date < ?
                    UNION ALL SELECT substr(date, 1, 7) || '-01', cents, 0, 1 FROM incomes WHERE date < ?
                    UNION ALL SELECT month, 0, 0, 0 FROM month_closures WHERE month < ?
                    UNION ALL SELECT month, 0, 0, 0 FROM reserve_calendar WHERE balance <> 0 OR moves <> 0
                    UNION ALL SELECT substr(date, 1, 7) || '-01', 0, 0, 0 FROM opening_balance
                    UNION ALL SELECT substr(settled_on, 1, 7) || '-01', 0, 0, 0 FROM bill_waivers WHERE settled_on <> ''
                    UNION ALL SELECT substr(created_on, 1, 7) || '-01', 0, 0, 0 FROM bill_plans
                        WHERE created_on <> '' AND is_income = 0 AND monthly_bill = 0 AND (installments = 0 OR plan_kind <> 0)
                    UNION ALL SELECT substr(canceled_on, 1, 7) || '-01', 0, 0, 0 FROM bill_plans
                        WHERE canceled_on <> '' AND is_income = 0 AND monthly_bill = 0 AND (installments = 0 OR plan_kind <> 0)
                )
                WHERE month < ?
                GROUP BY month
                ON CONFLICT(month) DO UPDATE SET income=excluded.income, expenses=excluded.expenses, entries=excluded.entries
                WHERE month_closures.income <> excluded.income OR month_closures.expenses <> excluded.expenses OR month_closures.entries <> excluded.entries",
                cutoff, cutoff, cutoff, cutoff, cutoff, cutoff);
        }

        internal void SaveClosure(DateTime date, bool existingOnly = false)
        {
            DateTime start = new DateTime(date.Year, date.Month, 1);
            string from = IsoDate(start);
            string to = IsoDate(start.AddMonths(1));
            Execute(@"INSERT OR REPLACE INTO month_closures(month, income, expenses, entries)
                SELECT ?, COALESCE(SUM(income), 0), COALESCE(SUM(expenses), 0), COUNT(*)
                FROM (
                    SELECT 0 AS income, cents AS expenses FROM expenses WHERE date >= ? AND date < ?
                    UNION ALL SELECT cents, 0 FROM incomes WHERE date >= ? AND date < ?
                ) HAVING ? = 0 OR EXISTS (SELECT 1 FROM month_closures WHERE month = ?)",
                from, from, to, from, to, existingOnly ? 1L : 0L, from);
        }

        internal List<MonthClosure> Closures(DateTime? before = null, bool includeCurrent = false)
        {
            DateTime open = before.HasValue ? new DateTime(before.Value.Year, before.Value.Month, 1) : DateTime.MaxValue.Date;
            string cutoff = IsoDate(includeCurrent && before.HasValue ? open.AddMonths(1) : open);
            List<MonthClosure> result = new List<MonthClosure>();
            using (Statement query = Prepare("SELECT month, income, expenses, entries FROM month_closures WHERE month < ? ORDER BY month DESC",
                new object[] { cutoff }))
            {
                while (query.Step() == Row)
                {
                    DateTime date = DateTime.ParseExact(query.Text(0), "yyyy-MM-dd", CultureInfo.InvariantCulture);
                    result.Add(new MonthClosure {
                        Month = date, Income = query.Integer(1), Expenses = query.Integer(2), Entries = query.Integer(3),
                        IsPartial = includeCurrent && date == open
                    });
                }
            }
            return result;
        }
    }

    internal sealed partial class Program
    {
        private void WireClosures()
        {
            Click("OpenClosures", delegate
            {
                if (month <= MonthStart(Today)) store.SaveClosure(month);
                Navigate("Closures");
            });
            Click("ClosureDetails", delegate
            {
                MonthClosure selected = Get<DataGrid>("ClosureGrid").SelectedItem as MonthClosure;
                if (selected == null) return;
                Navigate("Summary", delegate { month = selected.Month; });
            });
            Click("ClosureBills", delegate
            {
                MonthClosure selected = Get<DataGrid>("ClosureGrid").SelectedItem as MonthClosure;
                if (selected == null) return;
                Navigate("Bills", delegate
                {
                    showIncomePlans = false;
                    month = selected.Month;
                    agendaMode = month == MonthStart(Today) ? AgendaMode.Current : AgendaMode.Month;
                });
            });
            Click("ClosureExport", ExportClosure);
            Get<DataGrid>("ClosureGrid").SelectionChanged += delegate { ShowClosure(); };
            Get<Expander>("ClosureTransactionDetails").Expanded += delegate { ShowClosureTransactions(); };
            Get<Expander>("ClosureLimitDetails").Expanded += delegate { ShowClosureLimits(); };
        }

        private void UpdateClosures()
        {
            store.UpdateClosures(Today);
            store.SaveClosure(Today, true);
        }

        private void LoadClosurePage()
        {
            UpdateClosures();
            List<MonthClosure> rows = store.Closures(Today, true);
            DataGrid grid = Get<DataGrid>("ClosureGrid");
            grid.ItemsSource = rows;
            int matching = rows.FindIndex(item => item.Month == month);
            grid.SelectedIndex = rows.Count == 0 ? -1 : Math.Max(0, matching);
            if (grid.SelectedItem != null) grid.ScrollIntoView(grid.SelectedItem);
            Get<TextBlock>("ClosureEmpty").Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            ShowClosure();
        }

        private void ShowClosure()
        {
            MonthClosure item = Get<DataGrid>("ClosureGrid").SelectedItem as MonthClosure;
            Get<Border>("ClosureChart").Visibility = item == null ? Visibility.Collapsed : Visibility.Visible;
            if (item == null) return;
            Get<TextBlock>("ClosureMonth").Text = item.Month.ToString("MMMM yyyy", LedgerEntry.Brazilian);
            TextBlock kind = Get<TextBlock>("ClosureKind");
            kind.Text = item.IsPartial ? "Fechamento parcial · este mês continua aberto para lançamentos." : "";
            kind.Visibility = item.IsPartial ? Visibility.Visible : Visibility.Collapsed;
            Get<TextBlock>("ClosureBalanceLabel").Text = openingBalance != null && openingBalance.Date < item.Month.AddMonths(1) ?
                (item.IsPartial ? "Resultado dos lançamentos até agora" : "Resultado dos lançamentos do mês") :
                (item.IsPartial ? "Saldo até agora, antes das reservas" : "Saldo após despesas");
            Get<TextBlock>("ClosureCount").Text = item.Entries == 1 ? "1 ganho ou despesa no mês" : item.Entries + " ganhos e despesas no mês";
            Get<TextBlock>("ClosureIncome").Text = LedgerEntry.Money(item.Income);
            Get<TextBlock>("ClosureExpenses").Text = LedgerEntry.Money(item.Expenses);
            string adjustments = store.AdjustmentSummary(item.Month);
            Get<TextBlock>("ClosureAdjustments").Text = adjustments;
            Get<TextBlock>("ClosureAdjustments").Visibility = adjustments.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            Get<Expander>("ClosureTransactionDetails").IsExpanded = false;
            Get<ItemsControl>("ClosureTransactions").ItemsSource = null;
            Get<Expander>("ClosureLimitDetails").IsExpanded = false;
            Get<Expander>("ClosureLimitDetails").Visibility = store.CategoryLimits(item.Month).Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            Get<ItemsControl>("ClosureLimits").ItemsSource = null;
            List<ReserveBalance> reserves = store.ReserveBalances(item.Month);
            decimal added = reserves.Sum(place => (decimal)place.Added);
            decimal withdrawn = reserves.Sum(place => (decimal)place.Withdrawn);
            Get<TextBlock>("ClosureReserveTotal").Text = LedgerEntry.Money(reserves.Sum(place => (decimal)place.Balance));
            Get<TextBlock>("ClosureReserveActivity").Text = "Guardado no mês " + LedgerEntry.Money(added) +
                "  ·  Resgatado no mês " + LedgerEntry.Money(withdrawn);
            Get<TextBlock>("ClosureAvailable").Text = LedgerEntry.Money(store.AvailableBalance(item.Month));
            Get<TextBlock>("ClosureOpening").Text = openingBalance != null && MonthStart(openingBalance.Date) == item.Month ?
                "Saldo inicial em " + ReportDate(openingBalance.Date) + " · " + LedgerEntry.Money(openingBalance.Cents) : "";
            Get<TextBlock>("ClosureOpening").Visibility = Get<TextBlock>("ClosureOpening").Text.Length == 0 ?
                Visibility.Collapsed : Visibility.Visible;
            Get<ItemsControl>("ClosureReservePlaces").ItemsSource = reserves;
            Get<ItemsControl>("ClosureReserveMoves").ItemsSource = store.ReserveMovements(item.Month);
            List<string> subscriptions = bills.SubscriptionsForReport(item.Month, Today)
                .Select(plan => SubscriptionReportText(plan, item.Month)).ToList();
            Get<ItemsControl>("ClosureSubscriptions").ItemsSource = subscriptions;
            Get<StackPanel>("ClosureSubscriptionsSection").Visibility = subscriptions.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
            decimal billsPending = bills.Month(item.Month, Today).Where(IsOpenPrediction).Sum(row => (decimal)row.Cents);
            decimal incomePending = bills.Month(item.Month, Today, true).Where(IsOpenPrediction).Sum(row => (decimal)row.Cents);
            Get<TextBlock>("ClosurePending").Text = "Previsões pendentes do mês · A pagar " + LedgerEntry.Money(billsPending) +
                "  ·  A receber " + LedgerEntry.Money(incomePending);
            TextBlock balance = Get<TextBlock>("ClosureBalance");
            balance.Text = item.BalanceDisplay;
            Paint(balance, TextBlock.ForegroundProperty, item.Balance < 0 ? "DangerText" : "AccentText");
            double maximum = Math.Max(1, Math.Max(item.Income, item.Expenses));
            Get<ProgressBar>("ClosureIncomeBar").Value = item.Income / maximum;
            Get<ProgressBar>("ClosureExpensesBar").Value = item.Expenses / maximum;
        }

        private void ShowClosureLimits()
        {
            MonthClosure item = Get<DataGrid>("ClosureGrid").SelectedItem as MonthClosure;
            if (item != null) Get<ItemsControl>("ClosureLimits").ItemsSource = store.CategoryProgressFor(item.Month)
                .Select(limit => limit.Display).ToArray();
        }

        private string SubscriptionReportText(BillPlan plan, DateTime period)
        {
            DateTime through = period.AddMonths(1).AddDays(-1);
            if (through > Today) through = Today;
            string status = "Ativa";
            DateTime? canceled = plan.CanceledOn ?? plan.StopBefore;
            if (plan.WasRevised && canceled.HasValue && canceled.Value <= through)
                return plan.Description + " · plano ajustado a partir de " + ReportDate(canceled.Value);
            if (canceled.HasValue && canceled.Value <= through)
            {
                status = plan.CanceledOn.HasValue ? "Cancelada em " + ReportDate(plan.CanceledOn.Value) : "Cancelada";
                status += " · sem novas cobranças a partir de " + ReportDate(plan.StopBefore.Value);
            }
            else if (plan.Installments > 0)
            {
                DateTime last = plan.FirstDue.AddMonths((plan.RemainingTerms - 1) * plan.Interval);
                if (last <= through) status = "Última cobrança prevista para " + ReportDate(last);
            }
            if (plan.CreatedOn.HasValue && MonthStart(plan.CreatedOn.Value) == period)
                status = "Cadastrada em " + ReportDate(plan.CreatedOn.Value) + " · " + status;
            return plan.Description + " · " + status;
        }

        private void ShowClosureTransactions()
        {
            MonthClosure item = Get<DataGrid>("ClosureGrid").SelectedItem as MonthClosure;
            if (item == null) return;
            string[] registered = store.Month(item.Month, true).Concat(store.Month(item.Month)).OrderByDescending(entry => entry.Date)
                .Select(entry => entry.DateDisplay + " · " + entry.Description + " · " + entry.SignedAmount).ToArray();
            string[] latePaid = bills.Month(item.Month, Today).Concat(bills.Month(item.Month, Today, true))
                .Where(row => row.Payment != null && MonthStart(row.Payment.RecordedOn) != item.Month)
                .Select(row => row.Description + " · vencimento " + row.DueDisplay + " · confirmado em " + ReportDate(row.Payment.RecordedOn)).ToArray();
            Get<ItemsControl>("ClosureTransactions").ItemsSource = registered.Concat(latePaid).DefaultIfEmpty("Nenhum lançamento no mês.").ToArray();
        }

        private static string ReportDate(DateTime date)
        {
            return date.ToString("dd/MM/yyyy", LedgerEntry.Brazilian);
        }

        private void ExportClosure()
        {
            MonthClosure item = Get<DataGrid>("ClosureGrid").SelectedItem as MonthClosure;
            if (item == null) return;
            SaveFileDialog dialog = new SaveFileDialog {
                Title = "Exportar relatório do mês", Filter = "Arquivo CSV (*.csv)|*.csv",
                FileName = "spentz-relatorio-" + item.Month.ToString("yyyy-MM", CultureInfo.InvariantCulture)
            };
            if (dialog.ShowDialog(window) == true) WriteMonthlyReport(dialog.FileName, item);
        }

        private void WriteMonthlyReport(string filename, MonthClosure item)
        {
            List<ReserveBalance> reserves = store.ReserveBalances(item.Month);
            decimal added = reserves.Sum(place => (decimal)place.Added);
            decimal withdrawn = reserves.Sum(place => (decimal)place.Withdrawn);
            using (StreamWriter writer = new StreamWriter(filename, false, new UTF8Encoding(true)))
            {
                writer.WriteLine("Seção;Descrição;Data ou período;Valor (BRL)");
                string period = item.Month.ToString("MM/yyyy", CultureInfo.InvariantCulture);
                WriteReportLine(writer, "Resumo", item.IsPartial ? "Fechamento parcial · ganhos" : "Ganhos registrados", period, item.Income);
                WriteReportLine(writer, "Resumo", "Despesas registradas", period, item.Expenses);
                WriteReportLine(writer, "Resumo", "Saldo antes das reservas", period, item.Balance);
                WriteReportLine(writer, "Resumo", "Disponível após reservas", period, store.AvailableBalance(item.Month));
                if (openingBalance != null && MonthStart(openingBalance.Date) == item.Month)
                    WriteReportLine(writer, "Resumo", "Saldo inicial informado", ReportDate(openingBalance.Date), openingBalance.Cents);
                WriteReportLine(writer, "Reservas", "Total guardado até o mês", period, reserves.Sum(place => (decimal)place.Balance));
                WriteReportLine(writer, "Reservas", "Guardado no mês", period, added);
                WriteReportLine(writer, "Reservas", "Resgatado no mês", period, withdrawn);
                foreach (LedgerEntry entry in store.Month(item.Month, true).Concat(store.Month(item.Month)).OrderBy(entry => entry.Date))
                    WriteReportLine(writer, entry.TypeDisplay, entry.Description + " · " + entry.Category, entry.DateDisplay,
                        entry.IsIncome ? entry.Cents : -entry.Cents);
                foreach (CategoryProgress limit in store.CategoryProgressFor(item.Month))
                {
                    WriteReportLine(writer, "Limite mensal", limit.Name, period, limit.Limit);
                    WriteReportLine(writer, "Uso do limite", limit.Name, period, limit.Used);
                }
                foreach (ReserveBalance place in reserves)
                    WriteReportLine(writer, "Saldo guardado por local", place.Place, period, place.Balance);
                foreach (ReserveMovement movement in store.ReserveMovements(item.Month))
                    WriteReportLine(writer, movement.Kind, movement.Place, movement.DateDisplay, movement.Cents);
                foreach (BillPlan plan in bills.SubscriptionsForReport(item.Month, Today))
                    WriteReportLine(writer, "Assinaturas", SubscriptionReportText(plan, item.Month), period, plan.Cents);
                foreach (BillOccurrence row in bills.Month(item.Month, Today).Concat(bills.Month(item.Month, Today, true))
                    .Where(row => row.Payment != null || row.Waived || IsOpenPrediction(row)))
                {
                    string section = row.Plan.IsIncome ? "Ganhos fixos" : "Contas futuras";
                    string status = row.Status + (row.Payment == null ? "" : " em " + ReportDate(row.Payment.RecordedOn)) +
                        (row.Payment != null && row.Payment.InOpeningBalance ? " · incluído no saldo inicial" : "");
                    WriteReportLine(writer, section, row.Description + " · " + row.Series + " · " + status, row.DueDisplay, row.Cents);
                }
            }
        }

        private static void WriteReportLine(StreamWriter writer, string section, string description, string date, decimal cents)
        {
            writer.WriteLine(CsvText(section) + ";" + CsvText(description) + ";" + CsvText(date) + ";" +
                (cents / 100m).ToString("0.00", LedgerEntry.Brazilian));
        }
    }
}
