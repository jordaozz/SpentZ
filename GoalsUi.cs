using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;

namespace SpentZ
{
    internal sealed partial class Program
    {
        private void RefreshGoals()
        {
            bool showArchivedGoals = currentPage == "ArchivedGoals";
            StackPanel cards = Get<StackPanel>("GoalCards");
            cards.Children.Clear();
            var goals = store.SavingsGoals(showArchivedGoals);
            Get<WrapPanel>("GoalPageActions").Visibility = showArchivedGoals ? Visibility.Collapsed : Visibility.Visible;
            decimal free = showArchivedGoals ? 0 : store.GoalFundingByPlace(0).Sum(place => (decimal)place.Free);
            TextBlock status = Get<TextBlock>("GoalStatus");
            status.Text = showArchivedGoals ? (goals.Count == 0 ? "Nenhuma meta arquivada." : "") : goals.Count == 0 ?
                "Crie uma meta para começar, como Moto ou Viagem." :
                free > 0 ? "Livre no Cofrinho para novas metas · " + LedgerEntry.Money(free) :
                "Para reservar mais, guarde dinheiro no Cofrinho ou libere um valor de outra meta.";
            status.Visibility = status.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
            foreach (SavingsGoal goal in goals)
            {
                var content = new StackPanel();
                content.Children.Add(new TextBlock {
                    Text = goal.Name, TextWrapping = TextWrapping.Wrap, FontWeight = FontWeights.Bold, FontSize = 18
                });
                content.Children.Add(new TextBlock {
                    Text = LedgerEntry.Money(goal.Saved) + " reservado" + (goal.Spent > 0 ? " · " + LedgerEntry.Money(goal.Spent) + " usado" : "") +
                        (goal.Progress >= goal.Target ? " · Meta alcançada" : " · Faltam " + LedgerEntry.Money(goal.Target - goal.Progress)),
                    TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 7)
                });
                ProgressBar progress = new ProgressBar {
                    Maximum = 100, Value = Math.Min(100, 100.0 * goal.Progress / goal.Target), Height = 8,
                    Background = Tone("AccentSoft"), Foreground = Tone("Accent"), Margin = new Thickness(0, 0, 0, 10)
                };
                AutomationProperties.SetName(progress, "Progresso de " + goal.Name);
                content.Children.Add(progress);
                WrapPanel actions = new WrapPanel();
                if (showArchivedGoals)
                    actions.Children.Add(GoalAction("Reativar meta", goal, () => { store.SetGoalArchived(goal.Id, false); RefreshGoals(); }));
                else
                {
                    var reserve = GoalAction("Reservar valor", goal, () => EditGoalFunding(goal, false));
                    reserve.IsEnabled = free > 0;
                    actions.Children.Add(reserve);
                    if (goal.Saved > 0) actions.Children.Add(GoalAction("Usar na compra", goal, () => EditGoalFunding(goal, true, true)));
                }
                content.Children.Add(actions);
                var details = new Expander { Header = "Detalhes e histórico", Margin = new Thickness(0, 4, 0, 0) };
                details.Expanded += delegate
                {
                    if (details.Content != null) return;
                    var body = new StackPanel { Margin = new Thickness(0, 6, 0, 0) };
                    body.Children.Add(new TextBlock { Text = "Alvo · " + LedgerEntry.Money(goal.Target) });
                    foreach (GoalFunding place in store.GoalFundingByPlace(goal.Id).Where(item => item.Reserved > 0))
                        body.Children.Add(new TextBlock { Text = place.Place + " · " + LedgerEntry.Money(place.Reserved), TextWrapping = TextWrapping.Wrap });
                    if (!showArchivedGoals)
                    {
                        var extra = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
                        if (goal.Saved > 0) extra.Children.Add(GoalAction("Liberar valor", goal, () => EditGoalFunding(goal, true)));
                        extra.Children.Add(GoalAction("Editar meta", goal, () => EditGoal(goal)));
                        extra.Children.Add(GoalAction("Arquivar", goal, () => ArchiveGoal(goal)));
                        body.Children.Add(extra);
                    }
                    foreach (GoalPurchase purchase in store.GoalPurchases(goal.Id))
                    {
                        body.Children.Add(new TextBlock { Text = purchase.ToString(), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 3) });
                        if (!showArchivedGoals)
                            body.Children.Add(GoalAction("Desfazer esta compra", goal, () => UndoPurchase(goal, purchase)));
                    }
                    details.Content = body;
                };
                content.Children.Add(details);
                cards.Children.Add(new Border {
                    Style = (Style)window.Resources["Card"], Child = content, Margin = new Thickness(0, 0, 0, 12)
                });
            }
        }

        private void ArchiveGoal(SavingsGoal goal)
        {
            if (MessageBox.Show(window, "Arquivar “" + goal.Name + "”? O histórico fica em Ver arquivadas. " +
                "O que ainda está reservado volta a ficar livre no Cofrinho.", "Arquivar meta", MessageBoxButton.YesNo,
                MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;
            store.SetGoalArchived(goal.Id, true);
            RefreshGoals();
        }

        private void UndoPurchase(SavingsGoal goal, GoalPurchase purchase)
        {
            if (MessageBox.Show(window, "Remover a despesa registrada e devolver o valor à meta?",
                "Desfazer compra da meta", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;
            store.UndoGoalPurchase(goal.Id, purchase.ExpenseId);
            RefreshLedgerViews();
            RefreshGoals();
        }

        private Button GoalAction(string label, SavingsGoal goal, Action action)
        {
            Button button = new Button { Content = label, Margin = new Thickness(0, 0, 6, 4) };
            if (label == "Editar meta") button.ContentTemplate = (DataTemplate)window.Resources["EditWithIcon"];
            AutomationProperties.SetName(button, label + " · " + goal.Name);
            button.Click += delegate { Safely(action); };
            return button;
        }

        private void EditGoal(SavingsGoal goal)
        {
            Window dialog = CreateDialog(goal == null ? "Criar meta" : "Editar meta", 420);
            var fields = new StackPanel { Margin = new Thickness(20) };
            TextBox name = new TextBox { Text = goal == null ? "" : goal.Name, MaxLength = 80, Padding = new Thickness(7) };
            TextBox target = new TextBox {
                Text = goal == null ? "" : (goal.Target / 100m).ToString("0.00", LedgerEntry.Brazilian),
                MaxLength = 16, Padding = new Thickness(7)
            };
            AddField(fields, "Nome da meta", name);
            AddField(fields, "Quanto você quer guardar (R$)", target);
            TextBlock feedback = new TextBlock { Foreground = Tone("DangerText"), TextWrapping = TextWrapping.Wrap };
            fields.Children.Add(feedback);
            Button save = PrimaryDialogButton("Salvar meta");
            fields.Children.Add(DialogActions(save));
            dialog.Content = fields;
            dialog.Loaded += delegate { name.Focus(); };
            save.Click += delegate
            {
                try
                {
                    long cents;
                    if (!LedgerEntry.TryAmount(target.Text, out cents)) throw new IOException("Informe um valor positivo para a meta.");
                    store.SaveSavingsGoal(goal == null ? 0 : goal.Id, name.Text, cents);
                    dialog.DialogResult = true;
                }
                catch (IOException error) { feedback.Text = error.Message; }
            };
            if (dialog.ShowDialog() == true) RefreshGoals();
        }

        private void EditGoalFunding(SavingsGoal goal, bool release, bool purchase = false)
        {
            var places = store.GoalFundingByPlace(goal.Id).Where(place => (release ? place.Reserved : place.Free) > 0).ToList();
            if (places.Count == 0) { RefreshGoals(); return; }
            string action = purchase ? "Registrar compra" : release ? "Liberar valor" : "Reservar valor";
            Window dialog = CreateDialog(action + " · " + goal.Name, 425);
            var fields = new StackPanel { Margin = new Thickness(20) };
            fields.Children.Add(new TextBlock {
                Text = purchase ? "Registre uma compra feita hoje com o dinheiro desta meta. O SpentZ atualiza o Cofrinho e lança a despesa." :
                    release ? "O valor fica livre no mesmo local do Cofrinho." :
                    "Separe dinheiro que já está no Cofrinho. O dinheiro continua no mesmo local.",
                TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12)
            });
            ComboBox placePicker = new ComboBox {
                ItemsSource = places.Select(place => place.Place + " · " + LedgerEntry.Money(release ? place.Reserved : place.Free)).ToArray(),
                SelectedIndex = 0
            };
            AddField(fields, release ? "Local do valor reservado" : "De onde vem o valor?", placePicker);
            TextBox amount = new TextBox { MaxLength = 16, Padding = new Thickness(7) };
            AddField(fields, "Valor (R$)", amount);
            TextBox description = new TextBox { Text = goal.Name, MaxLength = 240, Padding = new Thickness(7) };
            ComboBox category = new ComboBox { ItemsSource = store.ExpenseCategories(), SelectedItem = "Compras" };
            if (purchase)
            {
                AddField(fields, "Descrição da compra", description);
                AddField(fields, "Categoria", category);
            }
            TextBlock feedback = new TextBlock { Foreground = Tone("DangerText"), TextWrapping = TextWrapping.Wrap };
            fields.Children.Add(feedback);
            Button save = PrimaryDialogButton(action);
            fields.Children.Add(DialogActions(save));
            dialog.Content = fields;
            dialog.Loaded += delegate { if (places.Count == 1) amount.Focus(); else placePicker.Focus(); };
            save.Click += delegate
            {
                try
                {
                    long cents;
                    if (!LedgerEntry.TryAmount(amount.Text, out cents)) throw new IOException("Informe um valor positivo.");
                    long place = places[placePicker.SelectedIndex].PlaceId;
                    if (purchase)
                    {
                        if (description.Text.Trim().Length == 0) throw new IOException("Informe uma descrição para a compra.");
                        store.BuyFromGoal(goal.Id, place, cents, Today, description.Text.Trim(), (string)category.SelectedItem);
                    }
                    else store.MoveGoalFunds(goal.Id, place, cents, release);
                    dialog.DialogResult = true;
                }
                catch (IOException error) { feedback.Text = error.Message; }
            };
            if (dialog.ShowDialog() != true) return;
            if (purchase) RefreshLedgerViews();
            RefreshGoals();
        }
    }
}
