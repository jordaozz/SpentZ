using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;

namespace SpentZ
{
    internal sealed partial class Program
    {
        private static void VerifyChatLanguage(string folder)
        {
            DateTime today = new DateTime(2026, 11, 25);
            using (var store = new LedgerStore(Path.Combine(folder, Guid.NewGuid().ToString("N") + ".sqlite3")))
            {
                var program = new Program(store, () => today);
                ShowTestWindow(program);
                program.ChatText = "ganhei 150 de um free lance";
                program.RefreshChatHighlight();
                Require(program.Get<TextBlock>("ChatIntentPreview").Text == "Ganho", "O chat antecipa o tipo do ganho");
                program.RegisterMessage();
                Require(store.Month(today, true).Single().Category == "Trabalho extra", "Ganho com free lance usa a categoria correta");
                program.ChatText = "paguei 150 reais em uma peça da moto";
                program.RefreshChatHighlight();
                var input = program.Get<RichTextBox>("ChatInput");
                var format = new System.Windows.Documents.TextRange(ChatPosition(input.Document.ContentStart, 0),
                    ChatPosition(input.Document.ContentStart, 6));
                Require(program.Get<TextBlock>("ChatIntentPreview").Text == "Despesa" &&
                    ((System.Windows.Media.SolidColorBrush)format.GetPropertyValue(System.Windows.Documents.TextElement.ForegroundProperty)).Color ==
                    program.Tone("ExpenseInk").Color, "O verbo da despesa é destacado dentro do campo enquanto se digita");
                program.Render(folder, "Chat", 1120, 720, "Chat-intencao");
                program.RegisterMessage();
                Require(store.Month(today).Single().Description == "peça da moto" && store.Month(today).Single().Cents == 15000,
                    "Reais e os conectores são interpretados sem virar parte da descrição");
                Require(LedgerEntry.FromMessage("Hoje, eu recebi 150 de um bico", today).IsIncome &&
                    LedgerEntry.FromMessage("vou pagar o conserto 150", today) == null &&
                    LedgerEntry.FromMessage("não paguei a peça 150", today) == null,
                    "O chat reconhece variações de frase e ignora pagamentos planejados ou negados");
                program.ChatText = "parcelei 1200 reais em 6x na moto";
                program.RefreshChatHighlight();
                Require(program.Get<TextBlock>("ChatIntentPreview").Text.StartsWith("Parcelamento"), "O chat identifica parcelas antes de salvar");
                DriveDialog(program, "revisão das parcelas", program.RegisterMessage, dialog =>
                {
                    Require(Field<TextBox>(dialog, "Valor de cada cobrança (R$)").Text == "200,00" &&
                        Field<TextBox>(dialog, "Total de parcelas").Text == "6", "O cartão de revisão recebe seis parcelas de 200 reais");
                    Field<ComboBox>(dialog, "Já pagou a cobrança dessa data?").SelectedIndex = 0;
                    Press(dialog, "Salvar");
                });
                Require(store.Month(today).Count == 1 && store.Bills().Month(today, today).Single().Plan.Installments == 6,
                    "Parcelamento não lança o valor inteiro como despesa paga");
                program.window.Close();
            }
        }

        private static void VerifyChatSubscriptions(string folder)
        {
            DateTime today = new DateTime(2026, 11, 25);
            using (var store = new LedgerStore(Path.Combine(folder, Guid.NewGuid().ToString("N") + ".sqlite3")))
            {
                var program = new Program(store, () => today);
                ShowTestWindow(program);
                program.ChatText = "fiz uma assinatura do Discord Nitro ontem por 25 reais";
                program.RefreshChatHighlight();
                Require(program.Get<TextBlock>("ChatIntentPreview").Text.StartsWith("Assinatura"),
                    "O Chat identifica assinatura dentro da frase");
                program.Render(folder, "Chat", 1120, 720, "Chat-assinatura");
                DriveDialog(program, "cadastro de assinatura pelo Chat", program.RegisterMessage, dialog =>
                {
                    Require(Field<TextBox>(dialog, "Descrição").Text == "Discord Nitro" &&
                        Field<ComboBox>(dialog, "Frequência e tipo de cobrança").SelectedIndex == 1 &&
                        Field<TextBox>(dialog, "Valor de cada cobrança (R$)").Text == "25,00" &&
                        Field<TextBox>(dialog, "Primeiro vencimento (dia/mês/ano)").Text == "24/11/2026",
                        "A assinatura de ontem usa o serviço, valor e data informados");
                    Require(Fields<TextBlock>(dialog).Any(item => item.Text.Contains("24/12/2026")),
                        "O formulário exibe a próxima data automaticamente");
                    Field<ComboBox>(dialog, "Já pagou a cobrança dessa data?").SelectedIndex = 1;
                    Press(dialog, "Salvar");
                });
                Require(store.Month(today).Count == 1, "O primeiro pagamento de uma assinatura sai do saldo uma vez");
                var next = store.Bills().Month(today.AddMonths(1), today).Single();
                Require(next.Due.Day == 24 && next.Cents == 2500 && next.IsPending, "Renovação mensal aparece na agenda");
                Press(program.window, "Ver em Contas futuras");
                Require(program.agendaMode == AgendaMode.Plans && program.SelectedBill.Plan.Id == next.Plan.Id,
                    "O atalho do Chat encontra a assinatura cadastrada");
                DriveDialog(program, "BillEdit", dialog =>
                {
                    Field<TextBox>(dialog, "Próxima data (dia/mês/ano)").Text = "28/12/2026";
                    Field<TextBox>(dialog, "Novo valor (R$)").Text = "29,90";
                    Press(dialog, "Salvar alteração");
                });
                Require(store.Month(today).Single().Cents == 2500 && store.Bills().Month(today.AddMonths(1), today).Single().Cents == 2990 &&
                    store.Bills().Month(today.AddMonths(2), today).Single().Due.Day == 28,
                    "Editar a assinatura muda data e valor futuros e preserva o pagamento original");
                LedgerEntry previous = LedgerEntry.FromMessage("40 assinatura da Netflix", today);
                store.Save(previous);
                program.RefreshMonth();
                program.Navigate("History");
                program.Get<DataGrid>("HistoryGrid").SelectedItem = program.Get<DataGrid>("HistoryGrid")
                    .Items.Cast<LedgerEntry>().Single(item => item.Id == previous.Id);
                Require(program.Get<Button>("HistorySubscription").Visibility == Visibility.Visible, "Despesa antiga oferece conversão");
                DriveDialog(program, "HistorySubscription", dialog =>
                {
                    Field<ComboBox>(dialog, "Frequência e tipo de cobrança").SelectedIndex = 2;
                    Press(dialog, "Salvar");
                });
                Require(store.Month(today).Count == 2 && store.Bills().Month(today.AddYears(1), today).Any(row =>
                    row.Plan.Description == "Netflix" && row.Plan.Kind == BillKind.Annual), "Conversão vincula o registro e agenda a cobrança anual sem duplicar");
                program.Navigate("Chat");
                program.ChatText = "25 assinatura do Discord Nitro";
                DriveDialog(program, "assinatura com valor primeiro", program.RegisterMessage, dialog =>
                {
                    Require(Field<TextBox>(dialog, "Descrição").Text == "Discord Nitro" &&
                        Field<TextBox>(dialog, "Valor de cada cobrança (R$)").Text == "25,00", "Aceita o formato usado no Chat");
                    dialog.Close();
                });
                Require(store.Month(today).Count == 2 && program.ChatText.Length > 0,
                    "Cancelar mantém o rascunho e os registros existentes");
                program.window.Close();
            }
        }

