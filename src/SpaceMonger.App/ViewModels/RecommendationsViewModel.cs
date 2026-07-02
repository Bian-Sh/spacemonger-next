using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SpaceMonger.App.Diagnostics;
using SpaceMonger.App.Converters;
using SpaceMonger.App.Localization;
using SpaceMonger.Core.Enums;
using SpaceMonger.Core.Models;
using SpaceMonger.Core.Services.Analysis;

namespace SpaceMonger.App.ViewModels;

public partial class RecommendationsViewModel : ObservableObject
{
    private readonly IRecommendationEngine _recommendationEngine;
    private readonly ILogger<RecommendationsViewModel> _logger;
    private ScanSession? _currentSession;
    private string? _apiKey;
    private string? _baseUrl;
    private string? _modelName;
    private bool _enableThinking;
    private string? _responseLanguage;
    private FileEntry? _focusEntry;

    [ObservableProperty]
    private ObservableCollection<CleanupRecommendation> _recommendations = new();

    [ObservableProperty]
    private ObservableCollection<CleanupRecommendation> _filteredRecommendations = new();

    [ObservableProperty]
    private object? _selectedCategoryFilter = string.Empty;

    [ObservableProperty]
    private object? _selectedSafetyFilter = string.Empty;

    [ObservableProperty]
    private string _totalRecoverableSpace = FileSizeConverter.FormatSize(0);

    [ObservableProperty]
    private int _totalSelectedCount;

    [ObservableProperty]
    private int _totalItemCount;

    [ObservableProperty]
    private bool? _allRecommendationsSelectionState = false;

    [ObservableProperty]
    private bool _isAnalyzing;

    [ObservableProperty]
    private bool _isWaitingForExternalRecommendations;

    [ObservableProperty]
    private string? _analysisError;

    [ObservableProperty]
    private bool _hasRecommendations;

    [ObservableProperty]
    private AnalysisDiagnostics? _lastDiagnostics;

    /// <summary>
    /// Returns true if any recommendation has been accepted by the user.
    /// Used to warn before re-running analysis that would replace accepted items.
    /// </summary>
    public bool HasAcceptedRecommendations =>
        Recommendations.Any(r => r.IsAccepted);

    public bool HasAnyRecommendations => Recommendations.Count > 0;

    public string EmptyStateTitleText => IsWaitingForExternalRecommendations
        ? L.Text("RecommendationsWaitingForAiTitle")
        : L.Text("RecommendationsEmptyTitle");

    public string EmptyStateDescriptionText => IsWaitingForExternalRecommendations
        ? string.Empty
        : L.Text("RecommendationsEmptyDescription");

    public RecommendationsViewModel(IRecommendationEngine recommendationEngine, ILogger<RecommendationsViewModel>? logger = null)
    {
        _recommendationEngine = recommendationEngine;
        _logger = logger ?? NullLogger<RecommendationsViewModel>.Instance;
        _logger.LogInformation("RecommendationsViewModel created");
        L.LanguageChanged += OnLanguageChanged;
    }

    partial void OnIsWaitingForExternalRecommendationsChanged(bool value)
    {
        OnPropertyChanged(nameof(EmptyStateTitleText));
        OnPropertyChanged(nameof(EmptyStateDescriptionText));
    }

    private void OnLanguageChanged()
    {
        OnPropertyChanged(nameof(EmptyStateTitleText));
        OnPropertyChanged(nameof(EmptyStateDescriptionText));
    }

    public void SetContext(ScanSession session, string apiKey, string? baseUrl, string? modelName, bool enableThinking, string? responseLanguage, FileEntry? focusEntry = null)
    {
        _currentSession = session;
        _apiKey = apiKey;
        _baseUrl = baseUrl;
        _modelName = modelName;
        _enableThinking = enableThinking;
        _responseLanguage = responseLanguage;
        _focusEntry = focusEntry;
        _logger.LogInformation("Recommendation context set: target={TargetPath}, focus={FocusPath}, model={ModelName}, thinking={EnableThinking}", session.TargetPath, focusEntry?.Path, modelName, enableThinking);
    }

    public void SetExternalRecommendations(IEnumerable<CleanupRecommendation> recommendations, AnalysisDiagnostics? diagnostics = null)
    {
        var indexedRecommendations = recommendations.ToList();
        for (var index = 0; index < indexedRecommendations.Count; index++)
        {
            if (string.IsNullOrWhiteSpace(indexedRecommendations[index].Id))
            {
                indexedRecommendations[index].Id = (index + 1).ToString();
            }
        }

        _logger.LogInformation("External recommendations applied: count={Count}", indexedRecommendations.Count);
        AnalysisError = null;
        IsWaitingForExternalRecommendations = false;
        LastDiagnostics = diagnostics;
        Recommendations = new ObservableCollection<CleanupRecommendation>(indexedRecommendations);
        ApplyFilters();
        UpdateTotals();
        HasRecommendations = Recommendations.Count > 0;
    }

    public void BeginExternalRecommendationLoad()
    {
        _logger.LogInformation("External recommendation load started");
        AnalysisError = null;
        IsWaitingForExternalRecommendations = true;
        HasRecommendations = false;
        Recommendations = new ObservableCollection<CleanupRecommendation>();
        ApplyFilters();
        UpdateTotals();
    }

    public void EndExternalRecommendationLoad()
    {
        _logger.LogInformation("External recommendation load ended");
        IsWaitingForExternalRecommendations = false;
    }

