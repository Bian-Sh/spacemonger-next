using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using SpaceMonger.App.Localization;
using SpaceMonger.App.ViewModels;
using SpaceMonger.Core.Models;

namespace SpaceMonger.App.Views;

public partial class ChatPanel : UserControl
{
    private ChatViewModel? _viewModel;

    public event Action? OpenSettingsRequested;

    public ChatPanel()
    {
        InitializeComponent();
        L.LanguageChanged += OnLanguageChanged;
    }

    public void SetViewModel(ChatViewModel vm)
    {
        // Unsubscribe from previous view model's collection if any
        if (_viewModel != null)
        {
            _viewModel.Messages.CollectionChanged -= Messages_CollectionChanged;
            foreach (var message in _viewModel.Messages)
            {
                message.PropertyChanged -= Message_PropertyChanged;
            }
        }

        _viewModel = vm;
        DataContext = vm;

        // Subscribe to collection changes for auto-scroll
        vm.Messages.CollectionChanged += Messages_CollectionChanged;
        foreach (var message in vm.Messages)
        {
            message.PropertyChanged += Message_PropertyChanged;
        }
    }


    public void FocusInput()
    {
        InputTextBox.Focus();
        Keyboard.Focus(InputTextBox);
        InputTextBox.CaretIndex = InputTextBox.Text?.Length ?? 0;
    }
    private void OnLanguageChanged()
    {
        Dispatcher.InvokeAsync(() =>
        {
            // Update all copy button tooltips
            UpdateCopyButtonTooltips();
        });
    }

    private void UpdateCopyButtonTooltips()
    {
        var tooltipText = L.Text("CopyMessageToolTip");
        
        // Find all copy buttons in the visual tree
        var stackPanel = FindChild<StackPanel>(MessagesItemsControl);
        if (stackPanel == null) return;

        foreach (var child in stackPanel.Children)
        {
            if (child is FrameworkElement element)
            {
                var aiCopyBorder = FindChildByName<Border>(element, "AiCopyButtonBorder");
                if (aiCopyBorder != null)
                {
                    ToolTipService.SetToolTip(aiCopyBorder, tooltipText);
                }

                var userCopyBorder = FindChildByName<Border>(element, "UserCopyButtonBorder");
                if (userCopyBorder != null)
                {
                    ToolTipService.SetToolTip(userCopyBorder, tooltipText);
                }
            }
        }
    }

