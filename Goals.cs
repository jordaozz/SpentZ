using System;
using System.Collections.Generic;
using System.IO;

namespace SpentZ
{
    internal sealed class SavingsGoal
    {
        internal long Id { get; set; }
        internal string Name { get; set; }
        internal long Target { get; set; }
        internal long Saved { get; set; }
        internal long Spent { get; set; }
        internal long Progress { get { return Saved + Spent; } }
    }

    internal sealed class GoalPurchase
    {
        internal long ExpenseId { get; set; }
        internal string Description { get; set; }
        internal long Cents { get; set; }
        internal string Date { get; set; }
        public override string ToString() { return Date + " · " + LedgerEntry.Money(Cents) + " · " + Description; }
    }

    internal sealed class GoalFunding
    {
        internal long PlaceId { get; set; }
        internal string Place { get; set; }
        internal long Free { get; set; }
        internal long Reserved { get; set; }
    }

    internal sealed partial class LedgerStore
    {
        private void EnsureGoalsSchema()
        {
            Execute(@"CREATE TABLE IF NOT EXISTS savings_goals (
                id INTEGER PRIMARY KEY, name TEXT NOT NULL, target INTEGER NOT NULL CHECK(target > 0))");
            Execute(@"CREATE TABLE IF NOT EXISTS goal_funds (
                goal_id INTEGER NOT NULL REFERENCES savings_goals(id) ON DELETE CASCADE,
                place_id INTEGER NOT NULL REFERENCES reserve_places(id),
                cents INTEGER NOT NULL CHECK(cents > 0), PRIMARY KEY(goal_id, place_id))");
            Execute("CREATE INDEX IF NOT EXISTS goal_funds_by_place ON goal_funds(place_id)");
            if (!HasColumn("savings_goals", "archived"))
                Execute("ALTER TABLE savings_goals ADD COLUMN archived INTEGER NOT NULL DEFAULT 0 CHECK(archived IN (0, 1))");
            Execute(@"CREATE TABLE IF NOT EXISTS goal_purchases (
                expense_id INTEGER PRIMARY KEY REFERENCES expenses(id),
                move_id INTEGER NOT NULL UNIQUE REFERENCES reserve_moves(id),
                goal_id INTEGER NOT NULL REFERENCES savings_goals(id))");
            Execute("CREATE INDEX IF NOT EXISTS goal_purchases_goal ON goal_purchases(goal_id)");
        }

