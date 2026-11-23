using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace SpentZ
{
    internal sealed class CategoryProgress
    {
        internal string Name { get; set; }
        internal long Limit { get; set; }
        internal decimal Used { get; set; }
        internal decimal Remaining { get { return Limit - Used; } }
        internal string Display
        {
            get
            {
                return Name + " · " + LedgerEntry.Money(Used) + " de " + LedgerEntry.Money(Limit) + " · " +
                    (Remaining >= 0 ? "Restam " + LedgerEntry.Money(Remaining) : LedgerEntry.Money(-Remaining) + " acima do limite");
            }
        }
    }

    internal sealed partial class LedgerStore
    {
        private void EnsureBudgetSchema()
        {
            foreach (string table in new[] { "expenses", "incomes" })
                if (!HasColumn(table, "entry_kind"))
                    Execute("ALTER TABLE " + table + " ADD COLUMN entry_kind INTEGER NOT NULL DEFAULT 0 CHECK(entry_kind BETWEEN 0 AND 2)");
            if (!HasColumn("incomes", "refund_of"))
                Execute("ALTER TABLE incomes ADD COLUMN refund_of INTEGER REFERENCES expenses(id) ON DELETE SET NULL");
            Execute("CREATE INDEX IF NOT EXISTS incomes_refund ON incomes(refund_of)");
            Execute(@"CREATE TABLE IF NOT EXISTS custom_categories (
                name_key TEXT PRIMARY KEY, name TEXT NOT NULL)");
            Execute(@"CREATE TABLE IF NOT EXISTS category_limits (
                category TEXT NOT NULL, since TEXT NOT NULL, cents INTEGER NOT NULL CHECK(cents >= 0),
                PRIMARY KEY(category, since))");
        }

        private bool HasColumn(string table, string column)
        {
            using (Statement query = Prepare("PRAGMA table_info(" + table + ")", new object[0]))
                while (query.Step() == Row)
                    if (query.Text(1) == column) return true;
            return false;
        }

        internal string[] ExpenseCategories()
        {
            List<string> result = new List<string>(LedgerEntry.CategoryNames);
            using (Statement query = Prepare("SELECT name FROM custom_categories ORDER BY name_key", new object[0]))
                while (query.Step() == Row) result.Add(query.Text(0));
            return result.ToArray();
        }

        internal string AddExpenseCategory(string input)
        {
            string name = input.Trim();
            if (name.Length == 0 || name.Length > 40 || !name.Any(char.IsLetter) || name.Any(char.IsControl))
                throw new IOException("Informe um nome de categoria de até 40 caracteres.");
            string key = LedgerEntry.NormalizeName(name);
            if (key == "ajuste de saldo") throw new IOException("Esse nome é reservado para os ajustes do SpentZ.");
            string existing = ExpenseCategories().FirstOrDefault(item => LedgerEntry.NormalizeName(item) == key);
            if (existing != null) return existing;
            Execute("INSERT OR IGNORE INTO custom_categories(name_key, name) VALUES (?, ?)", key, name);
            using (Statement query = Prepare("SELECT name FROM custom_categories WHERE name_key = ?", new object[] { key }))
                return query.Step() == Row ? query.Text(0) : name;
        }

        internal void SetCategoryLimit(string category, DateTime month, long cents)
        {
            Execute("INSERT OR REPLACE INTO category_limits(category, since, cents) VALUES (?, ?, ?)",
                category, IsoDate(new DateTime(month.Year, month.Month, 1)), cents);
        }

        internal Dictionary<string, long> CategoryLimits(DateTime month)
        {
            Dictionary<string, long> result = new Dictionary<string, long>();
            using (Statement query = Prepare(@"SELECT limits.category, limits.cents FROM category_limits limits
                WHERE limits.since = (SELECT MAX(history.since) FROM category_limits history
                    WHERE history.category = limits.category AND history.since <= ?) AND limits.cents > 0
                ORDER BY limits.category", new object[] { IsoDate(new DateTime(month.Year, month.Month, 1)) }))
                while (query.Step() == Row) result.Add(query.Text(0), query.Integer(1));
            return result;
        }

        internal decimal AdjustAvailable(DateTime date, long target)
        {
            Execute("BEGIN IMMEDIATE");
            try
            {
                decimal current = AvailableBalance(date);
                decimal delta = target - current;
                if (Math.Abs(delta) > long.MaxValue) throw new IOException("O valor do ajuste excede o intervalo aceito.");
                if (delta != 0)
                    Save(new LedgerEntry {
                        Date = date, Cents = (long)Math.Abs(delta), Kind = 1, IsIncome = delta > 0,
                        Description = delta > 0 ? "Ajuste de saldo · entrada sem origem" : "Ajuste de saldo · gasto não informado",
                        Category = "Ajuste de saldo"
                    });
                Execute("COMMIT");
                return delta;
            }
            catch
            {
                Execute("ROLLBACK");
                throw;
            }
        }

        internal Dictionary<string, long> RefundsForCategoryLimits(DateTime month)
        {
            DateTime first = new DateTime(month.Year, month.Month, 1);
            string start = IsoDate(first);
            string end = IsoDate(first.AddMonths(1));
            var result = new Dictionary<string, long>();
            using (Statement query = Prepare(@"SELECT expense.category, SUM(refund.cents)
                FROM incomes refund JOIN expenses expense ON expense.id = refund.refund_of
                WHERE refund.date >= ? AND refund.date < ? AND expense.date >= ? AND expense.date < ?
                GROUP BY expense.category", new object[] { start, end, start, end }))
                while (query.Step() == Row) result.Add(query.Text(0), query.Integer(1));
            return result;
        }

        internal List<CategoryProgress> CategoryProgressFor(DateTime month, IEnumerable<LedgerEntry> expenses = null)
        {
            Dictionary<string, long> limits = CategoryLimits(month);
            if (limits.Count == 0) return new List<CategoryProgress>();
            var byCategory = (expenses ?? Month(month)).Where(entry => entry.Kind == 0).ToLookup(entry => entry.Category);
            Dictionary<string, long> refunds = RefundsForCategoryLimits(month);
            List<CategoryProgress> result = new List<CategoryProgress>();
            foreach (var limit in limits)
            {
                long refunded;
                refunds.TryGetValue(limit.Key, out refunded);
                result.Add(new CategoryProgress { Name = limit.Key, Limit = limit.Value,
                    Used = Math.Max(0, byCategory[limit.Key].Sum(entry => (decimal)entry.Cents) - refunded) });
            }
            return result;
        }

        internal string AdjustmentSummary(DateTime month)
        {
            DateTime first = new DateTime(month.Year, month.Month, 1);
            string start = IsoDate(first);
            string end = IsoDate(first.AddMonths(1));
            using (Statement query = Prepare(@"SELECT
                (SELECT COALESCE(SUM(cents), 0) FROM incomes WHERE entry_kind = 1 AND date >= ? AND date < ?),
                (SELECT COALESCE(SUM(cents), 0) FROM expenses WHERE entry_kind = 1 AND date >= ? AND date < ?)",
                new object[] { start, end, start, end }))
            {
                if (query.Step() != Row || (query.Integer(0) == 0 && query.Integer(1) == 0)) return "";
                return "Ajustes de saldo sem origem · Entradas " + LedgerEntry.Money(query.Integer(0)) +
                    "  ·  Saídas " + LedgerEntry.Money(query.Integer(1));
            }
        }

        internal void SaveRefund(LedgerEntry expense, DateTime date, long cents)
        {
            Execute("BEGIN IMMEDIATE");
            try
            {
                using (Statement query = Prepare(@"SELECT cents - COALESCE(
                    (SELECT SUM(cents) FROM incomes WHERE refund_of = expenses.id), 0)
                    FROM expenses WHERE id = ? AND version = ? AND entry_kind = 0",
                    new object[] { expense.Id, expense.Version ?? "" }))
                {
                    if (date < expense.Date || cents <= 0 || query.Step() != Row || cents > query.Integer(0))
                        throw new IOException("Confira a despesa original, a data e o valor que ainda pode ser estornado.");
                }
                Execute(@"INSERT INTO incomes(date, description, category, cents, entry_kind, refund_of, version)
                    VALUES (?, ?, ?, ?, 2, ?, lower(hex(randomblob(16))))",
                    IsoDate(date), "Estorno · " + expense.Description, expense.Category, cents, expense.Id);
                Execute("COMMIT");
            }
            catch
            {
                Execute("ROLLBACK");
                throw;
            }
        }

        internal bool HasRefunds(LedgerEntry expense)
        {
            using (Statement query = Prepare(@"SELECT 1 FROM incomes JOIN expenses ON expenses.id = incomes.refund_of
                WHERE expenses.id = ? AND expenses.version = ? LIMIT 1", new object[] { expense.Id, expense.Version ?? "" }))
                return query.Step() == Row;
        }

        internal decimal RemainingRefund(LedgerEntry expense)
        {
            using (Statement query = Prepare("SELECT COALESCE(SUM(cents), 0) FROM incomes WHERE refund_of = ?", new object[] { expense.Id }))
                return query.Step() == Row ? expense.Cents - (decimal)query.Integer(0) : expense.Cents;
        }
    }
}
