using FiatMedica.Domain;
using FiatMedica.Services;
using Microsoft.Win32;
using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;

namespace FiatMedicaWPF;

public partial class MainWindow : Window
{
    public ObservableCollection<ChatSession> AllSessions { get; } = new();
    public ObservableCollection<ChatMessage> ChatMessages { get; } = new();

    private ChatSession? _activeSession;
    private readonly ILlmEngine _llmEngine;
    private CancellationTokenSource? _cancellationTokenSource;
    private bool _isModelLoading;
    private bool _isGenerating;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = this;

        // Initialize the LLM engine with settings
        var settings = new LlmSettings();
        _llmEngine = new LlamaSharpEngine(settings);

        // Keep active sessions first, then newest first
        var view = CollectionViewSource.GetDefaultView(AllSessions);
        view.SortDescriptions.Add(new SortDescription(nameof(ChatSession.IsActive), ListSortDirection.Descending));
        view.SortDescriptions.Add(new SortDescription(nameof(ChatSession.StartedAt), ListSortDirection.Descending));

        // Initialize the model on startup
        _ = InitializeModelAsync();
    }

    private async Task InitializeModelAsync()
    {
        if (_isModelLoading || _llmEngine.IsLoaded) return;

        _isModelLoading = true;
        ChatTitleText.Text = "Loading model...";

        try
        {
            await _llmEngine.InitializeAsync();
            ChatTitleText.Text = "Model loaded - (No session open)";
            MessageBox.Show("LLM model loaded successfully!", "Ready",
                          MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            ChatTitleText.Text = "Model failed to load";
            MessageBox.Show($"Failed to load model: {ex.Message}", "Error",
                          MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _isModelLoading = false;
        }
    }

    private void NewSession_Click(object sender, RoutedEventArgs e)
    {
        if (!_llmEngine.IsLoaded)
        {
            MessageBox.Show("Please wait for the model to load first.", "Model Loading",
                          MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

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

        // Reset the conversation for the new session
        _llmEngine.ResetConversation();
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

        // Cancel any ongoing generation
        _cancellationTokenSource?.Cancel();

        _activeSession.IsActive = false;
        _activeSession.EndedAt = DateTime.Now;

        CollectionViewSource.GetDefaultView(AllSessions).Refresh();

        _activeSession = null;
        ChatTitleText.Text = _llmEngine.IsLoaded ? "Model loaded - (No session open)" : "(No session open)";
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

    private async void SendInputIfAny()
    {
        if (_activeSession is null)
        {
            MessageBox.Show("Open a active session first.", "No active session",
                            MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (!_llmEngine.IsLoaded)
        {
            MessageBox.Show("Model is still loading. Please wait.", "Model Loading",
                          MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (_isGenerating)
        {
            MessageBox.Show("Please wait for the current response to complete.", "Generating",
                          MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var text = ChatInputTextBox.Text?.Trim();
        if (string.IsNullOrEmpty(text)) return;

        // Add user message
        ChatMessages.Add(new ChatMessage
        {
            IsUser = true,
            Text = text,
            Timestamp = DateTime.Now
        });

        ChatInputTextBox.Clear();

        // Prepare assistant message placeholder
        var assistantMessage = new ChatMessage
        {
            IsUser = false,
            Text = "",
            Timestamp = DateTime.Now
        };
        ChatMessages.Add(assistantMessage);

        // Scroll to bottom
        await Dispatcher.InvokeAsync(() =>
        {
            if (ChatItemsControl.Items.Count > 0)
            {
                var scrollViewer = FindScrollViewer(ChatItemsControl);
                scrollViewer?.ScrollToEnd();
            }
        }, System.Windows.Threading.DispatcherPriority.Background);

        // Stream the response
        _isGenerating = true;
        _cancellationTokenSource = new CancellationTokenSource();

        try
        {
            await foreach (var token in _llmEngine.StreamChatAsync(text, _cancellationTokenSource.Token))
            {
                assistantMessage.Text += token;

                // Periodically scroll to bottom during generation
                await Dispatcher.InvokeAsync(() =>
                {
                    var scrollViewer = FindScrollViewer(ChatItemsControl);
                    scrollViewer?.ScrollToEnd();
                }, System.Windows.Threading.DispatcherPriority.Background);
            }
        }
        catch (OperationCanceledException)
        {
            assistantMessage.Text += "\n[Generation cancelled]";
        }
        catch (Exception ex)
        {
            assistantMessage.Text += $"\n[Error: {ex.Message}]";
        }
        finally
        {
            _isGenerating = false;
            _cancellationTokenSource?.Dispose();
            _cancellationTokenSource = null;
        }
    }

    private static ScrollViewer? FindScrollViewer(DependencyObject obj)
    {
        if (obj is ScrollViewer sv) return sv;

        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(obj); i++)
        {
            var child = VisualTreeHelper.GetChild(obj, i);
            var result = FindScrollViewer(child);
            if (result != null) return result;
        }

        return null;
    }

    protected override async void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);

        // Cancel any ongoing generation
        _cancellationTokenSource?.Cancel();

        // Dispose the LLM engine
        if (_llmEngine is IAsyncDisposable asyncDisposable)
        {
            await asyncDisposable.DisposeAsync();
        }
    }
}

public class ChatSession
{
    public string Id { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public bool IsActive { get; set; }
    public DateTime StartedAt { get; set; }
    public DateTime? EndedAt { get; set; }
}

public class ChatMessage : INotifyPropertyChanged
{
    private string _text = string.Empty;

    public bool IsUser { get; set; }

    public string Text
    {
        get => _text;
        set
        {
            if (_text != value)
            {
                _text = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Text)));
            }
        }
    }

    public DateTime Timestamp { get; set; }

    public event PropertyChangedEventHandler? PropertyChanged;
}

// Converters for XAML bindings
public class BoolToTextConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
    {
        if (value is bool b && parameter is string s)
        {
            var parts = s.Split('|');
            return b ? parts[0] : (parts.Length > 1 ? parts[1] : "");
        }
        return "";
    }

    public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
        => throw new NotImplementedException();
}

public class BoolToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
    {
        if (value is bool b && parameter is string s)
        {
            var parts = s.Split('|');
            var color = b ? parts[0] : (parts.Length > 1 ? parts[1] : "#FFFFFF");
            return new System.Windows.Media.SolidColorBrush(
                (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(color));
        }
        return System.Windows.Media.Brushes.White;
    }

    public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
        => throw new NotImplementedException();
}

public class BoolToOpacityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
    {
        if (value is bool b && parameter is string s)
        {
            var parts = s.Split('|');
            return b ? double.Parse(parts[0]) : (parts.Length > 1 ? double.Parse(parts[1]) : 1.0);
        }
        return 1.0;
    }

    public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
        => throw new NotImplementedException();
}