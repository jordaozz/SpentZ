using System;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Threading;

namespace SpentZ
{
    internal sealed partial class Program
    {
        private static readonly Regex ParcelVerb = new Regex(LedgerEntry.ChatStart + @"(?<keyword>parcelei|parcelar|parcelado|parcelamento)\b\s*", RegexOptions.IgnoreCase);
        private static readonly Regex ParcelCount = new Regex(@"\b(?:em\s+)?(?<keyword>(?<count>[0-9]{1,3})\s*(?:x\b|×|parcelas\b))", RegexOptions.IgnoreCase);
        private static readonly Regex SubscriptionKeyword = new Regex(@"\b(?<keyword>assinatura|assinei|assinar)\b", RegexOptions.IgnoreCase);
        private static readonly Regex SubscriptionMarker = new Regex(
            @"\b(?:(?:fiz|paguei|contratei|comprei|renovei)\s+(?:(?:uma|a)\s+)?|(?:pela?|uma)\s+)?(?:assinatura|assinei|assinar)\b(?:\s+(?:do|da|de|dos|das|[oa]|por)\b)?",
            RegexOptions.IgnoreCase);
        private static readonly Regex SubscriptionCycle = new Regex(
            @"\b(?:(?<annual>anual|(?:ao|por)\s+ano)|(?<monthly>mensal|(?:ao|por)\s+m[eê]s))\b", RegexOptions.IgnoreCase);
        private static readonly Regex SubscriptionDate = new Regex(@"\b(?:em\s+|dia\s+)?(?<date>ontem|hoje|[0-9]{1,2}/[0-9]{1,2}/[0-9]{4})\b", RegexOptions.IgnoreCase);
        private bool chatHighlightQueued;
        private bool chatFormatting;

        private string ChatText
        {
            get
            {
                var box = Get<RichTextBox>("ChatInput");
                return new TextRange(box.Document.ContentStart, box.Document.ContentEnd).Text.TrimEnd('\r', '\n');
            }
            set
            {
                var box = Get<RichTextBox>("ChatInput");
                box.Document.Blocks.Clear();
                box.Document.Blocks.Add(new Paragraph(new Run(value)) { Margin = new Thickness(0) });
                box.CaretPosition = box.Document.ContentEnd.GetInsertionPosition(LogicalDirection.Backward);
                QueueChatHighlight();
            }
        }

        private static bool HasInstallmentIntent(string text)
        {
            return !LedgerEntry.IsTentativeChat(text) &&
                (ParcelVerb.IsMatch(text) || (ParcelCount.IsMatch(text) && !LedgerEntry.IncomePrefix.IsMatch(text)));
        }

        private static Match ChatSubscription(string text)
        {
            return SubscriptionKeyword.Match(text);
        }

        private void QueueChatHighlight()
        {
            if (chatFormatting || chatHighlightQueued) return;
            chatHighlightQueued = true;
            window.Dispatcher.BeginInvoke(DispatcherPriority.Background, (Action)RefreshChatHighlight);
        }

        private void RefreshChatHighlight()
        {
            chatHighlightQueued = false;
            string message = ChatText;
            Match verb = ParcelVerb.Match(message);
            Match income = LedgerEntry.IncomePrefix.Match(message);
            Match expense = LedgerEntry.ExpensePrefix.Match(message);
            Match subscription = ChatSubscription(message);
            bool isSubscription = subscription.Success && !income.Success;
            bool installments = !isSubscription && HasInstallmentIntent(message);
            Group token = (isSubscription ? subscription : installments ? (verb.Success ? verb : ParcelCount.Match(message)) :
                income.Success ? income : expense).Groups["keyword"];
            string tone = isSubscription || installments ? "AccentText" : income.Success ? "IncomeInk" : "ExpenseInk";
            TextBlock preview = Get<TextBlock>("ChatIntentPreview");
            bool tentative = LedgerEntry.IsTentativeChat(message);
            string label = tentative ? ChatHelpForPending :
                isSubscription ? "Assinatura · revise a frequência e a data antes de salvar" :
                installments ? "Parcelamento · abre para você revisar as parcelas" :
                income.Success ? "Ganho" : "Despesa";
            preview.Text = message.Trim().Length == 0 ? "" : message.Length > 240 ? "Use até 240 caracteres." : label;
            preview.Visibility = preview.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
            Paint(preview, TextBlock.ForegroundProperty,
                tentative || message.Length > 240 ? "Muted" : tone);

            RichTextBox box = Get<RichTextBox>("ChatInput");
            chatFormatting = true;
            try
            {
                box.BeginChange();
                new TextRange(box.Document.ContentStart, box.Document.ContentEnd).ApplyPropertyValue(TextElement.ForegroundProperty, Tone("Ink"));
                if (!tentative && token.Success)
                {
                    TextPointer start = ChatPosition(box.Document.ContentStart, token.Index);
                    TextPointer end = ChatPosition(box.Document.ContentStart, token.Index + token.Length);
                    if (start != null && end != null)
                        new TextRange(start, end).ApplyPropertyValue(TextElement.ForegroundProperty, Tone(tone));
                }
            }
            finally { box.EndChange(); chatFormatting = false; }
        }

        private static TextPointer ChatPosition(TextPointer point, int remaining)
        {
            while (point != null)
            {
                if (point.GetPointerContext(LogicalDirection.Forward) == TextPointerContext.Text)
                {
                    int size = point.GetTextRunLength(LogicalDirection.Forward);
                    if (remaining <= size) return point.GetPositionAtOffset(remaining);
                    remaining -= size;
                }
                point = point.GetNextContextPosition(LogicalDirection.Forward);
            }
            return null;
        }

