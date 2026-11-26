using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;

namespace SpentZ
{
    internal static class BackupPackage
    {
        internal const string DatabaseName = "dados-spentz.sqlite3";
        internal const string ProgramName = "SpentZ.exe";

        internal static string Create(LedgerStore store, string parent, string executable)
        {
            string name = "SpentZ Backup " + DateTime.Now.ToString("yyyy-MM-dd HHmmss-fff", CultureInfo.InvariantCulture);
            string target = Path.Combine(parent, name);
            string staging = Path.Combine(parent, ".spentz-backup-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(staging);
            try
            {
                File.Copy(executable, Path.Combine(staging, ProgramName));
                store.SaveBackup(Path.Combine(staging, DatabaseName));
                Directory.Move(staging, target);
                return target;
            }
            finally
            {
                if (Directory.Exists(staging))
                {
                    File.Delete(Path.Combine(staging, ProgramName));
                    File.Delete(Path.Combine(staging, DatabaseName));
                    Directory.Delete(staging);
                }
            }
        }

        internal static string FindData(string folder, bool allowOldName)
        {
            if (!Directory.Exists(folder)) return null;
            string standard = Path.Combine(folder, DatabaseName);
            if (File.Exists(standard)) return standard;
            if (!allowOldName) return null;
            string[] candidates = Directory.GetFiles(folder, "*.sqlite3");
            return candidates.Length == 1 ? candidates[0] : null;
        }
    }

    internal sealed partial class Program
    {
        private const string LastBackupFolder = "last-backup-folder";
        private const string LastSavedBackupFolder = "last-saved-backup-folder";
        private const string LastProtectedFile = "last-protected-backup";

        private void RefreshBackupInfo()
        {
            string folder = store.GetPreference(LastSavedBackupFolder);
            string protectedFile = store.GetPreference(LastProtectedFile);
            string data = protectedFile;
            if (!File.Exists(data)) data = BackupPackage.FindData(folder, false);
            Get<TextBlock>("LastBackupInfo").Text = data == null ?
                (string.IsNullOrEmpty(folder) && string.IsNullOrEmpty(protectedFile) ?
                    "Nenhum backup salvo neste computador." : "O último backup foi movido ou está em uma unidade desconectada.") :
                "Último backup salvo em " + File.GetLastWriteTime(data).ToString("dd/MM/yyyy 'às' HH:mm", LedgerEntry.Brazilian);
            Get<Button>("OpenLastBackup").IsEnabled = data != null;
        }

        private void OpenLastSavedBackup()
        {
            string protectedFile = store.GetPreference(LastProtectedFile);
            string folder = File.Exists(protectedFile) ? Path.GetDirectoryName(protectedFile) : store.GetPreference(LastSavedBackupFolder);
            if (!Directory.Exists(folder)) { RefreshBackupInfo(); return; }
            Process.Start(new ProcessStartInfo(folder) { UseShellExecute = true });
        }

        private void ConfirmResetRecords()
        {
            Window dialog = CreateDialog("Resetar o SpentZ", 440);
            StackPanel body = new StackPanel { Margin = new Thickness(20) };
            body.Children.Add(new TextBlock {
                Text = "Quer apagar todos os registros?", FontSize = 18, FontWeight = FontWeights.SemiBold
            });
            body.Children.Add(new TextBlock {
                Text = "Ao confirmar, você perderá todos os lançamentos, contas futuras, categorias criadas, limites, Cofrinho e fechamentos. " +
                    "O SpentZ voltará com os saldos zerados. Esta ação é definitiva.",
                TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 8)
            });
            body.Children.Add(new TextBlock {
                Text = "Backups manuais e cópias criadas durante restaurações anteriores continuam guardados. Para fazer uma nova cópia, cancele e salve um backup primeiro.",
                Foreground = Tone("Muted"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 16)
            });
            Button reset = new Button {
                Content = "Apagar todos os dados", Background = Tone("ResetSurface"), Foreground = Brushes.White,
                Margin = new Thickness(8, 0, 0, 0)
            };
            StackPanel actions = DialogActions(reset);
            Button cancel = (Button)actions.Children[0];
            cancel.IsDefault = true;
            reset.Click += delegate { dialog.DialogResult = true; };
            dialog.Loaded += delegate { cancel.Focus(); };
            body.Children.Add(actions);
            dialog.Content = body;
            if (dialog.ShowDialog() != true) return;

            store.ResetRecords();
            openingBalance = null;
            lastCreatedCategory = null;
            Get<TextBlock>("BudgetStatus").Text = "";
            pageMonths.Clear();
            month = MonthStart(Today);
            ChatText = "";
            Get<Expander>("SummaryReserveDetails").IsExpanded = false;
            ReloadBills();
            RefreshMonth();
            Get<TextBox>("HistorySearch").Clear();
            Get<ComboBox>("HistoryCategory").SelectedIndex = 0;
            Get<ComboBox>("HistoryType").SelectedIndex = 0;
            RefreshHistoryCategories();
            RefreshChat();
            Navigate("Chat");
            AddBubble(AppName, "Registros apagados. Você já pode começar de novo.", false);
        }