        private static void VerifyAccountOverview(string folder)
        {
            DateTime today = new DateTime(2026, 11, 25);
            using (var store = new LedgerStore(Path.Combine(folder, Guid.NewGuid().ToString("N") + ".sqlite3")))
            {
                store.SetOpeningBalance(today.AddDays(-2), 100000);
                store.Save(LedgerEntry.FromMessage("100 mercado", today));
                store.Save(LedgerEntry.FromMessage("recebi 50 freela", today));
                store.Save(LedgerEntry.FromMessage("1000 compra futura", today.AddDays(1)));
                store.AddReserveMovement("PicPay", today, 20000, false, null, true);
                store.AddReserveMovement("Caixinha", today, 5000, false);
                store.SavePlan(new BillPlan { Description = "Conta vencida", Cents = 30000, Category = "Moradia",
                    FirstDue = today.AddMonths(-1), IsMonthlyBill = true, Installments = 1 });
                store.SavePlan(new BillPlan { Description = "Assinatura", Cents = 20000, Category = "Lazer", FirstDue = today.AddDays(4) });
                store.SavePlan(new BillPlan { Description = "Ganho previsto", Cents = 50000, Category = "Salário",
                    FirstDue = today.AddDays(3), IsIncome = true });
                var program = new Program(store, () => today);
                ShowTestWindow(program);
                program.Navigate("Summary", () => program.month = MonthStart(today.AddYears(-1)));
                program.Get<Button>("OpenAccount").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Require(program.currentPage == "Account" && program.ParentPage() == "Summary", "Painel abre dentro do Resumo");
                Require(program.Get<TextBlock>("AccountCash").Text == LedgerEntry.Money(90000) &&
                    program.Get<TextBlock>("AccountAfter").Text == LedgerEntry.Money(40000) &&
                    program.Get<TextBlock>("AccountCommitted").Text == LedgerEntry.Money(50000) &&
                    program.Get<TextBlock>("AccountExpected").Text == LedgerEntry.Money(50000) &&
                    program.Get<TextBlock>("AccountSaved").Text == LedgerEntry.Money(25000),
                    "Painel usa o dia atual, inclui vencidas e mantém o Cofrinho e o ganho futuro fora do valor disponível");
                Require(program.Get<ItemsControl>("AccountBillRows").Items.Count == 2 &&
                    program.Get<ItemsControl>("AccountSubscriptionRows").Items.Count == 1 &&
                    program.Get<TextBlock>("AccountOverdue").Text.Contains(LedgerEntry.Money(30000)), "Lista separa pendências e assinaturas");
                program.Get<TextBox>("AccountPurchase").Text = "450";
                Require(program.Get<TextBlock>("AccountSimulation").Text.Contains(LedgerEntry.Money(5000)) &&
                    store.Month(today).Count == 2, "Simulação indica a diferença e não lança a compra");
                var preview = new Program(store, () => today);
                preview.Render(folder, "Account", 1120, 840, "Acompanhar-conta");
                preview.dark = true;
                preview.ApplyTheme();
                preview.Render(folder, "Account", 780, 590, "Acompanhar-conta-compacto");
                preview.window.Close();
                program.Get<Button>("NavigateBack").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Require(program.currentPage == "Summary" && program.month == MonthStart(today.AddYears(-1)),
                    "Voltar preserva o mês selecionado no Resumo");
                program.Get<Button>("OpenAccount").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                program.Get<Button>("AccountBills").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Require(program.currentPage == "Bills" && program.Get<DataGrid>("BillGrid").Items.Count == 2,
                    "O painel abre as pendências até o fim do mês, inclusive vencidas");
                program.window.Close();
            }
        }

