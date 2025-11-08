using Microsoft.Win32;
using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;

namespace FiatMedicaWPF
{
    public partial class MainWindow : Window
    {
        public ObservableCollection<ChatSession> AllSessions { get; } = new();
        public ObservableCollection<ChatMessage> ChatMessages { get; } = new();

        private ChatSession? _activeSession;

        public MainWindow()
        {
            InitializeComponent();
            DataContext = this;

            // Keep active sessions first, then newest first
            var view = CollectionViewSource.GetDefaultView(AllSessions);
            view.SortDescriptions.Add(new SortDescription(nameof(ChatSession.IsActive), ListSortDirection.Descending));
            view.SortDescriptions.Add(new SortDescription(nameof(ChatSession.StartedAt), ListSortDirection.Descending));
        }

        private void NewSession_Click(object sender, RoutedEventArgs e)
        {
            var session = new ChatSession
            {
                Id = Guid.NewGuid().ToString(),
                Title = $"New Session {DateTime.Now:HH:mm}",
                IsActive = true,
                StartedAt = DateTime.Now
            };

            AllSessions.Add(session);
            SessionsComboBox.SelectedItem = session;
            OpenSession(session);
        }

        private void SessionsComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (SessionsComboBox.SelectedItem is not ChatSession s) return;

            if (!s.IsActive)
            {
                MessageBox.Show("Previous sessions are immutable and cannot be reopened.",
                                "Read-only", MessageBoxButton.OK, MessageBoxImage.Information);

                // revert to active (if any) or clear selection
                SessionsComboBox.SelectedItem = _activeSession ?? null;
                return;
            }

            OpenSession(s);
        }

        private void UploadPdfs_Click(object sender, RoutedEventArgs e)
        {
            if (_activeSession is null)
            {
                MessageBox.Show("Open a active session before uploading PDFs.", "No active session",
                                MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var dlg = new OpenFileDialog
            {
                Filter = "PDF Files|*.pdf",
                Multiselect = true,
                Title = "Select PDF(s) to add to RAG"
            };

            if (dlg.ShowDialog() == true)
            {
                foreach (var f in dlg.FileNames)
                {
                    ChatMessages.Add(new ChatMessage
                    {
                        IsUser = false,
                        Text = $"📎 PDF queued for RAG: {System.IO.Path.GetFileName(f)}",
                        Timestamp = DateTime.Now
                    });
                }
            }
        }

        private void EndChat_Click(object sender, RoutedEventArgs e)
        {
            if (_activeSession is null)
            {
                MessageBox.Show("No active session to end.", "End Chat",
                                MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            _activeSession.IsActive = false;
            _activeSession.EndedAt = DateTime.Now;

            CollectionViewSource.GetDefaultView(AllSessions).Refresh();

            _activeSession = null;
            ChatTitleText.Text = "(No session open)";
            ChatMessages.Clear();

            SessionsComboBox.SelectedIndex = -1;
        }

        private void SendMessage_Click(object sender, RoutedEventArgs e) => SendInputIfAny();

        private void ChatInputTextBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter && Keyboard.Modifiers != ModifierKeys.Shift)
            {
                e.Handled = true;
                SendInputIfAny();
            }
        }

        private void OpenSession(ChatSession session)
        {
            _activeSession = session;
            ChatTitleText.Text = session.Title;

            ChatMessages.Clear();
            ChatMessages.Add(new ChatMessage
            {
                IsUser = false,
                Text = "Session opened. You can start chatting.",
                Timestamp = DateTime.Now
            });
        }

        private void SendInputIfAny()
        {
            if (_activeSession is null)
            {
                MessageBox.Show("Open a active session first.", "No active session",
                                MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var text = ChatInputTextBox.Text?.Trim();
            if (string.IsNullOrEmpty(text)) return;

            ChatMessages.Add(new ChatMessage
            {
                IsUser = true,
                Text = text,
                Timestamp = DateTime.Now
            });

            ChatInputTextBox.Clear();

            ChatMessages.Add(new ChatMessage
            {
                IsUser = false,
                Text = "(assistant reply placeholder)",
                Timestamp = DateTime.Now
            });
        }
    }

    // ===== Models =====
    public class ChatSession
    {
        public string Id { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
        public bool IsActive { get; set; }
        public DateTime StartedAt { get; set; }
        public DateTime? EndedAt { get; set; }
    }

    public class ChatMessage
    {
        public bool IsUser { get; set; }
        public string Text { get; set; } = string.Empty;
        public DateTime Timestamp { get; set; }
    }

    // ===== Converters (null-safe) =====

    // bool -> "TrueText|FalseText"
    public class BoolToTextConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            var parts = (parameter as string)?.Split('|');
            var t = (parts is { Length: 2 } ? parts[0] : "True")!;
            var f = (parts is { Length: 2 } ? parts[1] : "False")!;
            return (value is bool b && b) ? t : f;
        }
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => DependencyProperty.UnsetValue; // never used
    }

    // bool -> Brush using "#True|#False"
    public class BoolToBrushConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            var parts = (parameter as string)?.Split('|');
            var t = (parts is { Length: 2 } ? parts[0] : "#FFFFFFFF")!;
            var f = (parts is { Length: 2 } ? parts[1] : "#FFFFFFFF")!;
            var hex = (value is bool b && b) ? t : f;

            var obj = new BrushConverter().ConvertFromString(hex);
            return obj as Brush ?? Brushes.Transparent; // non-null return
        }
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => DependencyProperty.UnsetValue;
    }

    // bool -> double opacity via "trueOpacity|falseOpacity"
    public class BoolToOpacityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            var parts = (parameter as string)?.Split('|');
            var tStr = (parts is { Length: 2 } ? parts[0] : "1")!;
            var fStr = (parts is { Length: 2 } ? parts[1] : "0.6")!;

            var t = double.TryParse(tStr, out var td) ? td : 1.0;
            var f = double.TryParse(fStr, out var fd) ? fd : 0.6;

            return (value is bool b && b) ? t : f;
        }
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => DependencyProperty.UnsetValue;
    }
}