        private void SaveBackupFolder()
        {
            SaveFileDialog target = new SaveFileDialog { Title = "Salvar backup protegido",
                FileName = "SpentZ-" + Today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + ".spentz",
                Filter = "Backup protegido do SpentZ (*.spentz)|*.spentz", DefaultExt = ".spentz", AddExtension = true };
            if (target.ShowDialog(window) != true) return;
            string password = AskBackupPassword("Proteger o backup", true);
            if (password == null) return;
            UpdateClosures();
            store.SaveProtectedBackup(target.FileName, password);
            store.SetPreference(LastProtectedFile, target.FileName);
            RefreshBackupInfo();
            ShowBackupStatus("Backup protegido salvo em " + target.FileName + ". Guarde a senha separadamente.");
        }

        private void RestoreBackupFolder()
        {
            string appFolder = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            string previousFolder = store.GetPreference(LastBackupFolder);
            string lastFile = store.GetPreference(LastProtectedFile);
            OpenFileDialog choose = new OpenFileDialog { Title = "Escolher backup",
                InitialDirectory = File.Exists(lastFile) ? Path.GetDirectoryName(lastFile) :
                    Directory.Exists(previousFolder) ? previousFolder : appFolder,
                Filter = "Backups do SpentZ (*.spentz;*.sqlite3)|*.spentz;*.sqlite3" };
            if (choose.ShowDialog(window) != true) return;
            string data = choose.FileName;
            bool encrypted = Path.GetExtension(data).Equals(".spentz", StringComparison.OrdinalIgnoreCase);
            string password = encrypted ? AskBackupPassword("Abrir backup protegido", false) : null;
            if (encrypted && password == null) return;
            string recovery;
            using (LedgerStore snapshot = store.OpenValidatedSnapshot(data, password))
            {
                if (MessageBox.Show(window, "Restaurar “" + Path.GetFileName(data) + "”?\n\n" + snapshot.DescribeSnapshot() +
                    "\n\nA restauração substitui os registros atuais. O SpentZ salva uma cópia protegida antes da troca.",
                    "Restaurar backup", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;
                string recoveryPassword = encrypted ? password : AskBackupPassword("Proteger a cópia dos registros atuais", true);
                if (recoveryPassword == null) return;
                recovery = store.RestoreSnapshot(snapshot, recoveryPassword);
            }
            openingBalance = store.GetOpeningBalance();
            lastCreatedCategory = null;
            Get<TextBlock>("BudgetStatus").Text = "";
            store.SetPreference(LastBackupFolder, Path.GetDirectoryName(data));
            LoadSettings();
            RefreshHistoryCategories();
            UpdateClosures();
            ReloadBills();
            RefreshMonth();
            RefreshChat();
            RefreshBackupInfo();
            ShowBackupStatus("Backup restaurado. A cópia anterior foi salva em " + recovery);
        }

        private string AskBackupPassword(string title, bool create)
        {
            Window dialog = CreateDialog(title, 430);
            StackPanel fields = new StackPanel { Margin = new Thickness(20) };
            if (create) fields.Children.Add(new TextBlock {
                Text = "Use pelo menos 12 caracteres. Guarde a senha em um lugar seguro para conseguir restaurar o backup.",
                TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10)
            });
            PasswordBox password = new PasswordBox { MaxLength = 128, Padding = new Thickness(7) };
            PasswordBox repeated = new PasswordBox { MaxLength = 128, Padding = new Thickness(7) };
            AddField(fields, "Senha do backup", password);
            if (create) AddField(fields, "Confirmar senha", repeated);
            TextBlock feedback = new TextBlock { Foreground = Tone("DangerText"), TextWrapping = TextWrapping.Wrap };
            fields.Children.Add(feedback);
            Button confirm = PrimaryDialogButton(create ? "Usar esta senha" : "Abrir backup");
            fields.Children.Add(DialogActions(confirm));
            dialog.Content = fields;
            dialog.Loaded += delegate { password.Focus(); };
            confirm.Click += delegate
            {
                if (password.Password.Length < PortableBackup.MinPassword || (create && password.Password != repeated.Password))
                {
                    feedback.Text = create ? "Use pelo menos 12 caracteres e confira se as duas senhas são iguais." : "Confira a senha do backup.";
                    return;
                }
                dialog.DialogResult = true;
            };
            return dialog.ShowDialog() == true ? password.Password : null;
        }

        private void ShowBackupStatus(string message)
        {
            TextBlock status = Get<TextBlock>("BackupStatus");
            status.Text = message;
            status.Visibility = Visibility.Visible;
        }
    }
}