        private void OpenChatSubscription(string text, string[] categories, LedgerEntry existing = null)
        {
            DateTime due = existing == null ? Today : existing.Date;
            Match when = SubscriptionDate.Match(text);
            if (existing == null && when.Success)
            {
                string date = when.Groups["date"].Value;
                DateTime parsed;
                if (date.Equals("ontem", StringComparison.OrdinalIgnoreCase)) due = Today.AddDays(-1);
                else if (TryEntryDate(date, out parsed)) due = parsed;
            }
            Match[] cycles = SubscriptionCycle.Matches(text).Cast<Match>().ToArray();
            bool annual = cycles.Any(item => item.Groups["annual"].Success);
            bool monthly = cycles.Any(item => item.Groups["monthly"].Success);
            string name = SubscriptionMarker.Replace(text, " ");
            name = SubscriptionCycle.Replace(SubscriptionDate.Replace(name, " "), " ");
            Match parcels = ParcelCount.Match(name);
            bool annualMonthly = annual && (monthly || parcels.Success && parcels.Groups["count"].Value == "12");
            if (parcels.Success) name = name.Remove(parcels.Index, parcels.Length);
            name = Regex.Replace(name, @"\b(?:por|de|no\s+valor\s+de)\s+(?=(?:R\$\s*)?[0-9])", " ", RegexOptions.IgnoreCase);
            name = Regex.Replace(name, @"\s+", " ").Trim(' ', ',', '.', '–', '-');
            name = Regex.Replace(name, @"^(?:eu\s+)?(?:(?:fiz|contratei|renovei|vou)\s+)?", "", RegexOptions.IgnoreCase);
            LedgerEntry entry = existing == null ? LedgerEntry.FromMessage(name, due, categories) : null;
            long cents = existing != null ? existing.Cents :
                entry != null && !LedgerEntry.HasAmbiguousAmount(name) ? entry.Cents : 0;
            if (entry != null) name = entry.Description == "Gasto sem descrição" ? "" : entry.Description;
            name = Regex.Replace(name, @"^[oa]\s+", "", RegexOptions.IgnoreCase);
            if (name.Length > 120) name = name.Substring(0, 120).TrimEnd();
            var plan = new BillPlan {
                Description = name,
                Category = existing != null && existing.Category != "Sem categoria" ? existing.Category :
                    (LedgerEntry.FromMessage("1 " + name, due, categories) ?? new LedgerEntry { Category = "Sem categoria" }).Category,
                Cents = cents, FirstDue = due,
                Kind = annualMonthly ? BillKind.AnnualMonthly : annual ? BillKind.Annual : BillKind.Monthly,
                Installments = annualMonthly ? 12 : 0
            };
            if (EditBill(plan, null, true, existing) && existing == null) ChatText = "";
        }

        private bool CanTurnIntoSubscription(LedgerEntry entry)
        {
            return entry != null && !entry.IsIncome && entry.Kind == 0 && entry.Date <= Today &&
                ChatSubscription(entry.Description).Success && store.CanLinkExpense(entry.Id);
        }

        private void ConvertSubscription(LedgerEntry entry)
        {
            if (CanTurnIntoSubscription(entry)) OpenChatSubscription(entry.Description, store.ExpenseCategories(), entry);
        }

        private void OpenChatInstallments(string text, string[] categories)
        {
            string body = ParcelVerb.Replace(text, "", 1).Trim();
            Match split = ParcelCount.Match(body);
            int count;
            bool validCount = split.Success && int.TryParse(split.Groups["count"].Value, out count) && count >= 2 && count <= 360;
            count = validCount ? int.Parse(split.Groups["count"].Value) : 2;
            string suffix = split.Success ? body.Substring(split.Index + split.Length).Trim() : "";
            bool each = split.Success && split.Index == 0 && suffix.StartsWith("de ", StringComparison.OrdinalIgnoreCase);
            string subject = split.Success ? (each ? suffix.Substring(3) : body.Remove(split.Index, split.Length)).Trim() : body;
            LedgerEntry entry = LedgerEntry.FromMessage(subject, Today, categories);
            long cents = entry == null ? 0 : each ? entry.Cents :
                validCount && entry.Cents % count == 0 ? entry.Cents / count : 0;
            string note = cents == 0 ? "Informe o valor de cada parcela antes de salvar." :
                "Confira o valor de cada parcela e o primeiro vencimento.";
            if (EditBill(new BillPlan {
                Description = entry == null ? "" : entry.Description, Category = entry == null ? "Sem categoria" : entry.Category,
                Cents = cents, FirstDue = Today, Installments = count
            }, note))
                ChatText = "";
        }

        private string ChatLimitNotice(LedgerEntry entry)
        {
            if (entry.IsIncome) return "";
            CategoryProgress progress = store.CategoryProgressFor(entry.Date).FirstOrDefault(item => item.Name == entry.Category);
            if (progress == null || progress.Used < progress.Limit * 0.8m) return "";
            return progress.Remaining < 0 ? "\nLimite de " + progress.Name + " ultrapassado em " + LedgerEntry.Money(-progress.Remaining) + "." :
                progress.Remaining == 0 ? "\nVocê atingiu o limite de " + progress.Name + "." :
                "\nRestam " + LedgerEntry.Money(progress.Remaining) + " no limite de " + progress.Name + ".";
        }
    }
}
