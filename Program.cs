using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;

[assembly: AssemblyTitle("SpentZ")]
[assembly: AssemblyProduct("SpentZ")]
[assembly: AssemblyVersion("1.2.1.0")]
[assembly: AssemblyFileVersion("1.2.1.0")]
[assembly: AssemblyInformationalVersion("1.2.1")]

namespace SpentZ
{
    // feito por jordaozz
    // github.com/jordaozz
    internal sealed partial class Program
    {
        private const string AppName = "SpentZ";
        private readonly Window window;
        private readonly LedgerStore store;
        private DateTime month;
        private List<LedgerEntry> monthlyExpenses;
        private List<LedgerEntry> monthlyIncomes;
        private decimal monthlyBalance;
        private string currentPage = "Chat";
        private bool summaryDirty = true;

        [STAThread]
        private static int Main(string[] args)
        {
            if (args.Length == 2 && args[0] == "--self-test")
                return SelfTest(args[1]);
            try
            {
                string folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GastoEmDia");
                Directory.CreateDirectory(folder);
                using (FileStream session = TryOpenSession(folder))
                {
                    if (session == null)
                    {
                        MessageBox.Show("O SpentZ já está aberto. Use a janela que está em execução.", AppName);
                        return 0;
                    }
                    using (LedgerStore store = new LedgerStore(Path.Combine(folder, "gastos.sqlite3")))
                    {
                        Application application = new Application();
                        Program program = new Program(store);
                        program.window.ContentRendered += delegate { program.Safely(program.OfferOpeningBalance); };
                        return application.Run(program.window);
                    }
                }
            }
            catch (Exception error)
            {
                MessageBox.Show("Não foi possível abrir o " + AppName + ".\n\n" + error.Message,
                    AppName, MessageBoxButton.OK, MessageBoxImage.Error);
                return 1;
            }
        }

