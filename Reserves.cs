using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace SpentZ
{
    public sealed class ReserveBalance
    {
        public string Place { get; set; }
        public long Opening { get; set; }
        public long Added { get; set; }
        public long Withdrawn { get; set; }
        public long Balance { get { return Opening + Added - Withdrawn; } }
        public string AmountDisplay { get { return LedgerEntry.Money(Balance); } }
    }

    public sealed class ReserveMovement
    {
        internal long Id { get; set; }
        internal long PlaceId { get; set; }
        public string Place { get; set; }
        public DateTime Date { get; set; }
        public long Cents { get; internal set; }
        internal bool Withdraw { get; set; }
        public string DateDisplay { get { return Date.ToString("dd/MM/yyyy", LedgerEntry.Brazilian); } }
        public string DayDisplay { get { return Date.ToString("dd/MM", LedgerEntry.Brazilian); } }
        public string Kind { get { return Withdraw ? "Resgate" : "Guardado"; } }
        public string SummaryLabel { get { return DayDisplay + "  ·  " + Kind + "  ·  " + Place; } }
        public string AmountDisplay { get { return LedgerEntry.Money(Cents); } }
    }

    internal sealed partial class LedgerStore
    {
        private void EnsureReserveSchema()
        {
            Execute(@"CREATE TABLE IF NOT EXISTS reserve_places (
                id INTEGER PRIMARY KEY, name TEXT NOT NULL UNIQUE COLLATE NOCASE)");
            Execute(@"CREATE TABLE IF NOT EXISTS reserve_moves (
                id INTEGER PRIMARY KEY, place_id INTEGER NOT NULL REFERENCES reserve_places(id),
                date TEXT NOT NULL, cents INTEGER NOT NULL CHECK(cents > 0),
                direction INTEGER NOT NULL CHECK(direction IN (-1, 1)))");
            Execute("CREATE INDEX IF NOT EXISTS reserve_moves_date ON reserve_moves(date)");
            Execute("CREATE INDEX IF NOT EXISTS reserve_moves_place_date ON reserve_moves(place_id, date)");
        }

        internal List<string> ReservePlaces()
        {
            List<string> places = new List<string>();
            using (Statement query = Prepare("SELECT name FROM reserve_places ORDER BY name COLLATE NOCASE", new object[0]))
                while (query.Step() == Row) places.Add(query.Text(0));
            return places;
        }

        internal List<ReserveBalance> ReserveBalances(DateTime month, DateTime? through = null)
        {
            DateTime first = new DateTime(month.Year, month.Month, 1);
            string start = IsoDate(first);
            string end = IsoDate(through.HasValue ? through.Value.Date.AddDays(1) : first.AddMonths(1));
            List<ReserveBalance> rows = new List<ReserveBalance>();
            using (Statement query = Prepare(@"SELECT place.name,
                SUM(CASE WHEN move.date < ? THEN move.cents * move.direction ELSE 0 END),
                SUM(CASE WHEN move.date >= ? AND move.direction = 1 THEN move.cents ELSE 0 END),
                SUM(CASE WHEN move.date >= ? AND move.direction = -1 THEN move.cents ELSE 0 END)
                FROM reserve_moves move JOIN reserve_places place ON move.place_id = place.id
                WHERE move.date < ? GROUP BY place.id ORDER BY place.name COLLATE NOCASE",
                new object[] { start, start, start, end }))
            {
                while (query.Step() == Row)
                {
                    ReserveBalance item = new ReserveBalance {
                        Place = query.Text(0), Opening = query.Integer(1), Added = query.Integer(2), Withdrawn = query.Integer(3)
                    };
                    if (item.Balance != 0 || item.Added != 0 || item.Withdrawn != 0) rows.Add(item);
                }
            }
            return rows;
        }

        internal List<ReserveMovement> ReserveMovements(DateTime month)
        {
            DateTime first = new DateTime(month.Year, month.Month, 1);
            List<ReserveMovement> rows = new List<ReserveMovement>();
            using (Statement query = Prepare(@"SELECT move.id, move.place_id, place.name, move.date, move.cents, move.direction
                FROM reserve_moves move JOIN reserve_places place ON place.id = move.place_id
                WHERE move.date >= ? AND move.date < ? ORDER BY move.date DESC, move.id DESC",
                new object[] { IsoDate(first), IsoDate(first.AddMonths(1)) }))
            {
                while (query.Step() == Row)
                    rows.Add(new ReserveMovement {
                        Id = query.Integer(0), PlaceId = query.Integer(1), Place = query.Text(2),
                        Date = DateTime.ParseExact(query.Text(3), "yyyy-MM-dd", CultureInfo.InvariantCulture),
                        Cents = query.Integer(4), Withdraw = query.Integer(5) == -1
                    });
            }
            return rows;
        }

        internal void AddReserveMovement(string name, DateTime date, long cents, bool withdraw, ReserveMovement previous = null)
        {
            string place = name.Trim();
            if (place.Length == 0 || cents <= 0) throw new IOException("Informe o local e um valor positivo.");
            Execute("BEGIN IMMEDIATE");
            try
            {
                if (!withdraw) Execute("INSERT OR IGNORE INTO reserve_places(name) VALUES (?)", place);
                long placeId;
                using (Statement query = Prepare("SELECT id FROM reserve_places WHERE name = ?", new object[] { place }))
                {
                    if (query.Step() != Row) throw new IOException("Selecione uma reserva existente para resgatar.");
                    placeId = query.Integer(0);
                }
                long direction = withdraw ? -1L : 1L;
                if (previous == null)
                    Execute("INSERT INTO reserve_moves(place_id, date, cents, direction) VALUES (?, ?, ?, ?)",
                        placeId, IsoDate(date), cents, direction);
                else
                {
                    Execute(@"UPDATE reserve_moves SET place_id = ?, date = ?, cents = ?, direction = ?
                        WHERE id = ? AND place_id = ? AND date = ? AND cents = ? AND direction = ?",
                        placeId, IsoDate(date), cents, direction, previous.Id, previous.PlaceId, IsoDate(previous.Date),
                        previous.Cents, previous.Withdraw ? -1L : 1L);
                    if (sqlite3_changes(database) == 0) throw new IOException("A movimentação mudou. Selecione-a novamente.");
                    if (previous.PlaceId != placeId) CheckReserveBalance(previous.PlaceId);
                }
                CheckReserveBalance(placeId);
                Execute("COMMIT");
            }
            catch
            {
                Execute("ROLLBACK");
                throw;
            }
        }

        internal bool RemoveReserveMovement(ReserveMovement entry)
        {
            Execute("BEGIN IMMEDIATE");
            try
            {
                Execute("DELETE FROM reserve_moves WHERE id = ? AND place_id = ? AND date = ? AND cents = ? AND direction = ?",
                    entry.Id, entry.PlaceId, IsoDate(entry.Date), entry.Cents, entry.Withdraw ? -1L : 1L);
                bool removed = sqlite3_changes(database) != 0;
                if (removed) CheckReserveBalance(entry.PlaceId);
                Execute("COMMIT");
                return removed;
            }
            catch
            {
                Execute("ROLLBACK");
                throw;
            }
        }

        private void CheckReserveBalance(long placeId)
        {
            decimal balance = 0;
            using (Statement query = Prepare("SELECT SUM(cents * direction) FROM reserve_moves WHERE place_id = ? GROUP BY date ORDER BY date",
                new object[] { placeId }))
            {
                while (query.Step() == Row)
                {
                    balance += query.Integer(0);
                    if (balance < 0)
                        throw new IOException("A reserva ficaria negativa em alguma data. Confira o valor, a data e os resgates já registrados.");
                }
            }
        }

        private void ValidateReserveData()
        {
            using (Statement dates = Prepare("SELECT DISTINCT date FROM reserve_moves", new object[0]))
            {
                while (dates.Step() == Row)
                {
                    DateTime date;
                    if (!DateTime.TryParseExact(dates.Text(0), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out date) ||
                        date.Year < 1900 || date.Year > 9998)
                        throw new IOException("O backup contém uma data de reserva inválida.");
                }
            }
            using (Statement places = Prepare("SELECT id FROM reserve_places", new object[0]))
                while (places.Step() == Row) CheckReserveBalance(places.Integer(0));
        }
    }
}