        private static void VerifyGoalPurchaseAndPrivateBackup(string folder)
        {
            DateTime today = new DateTime(2026, 11, 25);
            string file = Path.Combine(folder, Guid.NewGuid().ToString("N") + ".sqlite3");
            using (var store = new LedgerStore(file))
            {
                store.SetOpeningBalance(today, 83400);
                store.AddReserveMovement("Banco", today, 30000, false, null, true);
                Require(store.AvailableBalance(today) == 83400 && store.ReserveBalances(today).Single().Balance == 30000,
                    "Saldo já guardado entra no Cofrinho sem descontar do disponível inicial");
                store.SaveSavingsGoal(0, "Moto", 30000);
                var goal = store.SavingsGoals().Single();
                var place = store.GoalFundingByPlace(goal.Id).Single();
                store.MoveGoalFunds(goal.Id, place.PlaceId, 30000, false);
                try { store.BuyFromGoal(goal.Id, place.PlaceId, 31000, today, "Fictício", "Compras"); }
                catch (IOException) { }
                Require(store.Month(today).Count == 0 && store.SavingsGoals().Single().Saved == 30000,
                    "Falha na compra preserva a despesa e a alocação");
                store.BuyFromGoal(goal.Id, place.PlaceId, 20000, today, "Compra fictícia da moto", "Compras");
                Require(store.Month(today).Single().Cents == 20000 && store.AvailableBalance(today) == 83400 &&
                    store.SavingsGoals().Single().Saved == 10000 && store.SavingsGoals().Single().Spent == 20000,
                    "Compra da meta baixa a reserva e registra uma única despesa");
                store.SetGoalArchived(goal.Id, true);
                Require(store.SavingsGoals().Count == 0 && store.SavingsGoals(true).Single().Spent == 20000 &&
                    store.GoalFundingByPlace(goal.Id).Single().Free == 10000, "Arquivar preserva o histórico e libera a sobra");

                string backup = Path.Combine(folder, "protegido-teste.spentz");
                const string password = "frase longa de teste 2026";
                store.SaveProtectedBackup(backup, password);
                Require(!System.Text.Encoding.UTF8.GetString(File.ReadAllBytes(backup)).Contains("Compra fictícia"),
                    "Dados do backup não aparecem em texto no arquivo");
                bool denied = false;
                try { store.DescribeBackup(backup, "senha errada do teste"); }
                catch (IOException) { denied = true; }
                Require(denied && store.Month(today).Count == 1, "Senha errada não altera os dados");
                store.ResetRecords();
                string safety = store.RestoreBackup(backup, password, password);
                Require(safety.EndsWith(".spentz") && File.Exists(safety) && store.SavingsGoals(true).Count == 1 &&
                    store.GoalPurchases(goal.Id).Count == 1, "Backup protegido restaura compras, metas arquivadas e cria recuperação protegida");
                store.SetGoalArchived(goal.Id, false);
                store.UndoGoalPurchase(goal.Id, store.GoalPurchases(goal.Id).Single().ExpenseId);
                Require(store.Month(today).Count == 0 && store.SavingsGoals().Single().Saved == 20000 &&
                    store.AvailableBalance(today) == 83400, "Desfazer compra devolve valor à meta");
            }
        }

        private static void VerifySavingsGoals(string folder)
        {
            DateTime today = new DateTime(2026, 11, 25);
            using (LedgerStore store = new LedgerStore(Path.Combine(folder, Guid.NewGuid().ToString("N") + ".sqlite3")))
            {
                store.AddReserveMovement("Banco", today, 100000, false);
                decimal available = store.AvailableBalance(today);
                Program program = new Program(store, () => today);
                ShowTestWindow(program);
                program.Navigate("Reserves");
                program.Get<Button>("OpenGoals").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Require(program.currentPage == "Goals" && program.ParentPage() == "Reserves", "Metas abre dentro do Cofrinho");
                program.Get<Button>("GoalArchiveList").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Require(program.currentPage == "ArchivedGoals" && program.ParentPage() == "Goals" &&
                    program.Get<TextBlock>("GoalStatus").Text == "Nenhuma meta arquivada." &&
                    (string)program.Get<Button>("NavigateBack").Content == "‹  Voltar para Metas", "Lista vazia tem seu próprio botão Voltar");
                program.Get<Button>("NavigateBack").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Require(program.currentPage == "Goals" && program.Get<WrapPanel>("GoalPageActions").Visibility == Visibility.Visible,
                    "Voltar das arquivadas recupera as metas ativas");
                DriveDialog(program, "CreateGoal", dialog =>
                {
                    Field<TextBox>(dialog, "Nome da meta").Text = "Moto";
                    Field<TextBox>(dialog, "Quanto você quer guardar (R$)").Text = "12000";
                    Press(dialog, "Salvar meta");
                });
                store.SaveSavingsGoal(0, "Viagem", 200000);
                SavingsGoal moto = store.SavingsGoals().Single(goal => goal.Name == "Moto");
                SavingsGoal viagem = store.SavingsGoals().Single(goal => goal.Name == "Viagem");
                long bank = store.GoalFundingByPlace(moto.Id).Single().PlaceId;
                DriveDialog(program, "reservar valor", () => program.EditGoalFunding(moto, false), dialog =>
                {
                    Field<TextBox>(dialog, "Valor (R$)").Text = "700";
                    Press(dialog, "Reservar valor");
                });
                store.MoveGoalFunds(viagem.Id, bank, 20000, false);
                bool blocked = false;
                try { store.AddReserveMovement("Banco", today, 10001, true); }
                catch (IOException) { blocked = true; }
                Require(blocked && store.AvailableBalance(today) == available && store.ReserveBalances(today).Single().Balance == 100000 &&
                    store.GoalFundingByPlace(moto.Id).Single().Free == 10000, "Metas no mesmo banco não duplicam dinheiro e protegem a parte reservada");
                store.MoveGoalFunds(moto.Id, bank, 10000, true);
                store.AddReserveMovement("Banco", today, 20000, true);
                Require(store.SavingsGoals().Single(goal => goal.Id == moto.Id).Saved == 60000 &&
                    store.GoalFundingByPlace(moto.Id).Single().Free == 0, "Liberar um valor permite sacar sem alterar outra meta");
                Program preview = new Program(store, () => today);
                preview.Render(folder, "Goals", 980, 680, "Metas");
                preview.Render(folder, "Goals", 780, 550, "Metas-compacto");
                preview.window.Close();
                string backup = Path.Combine(folder, "backup-metas.sqlite3");
                store.SaveBackup(backup);
                store.ResetRecords();
                Require(store.SavingsGoals().Count == 0, "O reset limpa as metas");
                store.RestoreBackup(backup);
                Require(store.SavingsGoals().Count == 2 && store.ReserveBalances(today).Single().Balance == 80000,
                    "Restauração recupera metas e valores por local");
                store.SetGoalArchived(moto.Id, true);
                program.Get<Button>("GoalArchiveList").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Require(program.Get<StackPanel>("GoalCards").Children.Count == 1, "Subtela lista somente metas arquivadas");
                var archivePreview = new Program(store, () => today);
                archivePreview.Render(folder, "ArchivedGoals", 980, 680, "Metas-arquivadas");
                archivePreview.window.Close();
                program.GoBack();
                Require(program.currentPage == "Goals" && program.Get<StackPanel>("GoalCards").Children.Count == 1,
                    "Voltar das arquivadas leva às metas ativas");
                program.GoBack();
                Require(program.currentPage == "Reserves", "Voltar retorna ao Cofrinho");
                program.window.Close();
            }
        }

