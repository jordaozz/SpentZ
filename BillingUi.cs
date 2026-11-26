using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;

namespace SpentZ
{
    internal sealed partial class Program
    {
        private BillCalendar bills;
        private enum AgendaMode { Current, Next, Overdue, Month, Pending, ThroughMonth, Plans, Canceled, Confirmed }
        private AgendaMode agendaMode;
        private bool showIncomePlans;

        private void WireBills()
        {
            Click("BillsNav", delegate { OpenAgenda(false, false); });
            Click("IncomeSchedules", delegate { OpenAgenda(true, false); });
            Click("ReminderOpen", delegate { OpenAgenda(false, true); });
            Click("BillOverview", delegate { OpenAgenda(false, false); });
            Click("BillMonthView", delegate {
                if (agendaMode == AgendaMode.Current) month = MonthStart(Today);
                agendaMode = AgendaMode.Month;
                RefreshMonth();
            });
            Click("BillPendingView", delegate { agendaMode = showIncomePlans ? AgendaMode.Pending : AgendaMode.Overdue; RefreshBillViews(); });
            Click("BillPlansView", delegate { agendaMode = AgendaMode.Plans; RefreshBillViews(); });
            Click("BillNextMonth", delegate { OpenNextBillMonth(false); });
            Click("IncomeNextMonth", delegate { OpenNextBillMonth(true); });
            Click("OpenNextBills", delegate { OpenNextBillMonth(false); });
            Click("BillHistory", delegate { OpenBillHistory(AgendaMode.Canceled); });
            Click("BillCanceledView", delegate { OpenBillHistory(AgendaMode.Canceled); });
            Click("BillConfirmedView", delegate { OpenBillHistory(AgendaMode.Confirmed); });
            Click("BillAdd", delegate { AddPlan(showIncomePlans); });
            Click("QuickFixedExpense", delegate { AddPlan(false); });
            Click("QuickFixedIncome", delegate { AddPlan(true); });
            Click("BillEdit", EditSelectedPlan);
            Click("BillSchedule", ShowPlanSchedule);
            Click("BillPay", ReconcileBill);
            Click("BillUnlink", UnlinkRecordedBill);
            Click("BillArchive", ArchiveBill);
            Click("BillCancel", CancelRenewal);
            Click("BillWaive", WaiveSelectedBill);
            DataGrid grid = Get<DataGrid>("BillGrid");
            grid.SelectionChanged += delegate { UpdateBillActions(); };
            grid.SizeChanged += delegate { SizeBillColumns(grid); };
        }

        private void OpenAgenda(bool income, bool pending)
        {
            Navigate("Bills", delegate {
                showIncomePlans = income;
                agendaMode = pending ? AgendaMode.Pending : income ? AgendaMode.Month : AgendaMode.Current;
                if (income && pageMonths.ContainsKey("Income")) month = pageMonths["Income"];
                else if (!income && !pending) month = MonthStart(Today);
            });
        }

        private void OpenNextBillMonth(bool income)
        {
            Navigate("Bills", delegate {
                month = MonthStart(Today).AddMonths(1);
                showIncomePlans = income;
                agendaMode = income ? AgendaMode.Month : AgendaMode.Next;
            });
        }

        private bool IsBillHistory
        {
            get { return agendaMode == AgendaMode.Canceled || agendaMode == AgendaMode.Confirmed; }
        }

        private void OpenBillHistory(AgendaMode mode)
        {
            Navigate("Bills", delegate {
                if (agendaMode != AgendaMode.Confirmed && mode == AgendaMode.Confirmed) month = MonthStart(Today);
                showIncomePlans = false;
                agendaMode = mode;
            });
        }

        private void ReloadBills()
        {
            bills = store.Bills();
            RefreshBillViews();
        }

        private void AddPlan(bool income)
        {
            EditBill(new BillPlan {
                Description = "", Category = income ? LedgerEntry.IncomeCategories[0] : LedgerEntry.CategoryNames.Last(),
                FirstDue = Today, IsIncome = income, IsMonthlyBill = !income
            });
        }