    private void Messages_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems is not null)
        {
            foreach (ChatMessage message in e.OldItems)
            {
                message.PropertyChanged -= Message_PropertyChanged;
            }
        }

        if (e.NewItems is not null)
        {
            foreach (ChatMessage message in e.NewItems)
            {
                message.PropertyChanged += Message_PropertyChanged;
            }
        }

        if (e.Action == NotifyCollectionChangedAction.Add)
        {
            ScrollToBottom();
        }
    }

    private void Message_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ChatMessage.Text) or nameof(ChatMessage.Thinking))
        {
            ScrollToBottom();
        }
    }

    private void ScrollToBottom()
    {
        // Use Dispatcher to ensure layout has updated before scrolling
        Dispatcher.InvokeAsync(() =>
        {
            MessagesScrollViewer.ScrollToEnd();
        }, System.Windows.Threading.DispatcherPriority.Background);
    }

    private void InputTextBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (_viewModel?.IsCompletionMenuOpen == true)
        {
            if (e.Key == Key.Down)
            {
                _viewModel.MoveCompletionSelection(1);
                e.Handled = true;
                return;
            }

            if (e.Key == Key.Up)
            {
                _viewModel.MoveCompletionSelection(-1);
                e.Handled = true;
                return;
            }

            if (e.Key is Key.Tab or Key.Enter)
            {
                _viewModel.ConfirmActiveCompletion();
                InputTextBox.CaretIndex = InputTextBox.Text?.Length ?? 0;
                e.Handled = true;
                return;
            }

            if (e.Key == Key.Escape)
            {
                _viewModel.IsSlashCommandMenuOpen = false;
                _viewModel.IsSkillMentionMenuOpen = false;
                e.Handled = true;
                return;
            }
        }

        if (e.Key == Key.Enter && !Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
        {
            if (_viewModel?.SubmitOrStopCommand.CanExecute(null) == true)
            {
                _viewModel.SubmitOrStopCommand.Execute(null);
            }
            e.Handled = true;
        }
    }

    private void OpenSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        OpenSettingsRequested?.Invoke();
    }

    private void ClearContextButton_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel != null)
        {
            _viewModel.LinkedEntry = null;
            _viewModel.LinkedRecommendation = null;
            _viewModel.LinkedItemPath = null;
        }
    }

    private void ThinkingBorder_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement element && element.DataContext is ChatMessage message)
        {
            message.IsThinkingExpanded = !message.IsThinkingExpanded;
            e.Handled = true;
        }
    }

    private void BubbleBorder_MouseEnter(object sender, MouseEventArgs e)
    {
        if (sender is Border border)
        {
            // Find the copy button border in the visual tree
            var grid = FindChild<Grid>(border);
            if (grid != null)
            {
                                // Find AI action row
                var aiActions = FindChildByName<StackPanel>(grid, "AiMessageActions");
                if (aiActions != null && aiActions.Visibility == Visibility.Visible)
                {
                    aiActions.Opacity = 1;
                }

                // Find User copy button
                var userCopyBorder = FindChildByName<Border>(grid, "UserCopyButtonBorder");
                if (userCopyBorder != null && userCopyBorder.Visibility == Visibility.Visible)
                {
                    userCopyBorder.Opacity = 1;
                }
            }
        }
    }

    private void BubbleBorder_MouseLeave(object sender, MouseEventArgs e)
    {
        if (sender is Border border)
        {
            var grid = FindChild<Grid>(border);
            if (grid != null)
            {
                var aiActions = FindChildByName<StackPanel>(grid, "AiMessageActions");
                if (aiActions != null)
                {
                    aiActions.Opacity = 0;
                }

                var userCopyBorder = FindChildByName<Border>(grid, "UserCopyButtonBorder");
                if (userCopyBorder != null)
                {
                    userCopyBorder.Opacity = 0;
                }
            }
        }
    }

    private void CopyButton_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is Border border && border.DataContext is ChatMessage message)
        {
            var textToCopy = message.Text;
            if (!string.IsNullOrEmpty(textToCopy))
            {
                try
                {
                    Clipboard.SetText(textToCopy);
                    
                    // Visual feedback - briefly brighten the icon
                    var icon = FindChild<Path>(border);
                    if (icon != null)
                    {
                        var originalOpacity = icon.Opacity;
                        icon.Opacity = 1;
                        icon.Stroke = new SolidColorBrush(Color.FromRgb(100, 200, 255)); // Light blue flash
                        
                        var timer = new System.Windows.Threading.DispatcherTimer
                        {
                            Interval = TimeSpan.FromMilliseconds(300)
                        };
                        timer.Tick += (s, args) =>
                        {
                            icon.Opacity = originalOpacity;
                            icon.Stroke = new SolidColorBrush(Colors.White);
                            timer.Stop();
                        };
                        timer.Start();
                    }
                }
                catch
                {
                    // Clipboard might be unavailable
                }
            }
            e.Handled = true;
        }
    }

    private void CopyButton_MouseEnter(object sender, MouseEventArgs e)
    {
        if (sender is Border border)
        {
            var icon = FindChild<Path>(border);
            if (icon != null)
            {
                icon.Opacity = 1; // Full brightness on hover
            }
        }
    }

    private void CopyButton_MouseLeave(object sender, MouseEventArgs e)
    {
        if (sender is Border border)
        {
            var icon = FindChild<Path>(border);
            if (icon != null)
            {
                icon.Opacity = 0.6; // Back to default
            }
        }
    }

    private void FlowDocumentScrollViewer_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        // Forward the mouse wheel event to the parent ScrollViewer
        if (sender is FlowDocumentScrollViewer)
        {
            var parentScrollViewer = FindParent<ScrollViewer>((DependencyObject)sender);
            if (parentScrollViewer != null)
            {
                parentScrollViewer.ScrollToVerticalOffset(parentScrollViewer.VerticalOffset - e.Delta);
                e.Handled = true;
            }
        }
    }

    private static T? FindParent<T>(DependencyObject child) where T : DependencyObject
    {
        var parent = VisualTreeHelper.GetParent(child);
        while (parent != null)
        {
            if (parent is T typedParent)
                return typedParent;
            parent = VisualTreeHelper.GetParent(parent);
        }
        return null;
    }

    private static T? FindChild<T>(DependencyObject parent) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T typedChild)
                return typedChild;

            var result = FindChild<T>(child);
            if (result != null)
                return result;
        }
        return null;
    }

    private static T? FindChildByName<T>(DependencyObject parent, string name) where T : FrameworkElement
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T typedChild && typedChild.Name == name)
                return typedChild;

            var result = FindChildByName<T>(child, name);
            if (result != null)
                return result;
        }
        return null;
    }
}