        private static void VerifyChatCategoryMatching(string folder)
        {
            DateTime today = new DateTime(2026, 11, 21);
            using (LedgerStore store = new LedgerStore(Path.Combine(folder, Guid.NewGuid().ToString("N") + ".sqlite3")))
            {
                string category = store.AddExpenseCategory("Carta Pokémon");
                store.SetCategoryLimit(category, today, 50000);
                Program program = new Program(store, () => today);
                string[] descriptions = { "CARTA POKEMON", "carta   pokémon", "carta-pokemon", "carta pokemom", "cartas pokmeon",
                    "carta de pokemon", "compras carta pokemom" };
                foreach (string description in descriptions)
                {
                    program.ChatText = "20 " + description;
                    program.RegisterMessage();
                    LedgerEntry saved = store.Month(today).First();
                    Require(saved.Category == category && saved.Description == description,
                        "Chat classifica a descrição original " + description + " em " + category);
                }
                Require(store.CategoryProgressFor(today).Single().Used == 14000, "Variações da categoria entram no mesmo limite");
                store.AddExpenseCategory("Cigarro");
                store.AddExpenseCategory("Cigarra");
                store.AddExpenseCategory("Pet");
                string[] categories = store.ExpenseCategories();
                Require(LedgerEntry.FromMessage("150 educaçao", today, categories).Category == "Educação" &&
                    LedgerEntry.FromMessage("20 almoco", today, categories).Category == "Alimentação", "Categorias padrão aceitam acentos ausentes");
                Require(LedgerEntry.FromMessage("10 cigarr", today, categories).Category == "Sem categoria" &&
                    LedgerEntry.FromMessage("10 pez", today, categories).Category == "Sem categoria" &&
                    LedgerEntry.FromMessage("10 mesa usada", today, categories).Category == "Sem categoria",
                    "Uma grafia ambígua ou muito curta não escolhe categoria sozinha");
                Require(LedgerEntry.FromMessage("20 cigarro", today, categories).Category == "Cigarro",
                    "Nome exato vence categorias parecidas");
                program.window.Close();
            }
        }

