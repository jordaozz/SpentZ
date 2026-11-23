using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace SpentZ
{
    internal enum BillKind
    {
        Monthly,
        Annual,
        AnnualMonthly
    }

    internal sealed class BillPlan
    {
        internal const int MaxInstallments = 360;
        internal long Id { get; set; }
        internal string Description { get; set; }
        internal string Category { get; set; }
        internal long Cents { get; set; }
        internal DateTime FirstDue { get; set; }
        internal int Installments { get; set; }
        internal int FirstInstallment { get; set; }
        internal bool Archived { get; set; }
        internal bool IsIncome { get; set; }
        internal DateTime? StopBefore { get; set; }
        internal DateTime? CanceledOn { get; set; }
        internal DateTime? CreatedOn { get; set; }
        internal bool HasPayments { get; set; }
        internal BillKind Kind { get; set; }
        internal bool IsMonthlyBill { get; set; }
        internal bool WasRevised { get; set; }
        internal bool IsOneTime { get { return IsMonthlyBill && Installments == 1; } }
        internal int Interval { get { return Kind == BillKind.Annual ? 12 : 1; } }
        internal bool CanStop { get { return !IsMonthlyBill && (Installments == 0 || Kind != BillKind.Monthly); } }
        internal bool IsSubscription { get { return !IsIncome && CanStop; } }
        internal bool IsActive { get { return !Archived && !StopBefore.HasValue; } }
        internal int RemainingTerms { get { return Installments - FirstInstallment + 1; } }

        internal BillPlan()
        {
            FirstInstallment = 1;
        }

        internal int MonthOffset(DateTime date)
        {
            return (date.Year - FirstDue.Year) * 12 + date.Month - FirstDue.Month;
        }

        internal bool Includes(int offset)
        {
            return offset >= 0 && offset % Interval == 0 && (Installments == 0 || offset / Interval < RemainingTerms);
        }

        internal bool Schedules(int offset)
        {
            return Includes(offset) && (!StopBefore.HasValue || FirstDue.AddMonths(offset) < StopBefore.Value);
        }

        internal int CountThrough(DateTime date)
        {
            if (StopBefore.HasValue && date >= StopBefore.Value) date = StopBefore.Value.AddDays(-1);
            int offset = MonthOffset(date);
            if (offset < 0) return 0;
            int index = offset / Interval;
            int count = index + (FirstDue.AddMonths(index * Interval) <= date ? 1 : 0);
            return Installments == 0 ? count : Math.Min(count, RemainingTerms);
        }
    }

    internal sealed class BillPayment
    {
        internal long PlanId { get; set; }
        internal long EntryId { get; set; }
        internal bool InOpeningBalance { get { return EntryId == 0; } }
        internal DateTime Due { get; set; }
        internal DateTime RecordedOn { get; set; }
        internal long Cents { get; set; }
    }

    public sealed class BillOccurrence
    {
        internal BillPlan Plan { get; set; }
        internal int Offset { get; set; }
        internal BillPayment Payment { get; set; }
        internal DateTime Today { get; set; }
        internal bool CancellationRecord { get; set; }
        internal bool ShowRecordedDate { get; set; }
        internal bool PlanRecord { get; set; }
        internal bool Waived { get; set; }
        internal bool IsPending { get { return Payment == null && !Waived && !Plan.Archived && Plan.Schedules(Offset); } }
        public DateTime Due { get { return Plan.FirstDue.AddMonths(Offset); } }
        public DateTime ShownDate
        {
            get
            {
                if (CancellationRecord) return Plan.CanceledOn ?? DateTime.MinValue;
                return ShowRecordedDate && Payment != null ? Payment.RecordedOn : Due;
            }
        }
        public string DueDisplay { get { return ShownDate == DateTime.MinValue ? "—" : ShownDate.ToString("dd/MM/yyyy", LedgerEntry.Brazilian); } }
        public string Description { get { return Plan.Description; } }
        public string Series
        {
            get
            {
                if (Plan.Kind == BillKind.Annual)
                    return Plan.Installments == 0 ? "Anual" : (Offset / Plan.Interval + 1) + " de " + Plan.Installments;
                if (Plan.IsOneTime) return "Conta avulsa";
                if (Plan.IsMonthlyBill) return "Conta mensal";
                return Plan.Installments == 0 ? "Mensal" : (Offset + Plan.FirstInstallment) + " de " + Plan.Installments;
            }
        }
        public long Cents { get { return Payment == null ? Plan.Cents : Payment.Cents; } }
        public string AmountDisplay { get { return LedgerEntry.Money(Cents); } }
        public string Status
        {
            get
            {
                if (CancellationRecord) return Plan.Archived ? "Arquivada" : "Cancelada";
                if (PlanRecord) return Plan.IsSubscription ? "Ativa" : "Ativo";
                if (Payment != null && Payment.InOpeningBalance)
                    return Plan.IsIncome ? "Já recebido" : "Já paga";
                if (Waived) return Plan.IsIncome ? "Dispensado" : "Dispensada";
                if (Plan.IsIncome)
                {
                    if (Payment != null) return "Recebido";
                    if (Plan.Archived) return "Arquivado";
                    if (!Plan.Schedules(Offset)) return "Encerrado";
                    if (Due < Today) return "Em atraso";
                    return Due == Today ? "Previsto hoje" : "Previsto";
                }
                if (Payment != null) return "Paga";
                if (Plan.Archived) return "Arquivada";
                if (!Plan.Schedules(Offset)) return "Cancelada";
                if (Due < Today) return "Atrasada";
                return Due == Today ? "Vence hoje" : "Prevista";
            }
        }
    }

    internal sealed class BillCalendar
    {
        private readonly List<BillPlan> plans;
        private readonly ILookup<long, BillPayment> payments;
        private readonly Dictionary<long, BillPlan> byId;
        private readonly ILookup<long, DateTime> waivers;

        internal BillCalendar(List<BillPlan> plans, List<BillPayment> payments, ILookup<long, DateTime> waivers)
        {
            this.plans = plans;
            this.payments = payments.ToLookup(payment => payment.PlanId);
            byId = plans.ToDictionary(plan => plan.Id);
            this.waivers = waivers;
        }

        internal List<BillOccurrence> Month(DateTime month, DateTime today, bool income = false)
        {
            List<BillOccurrence> rows = new List<BillOccurrence>();
            foreach (BillPlan plan in plans)
            {
                if (plan.IsIncome != income) continue;
                int offset = plan.MonthOffset(month);
                if (!plan.Includes(offset)) continue;
                DateTime due = plan.FirstDue.AddMonths(offset);
                BillPayment payment = payments[plan.Id].FirstOrDefault(item => item.Due == due);
                if ((!plan.Archived && plan.Schedules(offset)) || payment != null)
                    rows.Add(new BillOccurrence { Plan = plan, Offset = offset, Today = today, Payment = payment,
                        Waived = waivers[plan.Id].Contains(due) });
            }
            return rows.OrderBy(row => row.Due).ThenBy(row => row.Description).ToList();
        }

        internal List<BillOccurrence> Pending(DateTime today, int days, bool income = false)
        {
            DateTime through = today.AddDays(days);
            List<BillOccurrence> rows = new List<BillOccurrence>();
            foreach (BillPlan plan in plans.Where(item => !item.Archived && item.IsIncome == income))
            {
                int count = plan.CountThrough(through);
                HashSet<DateTime> paid = new HashSet<DateTime>(payments[plan.Id].Select(item => item.Due));
                paid.UnionWith(waivers[plan.Id]);
                for (int index = 0; index < count; index++)
                {
                    int offset = index * plan.Interval;
                    DateTime due = plan.FirstDue.AddMonths(offset);
                    if (!paid.Contains(due))
                        rows.Add(new BillOccurrence { Plan = plan, Offset = offset, Today = today });
                }
            }
            return rows.OrderBy(row => row.Due).ThenBy(row => row.Description).ToList();
        }

        internal int PendingThrough(DateTime date, bool income = false)
        {
            return plans.Where(plan => !plan.Archived && plan.IsIncome == income).Sum(plan => CountPending(plan, date));
        }

        internal decimal PendingAmountThrough(DateTime date, bool income = false)
        {
            return plans.Where(plan => !plan.Archived && plan.IsIncome == income).Sum(plan => (decimal)plan.Cents * CountPending(plan, date));
        }

        private int CountPending(BillPlan plan, DateTime date)
        {
            return Math.Max(0, plan.CountThrough(date) - payments[plan.Id].Select(item => item.Due)
                .Concat(waivers[plan.Id]).Count(day => day <= date && (!plan.StopBefore.HasValue || day < plan.StopBefore.Value)));
        }

        internal DateTime? NextRenewal(BillPlan plan, DateTime today)
        {
            if (!plan.IsActive) return null;
            int offset = Math.Max(0, plan.MonthOffset(today) / plan.Interval) * plan.Interval;
            int last = plan.MonthOffset(new DateTime(9998, 12, 1));
            HashSet<DateTime> paid = new HashSet<DateTime>(payments[plan.Id].Select(item => item.Due));
            paid.UnionWith(waivers[plan.Id]);
            for (; offset <= last && plan.Includes(offset); offset += plan.Interval)
            {
                DateTime due = plan.FirstDue.AddMonths(offset);
                if (due >= today && !paid.Contains(due)) return due;
            }
            return null;
        }

        internal List<BillOccurrence> Schedule(BillPlan plan, DateTime today)
        {
            if (plan.Archived)
                return payments[plan.Id].OrderBy(item => item.Due).Select(item =>
                    new BillOccurrence { Plan = plan, Offset = plan.MonthOffset(item.Due), Payment = item, Today = today }).ToList();
            int start = plan.Installments > 0 || !plan.IsActive ? 0 : plan.CountThrough(today.AddDays(-1));
            int limit = plan.Installments > 0 ? plan.RemainingTerms : start + (plan.Interval == 12 ? 3 : 12);
            if (plan.StopBefore.HasValue) limit = Math.Max(plan.CountThrough(plan.StopBefore.Value), limit);
            int last = plan.MonthOffset(new DateTime(9998, 12, 1));
            var confirmed = payments[plan.Id].ToDictionary(item => item.Due);
            List<BillOccurrence> result = new List<BillOccurrence>();
            for (int index = start; index < limit && index * plan.Interval <= last; index++)
            {
                int offset = index * plan.Interval;
                BillPayment payment;
                confirmed.TryGetValue(plan.FirstDue.AddMonths(offset), out payment);
                if ((!plan.Archived && plan.Schedules(offset)) || payment != null)
                    result.Add(new BillOccurrence { Plan = plan, Offset = offset, Payment = payment, Today = today,
                        Waived = waivers[plan.Id].Contains(plan.FirstDue.AddMonths(offset)) });
            }
            return result;
        }

        internal List<BillOccurrence> Plans(DateTime today, bool income)
        {
            return plans.Where(plan => plan.IsActive && plan.IsIncome == income &&
                (plan.Installments == 0 || payments[plan.Id].Count() + waivers[plan.Id].Count() < plan.RemainingTerms)).Select(plan =>
            {
                int index = plan.CountThrough(today.AddDays(-1));
                int last = Math.Max(0, plan.MonthOffset(new DateTime(9998, 12, 1)) / plan.Interval);
                if (plan.Installments > 0) last = Math.Min(last, plan.RemainingTerms - 1);
                int offset = Math.Min(index, last) * plan.Interval;
                DateTime due = plan.FirstDue.AddMonths(offset);
                BillPayment payment = payments[plan.Id].FirstOrDefault(item => item.Due == due);
                return new BillOccurrence { Plan = plan, Offset = offset, Today = today, Payment = payment, PlanRecord = true };
            }).OrderBy(row => row.Due).ThenBy(row => row.Description).ToList();
        }

        internal List<BillOccurrence> Cancellations(DateTime today)
        {
            return plans.Where(plan => plan.IsSubscription && !plan.IsActive && !plan.WasRevised)
                .OrderByDescending(plan => plan.CanceledOn ?? plan.StopBefore ?? plan.FirstDue)
                .ThenBy(plan => plan.Description)
                .Select(plan => new BillOccurrence { Plan = plan, Today = today, CancellationRecord = true }).ToList();
        }

        internal List<BillOccurrence> ConfirmedInMonth(DateTime month, DateTime today, bool income = false)
        {
            DateTime start = new DateTime(month.Year, month.Month, 1);
            DateTime end = start.AddMonths(1);
            return payments.SelectMany(group => group).Where(item =>
                    byId[item.PlanId].IsIncome == income && item.RecordedOn >= start && item.RecordedOn < end)
                .OrderByDescending(item => item.RecordedOn)
                .Select(item => new BillOccurrence {
                    Plan = byId[item.PlanId], Offset = byId[item.PlanId].MonthOffset(item.Due),
                    Today = today, Payment = item, ShowRecordedDate = true
                }).ToList();
        }

        internal List<BillPlan> SubscriptionsForReport(DateTime month, DateTime today)
        {
            DateTime start = new DateTime(month.Year, month.Month, 1);
            DateTime end = start.AddMonths(1);
            DateTime through = end.AddDays(-1) < today ? end.AddDays(-1) : today;
            return plans.Where(plan =>
            {
                if (!plan.IsSubscription || plan.Archived || (plan.CreatedOn ?? plan.FirstDue) > through) return false;
                DateTime? canceled = plan.CanceledOn ?? plan.StopBefore;
                if (canceled.HasValue && (plan.WasRevised ? canceled.Value <= start : canceled.Value < start)) return false;
                if (plan.Installments > 0 && !canceled.HasValue)
                {
                    DateTime last = plan.FirstDue.AddMonths((plan.RemainingTerms - 1) * plan.Interval);
                    if (last < start) return false;
                }
                return true;
            }).OrderBy(plan => plan.Description).ToList();
        }
    }

    internal sealed partial class LedgerStore
    {
        private void EnsureBillingSchema()
        {
            Execute(@"CREATE TABLE IF NOT EXISTS bill_plans (
                id INTEGER PRIMARY KEY, description TEXT NOT NULL, category TEXT NOT NULL,
                cents INTEGER NOT NULL CHECK(cents > 0), first_due TEXT NOT NULL,
                installments INTEGER NOT NULL CHECK(installments BETWEEN 0 AND 360),
                first_installment INTEGER NOT NULL DEFAULT 1 CHECK(first_installment BETWEEN 1 AND 360),
                archived INTEGER NOT NULL DEFAULT 0 CHECK(archived IN (0, 1)),
                stop_before TEXT NOT NULL DEFAULT '',
                is_income INTEGER NOT NULL DEFAULT 0 CHECK(is_income IN (0, 1)),
                plan_kind INTEGER NOT NULL DEFAULT 0 CHECK(plan_kind IN (0, 1, 2)),
                created_on TEXT NOT NULL DEFAULT '',
                canceled_on TEXT NOT NULL DEFAULT '',
                monthly_bill INTEGER NOT NULL DEFAULT 0 CHECK(monthly_bill IN (0, 1)),
                CHECK(installments = 0 OR first_installment <= installments))");
            Execute(@"CREATE TABLE IF NOT EXISTS bill_payments (
                plan_id INTEGER NOT NULL REFERENCES bill_plans(id) ON DELETE CASCADE,
                due TEXT NOT NULL, expense_id INTEGER NOT NULL UNIQUE REFERENCES expenses(id) ON DELETE CASCADE,
                PRIMARY KEY(plan_id, due))");
            Execute(@"CREATE TABLE IF NOT EXISTS income_receipts (
                plan_id INTEGER NOT NULL REFERENCES bill_plans(id) ON DELETE CASCADE,
                due TEXT NOT NULL, income_id INTEGER NOT NULL UNIQUE REFERENCES incomes(id) ON DELETE CASCADE,
                PRIMARY KEY(plan_id, due))");
            Execute("CREATE TABLE IF NOT EXISTS preferences (name TEXT PRIMARY KEY, value TEXT NOT NULL)");
            bool hasStopDate = false;
            bool hasIncome = false;
            bool hasKind = false;
            bool hasCreated = false;
            bool hasCanceled = false;
            bool hasMonthlyBill = false;
            using (Statement fields = Prepare("PRAGMA table_info(bill_plans)", new object[0]))
                while (fields.Step() == Row)
                {
                    hasStopDate |= fields.Text(1) == "stop_before";
                    hasIncome |= fields.Text(1) == "is_income";
                    hasKind |= fields.Text(1) == "plan_kind";
                    hasCreated |= fields.Text(1) == "created_on";
                    hasCanceled |= fields.Text(1) == "canceled_on";
                    hasMonthlyBill |= fields.Text(1) == "monthly_bill";
                }
            if (!hasStopDate) Execute("ALTER TABLE bill_plans ADD COLUMN stop_before TEXT NOT NULL DEFAULT ''");
            if (!hasIncome) Execute("ALTER TABLE bill_plans ADD COLUMN is_income INTEGER NOT NULL DEFAULT 0 CHECK(is_income IN (0, 1))");
            if (!hasKind) Execute("ALTER TABLE bill_plans ADD COLUMN plan_kind INTEGER NOT NULL DEFAULT 0 CHECK(plan_kind IN (0, 1, 2))");
            if (!hasCreated) Execute("ALTER TABLE bill_plans ADD COLUMN created_on TEXT NOT NULL DEFAULT ''");
            if (!hasCanceled) Execute("ALTER TABLE bill_plans ADD COLUMN canceled_on TEXT NOT NULL DEFAULT ''");
            if (!hasMonthlyBill) Execute("ALTER TABLE bill_plans ADD COLUMN monthly_bill INTEGER NOT NULL DEFAULT 0 CHECK(monthly_bill IN (0, 1))");
            if (!HasColumn("bill_plans", "was_revised"))
                Execute("ALTER TABLE bill_plans ADD COLUMN was_revised INTEGER NOT NULL DEFAULT 0 CHECK(was_revised IN (0, 1))");
            Execute(@"CREATE TABLE IF NOT EXISTS bill_waivers (plan_id INTEGER NOT NULL REFERENCES bill_plans(id) ON DELETE CASCADE,
                due TEXT NOT NULL, PRIMARY KEY(plan_id, due))");
            if (!HasColumn("bill_waivers", "settled_on"))
                Execute("ALTER TABLE bill_waivers ADD COLUMN settled_on TEXT NOT NULL DEFAULT ''");
            if (!HasColumn("bill_waivers", "settled_cents"))
                Execute("ALTER TABLE bill_waivers ADD COLUMN settled_cents INTEGER NOT NULL DEFAULT 0 CHECK(settled_cents >= 0)");
            Execute("DELETE FROM bill_payments WHERE expense_id NOT IN (SELECT id FROM expenses)");
            Execute("DELETE FROM income_receipts WHERE income_id NOT IN (SELECT id FROM incomes)");
        }

        internal string GetPreference(string name)
        {
            using (Statement query = Prepare("SELECT value FROM preferences WHERE name = ?", new object[] { name }))
                return query.Step() == Row ? query.Text(0) : "";
        }

        internal void SetPreference(string name, string value)
        {
            Execute("INSERT OR REPLACE INTO preferences(name, value) VALUES (?, ?)", name, value);
        }

        private const string SelectPlans = @"SELECT id, description, category, cents, first_due, installments, first_installment, archived, stop_before,
            is_income, plan_kind, created_on, canceled_on, monthly_bill, was_revised, EXISTS(SELECT 1 FROM bill_payments WHERE plan_id = bill_plans.id)
            OR EXISTS(SELECT 1 FROM income_receipts WHERE plan_id = bill_plans.id)
            OR EXISTS(SELECT 1 FROM bill_waivers WHERE plan_id = bill_plans.id AND settled_on <> '') FROM bill_plans";

        private static BillPlan ReadPlan(Statement query)
        {
            return new BillPlan {
                Id = query.Integer(0), Description = query.Text(1), Category = query.Text(2),
                Cents = query.Integer(3), FirstDue = DateTime.ParseExact(query.Text(4), "yyyy-MM-dd", CultureInfo.InvariantCulture),
                Installments = (int)query.Integer(5), FirstInstallment = (int)query.Integer(6),
                Archived = query.Integer(7) != 0,
                StopBefore = ReadOptionalDate(query.Text(8)),
                IsIncome = query.Integer(9) != 0, Kind = (BillKind)query.Integer(10),
                CreatedOn = ReadOptionalDate(query.Text(11)), CanceledOn = ReadOptionalDate(query.Text(12)),
                IsMonthlyBill = query.Integer(13) != 0, WasRevised = query.Integer(14) != 0, HasPayments = query.Integer(15) != 0
            };
        }

        private static DateTime? ReadOptionalDate(string value)
        {
            return value.Length == 0 ? (DateTime?)null : DateTime.ParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture);
        }

        internal BillCalendar Bills()
        {
            List<BillPlan> plans = new List<BillPlan>();
            List<BillPayment> payments = new List<BillPayment>();
            using (Statement query = Prepare(SelectPlans, new object[0]))
                while (query.Step() == Row) plans.Add(ReadPlan(query));
            using (Statement query = Prepare(
                @"SELECT plan_id, due, cents, expense_id, date FROM bill_payments JOIN expenses ON expenses.id = expense_id
                  UNION ALL SELECT plan_id, due, cents, income_id, date FROM income_receipts JOIN incomes ON incomes.id = income_id
                  UNION ALL SELECT plan_id, due, settled_cents, 0, settled_on FROM bill_waivers WHERE settled_on <> ''", new object[0]))
            {
                while (query.Step() == Row)
                    payments.Add(new BillPayment {
                        PlanId = query.Integer(0),
                        Due = DateTime.ParseExact(query.Text(1), "yyyy-MM-dd", CultureInfo.InvariantCulture),
                        Cents = query.Integer(2), EntryId = query.Integer(3),
                        RecordedOn = DateTime.ParseExact(query.Text(4), "yyyy-MM-dd", CultureInfo.InvariantCulture)
                    });
            }
            var waived = new List<KeyValuePair<long, DateTime>>();
            using (Statement query = Prepare("SELECT plan_id, due FROM bill_waivers WHERE settled_on = ''", new object[0]))
                while (query.Step() == Row)
                    waived.Add(new KeyValuePair<long, DateTime>(query.Integer(0),
                        DateTime.ParseExact(query.Text(1), "yyyy-MM-dd", CultureInfo.InvariantCulture)));
            return new BillCalendar(plans, payments, waived.ToLookup(item => item.Key, item => item.Value));
        }

        internal void SavePlan(BillPlan plan)
        {
            string due = IsoDate(plan.FirstDue);
            long income = plan.IsIncome ? 1 : 0;
            long monthlyBill = plan.IsMonthlyBill ? 1 : 0;
            string recorded = plan.IsIncome ? "income_receipts" : "bill_payments";
            if (plan.Id == 0)
            {
                Execute(@"INSERT INTO bill_plans(description, category, cents, first_due, installments, first_installment, is_income,
                    plan_kind, created_on, monthly_bill)
                    VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?)", plan.Description, plan.Category, plan.Cents, due, (long)plan.Installments,
                    (long)plan.FirstInstallment, income, (long)plan.Kind, plan.CreatedOn.HasValue ? IsoDate(plan.CreatedOn.Value) : "", monthlyBill);
                plan.Id = sqlite3_last_insert_rowid(database);
                return;
            }
            Execute(@"UPDATE bill_plans SET description = ?, category = ?, cents = ?, first_due = ?, installments = ?, first_installment = ?, plan_kind = ?, monthly_bill = ?
                WHERE id = ? AND is_income = ? AND archived = 0 AND
                ((first_due = ? AND installments = ? AND first_installment = ? AND plan_kind = ?) OR
                (stop_before = '' AND NOT EXISTS (SELECT 1 FROM " + recorded + " WHERE plan_id = bill_plans.id)))",
                plan.Description, plan.Category, plan.Cents, due, (long)plan.Installments, (long)plan.FirstInstallment,
                (long)plan.Kind, monthlyBill, plan.Id, income, due, (long)plan.Installments, (long)plan.FirstInstallment, (long)plan.Kind);
            if (sqlite3_changes(database) == 0)
                throw new IOException("Este plano mudou. Selecione a cobrança novamente para editar.");
        }

        internal void SaveNewPlanWithFirstPayment(BillPlan plan, DateTime actual, bool included, LedgerEntry existing = null)
        {
            Execute("BEGIN IMMEDIATE");
            try
            {
                SavePlan(plan);
                RecordPlanPayment(plan, plan.FirstDue, actual, plan.Cents, existing, included);
                Execute("COMMIT");
            }
            catch
            {
                Execute("ROLLBACK");
                throw;
            }
        }

        internal void ArchivePlan(long id)
        {
            Execute("UPDATE bill_plans SET archived = 1 WHERE id = ?", id);
        }

        internal void WaiveBill(BillOccurrence occurrence)
        {
            if (!occurrence.IsPending || occurrence.PlanRecord)
                throw new IOException("Selecione uma cobrança em aberto.");
            string when = IsoDate(occurrence.Due);
            Execute(@"INSERT OR IGNORE INTO bill_waivers(plan_id, due)
                SELECT id, ? FROM bill_plans WHERE id = ? AND archived = 0 AND first_due = ?
                    AND plan_kind = ? AND installments = ? AND first_installment = ?
                    AND (stop_before = '' OR stop_before > ?)
                    AND NOT EXISTS(SELECT 1 FROM bill_payments WHERE plan_id = bill_plans.id AND due = ?)
                    AND NOT EXISTS(SELECT 1 FROM income_receipts WHERE plan_id = bill_plans.id AND due = ?)",
                when, occurrence.Plan.Id, IsoDate(occurrence.Plan.FirstDue), (long)occurrence.Plan.Kind,
                (long)occurrence.Plan.Installments, (long)occurrence.Plan.FirstInstallment, when, when, when);
            if (sqlite3_changes(database) == 0) throw new IOException("A cobrança mudou. Atualize a lista antes de dispensá-la.");
        }

        internal void RevisePlan(BillPlan plan, DateTime cutoff, DateTime nextDate, long cents, DateTime today)
        {
            Execute("BEGIN IMMEDIATE");
            try
            {
                string cut = IsoDate(cutoff);
                int offset = plan.MonthOffset(cutoff);
                int installment = plan.Installments == 0 ? 1 : plan.FirstInstallment + offset / plan.Interval;
                if (!plan.Includes(offset) || plan.FirstDue.AddMonths(offset) != cutoff || nextDate < today ||
                    plan.MonthOffset(nextDate) < offset || cents <= 0 ||
                    (plan.Installments > 0 && nextDate.Year * 12 + nextDate.Month +
                        (plan.Installments - installment) * plan.Interval > 9998 * 12 + 12))
                    throw new IOException("Confira a nova data e o valor do plano.");
                Execute(@"UPDATE bill_plans SET stop_before = ?, was_revised = 1 WHERE id = ? AND archived = 0
                    AND stop_before = '' AND first_due = ? AND plan_kind = ? AND installments = ?
                    AND first_installment = ? AND cents = ? AND description = ? AND category = ? AND monthly_bill = ?
                    AND NOT EXISTS(SELECT 1 FROM bill_payments WHERE plan_id = bill_plans.id AND due >= ?)
                    AND NOT EXISTS(SELECT 1 FROM income_receipts WHERE plan_id = bill_plans.id AND due >= ?)
                    AND NOT EXISTS(SELECT 1 FROM bill_waivers WHERE plan_id = bill_plans.id AND due >= ?)",
                    cut, plan.Id, IsoDate(plan.FirstDue), (long)plan.Kind, (long)plan.Installments,
                    (long)plan.FirstInstallment, plan.Cents, plan.Description, plan.Category, plan.IsMonthlyBill ? 1L : 0L, cut, cut, cut);
                if (sqlite3_changes(database) == 0)
                    throw new IOException("O plano mudou ou já tem pagamentos futuros registrados.");
                SavePlan(new BillPlan {
                    Description = plan.Description, Category = plan.Category, Cents = cents, FirstDue = nextDate,
                    Installments = plan.Installments, FirstInstallment = installment, IsIncome = plan.IsIncome,
                    IsMonthlyBill = plan.IsMonthlyBill, Kind = plan.Kind, CreatedOn = today
                });
                Execute("COMMIT");
            }
            catch
            {
                Execute("ROLLBACK");
                throw;
            }
        }

        internal bool StopRenewal(BillPlan plan, DateTime due, DateTime canceledOn)
        {
            Execute(@"UPDATE bill_plans SET stop_before = ?, canceled_on = ?
                WHERE id = ? AND first_due = ? AND plan_kind = ? AND archived = 0
                AND monthly_bill = 0 AND (installments = 0 OR plan_kind <> 0) AND stop_before = ''",
                IsoDate(due), IsoDate(canceledOn), plan.Id, IsoDate(plan.FirstDue), (long)plan.Kind);
            return sqlite3_changes(database) > 0;
        }

        internal List<LedgerEntry> UnlinkedEntries(DateTime month, bool income)
        {
            DateTime start = new DateTime(month.Year, month.Month, 1);
            string entries = income ? "incomes" : "expenses";
            string links = income ? "income_receipts" : "bill_payments";
            string key = income ? "income_id" : "expense_id";
            return Read("SELECT id, date, description, category, cents, entry_kind, version, " + (income ? "1" : "0") + " FROM " + entries +
                " WHERE date >= ? AND date < ? AND entry_kind = 0 AND NOT EXISTS (SELECT 1 FROM " + links + " WHERE " + key + " = " + entries + ".id)" +
                " ORDER BY date DESC, id DESC", IsoDate(start), IsoDate(start.AddMonths(1)));
        }

        internal bool UnlinkBill(BillOccurrence bill)
        {
            if (bill.Payment.InOpeningBalance)
            {
                Execute("DELETE FROM bill_waivers WHERE plan_id = ? AND due = ? AND settled_on = ? AND settled_cents = ?",
                    bill.Plan.Id, IsoDate(bill.Due), IsoDate(bill.Payment.RecordedOn), bill.Payment.Cents);
                return sqlite3_changes(database) == 1;
            }
            string table = bill.Plan.IsIncome ? "income_receipts" : "bill_payments";
            string entryId = bill.Plan.IsIncome ? "income_id" : "expense_id";
            Execute("DELETE FROM " + table + " WHERE plan_id = ? AND due = ? AND " + entryId + " = ?",
                bill.Plan.Id, IsoDate(bill.Due), bill.Payment.EntryId);
            return sqlite3_changes(database) == 1;
        }

        internal bool PayBill(long planId, DateTime due, DateTime paidOn, long? actualCents = null, LedgerEntry existing = null, bool included = false)
        {
            Execute("BEGIN IMMEDIATE");
            try
            {
                BillPlan plan;
                using (Statement query = Prepare(SelectPlans + " WHERE id = ? AND archived = 0", new object[] { planId }))
                {
                    if (query.Step() != Row) throw new IOException("Este plano está indisponível.");
                    plan = ReadPlan(query);
                }
                int offset = plan.MonthOffset(due);
                if (!plan.Schedules(offset) || plan.FirstDue.AddMonths(offset) != due)
                    throw new IOException("O plano mudou. Selecione a nova data.");
                string table = plan.IsIncome ? "income_receipts" : "bill_payments";
                bool alreadyPaid;
                using (Statement query = Prepare("SELECT 1 FROM " + table + " WHERE plan_id = ? AND due = ? UNION ALL " +
                    "SELECT 1 FROM bill_waivers WHERE plan_id = ? AND due = ?",
                    new object[] { planId, IsoDate(due), planId, IsoDate(due) }))
                    alreadyPaid = query.Step() == Row;
                if (!alreadyPaid)
                    RecordPlanPayment(plan, due, paidOn, actualCents ?? plan.Cents, existing, included);
                Execute("COMMIT");
                return !alreadyPaid;
            }
            catch
            {
                Execute("ROLLBACK");
                throw;
            }
        }

        private void RecordPlanPayment(BillPlan plan, DateTime due, DateTime paidOn, long cents, LedgerEntry entry, bool included)
        {
            OpeningBalance opening = GetOpeningBalance();
            if (entry == null && opening != null && (paidOn < opening.Date || included))
            {
                if (paidOn > opening.Date) throw new IOException("Essa movimentação ocorreu depois do saldo inicial.");
                Execute("INSERT INTO bill_waivers(plan_id, due, settled_on, settled_cents) VALUES (?, ?, ?, ?)",
                    plan.Id, IsoDate(due), IsoDate(paidOn), cents);
                return;
            }
            if (included) throw new IOException("Defina o saldo inicial antes de marcar o pagamento como incluído.");
            string table = plan.IsIncome ? "income_receipts" : "bill_payments";
            string entryId = plan.IsIncome ? "income_id" : "expense_id";
            if (entry == null)
            {
                entry = new LedgerEntry { Date = paidOn, Description = plan.Description, Cents = cents,
                    Category = plan.Category, IsIncome = plan.IsIncome };
                Save(entry);
            }
            else
            {
                if (entry.IsIncome != plan.IsIncome)
                    throw new IOException("O lançamento precisa ser do mesmo tipo da previsão.");
                string entries = plan.IsIncome ? "incomes" : "expenses";
                using (Statement candidate = Prepare("SELECT 1 FROM " + entries +
                    " WHERE id = ? AND version = ? AND entry_kind = 0" +
                    " AND NOT EXISTS(SELECT 1 FROM " + table + " WHERE " + entryId + " = ?)",
                    new object[] { entry.Id, entry.Version ?? "", entry.Id }))
                    if (candidate.Step() != Row)
                        throw new IOException("O lançamento foi alterado, removido ou já está vinculado a outra previsão.");
            }
            Execute("INSERT INTO " + table + "(plan_id, due, " + entryId + ") VALUES (?, ?, ?)", plan.Id, IsoDate(due), entry.Id);
        }
    }
}
