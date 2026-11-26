using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace SpentZ
{
    public sealed class LedgerEntry
    {
        internal static readonly CultureInfo Brazilian = CultureInfo.GetCultureInfo("pt-BR");
        private const string AmountPattern = @"(?:[0-9]{1,3}(?:\.[0-9]{3})+(?:,[0-9]{1,2})?|[0-9]+(?:[,.][0-9]{1,2})?)";
        private static readonly Regex ChatPattern = new Regex(@"^(?:R\$\s*)?(?<amount>" + AmountPattern +
            @")(?:\s+re(?:al|ais)\b)?(?:\s+(?<description>.*?))?$", RegexOptions.IgnoreCase);
        private static readonly Regex ChatEndingAmount = new Regex(@"^(?<description>.+?)\s+(?:R\$\s*)?(?<amount>" +
            AmountPattern + @")(?:\s+re(?:al|ais)\b)?$", RegexOptions.IgnoreCase);
        internal const string ChatStart = @"^\s*(?:(?:hoje|agora)(?:\s*,\s*|\s+))?(?:eu\s+)?";
        internal static readonly Regex IncomePrefix = new Regex(ChatStart + @"(?<keyword>recebi|ganhei|entrou|vendi|faturei|lucrei)\b\s*", RegexOptions.IgnoreCase);
        internal static readonly Regex ExpensePrefix = new Regex(ChatStart + @"(?<keyword>gastei|paguei|comprei|desembolsei|quitei)\b\s*", RegexOptions.IgnoreCase);
        private static readonly Regex TentativeChat = new Regex(ChatStart +
            @"(?:(?:n[aã]o|ainda\s+n[aã]o)\s+(?:gastei|paguei|comprei|recebi|ganhei|vendi|parcelei|quitei|assinei|renovei|fiz\s+(?:uma|a)\s+assinatura)|cancelei\s+(?:(?:a|minha)\s+)?assinatura|(?:vou|pretendo|preciso)\s+(?:gastar|pagar|comprar|receber|ganhar|vender|parcelar))\b", RegexOptions.IgnoreCase);
        private static readonly Regex CategoryWord = new Regex(@"[\p{L}\p{N}]+");
        internal static readonly string[] CategoryNames = {
            "Alimentação", "Transporte", "Moradia", "Saúde", "Educação", "Compras", "Lazer", "Sem categoria"
        };
        internal static readonly string[] IncomeCategories = {
            "Salário", "Trabalho extra", "Vendas", "Outros ganhos"
        };
        private static readonly string[] IncomeKeywords = {
            @"salário|salario|pagamento|holerite",
            @"freela|free\s*lance|bico|extra|comissão",
            @"venda|vendas"
        };
        private static readonly string[] CategoryKeywords = {
            @"almoço|jantar|café|lanche|padaria|mercado|supermercado|ifood|pizza|restaurante",
            @"uber|99|ônibus|metrô|gasolina|combustível|estacionamento|pedágio",
            @"aluguel|condomínio|luz|energia|água|internet",
            @"farmácia|remédio|consulta|dentista|médico",
            @"curso|escola|faculdade|livro",
            @"roupa|sapato|presente",
            @"cinema|show|teatro|netflix|spotify|discord"
        };

        public long Id { get; set; }
        public DateTime Date { get; set; }
        public string Description { get; set; }
        public string Category { get; set; }
        public long Cents { get; set; }
        public bool IsIncome { get; set; }
        internal int Kind { get; set; }
        internal string Version { get; set; }
        public string TypeDisplay { get { return Kind == 1 ? "Ajuste de saldo" : Kind == 2 ? "Estorno" : IsIncome ? "Ganho" : "Gasto"; } }
        public string SignedAmount { get { return (IsIncome ? "+ " : "− ") + Money(Cents); } }
        public string DateDisplay { get { return Date.ToString("dd/MM/yyyy", Brazilian); } }
        public string AmountDisplay { get { return Money(Cents); } }

        internal static string Money(decimal cents)
        {
            return (cents / 100m).ToString("C", Brazilian);
        }

        internal static bool TryAmount(string text, out long cents)
        {
            cents = 0;
            decimal amount;
            string value = text.Trim();
            if (!Regex.IsMatch(value, "^" + AmountPattern + "$")) return false;
            if (Regex.IsMatch(value, @"^[0-9]+\.[0-9]{1,2}$")) value = value.Replace('.', ',');
            if (
                !decimal.TryParse(value, NumberStyles.Number, Brazilian, out amount) ||
                amount <= 0m || amount > 999999999.99m)
                return false;
            cents = (long)(amount * 100m);
            return true;
        }

        internal static bool IsTentativeChat(string text)
        {
            return TentativeChat.IsMatch(text) || text.TrimEnd().EndsWith("?", StringComparison.Ordinal);
        }

        internal static LedgerEntry FromMessage(string text, DateTime date, IEnumerable<string> custom = null, bool amountAtEnd = false)
        {
            if (IsTentativeChat(text)) return null;
            Match prefix = IncomePrefix.Match(text);
            Match verb = prefix.Success ? prefix : ExpensePrefix.Match(text);
            string body = text.Substring(verb.Length).Trim();
            Match match = amountAtEnd ? ChatEndingAmount.Match(body) : ChatPattern.Match(body);
            if (!amountAtEnd && !match.Success) match = ChatEndingAmount.Match(body);
            long cents;
            if (!match.Success || !TryAmount(match.Groups["amount"].Value, out cents))
                return null;
            string description = match.Groups["description"].Value.Trim();
            description = Regex.Replace(description, @"^(?:em|no|na|com|de)\s+", "", RegexOptions.IgnoreCase);
            description = Regex.Replace(description, @"^(?:um|uma)\s+", "", RegexOptions.IgnoreCase);
            string[] names = prefix.Success ? IncomeCategories : CategoryNames;
            string[] keywords = prefix.Success ? IncomeKeywords : CategoryKeywords;
            string category = names[names.Length - 1];
            string normalized = NormalizeName(description);
            string named = prefix.Success ? null : MatchExpenseCategory(normalized, (custom ?? Enumerable.Empty<string>()).Concat(names));
            if (named != null) category = named;
            for (int index = 0; named == null && index < keywords.Length; index++)
            {
                if (Regex.IsMatch(normalized, @"\b(?:" + NormalizeName(keywords[index]) + @")\b"))
                {
                    category = names[index];
                    break;
                }
            }
            if (prefix.Success && prefix.Groups["keyword"].Value.Equals("vendi", StringComparison.OrdinalIgnoreCase) &&
                category == IncomeCategories.Last()) category = "Vendas";
            return new LedgerEntry {
                Date = date.Date,
                Description = description.Length == 0 ? (prefix.Success ? "Ganho sem descrição" : "Gasto sem descrição") : description,
                Category = category,
                Cents = cents,
                IsIncome = prefix.Success
            };
        }

        private static string MatchExpenseCategory(string normalized, IEnumerable<string> categories)
        {
            string[] words = CategoryWords(normalized);
            if (words.Length == 0) return null;
            string phrase = " " + string.Join(" ", words) + " ";
            var names = categories.Distinct().Select(category => new {
                Name = category, Words = CategoryWords(NormalizeName(category))
            }).Where(item => item.Words.Length > 0).ToArray();

            var exact = names.Where(item => phrase.Contains(" " + string.Join(" ", item.Words) + " "))
                .OrderByDescending(item => item.Words.Sum(word => word.Length)).FirstOrDefault();
            bool generic = exact != null && exact.Words.Length == 1 && CategoryNames.Contains(exact.Name);
            if (exact != null && !generic) return exact.Name;

            string[] meaningful = WithoutCategoryConnectors(words);
            string closest = null;
            int best = 3;
            foreach (var item in names)
            {
                bool custom = !CategoryNames.Contains(item.Name);
                if (item.Name == "Sem categoria" || (generic && (!custom || item.Words.Length < 2))) continue;
                string[] title = WithoutCategoryConnectors(item.Words);
                if (title.Length == 0 || (generic && title.Length < 2)) continue;
                int distance = Math.Min(CategoryDistance(words, item.Words), CategoryDistance(meaningful, title));
                if (generic && distance > 1) continue;
                if (distance < best)
                {
                    best = distance;
                    closest = item.Name;
                }
                else if (distance == best)
                    closest = null;
            }
            return closest ?? (exact == null ? null : exact.Name);
        }

        private static string[] WithoutCategoryConnectors(string[] words)
        {
            return words.Where(word => word != "de" && word != "da" && word != "do" &&
                word != "das" && word != "dos").ToArray();
        }

        private static string[] CategoryWords(string normalized)
        {
            return CategoryWord.Matches(normalized).Cast<Match>().Select(match => match.Value).ToArray();
        }

        private static int CategoryDistance(string[] words, string[] category)
        {
            int best = 3;
            if (category.Length == 1 && category[0].Length < (words.Length == 1 ? 5 : 6)) return best;
            int allowed = category.Length > 1 && category.Sum(word => word.Length) >= 10 ? 2 : 1;
            for (int start = 0; start <= words.Length - category.Length; start++)
            {
                int distance = 0;
                for (int i = 0; i < category.Length && distance <= allowed; i++)
                {
                    string expected = category[i];
                    string written = words[start + i];
                    if (expected == written) continue;
                    // Short words must match exactly. Otherwise allow one nearby typo per word.
                    if (expected.Length < 5 || !IsOneEditApart(expected, written))
                    {
                        distance = allowed + 1;
                        break;
                    }
                    distance++;
                }
                if (distance <= allowed) best = Math.Min(best, distance);
            }
            return best;
        }

        private static bool IsOneEditApart(string first, string second)
        {
            if (Math.Abs(first.Length - second.Length) > 1) return false;
            if (first.Length > second.Length)
            {
                string shorter = second;
                second = first;
                first = shorter;
            }
            int index = 0;
            while (index < first.Length && first[index] == second[index]) index++;
            if (first.Length < second.Length)
                return string.CompareOrdinal(first, index, second, index + 1, first.Length - index) == 0;
            if (index == first.Length) return false;
            if (string.CompareOrdinal(first, index + 1, second, index + 1, first.Length - index - 1) == 0)
                return true;
            return index + 1 < first.Length && first[index] == second[index + 1] && first[index + 1] == second[index] &&
                string.CompareOrdinal(first, index + 2, second, index + 2, first.Length - index - 2) == 0;
        }

        internal static string NormalizeName(string value)
        {
            return new string(value.Normalize(NormalizationForm.FormD)
                .Where(ch => CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark)
                .ToArray()).ToLowerInvariant();
        }

        internal static bool HasAmbiguousAmount(string text)
        {
            string body = text.Trim();
            Match first = ChatPattern.Match(body);
            Match last = ChatEndingAmount.Match(body);
            return first.Success && last.Success && first.Groups["description"].Length > 0 &&
                first.Groups["amount"].Value != last.Groups["amount"].Value;
        }
    }

    internal sealed partial class LedgerStore : IDisposable
    {
        private const int Row = 100;
        private const int Done = 101;
        private const int ReadOnly = 1;
        private const int ReadWriteCreate = 6;
        private readonly string filename;
        private IntPtr database;

        internal LedgerStore(string filename) : this(filename, ReadWriteCreate, true)
        {
        }

        private LedgerStore(string filename, int flags, bool initialize)
        {
            this.filename = filename;
            int status = sqlite3_open_v2(Utf8(filename), out database, flags, IntPtr.Zero);
            try
            {
                Check(status);
                Check(sqlite3_busy_timeout(database, 3000));
                Execute("PRAGMA trusted_schema = OFF");
                Execute("PRAGMA temp_store = MEMORY");
                Execute("PRAGMA foreign_keys = ON");
                if (initialize) EnsureSchema();
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        private void EnsureSchema()
        {
            Execute("BEGIN IMMEDIATE");
            try
            {
                Execute(@"CREATE TABLE IF NOT EXISTS expenses (
                id INTEGER PRIMARY KEY, date TEXT NOT NULL, description TEXT NOT NULL,
                category TEXT NOT NULL, cents INTEGER NOT NULL CHECK(cents > 0))");
                Execute("CREATE INDEX IF NOT EXISTS expenses_date ON expenses(date)");
                Execute(@"CREATE TABLE IF NOT EXISTS incomes (
                id INTEGER PRIMARY KEY, date TEXT NOT NULL, description TEXT NOT NULL,
                category TEXT NOT NULL, cents INTEGER NOT NULL CHECK(cents > 0))");
                Execute("CREATE INDEX IF NOT EXISTS incomes_date ON incomes(date)");
                EnsureBillingSchema();
                EnsureReserveSchema();
                EnsureGoalsSchema();
                EnsureClosureSchema();
                EnsureBudgetSchema();
                EnsureOpeningSchema();
                foreach (string table in new[] { "expenses", "incomes" })
                {
                    if (!HasColumn(table, "version"))
                        Execute("ALTER TABLE " + table + " ADD COLUMN version TEXT NOT NULL DEFAULT ''");
                    Execute("UPDATE " + table + " SET version = lower(hex(randomblob(16))) WHERE version = ''");
                }
                Execute("COMMIT");
            }
            catch
            {
                Execute("ROLLBACK");
                throw;
            }
        }

        internal void SaveBackup(string destination)
        {
            string path = CheckBackupPath(destination);
            string staging = Path.Combine(Path.GetDirectoryName(path), ".spentz-" + Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                using (LedgerStore snapshot = new LedgerStore(staging, ReadWriteCreate, false))
                    CopyInto(snapshot);

                if (File.Exists(path)) File.Replace(staging, path, null);
                else File.Move(staging, path);
            }
            finally
            {
                if (File.Exists(staging)) File.Delete(staging);
            }
        }

        internal void ResetRecords()
        {
            Execute("BEGIN IMMEDIATE");
            try
            {
                Execute("PRAGMA secure_delete = ON");
                Execute("DELETE FROM bill_payments");
                Execute("DELETE FROM income_receipts");
                Execute("DELETE FROM bill_waivers");
                Execute("DELETE FROM bill_plans");
                Execute("DELETE FROM goal_purchases");
                Execute("DELETE FROM expenses");
                Execute("DELETE FROM incomes");
                Execute("DELETE FROM goal_funds");
                Execute("DELETE FROM savings_goals");
                Execute("DELETE FROM reserve_moves");
                Execute("DELETE FROM reserve_places");
                Execute("DELETE FROM month_closures");
                Execute("DELETE FROM category_limits");
                Execute("DELETE FROM custom_categories");
                Execute("DELETE FROM opening_balance");
                Execute("DELETE FROM preferences WHERE name = 'opening-prompt-skipped'");
                Execute("COMMIT");
            }
            catch
            {
                Execute("ROLLBACK");
                throw;
            }
        }

        internal string RestoreBackup(string source, string password = null, string recoveryPassword = null)
        {
            using (LedgerStore snapshot = OpenValidatedSnapshot(source, password))
                return RestoreSnapshot(snapshot, recoveryPassword);
        }

        internal string RestoreSnapshot(LedgerStore snapshot, string recoveryPassword)
        {
            string recoveryFolder = Path.Combine(Path.GetDirectoryName(filename), "Backups");
            Directory.CreateDirectory(recoveryFolder);
            string recovery = Path.Combine(recoveryFolder, "antes-da-restauracao-" +
                DateTime.Now.ToString("yyyy-MM-dd-HHmmss-fffffff", CultureInfo.InvariantCulture) +
                (recoveryPassword == null ? ".sqlite3" : ".spentz"));
            if (recoveryPassword == null) SaveBackup(recovery);
            else SaveProtectedBackup(recovery, recoveryPassword);
            snapshot.CopyInto(this);
            return recovery;
        }

        internal string DescribeBackup(string source, string password = null)
        {
            using (LedgerStore incoming = OpenValidatedSnapshot(source, password))
                return incoming.DescribeSnapshot();
        }

        internal string DescribeSnapshot()
        {
            using (Statement entries = Prepare(@"SELECT COUNT(*), MIN(date), MAX(date)
                FROM (SELECT date FROM expenses UNION ALL SELECT date FROM incomes)", new object[0]))
            {
                if (entries.Step() != Row || entries.Integer(0) == 0)
                    return "Este backup tem 0 lançamentos de entrada ou despesa. Ele ainda pode conter contas futuras e reservas.";
                DateTime first = ReadOptionalDate(entries.Text(1)).Value;
                DateTime last = ReadOptionalDate(entries.Text(2)).Value;
                return entries.Integer(0) + " lançamentos entre " + first.ToString("dd/MM/yyyy", LedgerEntry.Brazilian) +
                    " e " + last.ToString("dd/MM/yyyy", LedgerEntry.Brazilian) + ".";
            }
        }

        private string CheckBackupPath(string path)
        {
            string fullPath = Path.GetFullPath(path);
            if (string.Equals(fullPath, Path.GetFullPath(filename), StringComparison.OrdinalIgnoreCase))
                throw new IOException("Escolha um arquivo de backup separado do banco usado pelo app.");
            return fullPath;
        }

        private void CopyInto(LedgerStore destination)
        {
            byte[] main = Utf8("main");
            IntPtr operation = sqlite3_backup_init(destination.database, main, database, main);
            if (operation == IntPtr.Zero)
                throw new IOException("Não foi possível iniciar a cópia do banco. " + ReadUtf8(sqlite3_errmsg(destination.database)));

            int result;
            int finished;
            try { result = sqlite3_backup_step(operation, -1); }
            finally { finished = sqlite3_backup_finish(operation); }
            if (result != Done || finished != 0)
                throw new IOException("Não foi possível concluir a cópia do banco. Código SQLite " + (result != Done ? result : finished) + ".");
        }

        private void ValidateBackup()
        {
            using (Statement objects = Prepare(@"SELECT 1 FROM sqlite_master WHERE type IN ('view', 'trigger')
                OR (type = 'table' AND UPPER(TRIM(sql)) LIKE 'CREATE VIRTUAL TABLE%') LIMIT 1", new object[0]))
                if (objects.Step() == Row) throw new IOException("O backup contém uma estrutura incompatível com o SpentZ.");
            using (Statement check = Prepare("PRAGMA quick_check(1)", new object[0]))
            {
                if (check.Step() != Row || check.Text(0) != "ok")
                    throw new IOException("O arquivo de backup está danificado.");
            }
            HashSet<string> tables = new HashSet<string>();
            using (Statement schema = Prepare("SELECT name FROM sqlite_master WHERE type = 'table'", new object[0]))
            {
                HashSet<string> supported = new HashSet<string> { "expenses", "incomes", "bill_plans", "bill_payments",
                    "income_receipts", "preferences", "month_closures", "reserve_places", "reserve_moves",
                    "custom_categories", "category_limits", "bill_waivers", "opening_balance", "savings_goals", "goal_funds", "goal_purchases" };
                while (schema.Step() == Row) tables.Add(schema.Text(0));
                if (!tables.Contains("expenses") || !tables.IsSubsetOf(supported) ||
                    tables.Contains("bill_plans") != tables.Contains("bill_payments") ||
                    tables.Contains("reserve_places") != tables.Contains("reserve_moves") ||
                    tables.Contains("savings_goals") != tables.Contains("goal_funds"))
                    throw new IOException("O arquivo selecionado não é um backup do SpentZ.");
            }
            ValidateEntries("expenses");
            if (tables.Contains("incomes")) ValidateEntries("incomes");
            if (tables.Contains("preferences")) GetPreference("dark");
        }

        private void ValidatePlannedData()
        {
            try { Bills(); Closures(); GetOpeningBalance(); }
            catch (FormatException) { throw new IOException("O backup contém datas inválidas."); }
            ValidateReserveData();
            using (Statement references = Prepare("PRAGMA foreign_key_check", new object[0]))
                if (references.Step() != Done)
                    throw new IOException("O backup contém vínculos com registros ausentes.");
            using (Statement purchase = Prepare(@"SELECT 1 FROM goal_purchases p
                JOIN expenses e ON e.id = p.expense_id JOIN reserve_moves m ON m.id = p.move_id
                WHERE e.cents <> m.cents OR e.date <> m.date OR m.direction <> -1 OR m.initial_reserve <> 0 LIMIT 1", new object[0]))
                if (purchase.Step() == Row)
                    throw new IOException("O backup contém uma compra de meta que não corresponde à movimentação do Cofrinho.");
        }

        private void ValidateEntries(string table)
        {
            using (Statement rows = Prepare("SELECT id, date, description, category, cents FROM " + table, new object[0]))
            {
                while (rows.Step() == Row)
                {
                    DateTime date;
                    if (sqlite3_column_int64(rows.Handle, 0) <= 0 || sqlite3_column_int64(rows.Handle, 4) <= 0 ||
                        !DateTime.TryParseExact(rows.Text(1), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out date) ||
                        date.Year < 1900 || date.Year > 9998 || rows.Text(2).Length == 0 || rows.Text(3).Length == 0)
                        throw new IOException("O backup contém um lançamento inválido.");
                }
            }
        }

        internal void Save(LedgerEntry expense)
        {
            string table = expense.IsIncome ? "incomes" : "expenses";
            string version = Guid.NewGuid().ToString("N");
            if (expense.Id == 0)
            {
                Execute("INSERT INTO " + table + "(date, description, category, cents, entry_kind, version) VALUES (?, ?, ?, ?, ?, ?)",
                    IsoDate(expense.Date), expense.Description, expense.Category, expense.Cents, (long)expense.Kind, version);
                expense.Id = sqlite3_last_insert_rowid(database);
                expense.Version = version;
                return;
            }
            if (string.IsNullOrEmpty(expense.Version)) throw new IOException("O lançamento mudou. Abra-o novamente para editar.");
            string date = IsoDate(expense.Date);
            if (expense.IsIncome)
            {
                Execute(@"UPDATE incomes SET date = ?, description = ?, category = ?, cents = ?, version = ?
                    WHERE id = ? AND version = ? AND entry_kind <> 2",
                    date, expense.Description, expense.Category, expense.Cents, version, expense.Id, expense.Version);
                if (sqlite3_changes(database) == 0) throw new IOException("O lançamento mudou. Abra-o novamente para editar.");
                expense.Version = version;
                return;
            }
            Execute("BEGIN IMMEDIATE");
            try
            {
                Execute(@"UPDATE expenses SET date = ?, description = ?, category = ?, cents = ?, version = ? WHERE id = ? AND version = ?
                    AND (NOT EXISTS(SELECT 1 FROM goal_purchases WHERE expense_id = expenses.id) OR (date = ? AND cents = ?))
                    AND ? >= COALESCE((SELECT SUM(cents) FROM incomes WHERE refund_of = expenses.id), 0)
                    AND NOT EXISTS(SELECT 1 FROM incomes WHERE refund_of = expenses.id AND date < ?)",
                    date, expense.Description, expense.Category, expense.Cents, version, expense.Id, expense.Version,
                    date, expense.Cents, expense.Cents, date);
                if (sqlite3_changes(database) == 0)
                    throw new IOException(IsGoalPurchase(expense.Id, true) ? "Desfaça a compra na página Metas para corrigir a data ou o valor." :
                        "O gasto mudou ou a correção entra em conflito com um estorno registrado.");
                Execute("UPDATE incomes SET description = ?, category = ?, version = lower(hex(randomblob(16))) WHERE refund_of = ?",
                    "Estorno · " + expense.Description, expense.Category, expense.Id);
                Execute("COMMIT");
                expense.Version = version;
            }
            catch
            {
                Execute("ROLLBACK");
                throw;
            }
        }

        internal bool Delete(LedgerEntry entry)
        {
            string table = entry.IsIncome ? "incomes" : "expenses";
            string linked = entry.IsIncome ? "" : @" AND NOT EXISTS(SELECT 1 FROM incomes WHERE refund_of = expenses.id)
                AND NOT EXISTS(SELECT 1 FROM goal_purchases WHERE expense_id = expenses.id)";
            Execute("DELETE FROM " + table + " WHERE id = ? AND version = ?" + linked, entry.Id, entry.Version ?? "");
            if (sqlite3_changes(database) != 0) return true;
            if (!entry.IsIncome && HasRefunds(entry))
                throw new IOException("Este gasto tem estornos. Remova os estornos vinculados antes de excluir o gasto.");
            if (!entry.IsIncome && IsGoalPurchase(entry.Id, true))
                throw new IOException("Desfaça a compra na página Metas para remover este gasto.");
            return false;
        }

        internal decimal LedgerTotal(DateTime start, DateTime end, bool income = false)
        {
            using (Statement query = Prepare("SELECT COALESCE(SUM(cents), 0) FROM " + (income ? "incomes" : "expenses") +
                " WHERE date >= ? AND date < ?", new object[] { IsoDate(start), IsoDate(end) }))
                return query.Step() == Row ? query.Integer(0) : 0;
        }

        internal decimal AvailableBalance(DateTime date, DateTime? through = null)
        {
            DateTime start = new DateTime(date.Year, date.Month, 1);
            DateTime end = through.HasValue ? through.Value.Date.AddDays(1) : start.AddMonths(1);
            OpeningBalance opening = GetOpeningBalance();
            if (opening != null && opening.Date < end)
                return AvailableSinceOpening(opening, end);
            return BalanceForPeriod(start, end);
        }

        internal decimal BalanceForPeriod(DateTime start, DateTime end)
        {
            using (Statement query = Prepare(@"SELECT COALESCE(SUM(cents * direction), 0) FROM reserve_moves
                WHERE date >= ? AND date < ? AND initial_reserve = 0", new object[] { IsoDate(start), IsoDate(end) }))
                return LedgerTotal(start, end, true) - LedgerTotal(start, end) - (query.Step() == Row ? query.Integer(0) : 0);
        }

        internal List<LedgerEntry> Month(DateTime date, bool income = false)
        {
            DateTime start = new DateTime(date.Year, date.Month, 1);
            string source = income ? "1 FROM incomes" : "0 FROM expenses";
            return Read("SELECT id, date, description, category, cents, entry_kind, version, " + source + " WHERE date >= ? AND date < ? ORDER BY date DESC, id DESC",
                IsoDate(start), IsoDate(start.AddMonths(1)));
        }

        internal List<LedgerEntry> Recent(DateTime? since = null, DateTime? until = null)
        {
            string from = IsoDate(since ?? new DateTime(1900, 1, 1));
            string through = IsoDate(until ?? DateTime.MaxValue);
            return Read(@"SELECT id, date, description, category, cents, entry_kind, version, 0 FROM expenses WHERE date >= ? AND date <= ?
                UNION ALL SELECT id, date, description, category, cents, entry_kind, version, 1 FROM incomes WHERE date >= ? AND date <= ?
                ORDER BY date DESC, id DESC LIMIT 8", from, through, from, through);
        }

        private List<LedgerEntry> Read(string sql, params object[] args)
        {
            List<LedgerEntry> result = new List<LedgerEntry>();
            using (Statement query = Prepare(sql, args))
            {
                while (query.Step() == Row)
                {
                    result.Add(new LedgerEntry {
                        Id = sqlite3_column_int64(query.Handle, 0),
                        Date = DateTime.ParseExact(query.Text(1), "yyyy-MM-dd", CultureInfo.InvariantCulture),
                        Description = query.Text(2),
                        Category = query.Text(3),
                        Cents = sqlite3_column_int64(query.Handle, 4),
                        Kind = (int)query.Integer(5), Version = query.Text(6), IsIncome = query.Integer(7) != 0
                    });
                }
            }
            return result;
        }

        private static string IsoDate(DateTime value)
        {
            return value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        }

        private void Execute(string sql, params object[] args)
        {
            using (Statement statement = Prepare(sql, args))
                statement.Step();
        }

        private Statement Prepare(string sql, object[] args)
        {
            IntPtr handle;
            Check(sqlite3_prepare_v2(database, Utf8(sql), -1, out handle, IntPtr.Zero));
            Statement statement = new Statement(this, handle);
            try
            {
                for (int index = 0; index < args.Length; index++)
                {
                    object value = args[index];
                    string text = value as string;
                    if (value == null)
                        Check(sqlite3_bind_null(handle, index + 1));
                    else if (text == null)
                        Check(sqlite3_bind_int64(handle, index + 1, (long)value));
                    else
                        Check(sqlite3_bind_text(handle, index + 1, Utf8(text), -1, new IntPtr(-1)));
                }
                return statement;
            }
            catch
            {
                statement.Dispose();
                throw;
            }
        }

        private void Check(int status)
        {
            if (status != 0 && status != Row && status != Done)
                throw new IOException("SQLite (" + status + "). " + ReadUtf8(sqlite3_errmsg(database)));
        }

        private static byte[] Utf8(string value)
        {
            return Encoding.UTF8.GetBytes(value + "\0");
        }

        private static string ReadUtf8(IntPtr address)
        {
            if (address == IntPtr.Zero) return "";
            int length = 0;
            while (Marshal.ReadByte(address, length) != 0) length++;
            byte[] bytes = new byte[length];
            Marshal.Copy(address, bytes, 0, length);
            return Encoding.UTF8.GetString(bytes);
        }

        public void Dispose()
        {
            if (database == IntPtr.Zero) return;
            sqlite3_close(database);
            database = IntPtr.Zero;
        }

        private sealed class Statement : IDisposable
        {
            private readonly LedgerStore owner;
            internal readonly IntPtr Handle;
            internal Statement(LedgerStore owner, IntPtr handle) { this.owner = owner; Handle = handle; }
            internal int Step() { int status = sqlite3_step(Handle); owner.Check(status); return status; }
            internal long Integer(int column) { return sqlite3_column_int64(Handle, column); }
            internal string Text(int column) { return ReadUtf8(sqlite3_column_text(Handle, column)); }
            public void Dispose() { sqlite3_finalize(Handle); }
        }

        private const string Library = "winsqlite3.dll";
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_open_v2(byte[] filename, out IntPtr db, int flags, IntPtr vfs);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_close(IntPtr db);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern IntPtr sqlite3_errmsg(IntPtr db);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_busy_timeout(IntPtr db, int milliseconds);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_prepare_v2(IntPtr db, byte[] sql, int length, out IntPtr statement, IntPtr tail);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_bind_text(IntPtr statement, int index, byte[] value, int length, IntPtr destructor);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_bind_int64(IntPtr statement, int index, long value);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_bind_null(IntPtr statement, int index);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_column_type(IntPtr statement, int column);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_column_bytes(IntPtr statement, int column);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_step(IntPtr statement);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_finalize(IntPtr statement);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern long sqlite3_column_int64(IntPtr statement, int column);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern IntPtr sqlite3_column_text(IntPtr statement, int column);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern long sqlite3_last_insert_rowid(IntPtr db);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_changes(IntPtr db);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern IntPtr sqlite3_backup_init(IntPtr destination, byte[] destinationName, IntPtr source, byte[] sourceName);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_backup_step(IntPtr backup, int pages);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_backup_finish(IntPtr backup);
    }
}