        private static void VerifyBudgetAndAdjustments(string folder)
        {
            DateTime today = new DateTime(2026, 11, 23);
            string path = Path.Combine(folder, Guid.NewGuid().ToString("N") + ".sqlite3");
            string backup = Path.Combine(folder, "categorias-e-ajustes.sqlite3");
            using (LedgerStore store = new LedgerStore(path))
            {
                store.Save(LedgerEntry.FromMessage("recebi 1020 salário", today));
                store.Save(LedgerEntry.FromMessage("60 despesa antiga", today.AddDays(-10)));
                Program program = new Program(store, () => today);
                ShowTestWindow(program);
                Require(program.Get<StackPanel>("Messages").Children.Count == 2 && store.Month(today).Count == 1,
                    "O chat mostra três dias, preservando os registros anteriores");
                store.Delete(store.Month(today).Single());
                program.Navigate("Summary");
                DriveDialog(program, "CreateCategory", dialog =>
                {
                    Field<TextBox>(dialog, "Nome da categoria").Text = "Cigarro";
                    Press(dialog, "Criar categoria");
                });
                Require(store.ExpenseCategories().Contains("Cigarro") && store.CategoryLimits(today).Count == 0,
                    "Criar categoria não exige limite");
                DriveDialog(program, "BudgetSettings", dialog =>
                {
                    ComboBox category = Field<ComboBox>(dialog, "Categoria");
                    Require(category.Items.Contains("Alimentação") && category.Items.Contains("Cigarro"),
                        "O seletor oferece categorias padrão e personalizadas");
                    category.SelectedItem = "Cigarro";
                    Field<TextBox>(dialog, "Limite por mês (R$)").Text = "100";
                    Press(dialog, "Salvar limite");
                });
                program.Navigate("Chat");
                program.ChatText = "20 cigarro";
                program.Get<Button>("Send").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Require(store.Month(today).Single().Category == "Cigarro" &&
                    store.CategoryLimits(today.AddMonths(1))["Cigarro"] == 10000,
                    "Categoria criada pelo usuário funciona no chat e o limite se repete");
                program.Navigate("Summary");
                program.Render(folder, "Summary", 1120, 720, "Resumo-com-limites");
                DriveBalanceAdjustment(program, "890");
                DriveBalanceAdjustment(program, "1200");
                Require(store.AvailableBalance(today) == 120000 &&
                    store.Month(today).Single(entry => entry.Kind == 1).Cents == 11000 &&
                    store.Month(today, true).Single(entry => entry.Kind == 1).Cents == 31000 &&
                    Fields<TextBlock>(program.Get<StackPanel>("CategoryLimits")).Any(item => item.Text.Contains(LedgerEntry.Money(8000))),
                    "Ajustes registram as duas diferenças e não consomem o limite da categoria");
                program.Navigate("History");
                DataGrid history = program.Get<DataGrid>("HistoryGrid");
                Require(history.Items.Count == 4, "Lançamentos reúne ganhos, gastos e ajustes");
                history.SelectedItem = history.Items.Cast<LedgerEntry>().Single(entry => entry.Category == "Cigarro");
                DriveDialog(program, "HistoryRefund", dialog =>
                {
                    Field<TextBox>(dialog, "Valor devolvido (R$)").Text = "5";
                    Press(dialog, "Registrar estorno");
                });
                Require(store.AvailableBalance(today) == 120500 &&
                    store.RefundsForCategoryLimits(today)["Cigarro"] == 500 &&
                    store.Month(today).Single(entry => entry.Category == "Cigarro").Cents == 2000,
                    "Estorno parcial mantém o gasto original, devolve o valor e corrige o consumo do limite");
                program.Render(folder, "History", 1120, 720, "Lancamentos-com-cores");
                program.Get<ComboBox>("HistoryType").SelectedIndex = 2;
                Require(history.Items.Count == 3 && history.Items.Cast<LedgerEntry>().All(entry => entry.IsIncome),
                    "Filtro Ganhos reúne entradas com sinal positivo");
                program.Navigate("Summary");
                program.Get<Button>("OpenClosures").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Require(program.Get<TextBlock>("ClosureAdjustments").Text.Contains(LedgerEntry.Money(11000)) &&
                    program.Get<TextBlock>("ClosureAdjustments").Text.Contains(LedgerEntry.Money(31000)), "Fechamento detalha entradas e saídas por ajuste");
                string report = Path.Combine(folder, "relatorio-categorias-ajustes.csv");
                program.WriteMonthlyReport(report, (MonthClosure)program.Get<DataGrid>("ClosureGrid").SelectedItem);
                Require(File.ReadAllText(report).Contains("Ajuste de saldo · gasto não informado"), "O relatório registra a origem desconhecida do ajuste");

                program.Navigate("Chat");
                DriveDialog(program, "QuickExpense", dialog =>
                {
                    Field<TextBox>(dialog, "Descrição").Text = "IPVA";
                    Field<TextBox>(dialog, "Valor em reais").Text = "150";
                    Field<TextBox>(dialog, "Data (dia/mês/ano)").Text = "07/12/2026";
                    Press(dialog, "Agendar conta avulsa");
                });
                Require(store.Month(today.AddMonths(1)).Count == 0 &&
                    store.Bills().Month(today.AddMonths(1), today).Single().Plan.IsOneTime &&
                    store.Bills().Month(today.AddMonths(2), today).Count == 0, "Conta avulsa entra só na previsão, sem repetir");
                program.OpenNextBillMonth(false);
                program.Navigate("Summary");
                Require(program.month == MonthStart(today), "Próximo mês em Contas futuras não altera o mês do Resumo");
                store.SaveBackup(backup);
                Require(store.DescribeBackup(backup).StartsWith("5 lançamentos"), "Prévia de backup informa os lançamentos encontrados");
                store.ResetRecords();
                Require(store.CategoryLimits(today).Count == 0 && !store.ExpenseCategories().Contains("Cigarro"), "Reset limpa categorias e limites");
                store.RestoreBackup(backup);
                Require(store.CategoryLimits(today)["Cigarro"] == 10000 && store.AvailableBalance(today) == 120500,
                    "Backup restaura categorias, estornos e ajustes");
                program.window.Close();
            }
        }

        private static void DriveBalanceAdjustment(Program program, string amount)
        {
            DriveDialog(program, "AdjustBalance", dialog =>
            {
                Field<TextBox>(dialog, "Saldo disponível correto (R$)").Text = amount;
                Press(dialog, "Registrar ajuste");
            });
        }

