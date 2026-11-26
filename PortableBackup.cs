using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace SpentZ
{
    internal static class PortableBackup
    {
        internal const int MaxBytes = 64 * 1024 * 1024;
        internal const int MinPassword = 12;
        private static readonly byte[] Magic = Encoding.ASCII.GetBytes("SPENTZB1");
        private const int SaltSize = 16, IvSize = 16, TagSize = 32, KeySize = 32;
        private const int HeaderSize = 8 + SaltSize + IvSize;

        internal static byte[] Protect(byte[] content, string password)
        {
            if (password.Length < MinPassword) throw new IOException("Escolha uma senha com pelo menos 12 caracteres.");
            byte[] salt = new byte[SaltSize];
            byte[] iv = new byte[IvSize];
            using (RandomNumberGenerator random = RandomNumberGenerator.Create())
            {
                random.GetBytes(salt);
                random.GetBytes(iv);
            }
            byte[] key = Derive(password, salt);
            try
            {
                byte[] encrypted = Transform(content, 0, content.Length, key, iv, true);
                byte[] output = new byte[HeaderSize + encrypted.Length + TagSize];
                Buffer.BlockCopy(Magic, 0, output, 0, Magic.Length);
                Buffer.BlockCopy(salt, 0, output, Magic.Length, SaltSize);
                Buffer.BlockCopy(iv, 0, output, Magic.Length + SaltSize, IvSize);
                Buffer.BlockCopy(encrypted, 0, output, HeaderSize, encrypted.Length);
                Tag(output, key).CopyTo(output, output.Length - TagSize);
                return output;
            }
            finally { Array.Clear(key, 0, key.Length); }
        }

        internal static byte[] Unprotect(byte[] envelope, string password)
        {
            if (envelope.Length < HeaderSize + IvSize + TagSize || !Magic.SequenceEqual(envelope.Take(Magic.Length)))
                throw new IOException("Formato de backup protegido desconhecido.");
            byte[] key = Derive(password, envelope.Skip(Magic.Length).Take(SaltSize).ToArray());
            try
            {
                byte[] expected = Tag(envelope, key);
                int difference = 0;
                for (int index = 0; index < TagSize; index++)
                    difference |= expected[index] ^ envelope[envelope.Length - TagSize + index];
                if (difference != 0) throw new IOException("A senha está incorreta ou o backup foi alterado.");
                byte[] iv = envelope.Skip(Magic.Length + SaltSize).Take(IvSize).ToArray();
                return Transform(envelope, HeaderSize, envelope.Length - HeaderSize - TagSize, key, iv, false);
            }
            catch (CryptographicException) { throw new IOException("O conteúdo do backup protegido é inválido."); }
            finally { Array.Clear(key, 0, key.Length); }
        }

        private static byte[] Derive(string password, byte[] salt)
        {
            using (var kdf = new Rfc2898DeriveBytes(password, salt, 600000, HashAlgorithmName.SHA256))
                return kdf.GetBytes(KeySize * 2);
        }

        private static byte[] Tag(byte[] envelope, byte[] key)
        {
            using (var hmac = new HMACSHA256(key.Skip(KeySize).Take(KeySize).ToArray()))
                return hmac.ComputeHash(envelope, 0, envelope.Length - TagSize);
        }

        private static byte[] Transform(byte[] bytes, int offset, int count, byte[] key, byte[] iv, bool encrypt)
        {
            using (Aes aes = Aes.Create())
            {
                aes.Mode = CipherMode.CBC;
                aes.Padding = PaddingMode.PKCS7;
                aes.Key = key.Take(KeySize).ToArray();
                aes.IV = iv;
                using (ICryptoTransform transform = encrypt ? aes.CreateEncryptor() : aes.CreateDecryptor())
                    return transform.TransformFinalBlock(bytes, offset, count);
            }
        }
    }

    internal sealed partial class LedgerStore
    {
        // Version 1 stores only rows in this fixed order. A new layout needs a new reader/version.
        private static readonly string[] BackupLayout = {
            "expenses id date description category cents entry_kind included_in_opening version",
            "incomes id date description category cents entry_kind refund_of included_in_opening version",
            "bill_plans id description category cents first_due installments first_installment archived stop_before is_income plan_kind created_on canceled_on monthly_bill was_revised",
            "bill_payments plan_id due expense_id", "income_receipts plan_id due income_id",
            "bill_waivers plan_id due settled_on settled_cents",
            "reserve_places id name", "reserve_moves id place_id date cents direction included_in_opening initial_reserve",
            "savings_goals id name target archived", "goal_funds goal_id place_id cents", "goal_purchases expense_id move_id goal_id",
            "month_closures month income expenses entries", "custom_categories name_key name", "category_limits category since cents",
            "opening_balance id date cents", "preferences name value"
        };

        private static string Selection(string table, string[] columns)
        {
            return "SELECT " + string.Join(", ", columns) + " FROM " + table +
                (table == "preferences" ? " WHERE name IN ('dark', 'accent', 'reminder', 'opening-prompt-skipped')" : "");
        }

        private static string Insertion(string table, string[] columns)
        {
            return "INSERT INTO " + table + "(" + string.Join(", ", columns) + ") VALUES (" +
                string.Join(", ", columns.Select(column => "?")) + ")";
        }

        private static object[] ReadBackupRow(Statement query, int count)
        {
            object[] values = new object[count];
            for (int i = 0; i < count; i++)
            {
                int type = sqlite3_column_type(query.Handle, i);
                if (type == 5) continue;
                if (type == 1) values[i] = query.Integer(i);
                else if (type == 3)
                {
                    if (sqlite3_column_bytes(query.Handle, i) > 16384)
                        throw new IOException("O backup contém um texto grande demais.");
                    values[i] = query.Text(i);
                }
                else throw new IOException("O backup contém um valor de tipo inválido.");
            }
            return values;
        }

        private void CopyLegacyRows(LedgerStore target)
        {
            target.Execute("BEGIN IMMEDIATE");
            try
            {
                foreach (string spec in BackupLayout)
                {
                    string[] fields = spec.Split(' ');
                    string table = fields[0];
                    string[] columns = fields.Skip(1).Where(column => HasColumn(table, column)).ToArray();
                    if (columns.Length == 0) continue;
                    string insert = Insertion(table, columns);
                    using (Statement query = Prepare(Selection(table, columns), new object[0]))
                        while (query.Step() == Row) target.Execute(insert, ReadBackupRow(query, columns.Length));
                }
                target.Execute("COMMIT");
            }
            catch { target.Execute("ROLLBACK"); throw; }
        }

        internal void SaveProtectedBackup(string filename, string password)
        {
            string destination = CheckBackupPath(filename);
            byte[] contents;
            using (var memory = new MemoryStream())
            using (var writer = new BinaryWriter(memory, Encoding.UTF8, true))
            using (var snapshot = new LedgerStore(":memory:", ReadWriteCreate, false))
            {
                CopyInto(snapshot);
                writer.Write(1);
                foreach (string spec in BackupLayout)
                {
                    string[] fields = spec.Split(' ');
                    string table = fields[0];
                    string[] columns = fields.Skip(1).ToArray();
                    long position = memory.Position;
                    writer.Write(0);
                    int rows = 0;
                    using (Statement query = snapshot.Prepare(Selection(table, columns), new object[0]))
                        while (query.Step() == Row)
                        {
                            rows++;
                            foreach (object value in ReadBackupRow(query, columns.Length))
                            {
                                if (value == null) writer.Write((byte)0);
                                else if (value is long) { writer.Write((byte)1); writer.Write((long)value); }
                                else { writer.Write((byte)2); writer.Write((string)value); }
                            }
                            if (memory.Length > PortableBackup.MaxBytes - 128) throw new IOException("O backup excede o limite de 64 MB.");
                        }
                    long end = memory.Position;
                    memory.Position = position;
                    writer.Write(rows);
                    memory.Position = end;
                }
                contents = memory.ToArray();
            }
            byte[] protectedData;
            try { protectedData = PortableBackup.Protect(contents, password); }
            finally { Array.Clear(contents, 0, contents.Length); }
            string staging = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllBytes(staging, protectedData);
                if (File.Exists(destination)) File.Replace(staging, destination, null);
                else File.Move(staging, destination);
            }
            finally { if (File.Exists(staging)) File.Delete(staging); }
        }

        private static void ReadProtectedRows(LedgerStore target, string path, string password)
        {
            byte[] envelope;
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var reader = new BinaryReader(stream))
            {
                long length = stream.Length;
                if (length > PortableBackup.MaxBytes) throw new IOException("O backup excede o limite de 64 MB.");
                envelope = reader.ReadBytes((int)length);
                if (envelope.Length != length) throw new IOException("O arquivo de backup está incompleto.");
            }
            byte[] contents = PortableBackup.Unprotect(envelope, password);
            try
            {
                using (var memory = new MemoryStream(contents))
                using (var reader = new BinaryReader(memory, new UTF8Encoding(false, true)))
                {
                    if (reader.ReadInt32() != 1) throw new IOException("Versão do backup protegido desconhecida.");
                    target.Execute("BEGIN IMMEDIATE");
                    try
                    {
                        foreach (string spec in BackupLayout)
                        {
                            string[] fields = spec.Split(' ');
                            string[] columns = fields.Skip(1).ToArray();
                            string insert = Insertion(fields[0], columns);
                            int rows = reader.ReadInt32();
                            if (rows < 0 || rows > contents.Length - memory.Position)
                                throw new IOException("O backup contém uma quantidade inválida de registros.");
                            for (int row = 0; row < rows; row++)
                            {
                                object[] values = new object[columns.Length];
                                for (int column = 0; column < columns.Length; column++)
                                {
                                    switch (reader.ReadByte())
                                    {
                                        case 0: break;
                                        case 1: values[column] = reader.ReadInt64(); break;
                                        case 2:
                                            string value = reader.ReadString();
                                            if (value.Length > 16384) throw new IOException("O backup contém um texto grande demais.");
                                            values[column] = value;
                                            break;
                                        default: throw new IOException("O backup contém um tipo de valor desconhecido.");
                                    }
                                }
                                if (fields[0] != "preferences" || new[] { "dark", "accent", "reminder", "opening-prompt-skipped" }.Contains(values[0] as string))
                                    target.Execute(insert, values);
                            }
                        }
                        if (memory.Position != memory.Length) throw new IOException("O backup contém dados extras.");
                        target.Execute("COMMIT");
                    }
                    catch { target.Execute("ROLLBACK"); throw; }
                }
            }
            catch (DecoderFallbackException) { throw new IOException("O backup contém texto inválido."); }
            finally { Array.Clear(contents, 0, contents.Length); }
        }

        internal LedgerStore OpenValidatedSnapshot(string source, string password)
        {
            string path = CheckBackupPath(source);
            if (new FileInfo(path).Length > PortableBackup.MaxBytes) throw new IOException("O backup excede o limite de 64 MB.");
            var snapshot = new LedgerStore(":memory:");
            try
            {
                if (password != null) ReadProtectedRows(snapshot, path, password);
                else
                    using (var incoming = new LedgerStore(path, ReadOnly, false))
                    {
                        incoming.ValidateBackup();
                        incoming.CopyLegacyRows(snapshot);
                    }
                snapshot.EnsureSchema();
                snapshot.ValidateBackup();
                snapshot.ValidatePlannedData();
                return snapshot;
            }
            catch { snapshot.Dispose(); throw; }
        }
    }
}