    [RelayCommand]
    private async Task AnalyzeAsync()
    {
        if (_currentSession is null || _apiKey is null)
            return;

        _logger.LogInformation("Recommendation analysis started: target={TargetPath}, focus={FocusPath}", _currentSession.TargetPath, _focusEntry?.Path);
        IsAnalyzing = true;
        IsWaitingForExternalRecommendations = false;
        AnalysisError = null;

        // Clear previous recommendations immediately so stale results from
        // a different scope (e.g. subfolder) don't persist if this call fails.
        Recommendations = new ObservableCollection<CleanupRecommendation>();
        ApplyFilters();
        UpdateTotals();

        try
        {
            DebugBreakpoints.Hit("recommendation-engine-before");
            var session = _currentSession;
            var apiKey = _apiKey;
            var baseUrl = _baseUrl;
            var modelName = _modelName;
            var enableThinking = _enableThinking;
            var responseLanguage = _responseLanguage;
            var focusEntry = _focusEntry;

            var result = await Task.Run(() => _recommendationEngine.AnalyzeWithDiagnosticsAsync(
                session, apiKey, baseUrl, modelName, enableThinking, responseLanguage, CancellationToken.None, focusEntry));
            DebugBreakpoints.Hit("recommendation-engine-after");

            LastDiagnostics = result.Diagnostics;
            Recommendations = new ObservableCollection<CleanupRecommendation>(result.Recommendations);
            ApplyFilters();
            UpdateTotals();
            HasRecommendations = Recommendations.Count > 0;
            _logger.LogInformation("Recommendation analysis completed: count={Count}", Recommendations.Count);
            DebugBreakpoints.Hit("recommendations-applied");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Recommendation analysis failed");
            AnalysisError = ex.Message;
            LastDiagnostics = null;
            HasRecommendations = false;
            DebugBreakpoints.Hit("recommendation-error");
        }
        finally
        {
            _logger.LogInformation("Recommendation analysis ended");
            IsAnalyzing = false;
        }
    }

    [RelayCommand]
    private void Accept(CleanupRecommendation rec)
    {
        _logger.LogInformation("Recommendation accepted: id={Id}, path={TargetPath}", rec.Id, rec.TargetPath);
        rec.IsAccepted = true;
        UpdateTotals();
    }

    [RelayCommand]
    private void Dismiss(CleanupRecommendation rec)
    {
        _logger.LogInformation("Recommendation dismissed: id={Id}, path={TargetPath}", rec.Id, rec.TargetPath);
        rec.IsDismissed = true;
        UpdateTotals();
    }

    [RelayCommand]
    private void SelectAllSafe()
    {
        foreach (var rec in Recommendations.Where(r => r.SafetyRating == SafetyRating.Safe))
        {
            _logger.LogInformation("Recommendation accepted: id={Id}, path={TargetPath}", rec.Id, rec.TargetPath);
        rec.IsAccepted = true;
        }

        UpdateTotals();
    }

    [RelayCommand]
    private void DeselectAllCaution()
    {
        foreach (var rec in Recommendations.Where(r => r.SafetyRating == SafetyRating.Caution))
        {
            _logger.LogInformation("Recommendation dismissed: id={Id}, path={TargetPath}", rec.Id, rec.TargetPath);
        rec.IsDismissed = true;
        }

        UpdateTotals();
    }

    [RelayCommand]
    private void ToggleAllRecommendations()
    {
        var shouldSelectAll = TotalItemCount == 0 || TotalSelectedCount < TotalItemCount;

        foreach (var rec in Recommendations)
        {
            rec.IsAccepted = shouldSelectAll;
        }

        UpdateTotals();
    }

    [RelayCommand]
    private void SelectAllInCategory(RecommendationCategory category)
    {
        foreach (var rec in Recommendations.Where(r => r.Category == category))
        {
            _logger.LogInformation("Recommendation accepted: id={Id}, path={TargetPath}", rec.Id, rec.TargetPath);
        rec.IsAccepted = true;
        }

        UpdateTotals();
    }

    [RelayCommand]
    private void DeselectAllInCategory(RecommendationCategory category)
    {
        foreach (var rec in Recommendations.Where(r => r.Category == category))
        {
            _logger.LogInformation("Recommendation dismissed: id={Id}, path={TargetPath}", rec.Id, rec.TargetPath);
        rec.IsDismissed = true;
        }

        UpdateTotals();
    }

    partial void OnSelectedCategoryFilterChanged(object? value)
    {
        ApplyFilters();
    }

    partial void OnSelectedSafetyFilterChanged(object? value)
    {
        ApplyFilters();
    }

    private void ApplyFilters()
    {
        var filtered = Recommendations.AsEnumerable();

        if (SelectedCategoryFilter is RecommendationCategory category)
        {
            filtered = filtered.Where(r => r.Category == category);
        }

        if (SelectedSafetyFilter is SafetyRating safetyRating)
        {
            filtered = filtered.Where(r => r.SafetyRating == safetyRating);
        }

        FilteredRecommendations = new ObservableCollection<CleanupRecommendation>(filtered);
    }

    public void RefreshAfterCleanup()
    {
        ApplyFilters();
        UpdateTotals();
    }

    public void RefreshFilteredRecommendations()
    {
        ApplyFilters();
        UpdateTotals();
    }

    public void UpdateTotals()
    {
        TotalSelectedCount = Recommendations.Count(r => r.IsAccepted);
        TotalItemCount = Recommendations.Count;
        TotalRecoverableSpace = FileSizeConverter.FormatSize(
            Recommendations.Where(r => r.IsAccepted).Sum(r => r.Size));
        AllRecommendationsSelectionState = TotalSelectedCount == 0
            ? false
            : TotalSelectedCount == TotalItemCount
                ? true
                : null;
    }
}