        private static void VerifyOverdueAndPlanChanges(string folder)
        {
            DateTime today = new DateTime(2026, 11, 23);
            DateTime due = today.AddDays(-2);
            using (LedgerStore store = new LedgerStore(Path.Combine(folder, Guid.NewGuid().ToString("N") + ".sqlite3")))
            {
                BillPlan subscription = new BillPlan {
                    Description = "Assinatura", Category = "Lazer", Cents = 2499, FirstDue = due.AddMonths(-1)
                };
                store.SavePlan(subscription);
                Require(store.StopRenewal(subscription, due.AddMonths(1), today), "Cancela apenas a próxima renovação");
                store.Save(LedgerEntry.FromMessage("recebi 1000 salário", today));
                Program program = new Program(store, () => today);
                ShowTestWindow(program);
                program.Navigate("Summary");
                Require(program.Get<TextBlock>("ForecastBalance").Text == LedgerEntry.Money(95002) &&
                    program.Get<TextBlock>("SummaryCommitments").Text.Contains(LedgerEntry.Money(2499)),
                    "Saldo previsto considera dívida vencida de outro mês");
                BillCalendar calendar = store.Bills();
                Require(calendar.Month(due.AddMonths(1), today).Count == 0 && calendar.Pending(today, 0).Count == 2,
                    "Cancelamento mantém cobranças já vencidas e retira a renovação");
                program.OpenAgenda(false, false);
                program.Get<Button>("BillPendingView").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                DataGrid agenda = program.Get<DataGrid>("BillGrid");
                agenda.SelectedItem = agenda.Items.Cast<BillOccurrence>().Single(row => row.Due == due.AddMonths(-1));
                DriveDialog(program, "BillPay", dialog => Press(dialog, "Confirmar pagamento"));
                store.WaiveBill(store.Bills().Pending(today, 0).Single());
                Require(store.Bills().Pending(today, 0).Count == 0 && store.Bills().Month(today, today).Single().Waived,
                    "É possível quitar ou dispensar uma cobrança anterior ao cancelamento");
                string report = Path.Combine(folder, "pagamento-outro-mes.csv");
                store.SaveClosure(due.AddMonths(-1));
                program.ReloadBills();
                MonthClosure period = store.Closures(today).Single(item => item.Month == MonthStart(due.AddMonths(-1)));
                program.WriteMonthlyReport(report, period);
                Require(period.Expenses == 0 && File.ReadAllText(report).Contains("Paga em 23/11/2026"),
                    "Relatório anterior mostra vencimento e a data real de um pagamento em outro mês");

                BillPlan rent = new BillPlan { Description = "Aluguel", Category = "Moradia", IsMonthlyBill = true,
                    Cents = 45000, FirstDue = due };
                store.SavePlan(rent);
                store.Save(LedgerEntry.FromMessage("450 Aluguel", due));
                program.ReloadBills();
                program.OpenAgenda(false, false);
                agenda.SelectedItem = agenda.Items.Cast<BillOccurrence>().Single(row => row.Plan.Id == rent.Id);
                DriveDialog(program, "BillPay", dialog =>
                {
                    Require(Fields<CheckBox>(dialog).Single().IsChecked == true, "Pagamento equivalente sugere vincular o registro existente");
                    Press(dialog, "Vincular lançamento");
                });
                Require(store.Month(today).Count(entry => entry.Description == "Aluguel") == 1, "Vínculo evita despesa duplicada");
                program.Get<Button>("BillPlansView").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                agenda.SelectedItem = agenda.Items.Cast<BillOccurrence>().Single(row => row.Plan.Id == rent.Id);
                DriveDialog(program, "BillEdit", dialog =>
                {
                    Field<TextBox>(dialog, "Próxima data (dia/mês/ano)").Text = "26/12/2026";
                    Field<TextBox>(dialog, "Novo valor (R$)").Text = "480";
                    Press(dialog, "Salvar alteração");
                });
                BillOccurrence renewed = store.Bills().Month(today.AddMonths(1), today).Single();
                Require(renewed.Due.Day == 26 && renewed.Cents == 48000 && renewed.Plan.IsMonthlyBill &&
                    store.Bills().ConfirmedInMonth(today, today).Single(item => item.Plan.Id == rent.Id).Cents == 45000,
                    "Mudança de plano altera os próximos vencimentos e mantém o pagamento anterior");
                program.window.Close();
            }
        }

        private static void VerifyReserveCorrections(string folder)
        {
            DateTime today = new DateTime(2026, 11, 23);
            using (LedgerStore store = new LedgerStore(Path.Combine(folder, Guid.NewGuid().ToString("N") + ".sqlite3")))
            {
                store.AddReserveMovement("PicPay", today.AddDays(-2), 20000, false);
                store.AddReserveMovement("PicPay", today.AddDays(-1), 12000, true);
                Program program = new Program(store, () => today);
                ShowTestWindow(program);
                program.Navigate("Reserves");
                var grid = program.Get<DataGrid>("ReserveGrid");
                grid.SelectedItem = grid.Items.Cast<ReserveMovement>().Single(item => !item.Withdraw);
                DriveDialog(program, "ReserveEdit", dialog =>
                {
                    Field<TextBox>(dialog, "Valor (R$)").Text = "150";
                    Press(dialog, "Salvar alteração");
                });
                Require(store.ReserveBalances(today).Single().Balance == 3000 && store.ReserveMovements(today).Count == 2,
                    "Editar depósito corrige o saldo sem criar outra movimentação");
                ReserveMovement original = store.ReserveMovements(today).Single(item => !item.Withdraw);
                bool rejected = false;
                try { store.AddReserveMovement("PicPay", original.Date, 10000, false, original); }
                catch (IOException) { rejected = true; }
                Require(rejected && store.ReserveBalances(today).Single().Balance == 3000,
                    "Edição que invalida um saque posterior é revertida");
                program.window.Close();
            }
        }

