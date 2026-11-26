using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;

namespace SpentZ
{
    internal sealed partial class Program
    {
        private List<ReserveBalance> monthReserves = new List<ReserveBalance>();
        private decimal reserveNet;

        private void WireReserves()
        {
            Click("ReservesNav", delegate { Navigate("Reserves"); });
            Click("QuickCofrinho", delegate { EnterReserveMovement(); });
            Click("SummaryCofrinho", delegate { EnterReserveMovement(); });
            Click("ReserveMove", delegate { EnterReserveMovement(null, false); });
            Click("ReserveWithdraw", delegate { EnterReserveMovement(null, true); });
            Click("ReserveStarting", delegate { EnterReserveMovement(null, false, true); });
            Click("OpenGoals", delegate { Navigate("Goals"); });
            Click("CreateGoal", delegate { EditGoal(null); });
            Click("GoalArchiveList", delegate { Navigate("ArchivedGoals"); });
            Click("GoalDeposit", delegate { EnterReserveMovement(null, false); });
            Click("ReserveEdit", delegate { EnterReserveMovement(Get<DataGrid>("ReserveGrid").SelectedItem as ReserveMovement); });
            Click("ReserveRemove", RemoveReserveMovement);
            DataGrid grid = Get<DataGrid>("ReserveGrid");
            grid.SelectionChanged += delegate
            {
                Get<Button>("ReserveRemove").IsEnabled = grid.SelectedItem != null;
                Get<Button>("ReserveEdit").IsEnabled = grid.SelectedItem != null;
            };
            grid.SizeChanged += delegate { SizeDescriptionColumn(grid); };
            Get<Expander>("SummaryReserveDetails").Expanded += delegate { RefreshSummaryReserveMoves(); };
        }

        private void RefreshReserveNumbers()
        {
            monthReserves = store.ReserveBalances(month);
            decimal added = monthReserves.Sum(place => (decimal)place.Added);
            decimal withdrawn = monthReserves.Sum(place => (decimal)place.Withdrawn);
            reserveNet = added - withdrawn;
            string total = LedgerEntry.Money(monthReserves.Sum(place => (decimal)place.Balance));
            string activity = "Guardado no mês − " + LedgerEntry.Money(added) + "  ·  Resgatado no mês + " + LedgerEntry.Money(withdrawn);
            Get<TextBlock>("SummaryReserve").Text = total;
            Get<TextBlock>("SummaryReserveActivity").Text = activity;
            bool usingOpening = openingBalance != null && openingBalance.Date < month.AddMonths(1);
            availableBalance = usingOpening ? store.AvailableBalance(month) : monthlyBalance - reserveNet;
            Get<TextBlock>("SummaryAvailable").Text = (usingOpening ? "Saldo disponível · " : "Disponível no mês após reservas · ") +
                LedgerEntry.Money(availableBalance);
            TextBlock starting = Get<TextBlock>("SummaryOpening");
            bool firstMonth = usingOpening && MonthStart(openingBalance.Date) == month;
            starting.Visibility = firstMonth ? Visibility.Visible : Visibility.Collapsed;
            if (firstMonth)
                starting.Text = "Ponto de partida em " + ReportDate(openingBalance.Date) + " · " + LedgerEntry.Money(openingBalance.Cents);
            TextBlock sinceOpening = Get<TextBlock>("SummaryExpensesSinceOpening");
            sinceOpening.Visibility = firstMonth ? Visibility.Visible : Visibility.Collapsed;
            if (firstMonth)
                sinceOpening.Text = "Despesas desde o saldo inicial · " + LedgerEntry.Money(store.ExpensesSinceOpening(openingBalance, month.AddMonths(1)));
            Get<ItemsControl>("SummaryReservePlaces").ItemsSource = monthReserves;
            Get<TextBlock>("ReserveTotal").Text = total;
            Get<TextBlock>("ReserveActivity").Text = activity;
            Expander details = Get<Expander>("SummaryReserveDetails");
            details.Visibility = added > 0 || withdrawn > 0 ? Visibility.Visible : Visibility.Collapsed;
            if (details.IsExpanded) RefreshSummaryReserveMoves();
            if (currentPage == "Reserves") RefreshReservePage();
        }

