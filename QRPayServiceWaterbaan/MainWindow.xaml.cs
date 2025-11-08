using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media.Imaging;
using QRCoder;
using IOPath = System.IO.Path;
using MahApps.Metro.Controls;

namespace QRPayServiceWaterbaan
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : MetroWindow
    {
        private static readonly HttpClient httpClient = new();
        private string? _lastEpcContent;

        public MainWindow()
        {
            InitializeComponent();
            // Stel icoon in via code (vermijdt XAML parse issues met sommige ico formaten)
            try
            {
                Icon = new BitmapImage(new Uri("pack://application:,,,/Assets/app.ico"));
            }
            catch { /* negeren indien faalt */ }
        }

        private void UpdateButtonsEnabled()
        {
            var valid = ValidateInputs();
            GenereerButton.IsEnabled = valid;
            LaadVanLijstButton.IsEnabled = valid;

            var hasQr = QrImage?.Source != null && _lastEpcContent != null;
            CopyQrButton.IsEnabled = hasQr;
            SaveQrButton.IsEnabled = hasQr;
        }

        private bool ValidateInputs()
        {
            var name = NameTextBox.Text?.Trim();
            var iban = IbanTextBox.Text?.ToUpper().Trim().Replace(" ", string.Empty).Replace(".", string.Empty);
            var amount = AmountTextBox.Text?.Trim();

            if (string.IsNullOrWhiteSpace(name)) return false;
            if (string.IsNullOrWhiteSpace(iban)) return false;
            if (!IsLikelyIban(iban)) return false;
            if (string.IsNullOrWhiteSpace(amount)) return false;

            return double.TryParse(amount, NumberStyles.Number, CultureInfo.CurrentCulture, out _);
        }

        private static bool IsLikelyIban(string? iban)
        {
            if (string.IsNullOrWhiteSpace(iban)) return false;
            // Basic IBAN pattern: 2 letters country + 2 digits check + rest 11-30 alnum
            return Regex.IsMatch(iban, "^[A-Z]{2}[0-9]{2}[A-Z0-9]{11,30}$");
        }

        private void Input_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
        {
            UpdateButtonsEnabled();
            StatusTextBlock.Text = "Wijzigingen niet opgeslagen";
        }

        private async void GenereerButton_Click(object sender, RoutedEventArgs e)
        {
            var name = NameTextBox.Text?.Trim() ?? string.Empty;
            var iban = IbanTextBox.Text?.ToUpper().Trim().Replace(" ", "").Replace(".", "") ?? string.Empty;
            var amount = AmountTextBox.Text?.Trim() ?? string.Empty;
            var remark = RemarkTextBox.Text?.Trim() ?? string.Empty;

            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(iban) || string.IsNullOrWhiteSpace(amount))
            {
                System.Windows.MessageBox.Show("Vul naam, IBAN en bedrag in.", "Validatie", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            string amountpa;
            try
            {
                amountpa = double.Parse(amount, CultureInfo.CurrentCulture).ToString("F2", CultureInfo.InvariantCulture);
            }
            catch (Exception)
            {
                System.Windows.MessageBox.Show("Geen geldig bedrag");
                return;
            }

            string epc = BuildEpcString(name, iban, amountpa, remark);
            _lastEpcContent = epc;
            RenderQr(epc);
            StatusTextBlock.Text = "QR-code gegenereerd";
            await System.Threading.Tasks.Task.Yield();
            UpdateButtonsEnabled();
        }

        private string BuildEpcString(string name, string iban, string amountPa, string remark)
        {
            return $"BCD{Environment.NewLine}002{Environment.NewLine}1{Environment.NewLine}SCT{Environment.NewLine}{Environment.NewLine}{name}{Environment.NewLine}{iban}{Environment.NewLine}EUR{amountPa}{Environment.NewLine}{Environment.NewLine}{Environment.NewLine}{remark}";
        }

        private void RenderQr(string content)
        {
            using var qrGenerator = new QRCodeGenerator();
            using var qrData = qrGenerator.CreateQrCode(content, QRCodeGenerator.ECCLevel.M);
            using var qrCode = new PngByteQRCode(qrData);
            var qrBytes = qrCode.GetGraphic(20);

            var bitmap = new BitmapImage();
            using var ms = new MemoryStream(qrBytes);
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.StreamSource = ms;
            bitmap.EndInit();
            bitmap.Freeze();

            QrImage.Source = bitmap;
        }

        private async void LaadVanLijstButton_Click(object sender, RoutedEventArgs e)
        {
            string msg = "Dit zal een reeks qr codes in een folder plaatsen. Iedere qrcode zal zijn opmerking steeds aangevuld hebben met de volgende lijn tekst in het tekstbestand. ";
            System.Windows.MessageBox.Show(msg, "Ter info", MessageBoxButton.OK, MessageBoxImage.Information);

            var name = NameTextBox.Text?.Trim() ?? string.Empty;
            var iban = IbanTextBox.Text?.ToUpper().Trim().Replace(" ", "").Replace(".", "") ?? string.Empty;
            var amount = AmountTextBox.Text?.Trim() ?? string.Empty;

            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(iban) || string.IsNullOrWhiteSpace(amount))
            {
                System.Windows.MessageBox.Show("Vul naam, IBAN en bedrag in voor batch generatie.", "Validatie", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            string amountpa;
            try
            {
                amountpa = double.Parse(amount, CultureInfo.CurrentCulture).ToString("F2", CultureInfo.InvariantCulture);
            }
            catch
            {
                System.Windows.MessageBox.Show("Geen geldig bedrag", "Validatie", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            Microsoft.Win32.OpenFileDialog openDlg = new()
            {
                Title = "Selecteer tekstbestand met opmerkingen",
                Filter = "Text bestanden (*.txt)|*.txt|Alle bestanden (*.*)|*.*"
            };
            if (openDlg.ShowDialog(this) != true) return;

            var saveFolder = SelectFolder();
            if (string.IsNullOrEmpty(saveFolder)) return;

            string[] lines;
            try
            {
                lines = File.ReadAllLines(openDlg.FileName)
                             .Select(l => l.Trim())
                             .Where(l => !string.IsNullOrWhiteSpace(l))
                             .Distinct()
                             .ToArray();
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show($"Kon bestand niet lezen: {ex.Message}", "Fout", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            if (lines.Length == 0)
            {
                System.Windows.MessageBox.Show("Geen geldige regels gevonden.", "Info", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            try
            {
                GenereerButton.IsEnabled = false;
                LaadVanLijstButton.IsEnabled = false;
                BatchProgressBar.Visibility = Visibility.Visible;
                BatchProgressBar.Minimum = 0;
                BatchProgressBar.Maximum = lines.Length;
                BatchProgressBar.Value = 0;
                StatusTextBlock.Text = "Bezig met batch-generatie...";

                int saved = 0;
                for (int i = 0; i < lines.Length; i++)
                {
                    var line = lines[i];
                    var epc = BuildEpcString(name, iban, amountpa, line);
                    try
                    {
                        var fileName = SanitizeFileName(line);
                        var path = IOPath.Combine(saveFolder, fileName + ".png");
                        SaveQrToFile(epc, path);
                        saved++;
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine(ex.Message);
                    }

                    BatchProgressBar.Value = i + 1;
                    StatusTextBlock.Text = $"Opslaan... {i + 1}/{lines.Length}";
                    await System.Threading.Tasks.Task.Yield();
                }

                System.Windows.MessageBox.Show($"Klaar. {saved} QR-codes opgeslagen in: {saveFolder}", "Batch", MessageBoxButton.OK, MessageBoxImage.Information);
                StatusTextBlock.Text = $"Klaar: {saved} QR-codes opgeslagen";
            }
            finally
            {
                BatchProgressBar.Visibility = Visibility.Collapsed;
                UpdateButtonsEnabled();
            }
        }

        private static string SanitizeFileName(string raw)
        {
            var invalid = IOPath.GetInvalidFileNameChars();
            var sb = new StringBuilder();
            foreach (var c in raw)
            {
                sb.Append(invalid.Contains(c) ? '_' : c);
            }
            var result = sb.ToString().Trim();
            return string.IsNullOrEmpty(result) ? "qr" : result;
        }

        private void SaveQrToFile(string content, string path)
        {
            using var qrGenerator = new QRCodeGenerator();
            using var qrData = qrGenerator.CreateQrCode(content, QRCodeGenerator.ECCLevel.M);
            using var qrCode = new PngByteQRCode(qrData);
            var qrBytes = qrCode.GetGraphic(20);
            File.WriteAllBytes(path, qrBytes);
        }

        private string? SelectFolder()
        {
            using var dialog = new System.Windows.Forms.FolderBrowserDialog
            {
                Description = "Kies doelmap voor QR codes",
                ShowNewFolderButton = true
            };
            var result = dialog.ShowDialog();
            return result == System.Windows.Forms.DialogResult.OK ? dialog.SelectedPath : null;
        }

        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            // Voorbeelddata, enkel voor initiële load
            IbanTextBox.Text = "be35001286446837";
            NameTextBox.Text = "Tim";
            AmountTextBox.Text = "200";
            RemarkTextBox.Text = "TESTJE";
            StatusTextBlock.Text = "Klaar";
            UpdateButtonsEnabled();
        }

        private void HelpButton_Click(object sender, RoutedEventArgs e)
        {
            var helpText = "Bulk EPC QR Code generator\n\n" +
                           "Met dit programma kan je één of meerdere QR codes genereren voor SEPA overschrijvingen volgens het EPC formaat.\n\nVolgende banken zijn compatibel hiermee: Argenta, ASN, Belfius, Bunq, BNP Paribas Fortis, ING, KBC, Knab, SNS en VDK\n\n" +
                           "1. Vul Naam, IBAN en Bedrag in.\n" +
                           "2. Optioneel: voeg een Opmerking toe.\n" +
                           "3. Klik 'Genereer enkele qr code' om een enkele code te maken.\n" +
                           "4. Voor bulk: klik 'Laad van lijst' en kies een tekstbestand met opmerkingen (één per lijn).\n" +
                           "5. Kies een doelmap; elke lijn wordt een aparte QR code (PNG).\n\n" +
                           "Tip: Gebruik de knoppen boven de preview om de QR te kopiëren of op te slaan.";
            System.Windows.MessageBox.Show(helpText, "Help", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private void CopyQrButton_Click(object sender, RoutedEventArgs e)
        {
            if (QrImage.Source is BitmapSource bmp)
            {
                System.Windows.Clipboard.SetImage(bmp);
                StatusTextBlock.Text = "QR naar klembord gekopieerd";
            }
        }

        private void SaveQrButton_Click(object sender, RoutedEventArgs e)
        {
            if (_lastEpcContent == null)
            {
                System.Windows.MessageBox.Show("Geen QR om op te slaan.");
                return;
            }

            var sfd = new Microsoft.Win32.SaveFileDialog
            {
                Title = "Bewaar QR-code",
                Filter = "PNG Image (*.png)|*.png",
                FileName = "qr.png"
            };
            if (sfd.ShowDialog(this) == true)
            {
                try
                {
                    SaveQrToFile(_lastEpcContent, sfd.FileName);
                    StatusTextBlock.Text = $"Opgeslagen: {sfd.FileName}";
                }
                catch (Exception ex)
                {
                    System.Windows.MessageBox.Show($"Kon niet opslaan: {ex.Message}", "Fout", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }
    }
}