        private static void VerifyOpeningBalance(string folder)
        {
            DateTime today = new DateTime(2026, 11, 21);
            using (LedgerStore fresh = new LedgerStore(Path.Combine(folder, Guid.NewGuid().ToString("N") + ".sqlite3")))
            {
                Program first = new Program(fresh, () => today);
                ShowTestWindow(first);
                DriveDialog(first, "primeiro acesso", first.OfferOpeningBalance, dialog =>
                {
                    Require(Fields<Button>(dialog).Any(button => (string)button.Content == "Definir depois"),
                        "O saldo inicial pode ser adiado no primeiro acesso");
                    Field<TextBox>(dialog, "Saldo disponível (R$)").Text = "834";
                    Press(dialog, "Definir saldo inicial");
                });
                Require(fresh.AvailableBalance(today) == 83400 && fresh.Month(today, true).Count == 0,
                    "Primeiro acesso parte do saldo informado sem registrá-lo como ganho");
                first.window.Close();
            }
            string path = Path.Combine(folder, Guid.NewGuid().ToString("N") + ".sqlite3");
            string backup = Path.Combine(folder, "saldo-inicial.sqlite3");
            using (LedgerStore store = new LedgerStore(path))
            {
                Require(store.IsBlankForOpening() && store.GetOpeningBalance() == null, "Uma base nova oferece o saldo inicial");
                store.Save(LedgerEntry.FromMessage("recebi 2500 pagamento antigo", today.AddDays(-16)));
                store.Save(LedgerEntry.FromMessage("20 anotação anterior", today));
                Program program = new Program(store, () => today);
                ShowTestWindow(program);
                program.Navigate("Settings");
                DriveDialog(program, "SettingsOpeningBalance", dialog =>
                {
                    Field<TextBox>(dialog, "Saldo disponível (R$)").Text = "834";
                    Press(dialog, "Definir saldo inicial");
                });
                Require(store.AvailableBalance(today) == 83400 && store.Month(today, true).Count == 1,
                    "Saldo informado é o ponto de partida sem apagar registros existentes ou contar o dinheiro de novo");

                program.Navigate("Chat");
                DriveDialog(program, "QuickFixedIncome", dialog =>
                {
                    Field<TextBox>(dialog, "Descrição (ex.: Salário)").Text = "Salário";
                    Field<TextBox>(dialog, "Valor mensal (R$)").Text = "2500";
                    Field<TextBox>(dialog, "Primeira data prevista (dia/mês/ano)").Text = "05/11/2026";
                    Field<ComboBox>(dialog, "Já recebeu o valor dessa data?").SelectedIndex = 1;
                    Press(dialog, "Salvar");
                });
                DriveDialog(program, "QuickFixedExpense", dialog =>
                {
                    Field<ComboBox>(dialog, "Tipo de gasto fixo").SelectedIndex = (int)FixedExpenseType.MonthlySubscription;
                    Field<TextBox>(dialog, "Descrição").Text = "Netflix";
                    Field<TextBox>(dialog, "Valor de cada cobrança (R$)").Text = "49,90";
                    Field<TextBox>(dialog, "Primeiro vencimento (dia/mês/ano)").Text = "05/11/2026";
                    Field<ComboBox>(dialog, "Já pagou a cobrança dessa data?").SelectedIndex = 1;
                    Press(dialog, "Salvar");
                });
                BillCalendar agenda = store.Bills();
                Require(store.AvailableBalance(today) == 83400 &&
                    agenda.ConfirmedInMonth(today, today).Single().Payment.InOpeningBalance &&
                    agenda.ConfirmedInMonth(today, today, true).Single().Payment.InOpeningBalance &&
                    agenda.Month(today.AddMonths(1), today).Single().Due.Day == 5 &&
                    agenda.Month(today.AddMonths(1), today, true).Single().Due.Day == 5,
                    "Salário e assinatura já considerados ficam como confirmados, sem alterar o caixa ou a próxima data");
                DriveDialog(program, "QuickFixedExpense", dialog =>
                {
                    Field<TextBox>(dialog, "Descrição").Text = "Internet";
                    Field<TextBox>(dialog, "Valor mensal (R$)").Text = "100";
                    Field<TextBox>(dialog, "Primeiro vencimento (dia/mês/ano)").Text = "05/11/2026";
                    Field<ComboBox>(dialog, "Já pagou a cobrança dessa data?").SelectedIndex = 0;
                    Press(dialog, "Salvar");
                });
                Require(store.Bills().Pending(today, 0).Single().Description == "Internet", "Conta anterior ainda não paga continua em aberto");
                program.OpenAgenda(false, false);
                program.Get<DataGrid>("BillGrid").SelectedIndex = 0;
                DriveDialog(program, "BillPay", dialog => Press(dialog, "Confirmar pagamento"));
                Require(store.AvailableBalance(today) == 73400 && store.AvailableBalance(today.AddMonths(1)) == 73400,
                    "Pagamento posterior reduz o saldo, que segue para o mês seguinte");
                program.Navigate("Summary");
                Program preview = new Program(store, () => today);
                preview.Render(folder, "Summary", 780, 550, "Saldo-inicial-compacto");
                preview.Render(folder, "Summary", 1120, 720, "Saldo-inicial");
                preview.window.Close();
                program.Get<Button>("OpenClosures").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                var closure = (MonthClosure)program.Get<DataGrid>("ClosureGrid").SelectedItem;
                string report = Path.Combine(folder, "relatorio-saldo-inicial.csv");
                program.WriteMonthlyReport(report, closure);
                Require(File.ReadAllText(report).Contains("Saldo inicial informado") &&
                    File.ReadAllText(report).Contains("incluído no saldo inicial") &&
                    program.Get<TextBlock>("ClosureAvailable").Text == LedgerEntry.Money(73400), "Fechamento mostra o ponto de partida e o saldo real");
                store.SaveBackup(backup);
                store.ResetRecords();
                Require(store.GetOpeningBalance() == null, "Reset remove o saldo inicial");
                store.RestoreBackup(backup);
                Require(store.GetOpeningBalance().Cents == 83400 && store.AvailableBalance(today) == 73400 &&
                    store.Bills().ConfirmedInMonth(today, today).Any(item => item.Payment.InOpeningBalance),
                    "Backup preserva o saldo inicial e as ocorrências já pagas");
                program.window.Close();
            }
        }

