using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;

namespace SpentZ
{
    internal sealed partial class Program
    {
        private decimal accountAfterBills;

        private void WireAccount()
        {
            Click("OpenAccount", delegate { Navigate("Account"); Get<ScrollViewer>("AccountPage").ScrollToTop(); });
            Click("AccountHistory", delegate { Navigate("History", () => month = MonthStart(Today)); });
            Click("AccountReserves", delegate { Navigate("Reserves", () => month = MonthStart(Today)); });
            Click("AccountIncome", delegate { Navigate("Income", () => month = MonthStart(Today)); });
            Click("AccountBills", delegate { Navigate("Bills", delegate {
                showIncomePlans = false;
                month = MonthStart(Today);
                agendaMode = AgendaMode.ThroughMonth;
            }); });
            Click("AccountPlans", delegate { Navigate("Bills", delegate {
                showIncomePlans = false;
                agendaMode = AgendaMode.Plans;
            }); });
            Click("AccountOpening", delegate { ShowOpeningBalance(false); });
            Get<TextBox>("AccountPurchase").TextChanged += delegate { RefreshPurchaseSimulation(); };
        }

        private void RefreshAccount()
        {
            DateTime today = Today;
            DateTime start = MonthStart(today);
            DateTime end = start.AddMonths(1).AddDays(-1);
            decimal balance = store.AvailableBalance(today, today);
            decimal overdue = bills.PendingAmountThrough(today.AddDays(-1));
            decimal pending = bills.PendingAmountThrough(end);
            decimal incoming = bills.PendingAmountThrough(end, true) - bills.PendingAmountThrough(start.AddDays(-1), true);
            accountAfterBills = balance - pending;
            bool hasOpening = openingBalance != null && openingBalance.Date <= today;

            Get<TextBlock>("AccountDate").Text = "Posição em " + ReportDate(today);
            Get<TextBlock>("AccountCashLabel").Text = hasOpening ? "Saldo disponível hoje" : "Saldo registrado neste mês";
            Get<TextBlock>("AccountCash").Text = LedgerEntry.Money(balance);
            Get<TextBlock>("AccountCommitted").Text = LedgerEntry.Money(pending);
            Get<TextBlock>("AccountAfterLabel").Text = "Saldo após contas até " + end.ToString("dd/MM");
            TextBlock after = Get<TextBlock>("AccountAfter");
            after.Text = LedgerEntry.Money(accountAfterBills);
            Paint(after, TextBlock.ForegroundProperty, accountAfterBills < 0 ? "DangerText" : "AccentText");
            Get<TextBlock>("AccountStatus").Text = accountAfterBills < 0 ?
                (pending == 0 ? "O saldo registrado está negativo." : "Faltam " + LedgerEntry.Money(-accountAfterBills) + " para cobrir as contas registradas.") :
                accountAfterBills == 0 ? "Nenhum valor livre após as contas registradas." : "Valor restante antes de novos gastos.";
            Get<Button>("AccountOpening").Visibility = hasOpening ? Visibility.Collapsed : Visibility.Visible;

            Get<TextBlock>("AccountExpenses").Text = LedgerEntry.Money(store.LedgerTotal(start, today.AddDays(1)));
            Get<TextBlock>("AccountReceived").Text = LedgerEntry.Money(store.LedgerTotal(start, today.AddDays(1), true));
            Get<TextBlock>("AccountExpected").Text = LedgerEntry.Money(incoming);
            Get<TextBlock>("AccountOverdue").Text = pending == 0 ? "Nenhuma conta pendente até o fim do mês." :
                overdue > 0 ? LedgerEntry.Money(overdue) + " já vencidos e incluídos no total." : "Nenhuma conta vencida em aberto.";
            Get<ItemsControl>("AccountBillRows").ItemsSource = bills.Pending(today, (end - today).Days, limit: 4);

            var reserves = store.ReserveBalances(today, today).Where(place => place.Balance != 0).ToList();
            Get<TextBlock>("AccountSaved").Text = LedgerEntry.Money(reserves.Sum(place => (decimal)place.Balance));
            Get<ItemsControl>("AccountReserveRows").ItemsSource = reserves.Take(4);
            Get<TextBlock>("AccountReserveMore").Visibility = reserves.Count > 4 ? Visibility.Visible : Visibility.Collapsed;

            var subscriptions = bills.Plans(today, false).Where(row => row.Plan.IsSubscription).ToList();
            int count = subscriptions.Count;
            Get<TextBlock>("AccountSubscriptionCount").Text = count == 0 ? "Nenhuma assinatura ativa." :
                count == 1 ? "1 assinatura ativa" : count + " assinaturas ativas";
            Get<ItemsControl>("AccountSubscriptionRows").ItemsSource = subscriptions.Take(3);
            RefreshPurchaseSimulation();
        }

        private void RefreshPurchaseSimulation()
        {
            string input = Get<TextBox>("AccountPurchase").Text.Trim();
            TextBlock result = Get<TextBlock>("AccountSimulation");
            long cents;
            if (!LedgerEntry.TryAmount(input, out cents))
            {
                result.Text = input.Length == 0 ? "" : "Informe um valor positivo, como 29,90.";
                Paint(result, TextBlock.ForegroundProperty, "Muted");
                return;
            }
            decimal remaining = accountAfterBills - cents;
            result.Text = remaining < 0 ? "Faltariam " + LedgerEntry.Money(-remaining) + " para cobrir a compra e as contas." :
                "Restariam " + LedgerEntry.Money(remaining) + " depois da compra e das contas.";
            Paint(result, TextBlock.ForegroundProperty, remaining < 0 ? "DangerText" : "AccentText");
        }
    }
}