        private void RefreshSummaryReserveMoves()
        {
            Get<ItemsControl>("SummaryReserveMoves").ItemsSource = store.ReserveMovements(month);
        }

        private void RefreshReservePage()
        {
            Get<ItemsControl>("ReservePlaces").ItemsSource = monthReserves;
            Get<TextBlock>("ReserveEmpty").Visibility = monthReserves.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            DataGrid grid = Get<DataGrid>("ReserveGrid");
            grid.ItemsSource = store.ReserveMovements(month);
            SizeDescriptionColumn(grid);
            Get<Button>("ReserveRemove").IsEnabled = false;
            Get<Button>("ReserveEdit").IsEnabled = false;
        }

        private void EnterReserveMovement(ReserveMovement edit = null, bool? initialWithdrawal = null, bool alreadySaved = false)
        {
            List<string> names = store.ReservePlaces();
            if (edit != null && edit.Initial) initialWithdrawal = false;
            Window dialog = CreateDialog(edit != null ? "Editar movimentação" : alreadySaved ? "Registrar dinheiro já guardado" : "Cofrinho", 420);
            StackPanel fields = new StackPanel { Margin = new Thickness(20) };
            fields.Children.Add(new TextBlock {
                Text = "Total no cofrinho hoje", Foreground = Tone("Muted")
            });
            fields.Children.Add(new TextBlock {
                Text = LedgerEntry.Money(store.ReserveBalances(Today, Today).Sum(place => (decimal)place.Balance)),
                FontSize = 22, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 2, 0, 12)
            });
            ComboBox operation = new ComboBox {
                ItemsSource = new[] { "Depositar", "Sacar" }, SelectedIndex = (initialWithdrawal ?? (edit != null && edit.Withdraw)) ? 1 : 0
            };
            if (!initialWithdrawal.HasValue) AddField(fields, "O que você quer fazer?", operation);
            ComboBox places = new ComboBox();
            Label placeLabel = AddField(fields, "Local", places);
            TextBlock placeBalance = new TextBlock { Foreground = Tone("AccentText"), Margin = new Thickness(0, 0, 0, 8) };
            fields.Children.Add(placeBalance);
            StackPanel nameField = new StackPanel();
            TextBox name = new TextBox { MaxLength = 80, Padding = new Thickness(7) };
            AddField(nameField, "Nome do local (ex.: PicPay ou Caixinha em casa)", name);
            fields.Children.Add(nameField);

