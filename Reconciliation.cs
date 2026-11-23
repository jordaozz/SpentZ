using System;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;

namespace SpentZ
{
    internal sealed partial class Program
    {
        private sealed class PostedChoice
        {
            internal LedgerEntry Entry;
            public override string ToString()
            {
                return Entry == null ? "Registrar outro lançamento" :
                    Entry.DateDisplay + "  ·  " + Entry.AmountDisplay + "  ·  " + Entry.Description;
            }
        }

        private static bool SamePaymentDescription(string left, string right)
        {
            return LedgerEntry.Brazilian.CompareInfo.Compare(left, right, CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace) == 0;
        }

        private void ReconcileBill()
        {
            BillOccurrence bill = SelectedBill;
            if (bill == null || bill.Payment != null) return;
            bool income = bill.Plan.IsIncome;
            Window dialog = CreateDialog(income ? "Confirmar recebimento" : "Confirmar pagamento", 465);
            StackPanel body = new StackPanel { Margin = new Thickness(20) };
            body.Children.Add(new TextBlock {
                Text = bill.Description, FontWeight = FontWeights.SemiBold, FontSize = 17
            });
            body.Children.Add(new TextBlock {
                Text = "Previsto para " + bill.DueDisplay + "  ·  " + bill.AmountDisplay,
                Foreground = Tone("Muted"), Margin = new Thickness(0, 3, 0, 14)
            });

            TextBox date = new TextBox { Text = Today.ToString("dd/MM/yyyy", LedgerEntry.Brazilian), MaxLength = 10, Padding = new Thickness(7) };
            Label dateLabel = AddField(body, income ? "Data em que recebeu (dia/mês/ano)" : "Data em que pagou (dia/mês/ano)", date);
            TextBox amount = new TextBox {
                Text = (bill.Cents / 100m).ToString("0.00", LedgerEntry.Brazilian), MaxLength = 16, Padding = new Thickness(7)
            };
            StackPanel amountFields = new StackPanel();
            AddField(amountFields, income ? "Valor recebido (R$)" : "Valor pago (R$)", amount);
            body.Children.Add(amountFields);

            CheckBox linkExisting = new CheckBox { Content = "Já registrei esse lançamento no SpentZ", Margin = new Thickness(0, 3, 0, 10) };
            body.Children.Add(linkExisting);
            StackPanel linkFields = new StackPanel { Visibility = Visibility.Collapsed };
            ComboBox candidates = new ComboBox { MaxDropDownHeight = 230 };
            AddField(linkFields, "Vincular lançamento do mês indicado", candidates);
            body.Children.Add(linkFields);
            CheckBox alreadyInOpening = null;
            if (openingBalance != null)
            {
                alreadyInOpening = new CheckBox { Content = "Esse valor já estava no saldo inicial", Margin = new Thickness(0, 0, 0, 10) };
                body.Children.Add(alreadyInOpening);
            }

            TextBlock hint = new TextBlock { Foreground = Tone("Muted"), Margin = new Thickness(0, 0, 0, 7) };
            TextBlock feedback = new TextBlock { Foreground = Tone("DangerText"), Margin = new Thickness(0, 0, 0, 7) };
            body.Children.Add(hint);
            body.Children.Add(feedback);
            Button confirm = PrimaryDialogButton(income ? "Confirmar recebimento" : "Confirmar pagamento");
            body.Children.Add(DialogActions(confirm));
            dialog.Content = body;
            dialog.Loaded += delegate { date.Focus(); date.SelectAll(); };

            DateTime? listedMonth = null;
            PostedChoice suggested = null;
            Func<LedgerEntry, bool> sameName = entry => SamePaymentDescription(entry.Description, bill.Description);
            Action refresh = delegate
            {
                DateTime dateInput;
                DateTime? targetMonth = TryEntryDate(date.Text, out dateInput) && dateInput <= Today
                    ? (DateTime?)MonthStart(dateInput) : null;
                if (targetMonth != listedMonth || candidates.ItemsSource == null)
                {
                    listedMonth = targetMonth;
                    candidates.ItemsSource = targetMonth.HasValue
                        ? store.UnlinkedEntries(targetMonth.Value, income).Where(item => item.Date <= Today)
                            .OrderByDescending(sameName).ThenByDescending(item => item.Cents == bill.Cents)
                            .Select(item => new PostedChoice { Entry = item }).ToList()
                        : new PostedChoice[0].ToList();
                    candidates.SelectedIndex = -1;
                }
                var matches = targetMonth.HasValue ? candidates.Items.Cast<PostedChoice>().Where(item =>
                    sameName(item.Entry) && item.Entry.Cents == bill.Cents &&
                    (Math.Abs((item.Entry.Date - dateInput).TotalDays) <= 3 || Math.Abs((item.Entry.Date - bill.Due).TotalDays) <= 3))
                    .Take(2).ToArray() : new PostedChoice[0];
                suggested = matches.Length == 1 ? matches[0] : null;
                bool linking = linkExisting.IsChecked == true;
                if (alreadyInOpening != null)
                {
                    bool applies = !linking && targetMonth.HasValue && dateInput <= openingBalance.Date;
                    alreadyInOpening.Visibility = applies ? Visibility.Visible : Visibility.Collapsed;
                    alreadyInOpening.IsEnabled = applies && dateInput == openingBalance.Date;
                    if (!alreadyInOpening.IsEnabled) alreadyInOpening.IsChecked = applies;
                }
                linkFields.Visibility = linking ? Visibility.Visible : Visibility.Collapsed;
                amountFields.Visibility = linking ? Visibility.Collapsed : Visibility.Visible;
                dateLabel.Content = linking ? "Mês do registro (digite uma data desse mês)"
                    : income ? "Data em que recebeu (dia/mês/ano)" : "Data em que pagou (dia/mês/ano)";
                confirm.Content = linking ? "Vincular lançamento" : matches.Length > 0 ?
                    (income ? "Registrar outro recebimento" : "Registrar outro pagamento") :
                    income ? "Confirmar recebimento" : "Confirmar pagamento";
                bool likelyDuplicate = candidates.Items.Cast<PostedChoice>().Any(item => sameName(item.Entry) && item.Entry.Cents == bill.Cents);
                hint.Text = linking && targetMonth.HasValue && candidates.Items.Count == 0
                    ? "Nenhum lançamento disponível neste mês. Você pode escolher outra data."
                    : !linking && likelyDuplicate ? "Há um lançamento com o mesmo nome e valor neste mês. Se for o mesmo, vincule-o." : "";
                hint.Visibility = hint.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
                feedback.Text = "";
            };
            date.TextChanged += delegate { Safely(refresh); };
            linkExisting.Checked += delegate { Safely(refresh); candidates.Focus(); };
            linkExisting.Unchecked += delegate { Safely(refresh); };
            refresh();
            if (suggested != null)
            {
                linkExisting.IsChecked = true;
                candidates.SelectedItem = suggested;
            }

            DateTime recordedOn = Today;
            bool linked = false;
            bool inOpening = false;
            confirm.Click += delegate
            {
                DateTime enteredDate;
                if (!TryEntryDate(date.Text, out enteredDate) || enteredDate > Today)
                {
                    feedback.Text = "Informe uma data válida, até hoje, no formato dia/mês/ano.";
                    return;
                }
                PostedChoice choice = candidates.SelectedItem as PostedChoice;
                bool linking = linkExisting.IsChecked == true;
                long cents = 0;
                if (linking && choice == null)
                {
                    feedback.Text = "Escolha o lançamento que deseja vincular.";
                    return;
                }
                if (!linking && !LedgerEntry.TryAmount(amount.Text, out cents))
                {
                    feedback.Text = "Informe um valor positivo, como 42,90.";
                    return;
                }
                Safely(delegate
                {
                    LedgerEntry existing = linking ? choice.Entry : null;
                    recordedOn = linking ? existing.Date : enteredDate;
                    inOpening = !linking && alreadyInOpening != null && alreadyInOpening.IsChecked == true;
                    if (!store.PayBill(bill.Plan.Id, bill.Due, recordedOn, linking ? (long?)null : cents, existing, inOpening))
                    {
                        feedback.Text = "A previsão já foi confirmada em outra janela.";
                        return;
                    }
                    linked = linking;
                    dialog.DialogResult = true;
                });
            };
            if (dialog.ShowDialog() != true) return;
            if (recordedOn < MonthStart(Today)) UpdateClosures();
            ReloadBills();
            RefreshMonth();
            RefreshChat();
            string notice = linked ? "Lançamento existente vinculado." :
                inOpening ? "Confirmação guardada no histórico. O valor já fazia parte do saldo inicial." :
                (income ? "Recebimento" : "Pagamento") + " registrado em " + ReportDate(recordedOn) + ".";
            if (!linked && !income && !inOpening)
            {
                decimal available = store.AvailableBalance(recordedOn);
                notice += " Disponível em " + recordedOn.ToString("MMMM", LedgerEntry.Brazilian) + " · " + LedgerEntry.Money(available) + ".";
            }
            ShowAgendaNotice(notice);
        }

        private void UnlinkRecordedBill()
        {
            BillOccurrence bill = SelectedBill;
            if (bill == null || bill.Payment == null) return;
            string prompt = bill.Payment.InOpeningBalance ? "Reabrir “" + bill.Description + "”? Se ainda estiver prevista, esta ocorrência volta a ficar em aberto." :
                "Desvincular “" + bill.Description + "”? O lançamento fica no histórico. Uma previsão ativa volta a aparecer como pendente.";
            if (MessageBox.Show(window, prompt,
                "Desvincular lançamento", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;
            bool removed = store.UnlinkBill(bill);
            ReloadBills();
            ShowAgendaNotice(!removed ? "O vínculo já foi alterado em outra janela." : bill.Payment.InOpeningBalance ?
                "Confirmação removida do histórico." : "Vínculo removido. O lançamento continua registrado.");
        }

        private void ShowAgendaNotice(string message)
        {
            TextBlock status = Get<TextBlock>("BillCancelStatus");
            status.Text = message;
            status.Visibility = Visibility.Visible;
        }
    }
}