        internal List<SavingsGoal> SavingsGoals(bool archived = false)
        {
            var result = new List<SavingsGoal>();
            using (Statement query = Prepare(@"SELECT goal.id, goal.name, goal.target, COALESCE(SUM(fund.cents), 0),
                COALESCE((SELECT SUM(expense.cents - COALESCE((SELECT SUM(cents) FROM incomes WHERE refund_of = expense.id), 0))
                    FROM goal_purchases purchase JOIN expenses expense
                    ON expense.id = purchase.expense_id WHERE purchase.goal_id = goal.id), 0)
                FROM savings_goals goal LEFT JOIN goal_funds fund ON fund.goal_id = goal.id
                WHERE goal.archived = ? GROUP BY goal.id ORDER BY goal.name COLLATE NOCASE, goal.id", new object[] { archived ? 1L : 0L }))
                while (query.Step() == Row)
                    result.Add(new SavingsGoal { Id = query.Integer(0), Name = query.Text(1), Target = query.Integer(2),
                        Saved = query.Integer(3), Spent = query.Integer(4) });
            return result;
        }

        internal void SaveSavingsGoal(long id, string name, long target)
        {
            name = name.Trim();
            if (name.Length == 0 || name.Length > 80 || target <= 0) throw new IOException("Informe um nome e um valor positivo para a meta.");
            if (id == 0) Execute("INSERT INTO savings_goals(name, target) VALUES (?, ?)", name, target);
            else
            {
                Execute("UPDATE savings_goals SET name = ?, target = ? WHERE id = ?", name, target, id);
                if (sqlite3_changes(database) == 0) throw new IOException("Esta meta foi removida.");
            }
        }

        internal List<GoalFunding> GoalFundingByPlace(long goalId)
        {
            var result = new List<GoalFunding>();
            using (Statement query = Prepare(@"SELECT place.id, place.name,
                COALESCE((SELECT SUM(cents * direction) FROM reserve_moves WHERE place_id = place.id), 0) -
                COALESCE((SELECT SUM(cents) FROM goal_funds WHERE place_id = place.id), 0),
                COALESCE((SELECT cents FROM goal_funds WHERE place_id = place.id AND goal_id = ?), 0)
                FROM reserve_places place ORDER BY place.name COLLATE NOCASE", new object[] { goalId }))
                while (query.Step() == Row)
                    result.Add(new GoalFunding { PlaceId = query.Integer(0), Place = query.Text(1), Free = query.Integer(2), Reserved = query.Integer(3) });
            return result;
        }

        internal void MoveGoalFunds(long goalId, long placeId, long cents, bool release)
        {
            if (cents <= 0) throw new IOException("Informe um valor positivo.");
            Execute("BEGIN IMMEDIATE");
            try
            {
                ChangeGoalFunds(goalId, placeId, cents, release);
                Execute("COMMIT");
            }
            catch
            {
                Execute("ROLLBACK");
                throw;
            }
        }

        private void ChangeGoalFunds(long goalId, long placeId, long cents, bool release)
        {
            using (Statement query = Prepare("SELECT 1 FROM savings_goals WHERE id = ? AND archived = 0", new object[] { goalId }))
                if (query.Step() != Row) throw new IOException("A meta está arquivada ou indisponível.");
            if (!release)
            {
                Execute(@"INSERT INTO goal_funds(goal_id, place_id, cents) VALUES (?, ?, ?)
                    ON CONFLICT(goal_id, place_id) DO UPDATE SET cents = cents + excluded.cents", goalId, placeId, cents);
                CheckGoalFunding(placeId);
                return;
            }
            Execute("DELETE FROM goal_funds WHERE goal_id = ? AND place_id = ? AND cents = ?", goalId, placeId, cents);
            if (sqlite3_changes(database) == 1) return;
            Execute("UPDATE goal_funds SET cents = cents - ? WHERE goal_id = ? AND place_id = ? AND cents > ?",
                cents, goalId, placeId, cents);
            if (sqlite3_changes(database) == 0) throw new IOException("A meta não tem esse valor reservado no local escolhido.");
        }

        internal void BuyFromGoal(long goal, long place, long cents, DateTime date, string description, string category)
        {
            if (cents <= 0) throw new IOException("Informe um valor positivo.");
            Execute("BEGIN IMMEDIATE");
            try
            {
                ChangeGoalFunds(goal, place, cents, true);
                Execute("INSERT INTO reserve_moves(place_id, date, cents, direction) VALUES (?, ?, ?, -1)", place, IsoDate(date), cents);
                long move = sqlite3_last_insert_rowid(database);
                CheckReserveBalance(place);
                CheckGoalFunding(place);
                LedgerEntry purchase = new LedgerEntry { Date = date, Cents = cents, Description = description, Category = category };
                Save(purchase);
                Execute("INSERT INTO goal_purchases(expense_id, move_id, goal_id) VALUES (?, ?, ?)", purchase.Id, move, goal);
                Execute("COMMIT");
            }
            catch { Execute("ROLLBACK"); throw; }
        }

        internal List<GoalPurchase> GoalPurchases(long goal)
        {
            var result = new List<GoalPurchase>();
            using (Statement query = Prepare(@"SELECT expense.id, expense.description, expense.cents, expense.date
                FROM goal_purchases purchase JOIN expenses expense ON expense.id = purchase.expense_id
                WHERE purchase.goal_id = ? ORDER BY expense.date DESC, expense.id DESC", new object[] { goal }))
                while (query.Step() == Row)
                    result.Add(new GoalPurchase { ExpenseId = query.Integer(0), Description = query.Text(1), Cents = query.Integer(2),
                        Date = DateTime.ParseExact(query.Text(3), "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture).ToString("dd/MM/yyyy") });
            return result;
        }

        private bool IsGoalPurchase(long id, bool expense)
        {
            using (Statement query = Prepare("SELECT 1 FROM goal_purchases WHERE " + (expense ? "expense_id" : "move_id") + " = ?",
                new object[] { id }))
                return query.Step() == Row;
        }

        internal void UndoGoalPurchase(long goal, long expense)
        {
            Execute("BEGIN IMMEDIATE");
            try
            {
                long move, place, cents;
                using (Statement query = Prepare(@"SELECT movement.id, movement.place_id, movement.cents FROM goal_purchases purchase
                    JOIN reserve_moves movement ON movement.id = purchase.move_id
                    WHERE purchase.goal_id = ? AND purchase.expense_id = ?
                    AND NOT EXISTS(SELECT 1 FROM incomes WHERE refund_of = purchase.expense_id)", new object[] { goal, expense }))
                {
                    if (query.Step() != Row) throw new IOException("Confira se a compra ainda existe e se há um estorno vinculado.");
                    move = query.Integer(0);
                    place = query.Integer(1);
                    cents = query.Integer(2);
                }
                Execute("DELETE FROM goal_purchases WHERE expense_id = ?", expense);
                Execute("DELETE FROM expenses WHERE id = ?", expense);
                Execute("DELETE FROM reserve_moves WHERE id = ?", move);
                ChangeGoalFunds(goal, place, cents, false);
                Execute("COMMIT");
            }
            catch { Execute("ROLLBACK"); throw; }
        }

        internal void SetGoalArchived(long id, bool archived)
        {
            Execute("BEGIN IMMEDIATE");
            try
            {
                Execute("UPDATE savings_goals SET archived = ? WHERE id = ?", archived ? 1L : 0L, id);
                if (sqlite3_changes(database) != 1) throw new IOException("Esta meta foi removida.");
                if (archived) Execute("DELETE FROM goal_funds WHERE goal_id = ?", id);
                Execute("COMMIT");
            }
            catch { Execute("ROLLBACK"); throw; }
        }

        private void CheckGoalFunding(long placeId)
        {
            using (Statement query = Prepare(@"SELECT COALESCE((SELECT SUM(cents) FROM goal_funds WHERE place_id = ?), 0),
                COALESCE((SELECT SUM(cents * direction) FROM reserve_moves WHERE place_id = ?), 0)", new object[] { placeId, placeId }))
                if (query.Step() == Row && query.Integer(0) > query.Integer(1))
                    throw new IOException("Parte desse dinheiro está reservada em metas. Libere o valor na página Metas para usá-lo.");
        }
    }
}