            TextBox amount = new TextBox { Text = edit == null ? "" : (edit.Cents / 100m).ToString("0.00", LedgerEntry.Brazilian),
                MaxLength = 16, Padding = new Thickness(7) };
            TextBox date = new TextBox {
                Text = ReportDate(edit == null ? Today : edit.Date),
                MaxLength = 10, Padding = new Thickness(7)
            };
            AddField(fields, "Valor (R$)", amount);
            if (alreadySaved) fields.Children.Add(new TextBlock {
                Text = "Use para dinheiro que já estava separado antes de você começar. O saldo disponível permanece igual.",
                TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10)
            });
            AddField(fields, "Data da movimentação (dia/mês/ano)", date);
            TextBlock feedback = new TextBlock { Foreground = Tone("DangerText"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) };
            fields.Children.Add(feedback);
            Button save = PrimaryDialogButton("Depositar");
            fields.Children.Add(DialogActions(save));
            dialog.Content = fields;
            Func<bool> withdrawing = () => operation.SelectedIndex == 1;
            Func<string, DateTime, decimal> balanceAt = (place, on) =>
                store.ReserveBalances(on, on).Where(item => item.Place == place).Sum(item => (decimal)item.Balance) +
                (edit != null && edit.Place == place && edit.Date <= on ? (edit.Withdraw ? edit.Cents : -edit.Cents) : 0);
            Action updatePlace = delegate
            {
                bool newPlace = !withdrawing() && places.SelectedIndex == names.Count;
                nameField.Visibility = newPlace ? Visibility.Visible : Visibility.Collapsed;
                string selected = places.SelectedItem as string;
                DateTime on;
                placeBalance.Text = "";
                if (withdrawing() && names.Count == 0) placeBalance.Text = "Faça um depósito para poder sacar.";
                else if (!newPlace && selected != null && TryEntryDate(date.Text, out on) && on <= Today)
                    placeBalance.Text = "Saldo no local na data escolhida · " + LedgerEntry.Money(balanceAt(selected, on));
                placeBalance.Visibility = placeBalance.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
                save.IsEnabled = !withdrawing() || names.Count > 0;
            };
            Action updateOperation = delegate
            {
                string previous = places.SelectedItem as string;
                bool withdraw = withdrawing();
                placeLabel.Content = withdraw ? "De onde sai o dinheiro?" : "Onde vai guardar?";
                save.Content = edit != null ? "Salvar alteração" : alreadySaved ? "Registrar saldo guardado" : withdraw ? "Sacar" : "Depositar";
                feedback.Text = "";
                places.ItemsSource = withdraw ? names : names.Concat(new[] { "Cadastrar outro local…" }).ToList();
                places.SelectedIndex = Math.Max(0, names.IndexOf(previous));
                updatePlace();
            };
            places.SelectionChanged += delegate { updatePlace(); };
            date.TextChanged += delegate { updatePlace(); };
            operation.SelectionChanged += delegate { updateOperation(); };
            updateOperation();
            if (edit != null) places.SelectedItem = edit.Place;
            dialog.Loaded += delegate { if (initialWithdrawal.HasValue) places.Focus(); else operation.Focus(); };
            string result = "";
            save.Click += delegate
            {
                bool withdraw = withdrawing();
                string destination = !withdraw && places.SelectedIndex == names.Count ? name.Text.Trim() : (string)places.SelectedItem;
                long cents;
                DateTime on;
                if (string.IsNullOrWhiteSpace(destination) || !LedgerEntry.TryAmount(amount.Text, out cents))
                {
                    feedback.Text = "Informe o local e um valor positivo, como 200,00.";
                    return;
                }
                if (!TryEntryDate(date.Text, out on) || on > Today)
                {
                    feedback.Text = "Informe uma data válida até hoje.";
                    return;
                }
                if (withdraw && cents > balanceAt(destination, on))
                {
                    feedback.Text = "O valor do saque é maior que o saldo desse local na data escolhida.";
                    return;
                }
                try
                {
                    store.AddReserveMovement(destination, on, cents, withdraw, edit, alreadySaved);
                    if (currentPage == "Reserves" || currentPage == "Summary") month = MonthStart(on);
                    result = LedgerEntry.Money(cents) + (withdraw ? " resgatados de " : " guardados em ") + destination + ".";
                    dialog.DialogResult = true;
                }
                catch (System.IO.IOException error) { feedback.Text = error.Message; }
            };
            if (dialog.ShowDialog() != true) return;
            UpdateClosures();
            RefreshMonth();
            if (currentPage == "Goals") RefreshGoals();
            if (currentPage == "Chat") AddBubble(AppName, result, false);
        }

        private void RemoveReserveMovement()
        {
            ReserveMovement entry = Get<DataGrid>("ReserveGrid").SelectedItem as ReserveMovement;
            if (entry == null) return;
            if (MessageBox.Show(window, "Remover a movimentação de " + entry.AmountDisplay + " em “" + entry.Place + "”?",
                "Remover movimentação", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;
            store.RemoveReserveMovement(entry);
            UpdateClosures();
            RefreshMonth();
        }
    }
}