        private static FileStream TryOpenSession(string folder)
        {
            try
            {
                return new FileStream(Path.Combine(folder, "spentz-session.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite,
                    FileShare.None);
            }
            catch (IOException error)
            {
                int code = error.HResult & 0xffff;
                if (code == 32 || code == 33) return null;
                throw;
            }
        }

        private Program(LedgerStore store, Func<DateTime> clock = null)
        {
            this.store = store;
            openingBalance = store.GetOpeningBalance();
            localNow = clock ?? (() => DateTime.Now);
            calendarDay = Today;
            month = MonthStart(calendarDay);
            Assembly assembly = Assembly.GetExecutingAssembly();
            using (Stream markup = assembly.GetManifestResourceStream("Main.xaml"))
                window = (Window)XamlReader.Load(markup);
            using (Stream badge = assembly.GetManifestResourceStream("SpentZ.png"))
            {
                BitmapFrame icon = BitmapFrame.Create(badge, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
                window.Icon = icon;
                Get<Image>("BrandIcon").Source = icon;
            }
            AssemblyInformationalVersionAttribute version = (AssemblyInformationalVersionAttribute)Attribute.GetCustomAttribute(
                assembly, typeof(AssemblyInformationalVersionAttribute));
            Get<TextBlock>("AppVersion").Text = "SpentZ " + version.InformationalVersion;
            Click("ChatNav", delegate { Navigate("Chat"); });
            Click("SummaryNav", delegate { Navigate("Summary"); });
            Click("IncomeNav", delegate { Navigate("Income"); });
            Click("HistoryNav", delegate { Navigate("History"); });
            Click("SettingsNav", delegate { Navigate("Settings"); });
            Click("SettingsBackup", delegate { Navigate("Backup"); });
            Click("BackupSave", SaveBackupFolder);
            Click("BackupRestore", RestoreBackupFolder);
            Click("OpenLastBackup", OpenLastSavedBackup);
            Click("SettingsHelp", delegate { Navigate("Help"); });
            Click("SettingsOpeningBalance", delegate { ShowOpeningBalance(false); });
            Click("HelpOpenChat", delegate { Navigate("Chat"); });
            Click("PrevMonth", delegate { ShiftMonth(-1); });
            Click("NextMonth", delegate { ShiftMonth(1); });
            Click("ReturnCurrentMonth", ReturnToCurrentMonth);
            WireNavigation();
            WireAccount();
            Click("Send", RegisterMessage);
            foreach (string name in new[] { "ExampleMeal", "ExampleRide", "ExampleIncome" })
            {
                Button example = Get<Button>(name);
                example.Click += delegate
                {
                    ChatText = (string)example.Content;
                    Get<RichTextBox>("ChatInput").Focus();
                };
            }
            Click("Edit", delegate { EditEntry(SelectedEntry("HistoryGrid")); });
            Click("HistorySubscription", delegate { ConvertSubscription(SelectedEntry("HistoryGrid")); });
            Click("ExpenseAdd", delegate { AddEntry(false); });
            Click("HistoryAddIncome", delegate { AddEntry(true); });
            Click("QuickExpense", delegate { AddEntry(false); });
            Click("QuickIncome", delegate { AddEntry(true); });
            Click("Delete", delegate { ConfirmRemoval(SelectedEntry("HistoryGrid"), false); });
            Click("IncomeAdd", delegate { AddEntry(true); });
            Click("IncomeEdit", delegate { EditEntry(SelectedEntry("IncomeGrid")); });
            Click("IncomeDelete", delegate { ConfirmRemoval(SelectedEntry("IncomeGrid"), false); });
            Click("Export", ExportExpenses);
            ComboBox historyCategory = Get<ComboBox>("HistoryCategory");
            RefreshHistoryCategories();
            historyCategory.SelectionChanged += delegate { RefreshHistory(); };
            ComboBox historyType = Get<ComboBox>("HistoryType");
            historyType.ItemsSource = new[] { "Todos", "Gastos", "Ganhos" };
            historyType.SelectedIndex = 0;
            historyType.SelectionChanged += delegate { RefreshHistory(); };
            Get<TextBox>("HistorySearch").TextChanged += delegate { RefreshHistory(); };
            RichTextBox chat = Get<RichTextBox>("ChatInput");
            chat.TextChanged += delegate { QueueChatHighlight(); };
            DataObject.AddPastingHandler(chat, delegate(object sender, DataObjectPastingEventArgs args)
            {
                if (args.SourceDataObject.GetDataPresent(DataFormats.UnicodeText)) args.FormatToApply = DataFormats.UnicodeText;
                else args.CancelCommand();
            });
            chat.PreviewKeyDown += delegate(object sender, KeyEventArgs args)
            {
                if (args.Key != Key.Enter) return;
                args.Handled = true;
                Safely(RegisterMessage);
            };
            WireSelection("HistoryGrid", "Edit", "Delete");
            Get<DataGrid>("HistoryGrid").SelectionChanged += delegate
            {
                Get<WrapPanel>("HistoryRowActions").Visibility = SelectedEntry("HistoryGrid") == null ?
                    Visibility.Collapsed : Visibility.Visible;
                Get<Button>("HistorySubscription").Visibility = CanTurnIntoSubscription(SelectedEntry("HistoryGrid")) ?
                    Visibility.Visible : Visibility.Collapsed;
            };
            WireSelection("IncomeGrid", "IncomeEdit", "IncomeDelete");
            WireSettings();
            LoadSettings();
            WireBills();
            WireClosures();
            WireReserves();
            WireBudget();
            WireCalendarClock();
            UpdateClosures();
            bills = store.Bills();
            RefreshChat();
            RefreshMonth();
            Navigate("Chat");
            window.Loaded += delegate { Get<RichTextBox>("ChatInput").Focus(); };
        }

        private void WireSelection(string gridName, string editName, string deleteName)
        {
            DataGrid grid = Get<DataGrid>(gridName);
            Get<Button>(editName).IsEnabled = false;
            Get<Button>(deleteName).IsEnabled = false;
            grid.SelectionChanged += delegate
            {
                LedgerEntry selected = SelectedEntry(gridName);
                Get<Button>(editName).IsEnabled = selected != null && selected.Kind != 2;
                Get<Button>(deleteName).IsEnabled = selected != null;
            };
            grid.MouseDoubleClick += delegate(object sender, MouseButtonEventArgs args)
            {
                if (ItemsControl.ContainerFromElement(grid, args.OriginalSource as DependencyObject) is DataGridRow)
                    Safely(delegate { EditEntry(SelectedEntry(gridName)); });
            };
        }

        private T Get<T>(string name) where T : class
        {
            return (T)window.FindName(name);
        }

        private void Click(string name, Action action)
        {
            Get<Button>(name).Click += delegate { Safely(action); };
        }

        private void Safely(Action action)
        {
            try { action(); }
            catch (IOException error)
            {
                MessageBox.Show(window, error.Message, "Operação indisponível", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            catch (UnauthorizedAccessException error)
            {
                MessageBox.Show(window, error.Message, "Acesso ao arquivo", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void ShowPage(string page)
        {
            currentPage = page;
            string parent = ParentPage();
            Button back = Get<Button>("NavigateBack");
            back.Visibility = parent == null ? Visibility.Collapsed : Visibility.Visible;
            if (parent != null)
            {
                string name = parent == "Goals" ? "Metas" : (string)Get<Button>(parent + "Nav").Content;
                back.Content = "‹  Voltar para " + name;
                AutomationProperties.SetName(back, "Voltar para " + name);
            }
            foreach (string name in new[] { "Chat", "Summary", "Account", "Closures", "Income", "Reserves", "Goals", "Bills", "History", "Backup", "Settings", "Help" })
                Get<FrameworkElement>(name + "Page").Visibility = name == (page == "ArchivedGoals" ? "Goals" : page) ?
                    Visibility.Visible : Visibility.Collapsed;
            string activeNav = page == "Backup" || page == "Help" ? "Settings"
                : page == "Closures" || page == "Account" ? "Summary" : page == "Goals" || page == "ArchivedGoals" ? "Reserves" :
                page == "Bills" && showIncomePlans ? "Income" : page;
            foreach (string name in new[] { "Chat", "Summary", "Income", "Reserves", "Bills", "History", "Settings" })
            {
                bool selected = name == activeNav;
                Button button = Get<Button>(name + "Nav");
                if (selected)
                {
                    Paint(button, Control.BackgroundProperty, "NavSelected");
                    button.Foreground = Brushes.White;
                }
                else
                {
                    button.Background = Brushes.Transparent;
                    Paint(button, Control.ForegroundProperty, "NavText");
                }
            }
            bool isChat = page == "Chat";
            bool usesMonth = page == "Summary" || page == "Income" || page == "History" || page == "Reserves";
            Get<FrameworkElement>("MonthBar").Visibility = usesMonth ? Visibility.Visible : Visibility.Collapsed;
            string title;
            string description;
            switch (page)
            {
                case "Summary":
                    title = "Resumo mensal";
                    description = "Acompanhe suas despesas, seus ganhos e o saldo do mês.";
                    break;
                case "Closures":
                    title = "Fechamentos mensais";
                    description = "Compare ganhos, despesas e saldo por mês.";
                    break;
                case "Account":
                    title = "Acompanhar conta";
                    description = "Veja seu saldo de hoje e as contas que ainda faltam pagar.";
                    break;
                case "Income":
                    title = "Ganhos";
                    description = "Acompanhe salários, trabalhos extras e outras entradas.";
                    break;
                case "Reserves":
                    title = "Cofrinho";
                    description = "Veja seus saldos por local e registre depósitos e saques.";
                    break;
                case "Goals":
                    title = "Metas";
                    description = "Veja quanto já separou para cada objetivo.";
                    break;
                case "ArchivedGoals":
                    title = "Metas arquivadas";
                    description = "Consulte o histórico ou reative uma meta.";
                    break;
                case "History":
                    title = "Lançamentos";
                    description = "Consulte, corrija ou exporte as entradas e despesas do mês.";
                    break;
                case "Help":
                    title = "Como usar";
                    description = "Aprenda a registrar valores, acompanhar seu saldo e corrigir lançamentos.";
                    break;
                case "Backup":
                    title = "Backup e restauração";
                    description = "Guarde seus dados e leve o SpentZ para outro computador.";
                    break;
                case "Bills":
                    title = IsBillHistory ? "Histórico de cobranças" : showIncomePlans ? "Ganhos fixos" : "Contas futuras";
                    description = IsBillHistory ? "Consulte assinaturas canceladas e pagamentos já registrados."
                        : showIncomePlans ? "Planeje salários e outros valores que entram todo mês."
                        : "Confira as próximas cobranças antes de comprometer seu saldo.";
                    break;
                case "Settings":
                    title = "Configurações";
                    description = "Personalize a aparência e os avisos do SpentZ.";
                    Get<Button>("SettingsOpeningBalance").Content = openingBalance == null ? "Saldo inicial" : "Corrigir saldo inicial";
                    break;
                default:
                    title = "Chat";
                    description = "Registre ganhos e despesas em uma mensagem.";
                    break;
            }
            Get<TextBlock>("PageTitle").Text = title;
            Get<TextBlock>("PageDescription").Text = description;
            if (page == "Summary") RefreshSummaryCharts();
            if (page == "Closures") LoadClosurePage();
            if (page == "Reserves") RefreshReservePage();
            if (page == "Goals" || page == "ArchivedGoals")
            {
                RefreshGoals();
                Get<ScrollViewer>("GoalsPage").ScrollToTop();
            }
            if (page == "History") RefreshHistory();
            if (page == "Backup") RefreshBackupInfo();
            RefreshBillViews();
            if (isChat)
            {
                Get<ScrollViewer>("ChatScroll").ScrollToEnd();
                Get<RichTextBox>("ChatInput").Focus();
            }
            else Get<TextBlock>("PageTitle").Focus();
        }

        private void RefreshChat()
        {
            Get<StackPanel>("Messages").Children.Clear();
            AddBubble(AppName, "Digite 42,90 almoço para um gasto ou recebi 150 freela para um ganho. Os mais antigos ficam em Lançamentos.", false);
            foreach (LedgerEntry item in Enumerable.Reverse(store.Recent(Today.AddDays(-2), Today)))
                AddBubble(item.DateDisplay, Confirm(item), false, item, "Remover");
        }

        private static string Confirm(LedgerEntry item)
        {
            return item.TypeDisplay + " registrado · " + item.AmountDisplay + " · " + item.Description + " · " + item.Category;
        }

        private void AddBubble(string sender, string message, bool outgoing, LedgerEntry entry = null,
            string action = "Desfazer", BillPlan plan = null)
        {
            StackPanel messages = Get<StackPanel>("Messages");
            StackPanel content = new StackPanel();
            content.Children.Add(Paint(new TextBlock { Text = sender, FontSize = 12, FontWeight = FontWeights.SemiBold },
                TextBlock.ForegroundProperty, "AccentText"));
            TextBlock text = new TextBlock { Text = message, Margin = new Thickness(0, 4, 0, 0), TextWrapping = TextWrapping.Wrap };
            if (entry != null) Paint(text, TextBlock.ForegroundProperty, entry.IsIncome ? "IncomeInk" : "ExpenseInk");
            content.Children.Add(text);
            if (entry != null || plan != null)
            {
                WrapPanel actions = new WrapPanel { Margin = new Thickness(0, 7, 0, 0) };
                if (plan != null)
                    actions.Children.Add(ChatAction("Ver em Contas futuras", plan.Description, () => OpenActivePlan(plan)));
                else AddChatEntryActions(actions, entry, action);
                content.Children.Add(actions);
            }
            Border bubble = new Border {
                Child = content,
                CornerRadius = outgoing ? new CornerRadius(13, 13, 4, 13) : new CornerRadius(13, 13, 13, 4),
                Padding = new Thickness(15, 10, 15, 10),
                Margin = new Thickness(0, 0, 0, 10), MaxWidth = 520,
                HorizontalAlignment = outgoing ? HorizontalAlignment.Right : HorizontalAlignment.Left
            };
            Paint(bubble, Border.BackgroundProperty, outgoing ? "AccentSoft" : "Alternate");
            messages.Children.Add(bubble);
            if (messages.Children.Count > 19) messages.Children.RemoveAt(1);
            Get<ScrollViewer>("ChatScroll").ScrollToEnd();
        }

        private void AddChatEntryActions(WrapPanel actions, LedgerEntry entry, string action)
        {
            if (entry.Kind != 2)
            {
                Button edit = ChatAction("Editar", entry.Description, () => EditEntry(entry));
                edit.ContentTemplate = (DataTemplate)window.Resources["EditWithIcon"];
                actions.Children.Add(edit);
            }
            if (CanTurnIntoSubscription(entry))
                actions.Children.Add(ChatAction("Transformar em assinatura", entry.Description, () => ConvertSubscription(entry)));
            Button remove = ChatAction(action, entry.Description + " de " + entry.AmountDisplay, () => ConfirmRemoval(entry, true));
            Paint(remove, Control.BackgroundProperty, "AccentSelection");
            actions.Children.Add(remove);
        }

        private Button ChatAction(string label, string description, Action run)
        {
            Button button = new Button { Style = (Style)window.Resources["Example"], Content = label };
            AutomationProperties.SetName(button, label + " " + description);
            button.Click += delegate { Safely(run); };
            return button;
        }

        private const string ChatHelpForPending = "Descreva um lançamento já realizado ou use Gasto fixo ou Ganho fixo para agendar.";

        private void RegisterMessage()
        {
            RichTextBox box = Get<RichTextBox>("ChatInput");
            string message = System.Text.RegularExpressions.Regex.Replace(ChatText.Trim(), @"[\r\n]+", " ");
            if (message.Length == 0) return;
            if (message.Length > 240) { AddBubble(AppName, "Use até 240 caracteres por lançamento.", false); return; }
            if (LedgerEntry.IsTentativeChat(message))
            {
                AddBubble(AppName, ChatHelpForPending, false);
                return;
            }
            string[] categories = store.ExpenseCategories();
            if (!LedgerEntry.IncomePrefix.IsMatch(message) && ChatSubscription(message).Success)
            {
                OpenChatSubscription(message, categories);
                return;
            }
            if (HasInstallmentIntent(message))
            {
                OpenChatInstallments(message, categories);
                return;
            }
            LedgerEntry entry = LedgerEntry.FromMessage(message, Today, categories);
            if (entry != null && LedgerEntry.HasAmbiguousAmount(message))
            {
                LedgerEntry alternate = LedgerEntry.FromMessage(message, Today, categories, true);
                if (alternate != null && alternate.Cents != entry.Cents)
                {
                    entry = ChooseChatAmount(entry, alternate);
                    if (entry == null) return;
                }
            }
            if (entry != null)
                store.Save(entry);
            ChatText = "";
            AddBubble("Você", message, true);
            AddBubble(AppName, entry == null
                ? "Informe o valor em reais. Tente 42,90 almoço, almoço 42,90 ou recebi 150 freela."
                : Confirm(entry) + ChatLimitNotice(entry), false, entry);
            if (entry != null)
            {
                month = new DateTime(entry.Date.Year, entry.Date.Month, 1);
                RefreshMonth();
            }
            box.Focus();
        }

        private LedgerEntry ChooseChatAmount(LedgerEntry first, LedgerEntry last)
        {
            Window dialog = CreateDialog(first.IsIncome ? "Qual é o valor do ganho?" : "Qual é o valor do gasto?", 410);
            StackPanel panel = new StackPanel { Margin = new Thickness(20) };
            ComboBox choice = new ComboBox { ItemsSource = new[] { first, last }.Select(item =>
                item.AmountDisplay + " · " + item.Description).ToArray(), SelectedIndex = 0 };
            AddField(panel, "Como você quer registrar?", choice);
            Button confirm = PrimaryDialogButton(first.IsIncome ? "Confirmar ganho" : "Confirmar gasto");
            confirm.Click += delegate { dialog.DialogResult = true; };
            panel.Children.Add(DialogActions(confirm));
            dialog.Content = panel;
            return dialog.ShowDialog() != true ? null : choice.SelectedIndex == 0 ? first : last;
        }

        private void ShiftMonth(int amount)
        {
            if ((amount < 0 && month == new DateTime(1900, 1, 1)) ||
                (amount > 0 && month == new DateTime(9998, 12, 1))) return;
            month = month.AddMonths(amount);
            RefreshMonth();
        }

        private void RefreshMonth()
        {
            monthlyExpenses = store.Month(month);
            Button picker = Get<Button>("MonthPicker");
            string monthTitle = month.ToString("MMMM yyyy", LedgerEntry.Brazilian);
            Get<Button>("AdjustBalance").IsEnabled = month <= MonthStart(Today);
            picker.Content = monthTitle;
            AutomationProperties.SetName(picker, monthTitle + ". Escolher mês e ano");
            Get<Button>("ReturnCurrentMonth").Visibility = month == MonthStart(Today) ? Visibility.Collapsed : Visibility.Visible;
            decimal total = monthlyExpenses.Sum(expense => (decimal)expense.Cents);
            Get<TextBlock>("Total").Text = LedgerEntry.Money(total);
            Get<TextBlock>("Count").Text = monthlyExpenses.Count.ToString(LedgerEntry.Brazilian) + " despesas";
            Get<TextBlock>("Average").Text = "Média por gasto · " + LedgerEntry.Money(monthlyExpenses.Count == 0 ? 0 : total / monthlyExpenses.Count);
            Get<TextBlock>("Comparison").Text = CompareWithPreviousMonth(Today);
            summaryDirty = true;
            RefreshIncome(total);
            if (currentPage == "History") RefreshHistory();
            if (currentPage == "Summary") RefreshSummaryCharts();
            RefreshReserveNumbers();
            RefreshBillViews();
        }

        private void RefreshSummaryCharts()
        {
            if (!summaryDirty) return;
            RefreshTrend();
            RefreshCategories(monthlyExpenses.Sum(expense => (decimal)expense.Cents));
            RefreshBudget();
            summaryDirty = false;
        }

        private void RefreshIncome(decimal expenses)
        {
            monthlyIncomes = store.Month(month, true);
            decimal income = monthlyIncomes.Sum(entry => (decimal)entry.Cents);
            monthlyBalance = income - expenses;
            Get<TextBlock>("SummaryIncome").Text = LedgerEntry.Money(income);
            Get<TextBlock>("IncomeTotal").Text = LedgerEntry.Money(income);
            Get<TextBlock>("IncomeExpenses").Text = LedgerEntry.Money(expenses);
            foreach (string name in new[] { "SummaryBalance", "IncomeBalance" })
            {
                TextBlock label = Get<TextBlock>(name);
                label.Text = LedgerEntry.Money(monthlyBalance);
                Paint(label, TextBlock.ForegroundProperty, monthlyBalance < 0 ? "DangerText" : "AccentText");
            }
            Get<DataGrid>("IncomeGrid").ItemsSource = monthlyIncomes;
            Get<TextBlock>("IncomeStatus").Text = monthlyIncomes.Count == 0 ? "Nenhuma entrada registrada neste mês."
                : monthlyIncomes.Count == 1 ? "1 entrada registrada" : monthlyIncomes.Count + " entradas registradas";
        }

        private string CompareWithPreviousMonth(DateTime today)
        {
            DateTime previous = month.AddMonths(-1);
            bool current = month.Year == today.Year && month.Month == today.Month;
            int lastDay = current ? today.Day : 31;
            DateTime next = previous.AddMonths(1);
            DateTime through = previous.AddDays(lastDay);
            decimal previousTotal = store.LedgerTotal(previous, through < next ? through : next);
            if (previousTotal == 0) return "Sem gastos no período anterior";

            decimal currentTotal = monthlyExpenses.Where(item => item.Date.Day <= lastDay).Sum(item => (decimal)item.Cents);
            decimal difference = currentTotal - previousTotal;
            string change = difference == 0 ? "Mesmo total" : (difference > 0 ? "+" : "−") + LedgerEntry.Money(Math.Abs(difference));
            string period = previous.ToString("MMM yyyy", LedgerEntry.Brazilian);
            return change + " vs. " + period + (current ? " (dias 1 a " + lastDay + ")" : "");
        }

        private void RefreshHistory()
        {
            if (monthlyExpenses == null || monthlyIncomes == null) return;
            string query = Get<TextBox>("HistorySearch").Text.Trim();
            ComboBox category = Get<ComboBox>("HistoryCategory");
            bool filterCategory = category.SelectedIndex > 0;
            CompareInfo compare = LedgerEntry.Brazilian.CompareInfo;
            int type = Get<ComboBox>("HistoryType").SelectedIndex;
            List<LedgerEntry> visible = monthlyExpenses.Concat(monthlyIncomes).Where(item =>
                (type <= 0 || item.IsIncome == (type == 2)) &&
                (!filterCategory || item.Category == (string)category.SelectedItem) &&
                compare.IndexOf(item.Description, query, CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace) >= 0)
                .OrderByDescending(item => item.Date).ThenByDescending(item => item.Id).ToList();
            Get<Button>("Export").IsEnabled = monthlyExpenses.Count + monthlyIncomes.Count > 0;
            Get<DataGrid>("HistoryGrid").ItemsSource = visible;
            Get<Button>("Edit").IsEnabled = false;
            Get<Button>("Delete").IsEnabled = false;
            TextBlock status = Get<TextBlock>("HistoryStatus");
            if (visible.Count == 0)
            {
                status.Text = monthlyExpenses.Count + monthlyIncomes.Count == 0 ? "Nenhum lançamento neste mês." :
                    "Nenhum lançamento com esses filtros.";
                return;
            }
            string rowCount = visible.Count == 1 ? "1 lançamento" : visible.Count + " lançamentos";
            status.Text = rowCount + "  ·  Entradas " + LedgerEntry.Money(visible.Where(item => item.IsIncome).Sum(item => (decimal)item.Cents)) +
                "  ·  Despesas " + LedgerEntry.Money(visible.Where(item => !item.IsIncome).Sum(item => (decimal)item.Cents));
        }

        private void RefreshHistoryCategories()
        {
            ComboBox category = Get<ComboBox>("HistoryCategory");
            string selected = category.SelectedItem as string;
            category.ItemsSource = new[] { "Todas as categorias" }.Concat(store.ExpenseCategories())
                .Concat(LedgerEntry.IncomeCategories).Concat(new[] { "Ajuste de saldo" }).Distinct().ToArray();
            category.SelectedItem = selected;
            if (category.SelectedIndex < 0) category.SelectedIndex = 0;
        }

        private void RefreshTrend()
        {
            StackPanel trend = Get<StackPanel>("Trend");
            trend.Children.Clear();
            TextBlock hint = Get<TextBlock>("ChartHint");
            Border peakCard = Get<Border>("PeakCard");
            if (monthlyExpenses.Count == 0)
            {
                hint.Text = "O gráfico aparece quando houver gastos no mês.";
                peakCard.Visibility = Visibility.Collapsed;
                return;
            }

            int days = DateTime.DaysInMonth(month.Year, month.Month);
            decimal[] daily = new decimal[days];
            foreach (LedgerEntry expense in monthlyExpenses)
                daily[expense.Date.Day - 1] += expense.Cents;
            decimal max = daily.Max();
            int peakDay = Array.IndexOf(daily, max) + 1;
            peakCard.Visibility = Visibility.Visible;
            Get<TextBlock>("Peak").Text = month.AddDays(peakDay - 1).ToString("dd/MM", LedgerEntry.Brazilian) + "  ·  " + LedgerEntry.Money(max);
            hint.Text = "Passe o mouse sobre uma barra para ver o valor.";

            const double height = 130;
            Grid plot = new Grid { Height = height };
            UniformGrid guides = new UniformGrid { Columns = 1, Rows = 4, IsHitTestVisible = false };
            for (int row = 0; row < 4; row++)
                guides.Children.Add(Paint(new Border { BorderThickness = new Thickness(0, 1, 0, 0) }, Border.BorderBrushProperty, "Outline"));
            plot.Children.Add(guides);

            UniformGrid bars = new UniformGrid { Columns = days, Rows = 1 };
            for (int index = 0; index < days; index++)
            {
                int day = index + 1;
                DateTime date = month.AddDays(index);
                decimal value = daily[index];
                string detail = date.ToString("dd MMM", LedgerEntry.Brazilian) + "  ·  " + LedgerEntry.Money(value);
                Grid slot = new Grid { Background = Brushes.Transparent, ToolTip = detail };
                AutomationProperties.SetName(slot, detail);
                if (value > 0)
                {
                    Border bar = new Border {
                        VerticalAlignment = VerticalAlignment.Bottom,
                        CornerRadius = new CornerRadius(3, 3, 0, 0),
                        Margin = new Thickness(1, 0, 1, 0),
                        Height = Math.Max(3, height * (double)(value / max))
                    };
                    slot.Children.Add(Paint(bar, Border.BackgroundProperty, day == peakDay ? "AccentBar" : "AccentBarLight"));
                }
                bars.Children.Add(slot);
            }
            plot.Children.Add(bars);
            trend.Children.Add(Paint(new Border { BorderThickness = new Thickness(0, 0, 0, 1), Child = plot },
                Border.BorderBrushProperty, "InputOutline"));
            Grid ticks = new Grid { Margin = new Thickness(0, 4, 0, 0) };
            for (int day = 1; day <= days; day++)
                ticks.ColumnDefinitions.Add(new ColumnDefinition());
            foreach (int day in new[] { 1, 10, 20, days })
            {
                TextBlock tick = new TextBlock { Text = day.ToString(LedgerEntry.Brazilian), FontSize = 12, TextWrapping = TextWrapping.NoWrap };
                Paint(tick, TextBlock.ForegroundProperty, "Muted");
                Grid.SetColumn(tick, day == days ? days - 2 : day - 1);
                Grid.SetColumnSpan(tick, 2);
                if (day == days) tick.HorizontalAlignment = HorizontalAlignment.Right;
                ticks.Children.Add(tick);
            }
            trend.Children.Add(ticks);
        }

        private void RefreshCategories(decimal total)
        {
            StackPanel categories = Get<StackPanel>("Categories");
            categories.Children.Clear();
            if (monthlyExpenses.Count == 0)
            {
                categories.Children.Add(Paint(new TextBlock { Text = "Ainda não há categorias para mostrar." },
                    TextBlock.ForegroundProperty, "Muted"));
                return;
            }
            var groups = monthlyExpenses.GroupBy(expense => expense.Category)
                .Select(group => new { Name = group.Key, Total = group.Sum(expense => (decimal)expense.Cents) })
                .OrderByDescending(group => group.Total).ThenBy(group => group.Name);
            foreach (var group in groups)
            {
                double fraction = (double)(group.Total / total);
                DockPanel label = new DockPanel { Margin = new Thickness(0, 0, 0, 5) };
                TextBlock percent = Paint(new TextBlock { Text = fraction.ToString("P0", LedgerEntry.Brazilian), FontSize = 12, Margin = new Thickness(6, 0, 0, 0) },
                    TextBlock.ForegroundProperty, "Muted");
                DockPanel.SetDock(percent, Dock.Right);
                label.Children.Add(percent);
                StackPanel details = new StackPanel();
                details.Children.Add(new TextBlock { Text = group.Name, FontWeight = FontWeights.SemiBold });
                details.Children.Add(Paint(new TextBlock { Text = LedgerEntry.Money(group.Total), FontSize = 12 }, TextBlock.ForegroundProperty, "Muted"));
                label.Children.Add(details);
                categories.Children.Add(label);
                Grid track = new Grid { Height = 6, Margin = new Thickness(0, 0, 0, 11) };
                track.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(fraction, GridUnitType.Star) });
                track.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1 - fraction, GridUnitType.Star) });
                Border background = Paint(new Border { CornerRadius = new CornerRadius(4) }, Border.BackgroundProperty, "AccentSoft");
                Grid.SetColumnSpan(background, 2);
                track.Children.Add(background);
                track.Children.Add(Paint(new Border { CornerRadius = new CornerRadius(4) }, Border.BackgroundProperty, "AccentBar"));
                categories.Children.Add(track);
            }
        }

        private LedgerEntry SelectedEntry(string gridName)
        {
            return Get<DataGrid>(gridName).SelectedItem as LedgerEntry;
        }

        private void AddEntry(bool income)
        {
            DateTime today = Today;
            EditEntry(new LedgerEntry {
                IsIncome = income, Date = currentPage == "Chat" || month == MonthStart(today) ? today : month,
                Description = "", Category = income ? "Outros ganhos" : "Sem categoria"
            });
        }

        private void EditEntry(LedgerEntry selected)
        {
            if (selected == null || selected.Kind == 2) return;
            bool adding = selected.Id == 0;
            string noun = selected.IsIncome ? "ganho" : "gasto";
            string defaultDescription = selected.IsIncome ? "Ganho sem descrição" : "Gasto sem descrição";
            Window dialog = CreateDialog((adding ? "Adicionar " : "Editar ") + noun, 390);
            StackPanel fields = new StackPanel { Margin = new Thickness(20) };
            TextBox description = new TextBox { Text = selected.Description, MaxLength = 240, Padding = new Thickness(6) };
            if (selected.Description == defaultDescription) description.Clear();
            TextBox amount = new TextBox { Text = adding ? "" : (selected.Cents / 100m).ToString("0.00", LedgerEntry.Brazilian), MaxLength = 16, Padding = new Thickness(6) };
            TextBox date = new TextBox { Text = selected.DateDisplay, MaxLength = 10, Padding = new Thickness(6) };
            ComboBox category = new ComboBox {
                ItemsSource = selected.Kind == 1 ? new[] { selected.Category } :
                    selected.IsIncome ? LedgerEntry.IncomeCategories : store.ExpenseCategories(),
                SelectedItem = selected.Category
            };
            AddField(fields, "Descrição", description);
            AddField(fields, "Valor em reais", amount);
            AddField(fields, "Data (dia/mês/ano)", date);
            AddField(fields, "Categoria", category);
            TextBlock feedback = new TextBlock { Foreground = Tone("DangerText"), TextWrapping = TextWrapping.Wrap };
            fields.Children.Add(feedback);
            Button save = PrimaryDialogButton("Salvar");
            Button schedule = PrimaryDialogButton("Agendar conta avulsa");
            StackPanel actions = DialogActions(save);
            actions.Children.Add(schedule);
            fields.Children.Add(actions);
            dialog.Content = fields;
            dialog.Loaded += delegate { description.Focus(); };
            Action setDateMode = delegate
            {
                DateTime when;
                bool future = TryEntryDate(date.Text, out when) && when > Today;
                bool canSchedule = adding && !selected.IsIncome && future;
                schedule.Visibility = canSchedule ? Visibility.Visible : Visibility.Collapsed;
                save.Visibility = canSchedule ? Visibility.Collapsed : Visibility.Visible;
                schedule.IsDefault = canSchedule;
                save.IsDefault = !canSchedule;
                feedback.Text = canSchedule ? "A conta fica em Contas futuras até você confirmar o pagamento." : "";
            };
            date.TextChanged += delegate { setDateMode(); };
            setDateMode();
            bool scheduled = false;
            Action<bool> submit = delegate(bool asPlan)
            {
                long cents;
                DateTime when;
                if (!LedgerEntry.TryAmount(amount.Text, out cents))
                {
                    feedback.Text = "Informe um valor positivo, como 42,90.";
                    return;
                }
                if (!TryEntryDate(date.Text, out when))
                {
                    feedback.Text = "Informe uma data válida no formato dia/mês/ano.";
                    return;
                }
                if (when > Today && !asPlan)
                {
                    feedback.Text = "Use uma data até hoje para registrar um lançamento realizado.";
                    return;
                }
                if (adding && !asPlan && openingBalance != null && when < openingBalance.Date)
                {
                    feedback.Text = "O controle começa em " + ReportDate(openingBalance.Date) +
                        ". Use essa data ou uma posterior para um novo lançamento.";
                    return;
                }
                string name = description.Text.Trim();
                Safely(delegate
                {
                    if (asPlan)
                    {
                        store.SavePlan(new BillPlan {
                            Description = name.Length == 0 ? "Conta avulsa" : name, Category = (string)category.SelectedItem,
                            Cents = cents, FirstDue = when, Installments = 1, IsMonthlyBill = true, CreatedOn = Today
                        });
                        scheduled = true;
                        dialog.DialogResult = true;
                        return;
                    }
                    store.Save(new LedgerEntry {
                        Id = selected.Id, Version = selected.Version, Date = when, Cents = cents, IsIncome = selected.IsIncome, Kind = selected.Kind,
                        Description = name.Length == 0 ? defaultDescription : name,
                        Category = (string)category.SelectedItem
                    });
                    if (adding || MonthStart(when) != MonthStart(selected.Date)) month = MonthStart(when);
                    dialog.DialogResult = true;
                });
            };
            save.Click += delegate { submit(false); };
            schedule.Click += delegate { submit(true); };
            if (dialog.ShowDialog() == true)
            {
                if (adding && !selected.IsIncome)
                {
                    Get<TextBox>("HistorySearch").Clear();
                    Get<ComboBox>("HistoryCategory").SelectedIndex = 0;
                }
                ReloadBills();
                UpdateClosures();
                RefreshMonth();
                RefreshChat();
                if (adding && currentPage == "Chat")
                    AddBubble(AppName, scheduled ? "Conta avulsa agendada em Contas futuras." :
                        selected.IsIncome ? "Ganho registrado." : "Gasto registrado.", false);
            }
        }

        private static Label AddField(Panel panel, string title, Control field)
        {
            Label label = new Label { Content = title, Target = field, Padding = new Thickness(0, 0, 0, 4) };
            AutomationProperties.SetLabeledBy(field, label);
            panel.Children.Add(label);
            field.Margin = new Thickness(0, 0, 0, 10);
            panel.Children.Add(field);
            return label;
        }

        private static bool TryEntryDate(string text, out DateTime result)
        {
            return DateTime.TryParseExact(text.Trim(), "dd/MM/yyyy", LedgerEntry.Brazilian, DateTimeStyles.None, out result)
                && result.Year >= 1900 && result.Year <= 9998;
        }

        private void ConfirmRemoval(LedgerEntry selected, bool fromChat)
        {
            if (selected == null) return;
            string noun = selected.IsIncome ? "ganho" : "gasto";
            if (MessageBox.Show(window, "Remover o " + noun + " “" + selected.Description + "” de " + selected.AmountDisplay + "?",
                "Remover " + noun, MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;
            if (!RemoveEntry(selected, fromChat))
                MessageBox.Show(window, "O lançamento foi alterado ou já foi removido.", "Lançamento atualizado");
        }

        private bool RemoveEntry(LedgerEntry entry, bool fromChat)
        {
            bool removed = store.Delete(entry);
            if (removed && entry.Date < MonthStart(Today)) UpdateClosures();
            ReloadBills();
            RefreshMonth();
            RefreshChat();
            if (removed && fromChat)
                AddBubble(AppName, "Removi o lançamento de " + entry.AmountDisplay + " · " + entry.Description + ".", false);
            return removed;
        }

        private void ExportExpenses()
        {
            SaveFileDialog dialog = new SaveFileDialog {
                Title = "Exportar lançamentos do mês", Filter = "Arquivo CSV (*.csv)|*.csv",
                FileName = "lancamentos-" + month.ToString("yyyy-MM", CultureInfo.InvariantCulture)
            };
            if (dialog.ShowDialog(window) == true) WriteCsv(dialog.FileName, monthlyExpenses.Concat(monthlyIncomes)
                .OrderByDescending(entry => entry.Date), true);
        }

        private static void WriteCsv(string filename, IEnumerable<LedgerEntry> expenses, bool includeType = false)
        {
            using (StreamWriter writer = new StreamWriter(filename, false, new UTF8Encoding(true)))
            {
                writer.WriteLine("Data;Descrição;Categoria;Valor (BRL)" + (includeType ? ";Tipo" : ""));
                foreach (LedgerEntry item in expenses)
                    writer.WriteLine(item.DateDisplay + ";" + CsvText(item.Description) + ";" + CsvText(item.Category) + ";" +
                        ((includeType && !item.IsIncome ? -item.Cents : item.Cents) / 100m).ToString("0.00", LedgerEntry.Brazilian) +
                        (includeType ? ";" + CsvText(item.TypeDisplay) : ""));
            }
        }

        private static string CsvText(string value)
        {
            string trimmed = value.Normalize(NormalizationForm.FormKC).TrimStart();
            while (trimmed.Length > 0 && CharUnicodeInfo.GetUnicodeCategory(trimmed[0]) == UnicodeCategory.Format)
                trimmed = trimmed.Substring(1).TrimStart();
            if ((value.Length > 0 && "\t\r\n".IndexOf(value[0]) >= 0) ||
                (trimmed.Length > 0 && "=+-@".IndexOf(trimmed[0]) >= 0))
                value = "'" + value;
            return "\"" + value.Replace("\"", "\"\"") + "\"";
        }

        private static int SelfTest(string folder)
        {
            Directory.CreateDirectory(folder);
            string database = Path.Combine(folder, Guid.NewGuid().ToString("N") + ".sqlite3");
            string result = Path.Combine(folder, "resultado.txt");
            try
            {
                using (LedgerStore store = new LedgerStore(database))
                {
                    Program program = new Program(store);
                    Require(program.window.Icon != null && program.Get<Image>("BrandIcon").Source != null, "Ícone da janela e da barra lateral");
                    program.Get<Button>("SettingsHelp").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Require(program.Get<ScrollViewer>("HelpPage").Visibility == Visibility.Visible &&
                        program.Get<FrameworkElement>("MonthBar").Visibility == Visibility.Collapsed, "Abertura da aba Como usar");
                    program.Render(folder, "Help");
                    program.Render(folder, "Help", 780, 550, "Help-compact");
                    program.Get<Button>("HelpOpenChat").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Require(program.Get<Border>("ChatPage").Visibility == Visibility.Visible &&
                        program.Get<ScrollViewer>("HelpPage").Visibility == Visibility.Collapsed, "Atalho da ajuda abre o chat");
                    program.Get<Button>("SettingsBackup").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Require(program.Get<ScrollViewer>("BackupPage").Visibility == Visibility.Visible &&
                        program.Get<FrameworkElement>("MonthBar").Visibility == Visibility.Collapsed, "Abertura da aba Backup");
                    program.Render(folder, "Backup");
                    program.Render(folder, "Backup", 780, 550, "Backup-compact");
                    program.Get<Button>("ChatNav").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    program.Get<Button>("ExampleMeal").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Require(program.ChatText == "42,90 almoço" && store.Recent().Count == 0, "Exemplo preenche o chat");
                    program.Get<Button>("Send").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    program.ChatText = "18 Uber";
                    program.Get<Button>("Send").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    program.ChatText = "12.3456";
                    program.Get<Button>("Send").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Require(program.monthlyExpenses.Count == 2, "Mensagens válidas e inválidas");
                    Require(program.Get<TextBlock>("Total").Text == LedgerEntry.Money(6090), "Total do painel");
                    program.Navigate("Summary");
                    Require(program.Get<TextBlock>("Peak").Text.EndsWith(LedgerEntry.Money(6090)), "Soma dos gastos de um dia");
                    program.Get<TextBox>("HistorySearch").Text = "ALMOCO";
                    Require(program.Get<DataGrid>("HistoryGrid").Items.Count == 1, "Busca sem acentos");
                    program.Get<ComboBox>("HistoryCategory").SelectedItem = "Transporte";
                    Require(program.Get<DataGrid>("HistoryGrid").Items.Count == 0, "Busca combinada com categoria");
                    program.Get<TextBox>("HistorySearch").Clear();
                    Require(program.Get<DataGrid>("HistoryGrid").Items.Count == 1, "Filtro por categoria");
                    Require(program.monthlyExpenses.Any(expense => expense.Description == "almoço" && expense.Category == "Alimentação"), "Categorização");
                    Require(LedgerEntry.FromMessage("20", DateTime.Today).Cents == 2000, "Registro só com valor");
                    Require(LedgerEntry.FromMessage("almoço 12.34", DateTime.Today).Cents == 1234, "Valor ao fim com ponto decimal");
                    Require(LedgerEntry.FromMessage("paguei R$ 1.234,56 na farmácia", DateTime.Today).Cents == 123456, "Valor brasileiro com milhar");
                    Require(LedgerEntry.FromMessage("0 almoço", DateTime.Today) == null, "Rejeição de valor zero");
                    program.ChatText = "recebi 2800 salário";
                    program.Get<Button>("Send").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    LedgerEntry salary = store.Month(DateTime.Today, true).Single();
                    Require(salary.IsIncome && salary.Category == "Salário" && salary.Cents == 280000, "Ganho registrado no chat");
                    Require(program.Get<TextBlock>("SummaryBalance").Text == LedgerEntry.Money(273910), "Saldo mensal desconta despesas");
                    program.ChatText = "50 peça";
                    program.Get<Button>("Send").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    StackPanel confirmation = (StackPanel)((Border)program.Get<StackPanel>("Messages").Children.Cast<UIElement>().Last()).Child;
                    Require(Fields<Button>(confirmation).Any(button => button.Content.Equals("Desfazer")) &&
                        Fields<Button>(confirmation).Any(button => button.Content.Equals("Editar")), "Lançamento oferece edição e desfazer");
                    LedgerEntry purchase = store.Month(DateTime.Today).Single(entry => entry.Description == "peça");
                    Require(program.RemoveEntry(purchase, true), "Desfazer remove a compra");
                    Require(!program.RemoveEntry(purchase, true) && program.Get<TextBlock>("SummaryBalance").Text == LedgerEntry.Money(273910),
                        "Saldo recalculado e remoção repetida segura");
                    program.window.Close();
                }
                VerifyBackup(folder, database);
                using (LedgerStore store = new LedgerStore(database))
                {
                    DateTime today = DateTime.Today;
                    List<LedgerEntry> rows = store.Month(today);
                    Require(rows.Count == 2, "Persistência após reabrir banco");
                    LedgerEntry salary = store.Month(today, true).Single();
                    Require(salary.Cents == 280000, "Ganho persiste após reabrir banco");
                    salary.Cents = 290000;
                    store.Save(salary);
                    Require(store.Month(today, true).Single().Cents == 290000 && store.Month(today).Count == 2, "Edição de ganho preserva despesas");
                    LedgerEntry item = rows[0];
                    item.Cents = 2500;
                    item.Description = "=EXEMPLO()";
                    DateTime previous = new DateTime(today.Year, today.Month, 1).AddMonths(-1);
                    item.Date = previous;
                    store.Save(item);
                    Require(store.Month(today).Count == 1 && store.Month(previous).Single().Cents == 2500, "Edição e filtros de mês");
                    string csv = Path.Combine(folder, "teste.csv");
                    WriteCsv(csv, store.Month(previous));
                    Require(File.ReadAllText(csv).Contains(";\"'=EXEMPLO()\";\"Transporte\";25,00"), "Exportação CSV");
                    Require(store.Delete(item), "Remoção de lançamento existente");
                    Require(store.Month(previous).Count == 0, "Exclusão");
                    store.Save(LedgerEntry.FromMessage("12,90 almoço", previous));
                    Program preview = new Program(store);
                    Require(preview.Get<TextBlock>("Comparison").Text.StartsWith("+R$ 30,00"), "Comparação com mês anterior");
                    if (today.Day < DateTime.DaysInMonth(previous.Year, previous.Month))
                    {
                        store.Save(LedgerEntry.FromMessage("250 mercado", previous.AddDays(today.Day)));
                        preview.RefreshMonth();
                        Require(preview.Get<TextBlock>("Comparison").Text.StartsWith("+R$ 30,00"), "Comparação até o mesmo dia");
                    }
                    string[] examples = { "82,30 mercado", "24,90 ônibus", "69,90 farmácia", "54 cinema", "128,50 internet", "37,20 café", "49,90 livro" };
                    for (int index = 0; index < examples.Length; index++)
                    {
                        int day = 1 + (today.Day - 1) * index / examples.Length;
                        store.Save(LedgerEntry.FromMessage(examples[index], new DateTime(today.Year, today.Month, day)));
                    }
                    store.Save(LedgerEntry.FromMessage("ganhei 320,50 freela", today));
                    store.Save(LedgerEntry.FromMessage("recebi 80 venda", today));
                    preview.RefreshMonth();
                    preview.Render(folder, "Summary");
                    preview.Render(folder, "Income");
                    preview.Render(folder, "Income", 780, 550, "Income-compact");
                    preview.Render(folder, "History");
                    preview.Render(folder, "Summary", 780, 550, "Summary-compact");
                    preview.Get<StackPanel>("Messages").Children.Clear();
                    preview.AddBubble(AppName, "Para uma despesa, digite 42,90 almoço. Para um ganho, digite recebi 150 freela.", false);
                    preview.Get<Button>("ExampleRide").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    preview.Get<Button>("Send").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    preview.Get<Button>("ExampleMeal").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    preview.Get<Button>("Send").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    preview.Render(folder, "Chat");

                    DateTime first = new DateTime(today.Year, today.Month, 1);
                    store.SavePlan(new BillPlan { Description = "Netflix", Category = "Lazer", Cents = 3990, FirstDue = first.AddDays(2) });
                    store.SavePlan(new BillPlan { Description = "Notebook", Category = "Compras", Cents = 18990, FirstDue = first.AddDays(10), Installments = 6, FirstInstallment = 3 });
                    store.SavePlan(new BillPlan { Description = "Internet", Category = "Moradia", Cents = 8990, FirstDue = first.AddDays(24) });
                    preview.ReloadBills();
                    preview.agendaMode = AgendaMode.Month;
                    preview.Render(folder, "Bills");
                    Require(preview.Get<DataGrid>("BillGrid").Items.Count == 3 && preview.Get<TextBlock>("BillTotal").Text == LedgerEntry.Money(31970),
                        "Agenda e soma das contas do mês");
                    preview.Get<Button>("BillPendingView").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Require(preview.agendaMode == AgendaMode.Overdue && preview.Get<FrameworkElement>("MonthBar").Visibility == Visibility.Collapsed, "Filtro de contas vencidas");
                    preview.Get<Button>("BillMonthView").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    preview.Get<Button>("ThemeToggle").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    preview.paletteButtons["Azul"].IsChecked = true;
                    preview.Get<ComboBox>("ReminderDays").SelectedIndex = 3;
                    Require(store.GetPreference("dark") == "1" && store.GetPreference("accent") == "Azul" &&
                        store.GetPreference("reminder") == "15", "Preferências salvas pelo fluxo da interface");
                    preview.LoadSettings();
                    Require(preview.dark && preview.accent.Name == "Azul" && preview.reminderDays == 15, "Preferências recarregadas");
                    preview.Render(folder, "Bills", 1120, 720, "Bills-dark");
                    preview.Render(folder, "Bills", 780, 550, "Bills-compact");
                    preview.Render(folder, "Settings");
                    preview.Render(folder, "Summary", 1120, 720, "Summary-dark");
                    preview.Render(folder, "History", 1120, 720, "History-dark");

                    store.SavePlan(new BillPlan { Description = "Discord Nitro", Category = "Lazer", Cents = 2499, FirstDue = first.AddDays(14) });
                    preview.ReloadBills();
                    preview.Get<Button>("OpenNextBills").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    DataGrid billsGrid = preview.Get<DataGrid>("BillGrid");
                    BillOccurrence nitro = billsGrid.Items.Cast<BillOccurrence>().Single(row => row.Description == "Discord Nitro");
                    Require(preview.month == first.AddMonths(1) && nitro.Due.Day == 15 &&
                        preview.Get<TextBlock>("BillTotal").Text == LedgerEntry.Money(34469), "Visão do mês seguinte inclui data e valor do Discord Nitro");
                    preview.Render(folder, "Bills", 1120, 720, "Bills-next");
                    preview.Render(folder, "Bills", 780, 550, "Bills-next-compact");
                    store.Save(LedgerEntry.FromMessage("recebi 2900 salário", previous));
                    store.Save(LedgerEntry.FromMessage("1250 aluguel", previous));
                    store.Save(LedgerEntry.FromMessage("recebi 2800 salário", previous.AddMonths(-1)));
                    store.Save(LedgerEntry.FromMessage("1780 mudança", previous.AddMonths(-1)));
                    preview.Render(folder, "Closures");
                    Require(preview.Get<DataGrid>("ClosureGrid").Items.Count == 2 &&
                        preview.Get<TextBlock>("ClosureIncome").Text == LedgerEntry.Money(290000), "Resumo visual de meses encerrados");
                    preview.Get<Button>("ClosureDetails").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Require(preview.month == previous && preview.currentPage == "Summary", "Detalhes do fechamento abrem o mês correspondente");
                    preview.Render(folder, "Settings", 1120, 720, "Settings-organized");
                    preview.Render(folder, "Backup", 1120, 720, "Backup-guide");
                    preview.window.Close();
                }
                VerifyBills(folder);
                VerifyClosureAndPackage(folder);
                VerifyIncomeCalendar(folder);
                VerifyReleaseWorkflows(folder);
                VerifyAgendaAndReserves(folder);
                VerifyWithdrawAndCancellation(folder);
                VerifyResetRecords(folder);
                VerifyBudgetAndAdjustments(folder);
                VerifyOverdueAndPlanChanges(folder);
                VerifyReserveCorrections(folder);
                VerifyOpeningBalance(folder);
                VerifyChatCategoryMatching(folder);
                VerifyForecastAndFirstPayment(folder);
                VerifyStaleEdits(folder);
                VerifySavingsGoals(folder);
                VerifyChatLanguage(folder);
                VerifyChatSubscriptions(folder);
                VerifyAccountOverview(folder);
                VerifyGoalPurchaseAndPrivateBackup(folder);
                File.WriteAllText(result, "OK");
                return 0;
            }
            catch (Exception error)
            {
                File.WriteAllText(result, error.ToString());
                return 1;
            }
        }

        private static void VerifyBackup(string folder, string database)
        {
            string backup = Path.Combine(folder, "teste-backup.sqlite3");
            string fresh = Path.Combine(folder, Guid.NewGuid().ToString("N") + ".sqlite3");
            string invalid = Path.Combine(folder, "arquivo-invalido.sqlite3");
            DateTime today = DateTime.Today;
            DateTime previous = new DateTime(today.Year, today.Month, 1).AddMonths(-1);
            using (LedgerStore original = new LedgerStore(database))
            {
                LedgerEntry oldIncome = LedgerEntry.FromMessage("recebi 50 venda", previous);
                original.Save(oldIncome);
                original.SaveBackup(backup);
                original.SaveBackup(backup);
                original.Delete(oldIncome);
            }
            using (LedgerStore restored = new LedgerStore(fresh))
            {
                restored.Save(LedgerEntry.FromMessage("99 despesa atual", today));
                File.WriteAllText(invalid, "Este arquivo não é um banco.");
                bool refused = false;
                try { restored.RestoreBackup(invalid); }
                catch (IOException) { refused = true; }
                Require(refused && restored.Month(today).Single().Cents == 9900, "Arquivo inválido preserva o banco atual");

                string recovery = restored.RestoreBackup(backup);
                Require(restored.Month(today).Count == 2 && restored.Month(today, true).Single().Cents == 280000 &&
                    restored.Month(previous, true).Single().Cents == 5000, "Backup recupera gastos e ganhos de vários meses");
                using (LedgerStore before = new LedgerStore(recovery))
                    Require(before.Month(today).Single().Cents == 9900, "Cópia de segurança anterior à restauração");
            }
            using (LedgerStore reopened = new LedgerStore(fresh))
                Require(reopened.Month(today).Count == 2, "Dados restaurados persistem ao reabrir o app");
        }

        private static void VerifyBills(string folder)
        {
            string database = Path.Combine(folder, Guid.NewGuid().ToString("N") + ".sqlite3");
            string backup = Path.Combine(folder, "contas-backup.sqlite3");
            using (LedgerStore store = new LedgerStore(database))
            {
                DateTime january = new DateTime(2028, 1, 31);
                DateTime february = new DateTime(2028, 2, 29);
                DateTime march = new DateTime(2028, 3, 31);
                BillPlan plan = new BillPlan {
                    Description = "Notebook", Category = "Compras", Cents = 19990, FirstDue = january, Installments = 6, FirstInstallment = 4
                };
                store.SavePlan(plan);
                BillCalendar calendar = store.Bills();
                Require(calendar.Month(february, february).Single().Due == february &&
                    calendar.Month(february, february).Single().Series == "5 de 6" &&
                    calendar.Month(march, february).Single().Due == march &&
                    calendar.Month(march.AddMonths(1), march).Count == 0, "Dia 31 em fevereiro e fim das parcelas");
                Require(calendar.PendingThrough(february) == 2 && calendar.Pending(february, 0).Count == 2,
                    "Avisos de atraso e vencimento no dia");
                Require(store.Month(february).Count == 0 && store.PayBill(plan.Id, february, february) &&
                    !store.PayBill(plan.Id, february, february) && store.Month(february).Count == 1,
                    "Pagamento confirmado sem duplicar a despesa");
                Require(store.Bills().Month(february, february).Single().Status == "Paga", "Situação paga na agenda");
                store.SetPreference("accent", "Roxo");
                store.SaveBackup(backup);
                Require(store.Delete(store.Month(february).Single()) &&
                    store.Bills().Month(february, february).Single().Status == "Vence hoje", "Excluir a despesa reabre a cobrança");
                store.RestoreBackup(backup);
                Require(store.Bills().Month(february, february).Single().Status == "Paga" &&
                    store.GetPreference("accent") == "Roxo", "Backup restaura cobranças, pagamentos e preferências");
                store.ArchivePlan(plan.Id);
                Require(store.Bills().Month(march, february).Count == 0 && store.Bills().Month(february, february).Count == 1 &&
                    store.Month(february).Count == 1, "Arquivamento mantém pagamento no histórico");

                DateTime november = new DateTime(2026, 11, 15);
                DateTime december = new DateTime(2026, 12, 15);
                BillPlan nitro = new BillPlan { Description = "Discord Nitro", Category = "Lazer", Cents = 2499, FirstDue = november };
                store.SavePlan(nitro);
                Require(store.PayBill(nitro.Id, november, november), "Primeira assinatura paga");
                BillCalendar renewed = store.Bills();
                Require(renewed.Month(december, november).Single().Due == december && renewed.NextRenewal(nitro, november) == december,
                    "Renovação mantém o dia e preserva ciclos pagos");
                Require(store.StopRenewal(nitro, december, november.AddDays(8)), "Cancelamento da renovação");
                Require(store.Bills().Month(december, november).Count == 0 &&
                    store.Bills().Month(november, november).Single().Status == "Paga" && store.Month(november).Count == 1,
                    "Cancelamento tira projeção futura e mantém histórico");
            }
        }

        private static void VerifyClosureAndPackage(string folder)
        {
            DateTime today = new DateTime(2027, 1, 2);
            DateTime december = new DateTime(2026, 12, 1);
            string originalPath = Path.Combine(folder, Guid.NewGuid().ToString("N") + ".sqlite3");
            string package;
            using (LedgerStore store = new LedgerStore(originalPath))
            {
                LedgerEntry income = LedgerEntry.FromMessage("recebi 3000 salário", december);
                store.Save(income);
                store.Save(LedgerEntry.FromMessage("1200 aluguel", december));
                store.Save(LedgerEntry.FromMessage("60 compras", today));
                store.UpdateClosures(today);
                Require(store.Closures().Single().Balance == 180000, "Fechamento calcula somente meses terminados");
                Require(store.Delete(income), "Correção retroativa de ganho");
                store.UpdateClosures(today);
                Require(store.Closures().Single().Balance == -120000, "Fechamento recalculado após correção");
                package = BackupPackage.Create(store, folder, Assembly.GetExecutingAssembly().Location);
            }
            string moved = Path.Combine(folder, "Backup renomeado " + Guid.NewGuid().ToString("N"));
            Directory.Move(package, moved);
            string database = BackupPackage.FindData(moved, false);
            Require(database != null && File.Exists(Path.Combine(moved, BackupPackage.ProgramName)),
                "Pasta renomeada preserva a detecção e traz o executável");
            using (LedgerStore restored = new LedgerStore(Path.Combine(folder, Guid.NewGuid().ToString("N") + ".sqlite3")))
            {
                restored.RestoreBackup(database);
                Require(restored.Closures().Single().Balance == -120000 && restored.Month(today).Single().Cents == 6000,
                    "Restauração da pasta preserva lançamentos e fechamento gravado");
            }
        }

        private static void VerifyIncomeCalendar(string folder)
        {
            string path = Path.Combine(folder, Guid.NewGuid().ToString("N") + ".sqlite3");
            DateTime novemberDue = new DateTime(2026, 11, 5);
            DateTime decemberDue = new DateTime(2026, 12, 5);
            DateTime clock = new DateTime(2026, 11, 30, 23, 59, 59, 995);
            using (LedgerStore store = new LedgerStore(path))
            {
                BillPlan salary = new BillPlan {
                    Description = "Salário", Category = "Salário", Cents = 320000, FirstDue = novemberDue, IsIncome = true
                };
                store.SavePlan(salary);
                store.SavePlan(new BillPlan { Description = "Aluguel", Category = "Moradia", Cents = 95000, FirstDue = decemberDue });
                Require(store.Month(novemberDue, true).Count == 0 && store.Bills().Month(novemberDue, clock.Date, true).Single().Status == "Em atraso",
                    "Ganhos fixos previstos ficam fora dos ganhos recebidos");
                Require(store.PayBill(salary.Id, novemberDue, novemberDue) && !store.PayBill(salary.Id, novemberDue, novemberDue) &&
                    store.Month(novemberDue, true).Single().Cents == 320000 && store.Month(novemberDue).Count == 0,
                    "Confirmação de ganho fixo registra uma única entrada");
                Require(store.Bills().Month(decemberDue, clock.Date, true).Single().Due == decemberDue,
                    "Ganho de dia 5 repete no dia 5 do mês seguinte");

                Program program = new Program(store, () => clock);
                program.Navigate("Income");
                program.Get<Button>("IncomeSchedules").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Require(program.showIncomePlans && program.currentPage == "Bills" &&
                    program.Get<DataGrid>("BillGrid").Items.Cast<BillOccurrence>().Single().Status == "Recebido",
                    "Ganhos abre a agenda de recebimentos fixos");
                program.ShiftMonth(-43);
                Button returnButton = program.Get<Button>("ReturnCurrentMonth");
                Require(program.month == new DateTime(2023, 4, 1) && returnButton.Visibility == Visibility.Visible,
                    "Atalho fica visível em um mês distante");
                returnButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Require(program.month == MonthStart(novemberDue) && returnButton.Visibility == Visibility.Collapsed, "Atalho volta ao mês atual");

                program.window.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
                clock = new DateTime(2026, 12, 1);
                DateTime deadline = DateTime.UtcNow.AddSeconds(3);
                DispatcherFrame wait = new DispatcherFrame();
                DispatcherTimer observer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(20) };
                observer.Tick += delegate
                {
                    if (program.calendarDay == clock.Date || DateTime.UtcNow >= deadline) wait.Continue = false;
                };
                observer.Start();
                Dispatcher.PushFrame(wait);
                observer.Stop();
                Require(program.calendarDay == clock.Date && program.month == MonthStart(decemberDue) &&
                    store.Closures(clock.Date).Single().Income == 320000, "Relógio muda o dia e fecha o mês com o app aberto");
                Require(program.Get<TextBlock>("ForecastBalance").Text == LedgerEntry.Money(225000),
                    "Saldo previsto combina ganho fixo e conta pendente");
                program.ShiftMonth(-44);
                DateTime viewed = program.month;
                clock = decemberDue;
                program.RefreshCalendarDay();
                Require(program.month == viewed, "Consulta histórica permanece na virada do dia");
                returnButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Require(program.Get<DataGrid>("BillGrid").Items.Cast<BillOccurrence>().Single().Status == "Previsto hoje",
                    "Situação do ganho acompanha a data do calendário");
                program.Render(folder, "Bills", 1120, 720, "Ganhos-fixos");
                program.ShiftMonth(-44);
                program.Render(folder, "Income", 1120, 720, "Voltar-mes-atual");
                program.window.Close();

                string backup = Path.Combine(folder, "ganhos-fixos-" + Guid.NewGuid().ToString("N") + ".sqlite3");
                store.SaveBackup(backup);
                Require(store.Delete(store.Month(novemberDue, true).Single()) &&
                    store.Bills().Month(novemberDue, clock.Date, true).Single().Payment == null,
                    "Excluir um ganho confirmado reabre a previsão");
                store.RestoreBackup(backup);
                Require(store.Bills().Month(novemberDue, clock.Date, true).Single().Status == "Recebido",
                    "Backup preserva a confirmação do ganho fixo");
            }
        }

        private void Render(string folder, string page, int width = 1120, int height = 720, string filename = null)
        {
            Navigate(page);
            FrameworkElement content = (FrameworkElement)window.Content;
            content.Measure(new Size(width, height));
            content.Arrange(new Rect(0, 0, width, height));
            content.Dispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(delegate { }));
            content.UpdateLayout();
            RenderTargetBitmap bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(content);
            PngBitmapEncoder encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using (FileStream file = File.Create(Path.Combine(folder, (filename ?? page) + ".png"))) encoder.Save(file);
        }

        private static void Require(bool condition, string test)
        {
            if (!condition) throw new InvalidOperationException(test);
        }

        private static void VerifyReleaseWorkflows(string folder)
        {
            string path = Path.Combine(folder, Guid.NewGuid().ToString("N") + ".sqlite3");
            DateTime today = new DateTime(2026, 11, 21);
            using (LedgerStore store = new LedgerStore(path))
            {
                Program program = new Program(store, () => today);
                program.Get<Button>("SettingsNav").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                program.Get<Button>("SettingsBackup").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Require(program.Get<Button>("NavigateBack").Visibility == Visibility.Visible, "Backup exibe o botão Voltar");
                program.Render(folder, "Backup", 1120, 720, "Backup-com-voltar");
                program.Get<Button>("NavigateBack").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Require(program.currentPage == "Settings", "Voltar do backup retorna às configurações");
                program.Get<Button>("SettingsHelp").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                program.Get<Button>("NavigateBack").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Require(program.currentPage == "Settings", "Voltar do guia retorna às configurações");
                program.Navigate("Summary", delegate { program.month = new DateTime(2023, 4, 1); });
                Require(program.Get<Button>("NavigateBack").Visibility == Visibility.Collapsed, "Resumo não exibe Voltar após navegar por outras abas");
                program.Get<Button>("OpenClosures").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                program.Get<Button>("NavigateBack").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Require(program.currentPage == "Summary" && program.month == new DateTime(2023, 4, 1), "Voltar do fechamento retorna ao Resumo mantendo o mês");
                program.Get<Button>("OpenNextBills").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Require(program.currentPage == "Bills" && program.month == new DateTime(2026, 12, 1) &&
                    program.Get<Button>("NavigateBack").Visibility == Visibility.Collapsed, "Contas futuras continua uma seção principal");
                program.Navigate("Summary", delegate { program.month = new DateTime(2023, 4, 1); });

                ShowTestWindow(program);

                DriveDialog(program, "MonthPicker", dialog =>
                {
                    Field<ComboBox>(dialog, "Mês").SelectedIndex = 9;
                    Field<TextBox>(dialog, "Ano").Text = "2026";
                    Press(dialog, "Ir para o mês");
                });
                Require(program.month == new DateTime(2026, 10, 1), "Seletor abre um mês e ano específicos");

                program.Get<Button>("HistoryNav").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                DriveDialog(program, "ExpenseAdd", dialog =>
                {
                    Field<TextBox>(dialog, "Descrição").Text = "Netflix";
                    Field<TextBox>(dialog, "Valor em reais").Text = "39,90";
                    Field<TextBox>(dialog, "Data (dia/mês/ano)").Text = "19/10/2026";
                    Press(dialog, "Salvar");
                });
                LedgerEntry manual = store.Month(program.month).Single();
                Require(manual.Cents == 3990 && manual.Date.Day == 19, "Cadastro manual preserva data e valor");

                BillPlan subscription = new BillPlan { Description = "Netflix", Category = "Lazer", Cents = 3990, FirstDue = new DateTime(2026, 10, 20) };
                store.SavePlan(subscription);
                program.ReloadBills();
                program.Get<Button>("BillsNav").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                program.Navigate("Bills", delegate
                {
                    program.month = new DateTime(2026, 10, 1);
                    program.agendaMode = AgendaMode.Month;
                });
                program.Get<DataGrid>("BillGrid").SelectedIndex = 0;
                DriveDialog(program, "BillPay", dialog =>
                {
                    Fields<TextBox>(dialog).First().Text = "19/10/2026";
                    Fields<CheckBox>(dialog).Single().IsChecked = true;
                    Field<ComboBox>(dialog, "Vincular lançamento do mês indicado").SelectedIndex = 0;
                    Press(dialog, "Vincular lançamento");
                });
                BillOccurrence confirmed = store.Bills().Month(program.month, today).Single();
                Require(confirmed.Payment.EntryId == manual.Id && store.Month(program.month).Count == 1,
                    "Vincular pelo diálogo preserva um único gasto");
                Require(program.Get<DataGrid>("BillGrid").Items.Count == 0, "Confirmação remove a conta da lista em aberto");
                program.Get<Button>("BillHistory").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                program.Get<Button>("BillConfirmedView").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                program.ShiftMonth(-1);
                program.Get<DataGrid>("BillGrid").SelectedIndex = 0;
                Require(program.Get<Button>("BillUnlink").Visibility == Visibility.Visible, "Previsão confirmada oferece desvincular");
                Require(store.UnlinkBill(confirmed) && store.Month(program.month).Single().Id == manual.Id,
                    "Desvincular preserva o lançamento no histórico");
                program.ReloadBills();

                program.Get<Button>("BillsNav").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                program.Get<DataGrid>("BillGrid").SelectedIndex = 0;
                DriveDialog(program, "BillPay", dialog =>
                {
                    Field<TextBox>(dialog, "Data em que pagou (dia/mês/ano)").Text = "07/11/2026";
                    Field<TextBox>(dialog, "Valor pago (R$)").Text = "44,90";
                    Press(dialog, "Confirmar pagamento");
                });
                Require(store.Month(today).Single().Date.Day == 7 && store.Month(today).Single().Cents == 4490 &&
                    store.Bills().Month(today.AddMonths(1), today).Single().Cents == 3990,
                    "Valor e data reais não alteram o calendário futuro");
                program.window.Close();
            }
        }

        private static void VerifyAgendaAndReserves(string folder)
        {
            string path = Path.Combine(folder, Guid.NewGuid().ToString("N") + ".sqlite3");
            DateTime clock = new DateTime(2026, 11, 21);
            using (LedgerStore store = new LedgerStore(path))
            {
                Program program = new Program(store, () => clock);
                ShowTestWindow(program);
                DriveDialog(program, "QuickFixedExpense", dialog =>
                {
                    Field<TextBox>(dialog, "Descrição").Text = "Domínio";
                    Field<ComboBox>(dialog, "Tipo de gasto fixo").SelectedIndex = (int)FixedExpenseType.AnnualSubscription;
                    Field<TextBox>(dialog, "Valor cobrado por ano (R$)").Text = "120";
                    Field<TextBox>(dialog, "Primeiro vencimento (dia/mês/ano)").Text = "25/11/2026";
                    Field<TextBox>(dialog, "Número de cobranças anuais (opcional)").Text = "2";
                    Press(dialog, "Salvar");
                });
                DriveDialog(program, "QuickFixedExpense", dialog =>
                {
                    Field<TextBox>(dialog, "Descrição").Text = "Plano anual mensal";
                    Field<ComboBox>(dialog, "Tipo de gasto fixo").SelectedIndex = (int)FixedExpenseType.AnnualMonthly;
                    Field<TextBox>(dialog, "Valor de cada mensalidade (R$)").Text = "24,99";
                    Field<TextBox>(dialog, "Primeiro vencimento (dia/mês/ano)").Text = "15/12/2026";
                    Press(dialog, "Salvar");
                });
                DriveDialog(program, "QuickFixedExpense", dialog =>
                {
                    Field<ComboBox>(dialog, "Tipo de gasto fixo").SelectedIndex = (int)FixedExpenseType.Installment;
                    Field<TextBox>(dialog, "Descrição").Text = "Geladeira";
                    Field<TextBox>(dialog, "Valor de cada cobrança (R$)").Text = "150";
                    Field<TextBox>(dialog, "Primeiro vencimento (dia/mês/ano)").Text = "10/12/2026";
                    Field<TextBox>(dialog, "Total de parcelas").Text = "6";
                    Press(dialog, "Salvar");
                });
                DriveDialog(program, "QuickFixedExpense", dialog =>
                {
                    Require(Field<ComboBox>(dialog, "Tipo de gasto fixo").SelectedIndex == (int)FixedExpenseType.MonthlyBill,
                        "O gasto fixo abre com Conta mensal como tipo inicial");
                    Field<TextBox>(dialog, "Descrição").Text = "Aluguel";
                    Field<TextBox>(dialog, "Valor mensal (R$)").Text = "750";
                    Field<TextBox>(dialog, "Primeiro vencimento (dia/mês/ano)").Text = "24/11/2026";
                    Press(dialog, "Salvar");
                });
                DriveDialog(program, "QuickFixedIncome", dialog =>
                {
                    Field<TextBox>(dialog, "Descrição (ex.: Salário)").Text = "Salário";
                    Field<TextBox>(dialog, "Valor mensal (R$)").Text = "2000";
                    Field<TextBox>(dialog, "Primeira data prevista (dia/mês/ano)").Text = "05/12/2026";
                    Press(dialog, "Salvar");
                });
                Require(program.currentPage == "Chat" && program.Get<Button>("NavigateBack").Visibility == Visibility.Collapsed,
                    "Cadastros rápidos mantêm o chat como página principal");
                program.Render(folder, "Chat", 1120, 720, "Chat-atalhos");
                program.Get<Button>("BillsNav").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                DataGrid grid = program.Get<DataGrid>("BillGrid");
                Require(grid.Items.Count == 2 && program.Get<TextBlock>("BillTotal").Text == LedgerEntry.Money(87000),
                    "A agenda abre nas contas pendentes do mês atual");
                program.Render(folder, "Bills", 1120, 720, "Agenda-mes-atual");
                program.Get<Button>("BillNextMonth").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Require(grid.Items.Count == 3 && program.Get<TextBlock>("BillTotal").Text == LedgerEntry.Money(92499),
                    "O próximo mês reúne as contas previstas e mostra o total");
                BillOccurrence rent = grid.Items.Cast<BillOccurrence>().Single(item => item.Description == "Aluguel");
                Require(rent.Plan.IsMonthlyBill && !rent.Plan.IsSubscription && rent.Series == "Conta mensal" &&
                    rent.Due == new DateTime(2026, 12, 24) && rent.Cents == 75000 &&
                    store.Bills().SubscriptionsForReport(clock, clock).All(plan => plan.Description != "Aluguel"),
                    "Aluguel se repete no mesmo dia e não aparece como assinatura no fechamento");
                program.Render(folder, "Bills", 1120, 720, "Agenda-proximo-mes");
                program.Get<Button>("BillPlansView").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                BillOccurrence yearly = grid.Items.Cast<BillOccurrence>().Single(item => item.Description == "Domínio");
                BillPlan annual = yearly.Plan;
                List<BillOccurrence> annualDates = store.Bills().Schedule(annual, clock);
                Require(annualDates.Count == 2 && annualDates.Last().Due == new DateTime(2027, 11, 25) &&
                    store.Bills().Month(new DateTime(2027, 1, 1), clock).All(item => item.Plan.Id != annual.Id),
                    "Assinatura anual usa intervalo de doze meses");
                grid.SelectedItem = grid.Items.Cast<BillOccurrence>().Single(item => item.Description == "Plano anual mensal");
                DriveDialog(program, "BillSchedule", dialog =>
                {
                    List<BillOccurrence> schedule = Fields<DataGrid>(dialog).Single().Items.Cast<BillOccurrence>().ToList();
                    Require(schedule.Count == 12 && schedule.Last().Due == new DateTime(2027, 11, 15) &&
                        schedule.Sum(item => item.Cents) == 29988, "Cronograma lista doze mensalidades e o último vencimento");
                    dialog.Close();
                });
                Require(store.Bills().Month(new DateTime(2026, 12, 1), clock, true).Single().Due.Day == 5,
                    "Atalho cria ganho fixo na agenda de ganhos");

                program.Navigate("Chat");
                AddTestReserve(program, "PicPay", "200", "10/07/2026");
                AddTestReserve(program, "Mercado Pago", "150", "10/09/2026");
                DateTime july = new DateTime(2026, 7, 1);
                DateTime august = july.AddMonths(1);
                DateTime september = july.AddMonths(2);
                Require(store.ReserveBalances(july).Single().Balance == 20000 &&
                    store.ReserveBalances(august).Single().Balance == 20000 &&
                    store.ReserveBalances(september).Sum(place => place.Balance) == 35000 &&
                    store.ReserveMovements(august).Count == 0, "A reserva atravessa meses sem movimentação");
                Require(store.Closures(clock).Any(item => item.Month == august), "Saldo guardado gera fechamento de agosto mesmo sem atividade");
                Require(!store.Month(july).Any(), "Guardar dinheiro não entra no total de despesas");
                bool blocked = false;
                try { store.AddReserveMovement("PicPay", august, 20001, true); }
                catch (IOException) { blocked = true; }
                Require(blocked && store.ReserveBalances(august).Single().Balance == 20000, "Resgate maior que o saldo é revertido");

                program.Navigate("Reserves", delegate { program.month = september; });
                program.Render(folder, "Reserves", 1120, 720, "Reservas-por-local");
                Program compact = new Program(store, () => clock);
                compact.Navigate("Reserves", delegate { compact.month = september; });
                compact.Render(folder, "Reserves", 780, 550, "Reservas-compactas");
                compact.window.Close();
                store.Save(LedgerEntry.FromMessage("recebi 2800 salário", september.AddDays(4)));
                store.Save(LedgerEntry.FromMessage("950 aluguel", september.AddDays(6)));
                program.Navigate("Summary", delegate { program.month = september; });
                program.RefreshMonth();
                program.Get<Button>("OpenClosures").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Require(program.Get<TextBlock>("ClosureReserveTotal").Text == LedgerEntry.Money(35000), "Fechamento de setembro mostra reserva acumulada");
                Require(program.Get<TextBlock>("ClosureAvailable").Text == LedgerEntry.Money(170000),
                    "Separar dinheiro reduz o disponível sem aumentar despesas");
                program.Render(folder, "Closures", 1120, 720, "Fechamento-reservas");
                string report = Path.Combine(folder, "relatorio-reservas.csv");
                program.WriteMonthlyReport(report, (MonthClosure)program.Get<DataGrid>("ClosureGrid").SelectedItem);
                string csv = File.ReadAllText(report);
                Require(csv.Contains("\"PicPay\";\"09/2026\";200,00") &&
                    csv.Contains("\"Mercado Pago\";\"09/2026\";150,00"), "Relatório exporta saldos de cada local");

                program.Navigate("Reserves");
                DriveDialog(program, "ReserveWithdraw", dialog =>
                {
                    Field<ComboBox>(dialog, "De onde sai o dinheiro?").SelectedItem = "PicPay";
                    Field<TextBox>(dialog, "Valor (R$)").Text = "50";
                    Field<TextBox>(dialog, "Data da movimentação (dia/mês/ano)").Text = "10/10/2026";
                    Press(dialog, "Sacar");
                });
                Require(program.Get<TextBlock>("ReserveTotal").Text == LedgerEntry.Money(30000) &&
                    program.Get<TextBlock>("SummaryAvailable").Text.Contains(LedgerEntry.Money(5000)),
                    "Resgatar devolve dinheiro ao disponível e reduz a reserva");
                string backup = Path.Combine(folder, Guid.NewGuid().ToString("N") + ".sqlite3");
                store.SaveBackup(backup);
                using (LedgerStore restored = new LedgerStore(Path.Combine(folder, Guid.NewGuid().ToString("N") + ".sqlite3")))
                {
                    restored.RestoreBackup(backup);
                    Require(restored.ReserveBalances(september).Sum(place => place.Balance) == 35000 &&
                        restored.ReserveBalances(september.AddMonths(1)).Sum(place => place.Balance) == 30000 &&
                        restored.Bills().Schedule(annual, clock).Last().Due == new DateTime(2027, 11, 25) &&
                        restored.Bills().Month(clock, clock).Single(item => item.Description == "Aluguel").Plan.IsMonthlyBill,
                        "Backup restaura reservas, resgates e planos anuais");
                }

                program.Navigate("Summary", delegate { program.month = MonthStart(clock); });
                program.Get<Button>("OpenClosures").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                MonthClosure partial = (MonthClosure)program.Get<DataGrid>("ClosureGrid").SelectedItem;
                Require(partial.IsPartial && partial.Month == MonthStart(clock) &&
                    program.Get<TextBlock>("ClosureBalanceLabel").Text.Contains("até agora"), "Fechar mês abre o relatório parcial");
                clock = new DateTime(2026, 12, 1);
                program.RefreshCalendarDay();
                Require(!store.Closures(clock, true).Single(item => item.Month == partial.Month).IsPartial,
                    "O fechamento parcial se torna definitivo na virada");
                program.window.Close();
            }
        }

        private static void VerifyWithdrawAndCancellation(string folder)
        {
            DateTime clock = new DateTime(2026, 11, 21);
            using (LedgerStore store = new LedgerStore(Path.Combine(folder, Guid.NewGuid().ToString("N") + ".sqlite3")))
            {
                store.Save(LedgerEntry.FromMessage("recebi 1000 salário", clock));
                Program program = new Program(store, () => clock);
                ShowTestWindow(program);
                AddTestReserve(program, "PicPay", "200", "21/11/2026");
                program.Navigate("Summary");
                DriveDialog(program, "SummaryCofrinho", dialog =>
                {
                    Field<ComboBox>(dialog, "O que você quer fazer?").SelectedIndex = 1;
                    Field<ComboBox>(dialog, "De onde sai o dinheiro?").SelectedItem = "PicPay";
                    Require(Fields<TextBlock>(dialog).Any(item => item.Text.StartsWith("Saldo no local") && item.Text.Contains(LedgerEntry.Money(20000))),
                        "Cofrinho mostra saldo do PicPay na data informada");
                    Field<TextBox>(dialog, "Valor (R$)").Text = "20";
                    Press(dialog, "Sacar");
                });
                program.Get<Expander>("SummaryReserveDetails").IsExpanded = true;
                Require(store.Month(clock).Count == 0 && store.Month(clock, true).Count == 1 &&
                    store.ReserveBalances(clock).Single().Balance == 18000 &&
                    program.Get<TextBlock>("SummaryAvailable").Text.Contains(LedgerEntry.Money(82000)) &&
                    program.Get<ItemsControl>("SummaryReserveMoves").Items.Count == 2,
                    "Resumo mostra depósito e resgate sem lançá-los como ganhos ou despesas");
                program.Render(folder, "Summary", 1120, 720, "Resumo-resgates");
                store.Save(LedgerEntry.FromMessage("35 almoço", clock));
                BillPlan subscription = new BillPlan {
                    Description = "Discord Nitro", Category = "Lazer", Cents = 2499, FirstDue = clock, CreatedOn = clock
                };
                store.SavePlan(subscription);
                store.SavePlan(new BillPlan {
                    Description = "Netflix", Category = "Lazer", Cents = 3990, FirstDue = clock.AddMonths(1), CreatedOn = clock
                });
                program.ReloadBills();
                program.OpenAgenda(false, false);
                DataGrid grid = program.Get<DataGrid>("BillGrid");
                Require(grid.Items.Cast<BillOccurrence>().Single().Due == clock, "Cobrança aparece no mês da assinatura");
                grid.SelectedIndex = 0;
                DriveDialog(program, "BillPay", dialog => Press(dialog, "Confirmar pagamento"));
                Require(grid.Items.Count == 0 && store.Month(clock).Sum(entry => entry.Cents) == 5999 &&
                    program.Get<TextBlock>("SummaryAvailable").Text.Contains(LedgerEntry.Money(76001)),
                    "Pagamento e gasto real reduzem o saldo e a cobrança sai da lista em aberto");
                clock = clock.AddDays(2);
                program.RefreshCalendarDay();
                program.OpenNextBillMonth(false);
                grid.SelectedItem = grid.Items.Cast<BillOccurrence>().Single(item => item.Description == "Discord Nitro");
                Require(program.SelectedBill.Due == new DateTime(2026, 12, 21), "Renova no mesmo dia do próximo mês");
                DriveDialog(program, "BillCancel", dialog => Press(dialog, "Registrar cancelamento"));
                Require(grid.Items.Cast<BillOccurrence>().Single().Description == "Netflix",
                    "Cancelamento some do mês seguinte e preserva a assinatura ativa");
                program.Get<Button>("BillPendingView").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Require(grid.Items.Count == 0, "Pago e cancelado ficam fora das contas vencidas");
                program.Get<Button>("BillHistory").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                BillOccurrence canceled = grid.Items.Cast<BillOccurrence>().Single();
                Require(canceled.Status == "Cancelada" && canceled.ShownDate == clock &&
                    program.Get<Button>("NavigateBack").Visibility == Visibility.Visible,
                    "Histórico mostra a data do cancelamento e tem volta para a agenda");
                grid.SelectedIndex = 0;
                program.Render(folder, "Bills", 1120, 720, "Historico-canceladas");
                program.Get<Button>("BillConfirmedView").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Require(grid.Items.Cast<BillOccurrence>().Single().Payment.Cents == 2499, "Histórico preserva a cobrança efetivamente paga");
                program.GoBack();
                Require(program.agendaMode == AgendaMode.Current && program.currentPage == "Bills", "Voltar retorna a Contas a pagar");
                program.Navigate("Summary");
                program.Get<Button>("OpenClosures").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                ItemsControl subscriptions = program.Get<ItemsControl>("ClosureSubscriptions");
                Require(subscriptions.Items.Cast<string>().Any(item => item.Contains("Cancelada em 23/11/2026") && item.Contains("21/12/2026")) &&
                    subscriptions.Items.Cast<string>().Any(item => item.Contains("Netflix") && item.Contains("Ativa")) &&
                    program.Get<ItemsControl>("ClosureReserveMoves").Items.Count == 2,
                    "Fechamento mostra as movimentações e o estado das assinaturas no mês");
                string report = Path.Combine(folder, "relatorio-resgates-e-assinaturas.csv");
                program.WriteMonthlyReport(report, (MonthClosure)program.Get<DataGrid>("ClosureGrid").SelectedItem);
                string csv = File.ReadAllText(report);
                Require(csv.Contains("Cancelada em 23/11/2026") && csv.Contains("Resgatado no mês") &&
                    csv.Contains("Netflix") && csv.Contains("Ativa"), "CSV inclui reservas e situação das assinaturas");
                program.Render(folder, "Closures", 1120, 720, "Fechamento-resgates-e-assinaturas");
                program.Get<Button>("ClosureBills").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Require(program.currentPage == "Bills" && program.agendaMode == AgendaMode.Current, "Atalho do fechamento abre as contas a pagar");
                program.window.Close();
            }
        }

        private static void AddTestReserve(Program program, string place, string amount, string on)
        {
            DriveDialog(program, "QuickCofrinho", dialog =>
            {
                ComboBox places = Field<ComboBox>(dialog, "Onde vai guardar?");
                places.SelectedIndex = places.Items.Count - 1;
                Field<TextBox>(dialog, "Nome do local (ex.: PicPay ou Caixinha em casa)").Text = place;
                Field<TextBox>(dialog, "Valor (R$)").Text = amount;
                Field<TextBox>(dialog, "Data da movimentação (dia/mês/ano)").Text = on;
                Press(dialog, "Depositar");
            });
        }

        private static void VerifyResetRecords(string folder)
        {
            DateTime today = new DateTime(2026, 11, 21);
            string path = Path.Combine(folder, Guid.NewGuid().ToString("N") + ".sqlite3");
            string backup = Path.Combine(folder, "antes-do-reset-teste.sqlite3");
            using (LedgerStore store = new LedgerStore(path))
            {
                store.Save(LedgerEntry.FromMessage("35 almoço", today));
                store.Save(LedgerEntry.FromMessage("recebi 200 freela", today));
                BillPlan subscription = new BillPlan {
                    Description = "Discord Nitro", Category = "Lazer", Cents = 2499, FirstDue = today, CreatedOn = today
                };
                BillPlan salary = new BillPlan {
                    Description = "Salário", Category = "Salário", Cents = 100000, FirstDue = today, IsIncome = true
                };
                store.SavePlan(subscription);
                store.SavePlan(salary);
                Require(store.PayBill(subscription.Id, today, today) && store.PayBill(salary.Id, today, today) &&
                    store.StopRenewal(subscription, today.AddMonths(1), today), "Dados iniciais para testar o reset");
                store.AddReserveMovement("PicPay", today.AddMonths(-1), 20000, false);
                store.AddReserveMovement("PicPay", today, 2000, true);
                store.SaveClosure(today);
                store.UpdateClosures(today);
                store.SaveBackup(backup);

                Program program = new Program(store, () => today);
                ShowTestWindow(program);
                program.Navigate("Settings");
                Color normal = ((SolidColorBrush)program.Get<Button>("ResetRecords").Background).Color;
                program.Render(folder, "Settings", 1120, 720, "Config-reset");
                program.paletteButtons["Vermelho"].IsChecked = true;
                program.SetDark(true);
                Color alternate = ((SolidColorBrush)program.Get<Button>("ResetRecords").Background).Color;
                Require(normal != alternate && alternate != program.Tone("Accent").Color, "Reset muda de cor no tema vermelho");
                program.Render(folder, "Settings", 1120, 720, "Config-reset-tema-vermelho");
                DriveDialog(program, "ResetRecords", delegate(Window dialog)
                {
                    Button cancel = Fields<Button>(dialog).Single(button => (string)button.Content == "Cancelar");
                    Require(cancel.IsDefault && cancel.IsCancel, "Cancelar é a ação padrão da confirmação");
                    dialog.Close();
                });
                Require(store.Recent().Count == 4 && store.ReservePlaces().Count == 1 &&
                    store.Bills().Cancellations(today).Count == 1 && store.Closures(today, true).Count == 2,
                    "Cancelar o reset preserva os registros");

                DriveDialog(program, "ResetRecords", dialog => Press(dialog, "Apagar todos os dados"));
                Require(program.currentPage == "Chat" && program.month == MonthStart(today) &&
                    program.Get<TextBlock>("SummaryAvailable").Text.Contains(LedgerEntry.Money(0)),
                    "Reset volta ao chat e zera o resumo");
                program.Navigate("Closures");
                Require(program.Get<DataGrid>("ClosureGrid").Items.Count == 0, "Fechamentos ficam vazios após o reset");
                program.window.Close();
            }

            using (LedgerStore reopened = new LedgerStore(path))
            {
                Require(reopened.Recent().Count == 0 && reopened.Month(today).Count == 0 &&
                    reopened.Month(today, true).Count == 0 && reopened.Bills().Cancellations(today).Count == 0 &&
                    reopened.Bills().Plans(today, true).Count == 0 && reopened.Bills().ConfirmedInMonth(today, today).Count == 0 &&
                    reopened.Closures(today, true).Count == 0 && reopened.ReservePlaces().Count == 0 &&
                    reopened.ReserveMovements(today).Count == 0 && reopened.GetPreference("accent") == "Vermelho",
                    "Reabrir preserva apenas as preferências depois do reset");
                reopened.Save(LedgerEntry.FromMessage("10 almoço", today));
                Require(reopened.Month(today).Single().Cents == 1000, "É possível cadastrar dados novos após o reset");
            }
            using (LedgerStore savedBackup = new LedgerStore(backup))
                Require(savedBackup.Recent().Count == 4, "Reset preserva os backups salvos anteriormente");
        }

        private static void ShowTestWindow(Program program)
        {
            program.window.WindowStartupLocation = WindowStartupLocation.Manual;
            program.window.Left = -20000;
            program.window.Top = -20000;
            program.window.Opacity = 0;
            program.window.ShowInTaskbar = false;
            program.window.ShowActivated = false;
            program.window.Show();
        }

        private static IEnumerable<T> Fields<T>(DependencyObject root) where T : DependencyObject
        {
            foreach (object item in LogicalTreeHelper.GetChildren(root))
            {
                T match = item as T;
                if (match != null) yield return match;
                DependencyObject node = item as DependencyObject;
                if (node == null) continue;
                foreach (T nested in Fields<T>(node)) yield return nested;
            }
        }

        private static T Field<T>(Window dialog, string label) where T : Control
        {
            return (T)Fields<Label>(dialog).Single(item => (string)item.Content == label).Target;
        }

        private static void Press(Window dialog, string button)
        {
            Fields<Button>(dialog).Single(item => object.Equals(item.Content, button))
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        }

        private static void DriveDialog(Program program, string button, Action<Window> fill)
        {
            DriveDialog(program, button, delegate { program.Get<Button>(button).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); }, fill);
        }

        private static void DriveDialog(Program program, string flow, Action open, Action<Window> fill)
        {
            bool handled = false;
            Exception failure = null;
            program.window.Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(delegate
            {
                Window dialog = program.window.OwnedWindows.Cast<Window>().SingleOrDefault(item => item.IsVisible);
                if (dialog == null) return;
                dialog.Opacity = 0;
                try
                {
                    handled = true;
                    fill(dialog);
                    if (dialog.IsVisible) throw new InvalidOperationException("O formulário não aceitou os dados de teste.");
                }
                catch (Exception error) { failure = error; }
                finally { if (dialog.IsVisible) dialog.Close(); }
            }));
            open();
            if (failure != null) throw new InvalidOperationException("Falha no fluxo " + flow, failure);
            Require(handled, "Abertura do formulário " + flow);
        }
    }
}