        private void RefreshBillViews()
        {
            if (bills == null) return;
            DateTime today = Today;
            int late = bills.PendingThrough(today.AddDays(-1));
            int upcoming = bills.PendingThrough(today.AddDays(reminderDays)) - late;
            Button reminder = Get<Button>("ReminderOpen");
            List<string> portions = new List<string>();
            if (late > 0) portions.Add(late + (late == 1 ? " conta atrasada" : " contas atrasadas"));
            if (upcoming > 0)
            {
                string when = reminderDays == 0 ? " hoje" : " até " + today.AddDays(reminderDays).ToString("dd/MM");
                portions.Add(upcoming + (upcoming == 1 ? " conta a vencer" : " contas a vencer") + when);
            }
            reminder.Content = new TextBlock {
                Text = string.Join("  ·  ", portions) + "   ›", TextWrapping = TextWrapping.Wrap
            };
            Paint(reminder, Control.BackgroundProperty, late > 0 ? "WarningSurface" : "AccentSoft");
            Paint(reminder, Control.ForegroundProperty, late > 0 ? "WarningText" : "AccentText");
            reminder.Visibility = portions.Count > 0 && (currentPage == "Chat" || currentPage == "Summary")
                ? Visibility.Visible : Visibility.Collapsed;

            List<BillOccurrence> monthExpenses = bills.Month(month, today);
            List<BillOccurrence> monthIncomes = bills.Month(month, today, true);
            decimal monthPending = monthExpenses.Where(IsOpenPrediction).Sum(row => (decimal)row.Cents);
            decimal expectedIncome = monthIncomes.Where(IsOpenPrediction).Sum(row => (decimal)row.Cents);
            TextBlock incomePreview = Get<TextBlock>("ExpectedIncome");
            incomePreview.Text = LedgerEntry.Money(expectedIncome) + " a receber de ganhos fixos em " + month.ToString("MMMM", LedgerEntry.Brazilian);
            incomePreview.Visibility = expectedIncome > 0 ? Visibility.Visible : Visibility.Collapsed;
            TextBlock forecast = Get<TextBlock>("ForecastBalance");
            DateTime current = MonthStart(today);
            DateTime end = month.AddMonths(1);
            decimal priorExpenses = month >= current ? bills.PendingAmountThrough(month.AddDays(-1)) : 0;
            decimal priorIncome = month > current ? bills.PendingAmountThrough(month.AddDays(-1), true) -
                bills.PendingAmountThrough(current.AddDays(-1), true) : 0;
            decimal recorded = month > current && openingBalance == null ? store.BalanceForPeriod(current, end) : availableBalance;
            List<string> commitments = new List<string>();
            if (recorded != availableBalance) commitments.Add("Saldo registrado usado na previsão · " + LedgerEntry.Money(recorded));
            if (monthPending != 0) commitments.Add("Contas do mês · " + LedgerEntry.Money(monthPending));
            if (priorExpenses != 0) commitments.Add("Contas pendentes de meses anteriores · " + LedgerEntry.Money(priorExpenses));
            if (expectedIncome != 0) commitments.Add("Ganhos a receber no mês · " + LedgerEntry.Money(expectedIncome));
            if (priorIncome != 0) commitments.Add("Ganhos a receber dos meses anteriores da previsão · " + LedgerEntry.Money(priorIncome));
            Get<TextBlock>("SummaryCommitments").Text = commitments.Count == 0 ?
                "Nenhuma conta ou ganho pendente nesta previsão." : string.Join("\n", commitments);
            Get<TextBlock>("ForecastTitle").Text = "Previsto até " + end.AddDays(-1).ToString("dd/MM", LedgerEntry.Brazilian);
            decimal projected = recorded + expectedIncome + priorIncome - monthPending - priorExpenses;
            forecast.Text = LedgerEntry.Money(projected);
            Paint(forecast, TextBlock.ForegroundProperty, projected < 0 ? "DangerText" : "AccentText");
            DateTime next = new DateTime(today.Year, today.Month, 1).AddMonths(1);
            List<BillOccurrence> nextExpenses = bills.Month(next, today).Where(IsOpenPrediction).ToList();
            if (currentPage == "Account") RefreshAccount();
            if (currentPage != "Bills") return;

            bool income = showIncomePlans;
            bool past = IsBillHistory;
            bool nextCard = income && (agendaMode != AgendaMode.Month || month != next);
            if (nextCard)
            {
                List<BillOccurrence> nextRows = bills.Month(next, today, true).Where(IsOpenPrediction).ToList();
                Get<TextBlock>("BillNextTotal").Text = "Próximo mês (" + next.ToString("MMMM", LedgerEntry.Brazilian) + ") · " +
                    LedgerEntry.Money(nextRows.Sum(row => (decimal)row.Cents)) + " a receber";
                BillOccurrence first = nextRows.FirstOrDefault();
                Get<TextBlock>("BillNextFirst").Text = first == null ? "Nenhum recebimento pendente previsto."
                    : "Primeira entrada " + first.Due.ToString("dd/MM", LedgerEntry.Brazilian) + " · " + first.Description + " · " + first.AmountDisplay;
            }
            Get<Border>("BillNextCard").Visibility = nextCard ? Visibility.Visible : Visibility.Collapsed;
            Get<FrameworkElement>("MonthBar").Visibility = agendaMode == AgendaMode.Month || agendaMode == AgendaMode.Confirmed ? Visibility.Visible : Visibility.Collapsed;
            Get<WrapPanel>("BillRegularTabs").Visibility = past ? Visibility.Collapsed : Visibility.Visible;
            Get<WrapPanel>("BillHistoryTabs").Visibility = past ? Visibility.Visible : Visibility.Collapsed;
            Get<Button>("BillOverview").Visibility = !income ? Visibility.Visible : Visibility.Collapsed;
            Get<Button>("BillNextMonth").Visibility = !income ? Visibility.Visible : Visibility.Collapsed;
            Get<Button>("BillMonthView").Visibility = income || agendaMode == AgendaMode.Month ? Visibility.Visible : Visibility.Collapsed;
            Get<Button>("BillHistory").Visibility = !income && !past ? Visibility.Visible : Visibility.Collapsed;
            Get<Button>("BillAdd").Visibility = past ? Visibility.Collapsed : Visibility.Visible;
            Get<Button>("BillAdd").Content = income ? "Novo ganho fixo" : "Novo gasto fixo";
            Get<Button>("BillPay").Content = income ? "Marcar como recebido" : "Marcar como paga";
            Get<Button>("BillWaive").Content = income ? "Dispensar recebimento" : "Dispensar cobrança";
            Get<Button>("BillCancel").Content = income ? "Parar recorrência" : "Cancelar renovação";
            Get<Button>("BillCancel").ToolTip = income ? "Retirar os recebimentos futuros deste ganho" : "Retirar cobranças de uma assinatura que você cancelou";
            Get<Button>("BillArchive").ToolTip = income ? "Arquivar os recebimentos pendentes deste ganho" : "Arquivar as cobranças pendentes deste plano";
            Get<Button>("BillPendingView").Content = income ? "Atrasados e próximos" : "Vencidas";
            SetBillFilter("BillOverview", agendaMode == AgendaMode.Current);
            SetBillFilter("BillNextMonth", agendaMode == AgendaMode.Next);
            SetBillFilter("BillMonthView", agendaMode == AgendaMode.Month);
            SetBillFilter("BillPendingView", agendaMode == AgendaMode.Overdue || (income && agendaMode == AgendaMode.Pending));
            SetBillFilter("BillPlansView", agendaMode == AgendaMode.Plans);
            SetBillFilter("BillCanceledView", agendaMode == AgendaMode.Canceled);
            SetBillFilter("BillConfirmedView", agendaMode == AgendaMode.Confirmed);

            List<BillOccurrence> rows;
            string title;
            string detail;
            switch (agendaMode)
            {
                case AgendaMode.Next:
                    rows = nextExpenses;
                    title = "A pagar em " + next.ToString("MMMM", LedgerEntry.Brazilian);
                    detail = "Cobranças previstas para o próximo mês.";
                    break;
                case AgendaMode.Overdue:
                    rows = bills.Pending(today, 0).Where(row => row.Due < today).ToList();
                    title = "Total vencido";
                    detail = "Contas vencidas com pagamento em aberto.";
                    break;
                case AgendaMode.Pending:
                    rows = bills.Pending(today, reminderDays, income);
                    title = "Total dessas pendências";
                    detail = "Pendências até " + today.AddDays(reminderDays).ToString("dd/MM/yyyy") + ".";
                    break;
                case AgendaMode.ThroughMonth:
                    DateTime lastDay = next.AddDays(-1);
                    rows = bills.Pending(today, (lastDay - today).Days);
                    title = "Pendências até " + lastDay.ToString("dd/MM");
                    detail = "Inclui contas vencidas de meses anteriores.";
                    break;
                case AgendaMode.Plans:
                    rows = bills.Plans(today, income);
                    title = "Planos ativos";
                    detail = "Selecione um plano para editar ou ver o cronograma.";
                    break;
                case AgendaMode.Canceled:
                    rows = bills.Cancellations(today);
                    title = "Assinaturas encerradas";
                    detail = "Cancelamentos e assinaturas arquivadas.";
                    break;
                case AgendaMode.Confirmed:
                    rows = bills.ConfirmedInMonth(month, today);
                    title = "Total pago em " + month.ToString("MMMM", LedgerEntry.Brazilian);
                    detail = "Pagamentos registrados no mês selecionado.";
                    break;
                default:
                    DateTime period = agendaMode == AgendaMode.Current ? MonthStart(today) : month;
                    rows = income ? monthIncomes.Where(row => row.IsPending || row.Payment != null).ToList()
                        : bills.Month(period, today).Where(IsOpenPrediction).ToList();
                    title = (income ? "A receber em " : "A pagar em ") + period.ToString("MMMM", LedgerEntry.Brazilian);
                    detail = income ? "Previsões e recebimentos registrados do mês." : "Contas do mês com pagamento em aberto.";
                    break;
            }
            bool countOnly = agendaMode == AgendaMode.Plans || agendaMode == AgendaMode.Canceled;
            string total = countOnly ? rows.Count.ToString(LedgerEntry.Brazilian) :
                LedgerEntry.Money(rows.Where(row => agendaMode == AgendaMode.Confirmed || row.Payment == null).Sum(row => (decimal)row.Cents));
            Get<TextBlock>("BillTotalLabel").Text = title;
            Get<TextBlock>("BillTotal").Text = total;
            Get<TextBlock>("BillStatus").Text = rows.Count == 0 ? "Nenhum registro nesta lista." : detail;
            DataGrid grid = Get<DataGrid>("BillGrid");
            grid.Columns[0].Header = agendaMode == AgendaMode.Canceled ? "Cancelada em" :
                agendaMode == AgendaMode.Confirmed ? "Pago em" : income ? "Data prevista" : "Vencimento";
            grid.ItemsSource = rows;
            SizeBillColumns(grid);
            UpdateBillActions();
        }