        private static void VerifyForecastAndFirstPayment(string folder)
        {
            DateTime today = new DateTime(2026, 11, 21);
            string file = Path.Combine(folder, Guid.NewGuid().ToString("N") + ".sqlite3");
            using (LedgerStore store = new LedgerStore(file))
            {
                store.Save(LedgerEntry.FromMessage("20 anotação anterior", today));
                store.SetOpeningBalance(today, 83400);
                foreach (DateTime due in new[] { today.AddDays(3), today.AddMonths(1) })
                    store.SavePlan(new BillPlan {
                        Description = "Parcela de teste", Category = "Compras", Installments = 1, IsMonthlyBill = true,
                        FirstDue = due, Cents = due.Month == today.Month ? 30000 : 10000
                    });
                Program program = new Program(store, () => today);
                ShowTestWindow(program);
                program.Navigate("Summary");
                program.ShiftMonth(1);
                Require(program.Get<TextBlock>("ForecastBalance").Text == LedgerEntry.Money(43400) &&
                    program.Get<TextBlock>("SummaryCommitments").Text.Contains(LedgerEntry.Money(30000)),
                    "A previsão de dezembro inclui contas de novembro");
                Program preview = new Program(store, () => today);
                preview.Navigate("Summary", delegate { preview.month = MonthStart(today.AddMonths(1)); });
                preview.Render(folder, "Summary", 780, 550, "Resumo-previsao-compacto");
                preview.Get<Expander>("ForecastDetails").IsExpanded = true;
                preview.Render(folder, "Summary", 780, 550, "Resumo-previsao-detalhada");
                preview.window.Close();
                program.Navigate("Chat");
                program.ChatText = "50 Netflix";
                program.RegisterMessage();
                DriveDialog(program, "QuickFixedExpense", dialog =>
                {
                    Field<ComboBox>(dialog, "Tipo de gasto fixo").SelectedIndex = (int)FixedExpenseType.MonthlySubscription;
                    Field<TextBox>(dialog, "Descrição").Text = "Netflix";
                    Field<TextBox>(dialog, "Valor de cada cobrança (R$)").Text = "55";
                    ComboBox status = Field<ComboBox>(dialog, "Já pagou a cobrança dessa data?");
                    Require(status.SelectedIndex == -1, "A primeira cobrança vencida começa sem resposta");
                    Press(dialog, "Salvar");
                    Require(dialog.IsVisible, "Salvar exige escolher a situação da primeira cobrança");
                    status.SelectedIndex = 2;
                    ComboBox choices = Field<ComboBox>(dialog, "Vincular a um lançamento existente?");
                    Require(choices.Items.Count == 2 && choices.SelectedIndex == -1, "O usuário escolhe entre vincular e criar outro lançamento");
                    Press(dialog, "Salvar");
                    Require(dialog.IsVisible, "Não registra um pagamento parecido sem confirmação explícita");
                    choices.SelectedIndex = 0;
                    Press(dialog, "Salvar");
                });
                Require(store.Month(today).Count == 2 && store.AvailableBalance(today) == 78400 &&
                    store.Bills().ConfirmedInMonth(today, today).Single().Payment.EntryId == store.Month(today).First().Id,
                    "Vínculo da primeira cobrança mantém um único gasto real");
                program.Navigate("Settings");
                DriveDialog(program, "SettingsOpeningBalance", dialog =>
                {
                    Field<TextBox>(dialog, "Saldo disponível (R$)").Text = "800";
                    Press(dialog, "Confirmar correção");
                });
                Require(store.AvailableBalance(today) == 75000 && store.GetOpeningBalance().Date == today &&
                    store.Month(today).Count == 2 && program.Get<TextBlock>("Total").Text == LedgerEntry.Money(7000) &&
                    program.Get<TextBlock>("SummaryExpensesSinceOpening").Text.Contains(LedgerEntry.Money(5000)),
                    "Corrigir saldo inicial recalcula sem ajuste fictício e distingue os gastos anteriores");
                program.window.Close();
            }
        }

        private static void VerifyStaleEdits(string folder)
        {
            using (FileStream session = TryOpenSession(folder))
                Require(session != null && TryOpenSession(folder) == null, "O banco fica reservado a uma instância do SpentZ");
            using (FileStream session = TryOpenSession(folder))
                Require(session != null, "O banco pode ser aberto de novo após fechar a primeira instância");

            DateTime today = new DateTime(2026, 11, 21);
            string path = Path.Combine(folder, Guid.NewGuid().ToString("N") + ".sqlite3");
            using (LedgerStore first = new LedgerStore(path))
            using (LedgerStore second = new LedgerStore(path))
            {
                LedgerEntry obsolete = LedgerEntry.FromMessage("20 teste", today);
                first.Save(obsolete);
                second.ResetRecords();
                LedgerEntry replacement = LedgerEntry.FromMessage("20 teste", today);
                second.Save(replacement);
                Require(obsolete.Id == replacement.Id, "Reproduz reaproveitamento de ID após reset");
                bool rejected = false;
                try { obsolete.Description = "edição antiga"; first.Save(obsolete); }
                catch (IOException) { rejected = true; }
                Require(rejected && !first.Delete(obsolete) && second.Month(today).Single().Version == replacement.Version,
                    "Edição e exclusão antigas não atingem o registro novo com o mesmo ID");
            }
        }
    }
}
