using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Win32;
using Forms = System.Windows.Forms;

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

        private void RefreshBackupInfo()
        {
            string folder = store.GetPreference(LastSavedBackupFolder);
            string data = BackupPackage.FindData(folder, false);
            Get<TextBlock>("LastBackupInfo").Text = data == null ?
                (string.IsNullOrEmpty(folder) ? "Nenhum backup salvo neste computador." : "O último backup foi movido ou está em uma unidade desconectada.") :
                "Último backup salvo em " + File.GetLastWriteTime(data).ToString("dd/MM/yyyy 'às' HH:mm", LedgerEntry.Brazilian);
            Get<Button>("OpenLastBackup").IsEnabled = data != null;
        }

        private void OpenLastSavedBackup()
        {
            string folder = store.GetPreference(LastSavedBackupFolder);
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
                Text = "Se quiser recuperar os dados depois, cancele e salve um backup primeiro.",
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
            Get<TextBox>("ChatInput").Clear();
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
            string documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            string target = ChooseFolder("Escolha onde guardar a pasta de backup do SpentZ.", documents, true);
            if (target == null) return;
            UpdateClosures();
            string folder = BackupPackage.Create(store, target, Assembly.GetExecutingAssembly().Location);
            store.SetPreference(LastBackupFolder, folder);
            store.SetPreference(LastSavedBackupFolder, folder);
            RefreshBackupInfo();
            ShowBackupStatus("Backup salvo em " + folder + ". Guarde a pasta inteira em um local seguro.");
        }

        private void RestoreBackupFolder()
        {
            string appFolder = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            string previousFolder = store.GetPreference(LastBackupFolder);
            string suggested = BackupPackage.FindData(appFolder, false) != null ? appFolder :
                (BackupPackage.FindData(previousFolder, true) != null ? previousFolder : null);
            string folder = SelectRestoreFolder(suggested);
            if (folder == null) return;
            string data = BackupPackage.FindData(folder, true);
            if (data == null)
            {
                if (Directory.GetFiles(folder, "*.sqlite3").Length == 0)
                    throw new IOException("Essa pasta não contém um backup do SpentZ. Use Restaurar backup e selecione a pasta que você guardou.");
                OpenFileDialog choose = new OpenFileDialog {
                    Title = "Escolha qual backup recuperar",
                    InitialDirectory = folder, Filter = "Backup do SpentZ (*.sqlite3)|*.sqlite3"
                };
                if (choose.ShowDialog(window) != true) return;
                data = choose.FileName;
                folder = Path.GetDirectoryName(data);
            }
            string preview = store.DescribeBackup(data);
            if (MessageBox.Show(window, "Restaurar os dados da pasta “" + folder + "”?\n\n" + preview +
                "\n\nOs dados do backup vão substituir os registros atuais. O SpentZ vai guardar uma cópia de segurança antes da troca.",
                "Restaurar backup", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;
            string recovery = store.RestoreBackup(data);
            openingBalance = store.GetOpeningBalance();
            lastCreatedCategory = null;
            Get<TextBlock>("BudgetStatus").Text = "";
            store.SetPreference(LastBackupFolder, folder);
            LoadSettings();
            RefreshHistoryCategories();
            UpdateClosures();
            ReloadBills();
            RefreshMonth();
            RefreshChat();
            RefreshBackupInfo();
            ShowBackupStatus("Backup restaurado. A cópia anterior foi salva em " + recovery);
        }

        private string SelectRestoreFolder(string suggested)
        {
            if (suggested == null)
                return ChooseFolder("Selecione a pasta que contém o backup do SpentZ.",
                    Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), false);

            Window choice = new Window {
                Owner = window, Title = "Pasta de restauração", SizeToContent = SizeToContent.Height, Width = 500,
                WindowStartupLocation = WindowStartupLocation.CenterOwner, ResizeMode = ResizeMode.NoResize,
                Background = Tone("Surface"), Foreground = Tone("Ink"), FontSize = 14, ShowInTaskbar = false
            };
            choice.Resources.MergedDictionaries.Add(window.Resources);
            StackPanel body = new StackPanel { Margin = new Thickness(20) };
            body.Children.Add(new TextBlock { Text = "Encontramos este backup", FontSize = 17, FontWeight = FontWeights.SemiBold });
            body.Children.Add(new TextBlock { Text = suggested, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 16) });
            WrapPanel actions = new WrapPanel();
            Button use = new Button { Content = "Usar esta pasta", IsDefault = true, Background = Tone("Accent"), Foreground = Brushes.White };
            Button find = new Button { Content = "Procurar pasta", Margin = new Thickness(8, 0, 0, 0) };
            Button cancel = new Button { Content = "Cancelar", IsCancel = true, Margin = new Thickness(8, 0, 0, 0) };
            actions.Children.Add(use);
            actions.Children.Add(find);
            actions.Children.Add(cancel);
            body.Children.Add(actions);
            choice.Content = body;
            bool manual = false;
            use.Click += delegate { choice.DialogResult = true; };
            find.Click += delegate { manual = true; choice.DialogResult = true; };
            if (choice.ShowDialog() != true) return null;
            return manual ? ChooseFolder("Selecione a pasta que contém o backup do SpentZ.", suggested, false) : suggested;
        }

        private string ChooseFolder(string description, string initialFolder, bool allowNewFolder)
        {
            using (Forms.FolderBrowserDialog dialog = new Forms.FolderBrowserDialog())
            {
                dialog.Description = description;
                dialog.ShowNewFolderButton = allowNewFolder;
                if (Directory.Exists(initialFolder)) dialog.SelectedPath = initialFolder;
                FolderOwner owner = new FolderOwner(new WindowInteropHelper(window).Handle);
                return dialog.ShowDialog(owner) == Forms.DialogResult.OK ? dialog.SelectedPath : null;
            }
        }

        private sealed class FolderOwner : Forms.IWin32Window
        {
            internal FolderOwner(IntPtr handle) { Handle = handle; }
            public IntPtr Handle { get; private set; }
        }

        private void ShowBackupStatus(string message)
        {
            TextBlock status = Get<TextBlock>("BackupStatus");
            status.Text = message;
            status.Visibility = Visibility.Visible;
        }
    }
}