        private static bool IsOpenPrediction(BillOccurrence row)
        {
            return row.IsPending;
        }

        private void SizeBillColumns(DataGrid grid)
        {
            grid.Columns[2].Visibility = showIncomePlans || (grid.ActualWidth > 0 && grid.ActualWidth < 575)
                ? Visibility.Collapsed : Visibility.Visible;
            SizeDescriptionColumn(grid);
        }

        private static void SizeDescriptionColumn(DataGrid grid)
        {
            double fixedWidth = grid.Columns.Where((column, index) => index != 1 && column.Visibility == Visibility.Visible)
                .Sum(column => column.Width.Value);
            grid.Columns[1].Width = new DataGridLength(Math.Max(grid.Columns[1].MinWidth, grid.ActualWidth - fixedWidth - 24));
        }

        private void SetBillFilter(string control, bool selected)
        {
            Button button = Get<Button>(control);
            Paint(button, Control.BackgroundProperty, selected ? "Accent" : "AccentSoft");
            if (selected) button.Foreground = System.Windows.Media.Brushes.White;
            else Paint(button, Control.ForegroundProperty, "AccentText");
        }

        private BillOccurrence SelectedBill { get { return Get<DataGrid>("BillGrid").SelectedItem as BillOccurrence; } }

        private void UpdateBillActions()
        {
            BillOccurrence bill = SelectedBill;
            bool active = bill != null && bill.Plan.IsActive && !IsBillHistory;
            bool canPay = !IsBillHistory && agendaMode != AgendaMode.Plans;
            Get<Button>("BillPay").Visibility = canPay ? Visibility.Visible : Visibility.Collapsed;
            Get<Button>("BillPay").IsEnabled = canPay && bill != null && bill.IsPending;
            Get<Button>("BillWaive").Visibility = canPay && bill != null && bill.IsPending ? Visibility.Visible : Visibility.Collapsed;
            Get<Button>("BillSchedule").Visibility = bill == null && IsBillHistory ? Visibility.Collapsed : Visibility.Visible;
            Get<Button>("BillSchedule").IsEnabled = bill != null;
            Get<Button>("BillUnlink").Visibility = bill != null && bill.Payment != null && !bill.PlanRecord ? Visibility.Visible : Visibility.Collapsed;
            Get<Button>("BillUnlink").Content = bill != null && bill.Payment != null && bill.Payment.InOpeningBalance ?
                "Reabrir ocorrência" : "Desvincular";
            Get<Button>("BillEdit").IsEnabled = active;
            Get<Button>("BillEdit").Visibility = active ? Visibility.Visible : Visibility.Collapsed;
            if (active) Get<Button>("BillEdit").Content = bill.Plan.IsSubscription ? "Editar assinatura" : "Editar plano";
            Get<Button>("BillArchive").IsEnabled = active && (bill.Plan.IsIncome || !bill.Plan.IsSubscription);
            Get<Button>("BillArchive").Visibility = active && (bill.Plan.IsIncome || !bill.Plan.IsSubscription) ? Visibility.Visible : Visibility.Collapsed;
            Get<Button>("BillCancel").IsEnabled = active && bill.Plan.CanStop && bills.NextRenewal(bill.Plan, Today).HasValue;
            Get<Button>("BillCancel").Visibility = active && bill.Plan.CanStop ? Visibility.Visible : Visibility.Collapsed;
            TextBlock status = Get<TextBlock>("BillCancelStatus");
            DateTime? stop = bill == null ? null : bill.Plan.StopBefore;
            if (bill != null && bill.Payment != null && bill.Payment.InOpeningBalance)
            {
                status.Text = (bill.Plan.IsIncome ? "Recebido" : "Pago") + " em " + ReportDate(bill.Payment.RecordedOn) +
                    " e já considerado no saldo inicial.";
                status.Visibility = Visibility.Visible;
                return;
            }
            status.Visibility = stop.HasValue ? Visibility.Visible : Visibility.Collapsed;
            status.Text = stop.HasValue ? (bill.Plan.WasRevised ? "Plano alterado a partir de " :
                bill.Plan.IsIncome ? "Recorrência interrompida em " : "Sem novas cobranças a partir de ") +
                stop.Value.ToString("dd/MM/yyyy", LedgerEntry.Brazilian) + "." : "";
        }

        private void WaiveSelectedBill()
        {
            BillOccurrence row = SelectedBill;
            if (row == null || !row.IsPending || row.PlanRecord) return;
            if (MessageBox.Show(window, "Dispensar “" + row.Description + "” de " + ReportDate(row.Due) +
                "? O saldo permanece igual e os outros vencimentos seguem no calendário.", "Dispensar ocorrência",
                MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;
            store.WaiveBill(row);
            ReloadBills();
            ShowAgendaNotice("Ocorrência dispensada.");
        }

        private void EditSelectedPlan()
        {
            BillOccurrence row = SelectedBill;
            if (row == null || !row.Plan.IsActive) return;
            DateTime? cutoff = row.Plan.HasSettledOccurrences ? bills.NextEditableRenewal(row.Plan, Today) : null;
            if (cutoff.HasValue) EditUpcomingBill(row.Plan, cutoff.Value);
            else EditBill(row.Plan);
        }

        private void OpenActivePlan(BillPlan plan)
        {
            Navigate("Bills", delegate { agendaMode = AgendaMode.Plans; showIncomePlans = plan.IsIncome; });
            var grid = Get<DataGrid>("BillGrid");
            var row = grid.Items.Cast<BillOccurrence>().FirstOrDefault(item => item.Plan.Id == plan.Id);
            if (row == null) return;
            grid.SelectedItem = row;
            grid.ScrollIntoView(row);
        }

        private void EditUpcomingBill(BillPlan plan, DateTime cutoff)
        {
            Window dialog = CreateDialog(plan.IsSubscription ? "Editar assinatura" : "Editar próximos vencimentos", 440);
            StackPanel fields = new StackPanel { Margin = new Thickness(20) };
            fields.Children.Add(new TextBlock { Text = "O histórico e os vencimentos anteriores a " + ReportDate(cutoff) +
                " permanecem como estão.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12) });
            TextBox description = new TextBox { Text = plan.Description, MaxLength = 120, Padding = new Thickness(7) };
            AddField(fields, "Descrição", description);
            ComboBox cycle = new ComboBox { ItemsSource = new[] { "Mensal", "Anual" }, SelectedIndex = plan.Kind == BillKind.Annual ? 1 : 0 };
            bool editCycle = plan.IsSubscription && plan.Installments == 0;
            if (editCycle) AddField(fields, "Frequência", cycle);
            TextBox date = new TextBox { Text = ReportDate(cutoff), MaxLength = 10, Padding = new Thickness(7) };
            TextBox price = new TextBox { Text = (plan.Cents / 100m).ToString("0.00", LedgerEntry.Brazilian), MaxLength = 16, Padding = new Thickness(7) };
            AddField(fields, "Próxima data (dia/mês/ano)", date);
            AddField(fields, "Novo valor (R$)", price);
            ComboBox category = new ComboBox { ItemsSource = plan.IsIncome ? LedgerEntry.IncomeCategories : store.ExpenseCategories(), SelectedItem = plan.Category };
            AddField(fields, "Categoria", category);
            TextBlock feedback = new TextBlock { Foreground = Tone("DangerText"), TextWrapping = TextWrapping.Wrap };
            fields.Children.Add(feedback);
            Button save = PrimaryDialogButton("Salvar alteração");
            fields.Children.Add(DialogActions(save));
            dialog.MaxHeight = SystemParameters.WorkArea.Height - 24;
            dialog.Content = new ScrollViewer { Content = fields, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            BillPlan revised = null;
            save.Click += delegate
            {
                DateTime next;
                long cents;
                if (description.Text.Trim().Length == 0 || !TryEntryDate(date.Text, out next) || !LedgerEntry.TryAmount(price.Text, out cents))
                {
                    feedback.Text = "Informe a descrição, a data e um valor positivo.";
                    return;
                }
                revised = new BillPlan { Description = description.Text.Trim(), Category = (string)category.SelectedItem ?? plan.Category,
                    FirstDue = next, Cents = cents, Kind = editCycle ? (BillKind)cycle.SelectedIndex : plan.Kind };
                if (revised.Description == plan.Description && revised.Category == plan.Category &&
                    revised.Cents == plan.Cents && revised.FirstDue == cutoff && revised.Kind == plan.Kind)
                {
                    dialog.DialogResult = false;
                    return;
                }
                try { store.RevisePlan(plan, cutoff, revised, Today); }
                catch (System.IO.IOException error) { feedback.Text = error.Message; return; }
                dialog.DialogResult = true;
            };
            if (dialog.ShowDialog() != true) return;
            ReloadBills();
            OpenActivePlan(revised);
            ShowAgendaNotice("Próximos vencimentos atualizados.");
        }

        private void CancelRenewal()
        {
            BillOccurrence bill = SelectedBill;
            if (bill == null) return;
            DateTime canceledOn = Today;
            DateTime? stop = bills.NextRenewal(bill.Plan, canceledOn);
            if (!stop.HasValue) return;
            string date = stop.Value.ToString("dd/MM/yyyy", LedgerEntry.Brazilian);
            string prompt = bill.Plan.IsIncome ? "Retirar os recebimentos previstos de “" + bill.Description + "” a partir de " +
                date + "? Os ganhos já registrados ficam no histórico." :
                "Você já cancelou “" + bill.Description + "” com o serviço? " +
                "A cobrança prevista para " + date + " e as renovações seguintes saem da agenda. Contas anteriores em aberto continuam a pagar.";
            Window dialog = CreateDialog(bill.Plan.IsIncome ? "Parar recorrência" : "Cancelar renovação", 430);
            StackPanel body = new StackPanel { Margin = new Thickness(20) };
            body.Children.Add(new TextBlock { Text = prompt, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 14) });
            Button confirm = PrimaryDialogButton(bill.Plan.IsIncome ? "Parar recorrência" : "Registrar cancelamento");
            confirm.IsDefault = false;
            confirm.Click += delegate { dialog.DialogResult = true; };
            body.Children.Add(DialogActions(confirm));
            dialog.Content = body;
            if (dialog.ShowDialog() != true) return;
            if (!store.StopRenewal(bill.Plan, stop.Value, canceledOn))
                throw new System.IO.IOException("O plano mudou. Selecione-o novamente.");
            UpdateClosures();
            ReloadBills();
            ShowAgendaNotice(bill.Plan.IsIncome ? "Recebimentos futuros retirados da agenda." :
                "Renovações futuras retiradas. O cancelamento aparece no Histórico.");
        }

        private void ArchiveBill()
        {
            BillOccurrence bill = SelectedBill;
            if (bill == null) return;
            if (MessageBox.Show(window, "Arquivar “" + bill.Description + "”? As previsões pendentes sairão da agenda. Os lançamentos já registrados ficam no histórico.",
                "Arquivar plano", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes)
                return;
            store.ArchivePlan(bill.Plan.Id);
            ReloadBills();
        }

        private void ShowPlanSchedule()
        {
            BillOccurrence selected = SelectedBill;
            if (selected == null) return;
            BillPlan plan = selected.Plan;
            List<BillOccurrence> rows = bills.Schedule(plan, Today);
            Window dialog = CreateDialog("Cronograma · " + plan.Description, 655);
            StackPanel body = new StackPanel { Margin = new Thickness(20) };
            body.Children.Add(new TextBlock {
                Text = plan.Description, FontSize = 19, FontWeight = FontWeights.SemiBold
            });
            string period = plan.Installments == 0
                ? (plan.Kind == BillKind.Annual ? "Próximas 3 cobranças anuais" : "Próximos 12 meses")
                : plan.Kind == BillKind.Annual ? "Período contratado · cobranças anuais" : "Período contratado · cobranças mensais";
            decimal remaining = rows.Where(row => row.Payment == null).Sum(row => (decimal)row.Cents);
            body.Children.Add(new TextBlock {
                Text = rows.Count == 0 ? "Nenhuma ocorrência neste cronograma."
                    : period + "  ·  " + LedgerEntry.Money(remaining) + (plan.IsIncome ? " a receber no período" : " a pagar no período"),
                Foreground = Tone("Muted"), Margin = new Thickness(0, 5, 0, 6)
            });
            if (plan.Installments > 0 && rows.Count > 0)
                body.Children.Add(new TextBlock {
                    Text = "Última data do cronograma: " + rows.Last().DueDisplay,
                    Foreground = Tone("AccentText"), Margin = new Thickness(0, 0, 0, 10)
                });
            DataGrid grid = new DataGrid {
                AutoGenerateColumns = false, IsReadOnly = true, CanUserAddRows = false,
                HeadersVisibility = DataGridHeadersVisibility.Column, ItemsSource = rows,
                MaxHeight = 325, RowHeight = 36, Background = Tone("Surface")
            };
            grid.Columns.Add(new DataGridTextColumn { Header = "Data prevista", Binding = new Binding("DueDisplay"), Width = 130 });
            grid.Columns.Add(new DataGridTextColumn { Header = "Ciclo", Binding = new Binding("Series"), Width = 110 });
            grid.Columns.Add(new DataGridTextColumn { Header = "Valor", Binding = new Binding("AmountDisplay"), Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
            grid.Columns.Add(new DataGridTextColumn { Header = "Situação", Binding = new Binding("Status"), Width = 115 });
            body.Children.Add(grid);
            Button close = PrimaryDialogButton("Fechar");
            close.IsCancel = true;
            close.Margin = new Thickness(0, 12, 0, 0);
            close.HorizontalAlignment = HorizontalAlignment.Right;
            body.Children.Add(close);
            dialog.Content = body;
            dialog.ShowDialog();
        }

        private enum FixedExpenseType { MonthlyBill, MonthlySubscription, AnnualSubscription, AnnualMonthly, Installment, OneTime }

        private bool EditBill(BillPlan plan, string chatNote = null, bool chatSubscription = false, LedgerEntry linked = null)
        {
            bool adding = plan.Id == 0;
            Window dialog = CreateDialog(chatSubscription ? "Cadastrar assinatura" :
                adding ? (plan.IsIncome ? "Novo ganho fixo" : "Novo gasto fixo") :
                plan.IsSubscription ? "Editar assinatura" : "Editar plano", 420);
            StackPanel fields = new StackPanel { Margin = new Thickness(20) };
            TextBox description = new TextBox { Text = plan.Description, MaxLength = 120, Padding = new Thickness(7) };
            TextBox amount = new TextBox {
                Text = adding && plan.Cents == 0 ? "" : (plan.Cents / 100m).ToString("0.00", LedgerEntry.Brazilian),
                MaxLength = 16, Padding = new Thickness(7), IsEnabled = !plan.HasSettledOccurrences
            };
            TextBox due = new TextBox {
                Text = plan.FirstDue.ToString("dd/MM/yyyy", LedgerEntry.Brazilian), MaxLength = 10,
                Padding = new Thickness(7), IsEnabled = !plan.HasSettledOccurrences && !plan.StopBefore.HasValue
            };
            bool canEditDates = !plan.HasSettledOccurrences && !plan.StopBefore.HasValue;
            string[] choices = { "Conta mensal (ex.: aluguel)", "Assinatura mensal", "Assinatura anual (1 cobrança por ano)",
                "Plano anual (12 cobranças mensais)", "Compra parcelada", "Conta avulsa" };
            FixedExpenseType original = plan.IsOneTime ? FixedExpenseType.OneTime : plan.IsMonthlyBill ? FixedExpenseType.MonthlyBill :
                plan.Kind == BillKind.Annual ? FixedExpenseType.AnnualSubscription :
                plan.Kind == BillKind.AnnualMonthly ? FixedExpenseType.AnnualMonthly :
                plan.Installments > 0 ? FixedExpenseType.Installment : FixedExpenseType.MonthlySubscription;
            ComboBox kind = new ComboBox {
                ItemsSource = choices, SelectedIndex = (int)original, IsEnabled = canEditDates
            };
            TextBox count = new TextBox { MaxLength = 3, Padding = new Thickness(7) };
            TextBox firstInstallment = new TextBox { Text = plan.FirstInstallment.ToString(), MaxLength = 3,
                Padding = new Thickness(7) };
            ComboBox category = new ComboBox { ItemsSource = plan.IsIncome ? LedgerEntry.IncomeCategories : store.ExpenseCategories(), SelectedItem = plan.Category };
            if (!plan.IsIncome) AddField(fields, chatSubscription ? "Frequência e tipo de cobrança" : "Tipo de gasto fixo", kind);
            AddField(fields, plan.IsIncome ? "Descrição (ex.: Salário)" : "Descrição", description);
            Label amountLabel = AddField(fields, plan.IsIncome ? "Valor mensal (R$)" : "Valor de cada cobrança (R$)", amount);
            AddField(fields, plan.IsIncome ? "Primeira data prevista (dia/mês/ano)" : "Primeiro vencimento (dia/mês/ano)", due);
            if (plan.IsIncome)
                fields.Children.Add(new TextBlock {
                    Text = "O ganho se repete no mesmo dia de cada mês.", Foreground = Tone("Muted"), Margin = new Thickness(0, 0, 0, 8)
                });
            Grid terms = new Grid();
            terms.ColumnDefinitions.Add(new ColumnDefinition());
            terms.ColumnDefinitions.Add(new ColumnDefinition());
            StackPanel totalCount = new StackPanel { Margin = new Thickness(0, 0, 10, 0) };
            StackPanel firstCount = new StackPanel();
            Label countLabel = AddField(totalCount, "Total de parcelas", count);
            AddField(firstCount, "Parcela do 1º vencimento", firstInstallment);
            Grid.SetColumn(firstCount, 1);
            terms.Children.Add(totalCount);
            terms.Children.Add(firstCount);
            if (!plan.IsIncome) fields.Children.Add(terms);
            TextBlock termsHint = new TextBlock { Foreground = Tone("Muted"), Margin = new Thickness(0, 0, 0, 9) };
            fields.Children.Add(termsHint);
            Action refreshTerms = delegate
            {
                FixedExpenseType choice = (FixedExpenseType)kind.SelectedIndex;
                bool annual = choice == FixedExpenseType.AnnualSubscription;
                bool yearlyPlan = choice == FixedExpenseType.AnnualMonthly;
                bool installments = choice == FixedExpenseType.Installment;
                terms.Visibility = !plan.IsIncome && (annual || yearlyPlan || installments) ? Visibility.Visible : Visibility.Collapsed;
                firstCount.Visibility = yearlyPlan || installments ? Visibility.Visible : Visibility.Collapsed;
                Grid.SetColumnSpan(totalCount, annual ? 2 : 1);
                countLabel.Content = annual ? "Número de cobranças anuais (opcional)" : "Total de parcelas";
                amountLabel.Content = plan.IsIncome || choice == FixedExpenseType.MonthlyBill ? "Valor mensal (R$)" : annual ? "Valor cobrado por ano (R$)" :
                    yearlyPlan ? "Valor de cada mensalidade (R$)" : "Valor de cada cobrança (R$)";
                count.Text = annual ? (original == choice && plan.Installments > 0 ? plan.Installments.ToString() : "")
                    : yearlyPlan ? "12" : original == choice && plan.Installments > 0 ? plan.Installments.ToString() : "2";
                firstInstallment.Text = original == choice ? plan.FirstInstallment.ToString() : "1";
                count.IsEnabled = canEditDates && !yearlyPlan;
                firstInstallment.IsEnabled = canEditDates;
                termsHint.Text = annual ? "Deixe o número em branco para repetir todo ano." :
                    yearlyPlan ? "Uma cobrança por mês até completar 12, sem renovação automática no SpentZ." :
                    choice == FixedExpenseType.OneTime ? "A cobrança aparece uma vez, na data indicada." :
                    choice == FixedExpenseType.MonthlyBill ? "A conta aparece todo mês no mesmo dia do primeiro vencimento." : "";
                termsHint.Visibility = termsHint.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
            };
            kind.SelectionChanged += delegate { refreshTerms(); };
            refreshTerms();
            AddField(fields, "Categoria", category);
            if (chatNote != null) fields.Children.Add(new TextBlock {
                Text = chatNote, TextWrapping = TextWrapping.Wrap, Foreground = Tone("Muted"), Margin = new Thickness(0, 0, 0, 8)
            });
            if (linked != null) fields.Children.Add(new TextBlock {
                Text = "A primeira cobrança será vinculada à despesa de " + linked.AmountDisplay + " em " + linked.DateDisplay +
                    ". O valor já registrado no seu saldo será reaproveitado.",
                TextWrapping = TextWrapping.Wrap, Foreground = Tone("Muted"), Margin = new Thickness(0, 0, 0, 8)
            });
            if (chatSubscription)
            {
                var preview = new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = Tone("AccentText"), Margin = new Thickness(0, 0, 0, 8) };
                fields.Children.Add(preview);
                Action previewRenewal = delegate
                {
                    DateTime start = default(DateTime);
                    FixedExpenseType choice = (FixedExpenseType)kind.SelectedIndex;
                    int years;
                    bool single = choice == FixedExpenseType.AnnualSubscription && int.TryParse(count.Text, out years) && years == 1;
                    bool recurring = choice == FixedExpenseType.MonthlySubscription || choice == FixedExpenseType.AnnualMonthly ||
                        choice == FixedExpenseType.AnnualSubscription && !single;
                    int interval = choice == FixedExpenseType.AnnualSubscription ? 12 : 1;
                    bool canPreview = recurring && TryEntryDate(due.Text, out start) && start.AddMonths(interval).Year <= 9998;
                    preview.Visibility = canPreview ? Visibility.Visible : Visibility.Collapsed;
                    if (canPreview)
                        preview.Text = "Cobrança seguinte prevista para " + ReportDate(start.AddMonths(interval)) + ".";
                };
                due.TextChanged += delegate { previewRenewal(); };
                kind.SelectionChanged += delegate { previewRenewal(); };
                count.TextChanged += delegate { previewRenewal(); };
                previewRenewal();
            }
            bool hasOpening = openingBalance != null;
            StackPanel paymentFields = new StackPanel();
            ComboBox firstStatus = new ComboBox { ItemsSource = hasOpening ? new[] {
                "Ainda está em aberto",
                plan.IsIncome ? "Já recebi e estava no saldo inicial" : "Já paguei e estava no saldo inicial",
                plan.IsIncome ? "Já recebi depois do saldo inicial" : "Já paguei depois do saldo inicial"
            } : new[] { "Ainda está em aberto", plan.IsIncome ? "Já recebi" : "Já paguei" } };
            Label firstLabel = AddField(paymentFields, "Primeiro vencimento", firstStatus);
            StackPanel paymentDateField = new StackPanel();
            TextBox paymentDate = new TextBox { MaxLength = 10, Padding = new Thickness(7) };
            AddField(paymentDateField, plan.IsIncome ? "Data em que recebeu (dia/mês/ano)" : "Data em que pagou (dia/mês/ano)", paymentDate);
            paymentFields.Children.Add(paymentDateField);
            StackPanel matchFields = new StackPanel { Visibility = Visibility.Collapsed };
            ComboBox firstMatches = new ComboBox { MaxDropDownHeight = 200 };
            AddField(matchFields, "Vincular a um lançamento existente?", firstMatches);
            paymentFields.Children.Add(matchFields);
            if (adding) fields.Children.Add(paymentFields);
            Action updateMatches = delegate
            {
                DateTime on;
                long cents;
                bool needsActual = paymentDateField.Visibility == Visibility.Visible &&
                    !(hasOpening && firstStatus.SelectedIndex == 1);
                if (!needsActual || !TryEntryDate(paymentDate.Text, out on) || on > Today ||
                    !LedgerEntry.TryAmount(amount.Text, out cents) || description.Text.Trim().Length == 0)
                {
                    matchFields.Visibility = Visibility.Collapsed;
                    firstMatches.ItemsSource = null;
                    return;
                }
                List<PostedChoice> matches = store.UnlinkedEntries(on, plan.IsIncome)
                    .Where(item => SamePaymentDescription(item.Description, description.Text.Trim())
                        && item.Date <= Today && Math.Abs((item.Date - on).TotalDays) <= 3 &&
                        (!hasOpening || item.Date >= openingBalance.Date))
                    .OrderByDescending(item => item.Cents == cents).ThenBy(item => Math.Abs((item.Date - on).TotalDays))
                    .Select(item => new PostedChoice { Entry = item }).ToList();
                matchFields.Visibility = matches.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
                if (matches.Count > 0) matches.Add(new PostedChoice());
                firstMatches.ItemsSource = matches;
                firstMatches.SelectedIndex = -1;
            };
            Action updatePaymentFields = delegate
            {
                DateTime first;
                bool past = adding && linked == null && TryEntryDate(due.Text, out first) && first <= Today;
                paymentFields.Visibility = past ? Visibility.Visible : Visibility.Collapsed;
                paymentDateField.Visibility = past && firstStatus.SelectedIndex > 0 ? Visibility.Visible : Visibility.Collapsed;
                if (past) firstLabel.Content = plan.IsIncome ? "Já recebeu o valor dessa data?" : "Já pagou a cobrança dessa data?";
                updateMatches();
            };
            due.TextChanged += delegate
            {
                firstStatus.SelectedIndex = -1;
                paymentDate.Text = due.Text;
                updatePaymentFields();
            };
            paymentDate.TextChanged += delegate { updateMatches(); };
            description.TextChanged += delegate { updateMatches(); };
            amount.TextChanged += delegate { updateMatches(); };
            firstStatus.SelectionChanged += delegate
            {
                DateTime first;
                bool alreadyBeforeOpening = hasOpening && TryEntryDate(due.Text, out first) && first < openingBalance.Date;
                paymentDate.Text = firstStatus.SelectedIndex == 2 && alreadyBeforeOpening ? ReportDate(Today) : due.Text;
                updatePaymentFields();
            };
            paymentDate.Text = due.Text;
            updatePaymentFields();
            if (!canEditDates)
                fields.Children.Add(new TextBlock {
                    Text = "As datas e os valores de pagamentos confirmados podem ser corrigidos em Lançamentos.",
                    TextWrapping = TextWrapping.Wrap, Foreground = Tone("Muted"), Margin = new Thickness(0, 0, 0, 8)
                });
            TextBlock feedback = new TextBlock { Foreground = Tone("DangerText"), TextWrapping = TextWrapping.Wrap };
            fields.Children.Add(feedback);
            Button save = PrimaryDialogButton("Salvar");
            fields.Children.Add(DialogActions(save));
            dialog.MaxHeight = SystemParameters.WorkArea.Height - 24;
            dialog.Content = new ScrollViewer { Content = fields, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            dialog.Loaded += delegate { description.Focus(); };
            bool confirmedFirst = false;
            BillPlan savedPlan = null;
            save.Click += delegate
            {
                string name = description.Text.Trim();
                long cents;
                DateTime date;
                int installments = 0;
                int installmentNumber = 1;
                FixedExpenseType choice = (FixedExpenseType)kind.SelectedIndex;
                if (choice == FixedExpenseType.OneTime) installments = 1;
                bool annual = choice == FixedExpenseType.AnnualSubscription;
                bool yearlyPlan = choice == FixedExpenseType.AnnualMonthly;
                if (name.Length == 0 || !LedgerEntry.TryAmount(amount.Text, out cents))
                {
                    feedback.Text = "Informe uma descrição e um valor positivo, como 39,90.";
                    return;
                }
                if (!TryEntryDate(due.Text, out date))
                {
                    feedback.Text = "Informe uma data válida no formato dia/mês/ano.";
                    return;
                }
                if (!plan.IsIncome && annual && count.Text.Trim().Length > 0 &&
                    (!int.TryParse(count.Text, out installments) || installments < 1 || installments > 30))
                {
                    feedback.Text = "Informe de 1 a 30 cobranças anuais ou deixe esse campo em branco.";
                    return;
                }
                if (!plan.IsIncome && (yearlyPlan || choice == FixedExpenseType.Installment) && (!int.TryParse(count.Text, out installments) || installments < 1 ||
                    installments > BillPlan.MaxInstallments || !int.TryParse(firstInstallment.Text, out installmentNumber) ||
                    installmentNumber < 1 || installmentNumber > installments))
                {
                    feedback.Text = "Informe de 1 a 360 parcelas e uma parcela inicial dentro desse total.";
                    return;
                }
                if (installments > 0 && date.Year * 12 + date.Month + (installments - installmentNumber) * (annual ? 12 : 1) > 9998 * 12 + 12)
                {
                    feedback.Text = "As datas deste plano vão além do calendário. Revise a data inicial ou a quantidade.";
                    return;
                }
                bool askFirst = adding && linked == null && date <= Today;
                if (askFirst && firstStatus.SelectedIndex < 0)
                {
                    feedback.Text = plan.IsIncome ? "Escolha se você já recebeu o primeiro valor ou se ele está em aberto." :
                        "Escolha se a primeira cobrança já foi paga ou se está em aberto.";
                    firstStatus.Focus();
                    return;
                }
                bool registerFirst = askFirst && firstStatus.SelectedIndex > 0;
                bool included = hasOpening && firstStatus.SelectedIndex == 1;
                DateTime paidOn = date;
                if (registerFirst && (!TryEntryDate(paymentDate.Text, out paidOn) || paidOn > Today ||
                    (hasOpening && (included ? paidOn > openingBalance.Date : paidOn < openingBalance.Date))))
                {
                    feedback.Text = hasOpening ? "Confira a data em que ocorreu e se o valor já estava no saldo inicial." :
                        "Informe a data real, até hoje.";
                    return;
                }
                PostedChoice match = firstMatches.SelectedItem as PostedChoice;
                if (registerFirst && matchFields.Visibility == Visibility.Visible && match == null)
                {
                    feedback.Text = "Escolha o lançamento existente ou selecione Registrar outro lançamento.";
                    firstMatches.Focus();
                    return;
                }
                Safely(delegate
                {
                    BillPlan updated = new BillPlan {
                        Id = plan.Id, Description = name, Category = (string)category.SelectedItem,
                        Cents = cents, FirstDue = date, Installments = installments, FirstInstallment = installmentNumber,
                        IsIncome = plan.IsIncome, Kind = annual ? BillKind.Annual : yearlyPlan ? BillKind.AnnualMonthly : BillKind.Monthly,
                        IsMonthlyBill = !plan.IsIncome && (choice == FixedExpenseType.MonthlyBill || choice == FixedExpenseType.OneTime),
                        CreatedOn = Today
                    };
                    if (linked != null) store.SaveNewPlanWithFirstPayment(updated, linked.Date, false, linked);
                    else if (registerFirst) store.SaveNewPlanWithFirstPayment(updated, paidOn, included, match == null ? null : match.Entry);
                    else store.SavePlan(updated);
                    savedPlan = updated;
                    confirmedFirst = registerFirst || linked != null;
                    if (currentPage == "Bills" && showIncomePlans && (adding || date != plan.FirstDue))
                    {
                        month = MonthStart(date);
                        agendaMode = AgendaMode.Month;
                    }
                    dialog.DialogResult = true;
                });
            };
            bool saved = dialog.ShowDialog() == true;
            if (saved)
            {
                ReloadBills();
                UpdateClosures();
                RefreshMonth();
                if (confirmedFirst) RefreshChat();
                if (currentPage == "Chat")
                    AddBubble(AppName, "“" + description.Text.Trim() + "” cadastrado em " +
                        (plan.IsIncome ? "Ganhos fixos." : "Contas futuras."), false, plan: chatSubscription ? savedPlan : null);
            }
            return saved;
        }
    }
}
