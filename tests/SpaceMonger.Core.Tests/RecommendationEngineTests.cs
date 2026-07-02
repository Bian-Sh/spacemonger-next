using FluentAssertions;
using NSubstitute;
using SpaceMonger.Core.Enums;
using SpaceMonger.Core.Models;
using SpaceMonger.Core.Services.Analysis;
using SpaceMonger.Core.Services.Llm;
using SpaceMonger.Core.Services.Settings;
using System.Net;
using System.Text.Json;

namespace SpaceMonger.Core.Tests;

public class RecommendationEngineTests
{
    [Fact]
    public async Task AnalyzeWithDiagnosticsAsync_ParsesJsonFromUnclosedMarkdownFence()
    {
        var targetPath = @"C:\Users\BianShanghai\AppData\Local\npm-cache\_cacache";
        var root = new FileEntry
        {
            Path = @"C:\",
            Name = @"C:\",
            IsDirectory = true,
            Children =
            [
                new FileEntry
                {
                    Path = targetPath,
                    Name = "_cacache",
                    IsDirectory = true,
                    Size = 2_028_726_426,
                }
            ]
        };

        var session = new ScanSession
        {
            TargetPath = @"C:\",
            RootEntry = root,
        };

        var llmClient = Substitute.For<ILlmClient>();
        llmClient.SendAnalysisAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns("""
                ```json
                {
                  "recommendations": [
                    {
                      "path": "C:\\Users\\BianShanghai\\AppData\\Local\\npm-cache\\_cacache",
                      "size_bytes": 2028726426,
                      "category": "PackageManagerCache",
                      "safety_rating": "Safe",
                      "explanation": "npm cache directory containing downloaded package artifacts."
                    }
                  ]
                }
                """);

        var engine = new RecommendationEngine(llmClient, Substitute.For<IDuplicateDetector>());

        var result = await engine.AnalyzeWithDiagnosticsAsync(session, "api-key", null, null, false, "zh-CN", CancellationToken.None);

        result.Diagnostics.ParseError.Should().BeNull();
        result.Diagnostics.ExtractedJsonLength.Should().BeGreaterThan(0);
        result.Diagnostics.ParsedRecommendationCount.Should().Be(1);
        result.Recommendations.Should().ContainSingle().Which.Should().Match<CleanupRecommendation>(r =>
            r.TargetPath == targetPath &&
            r.Category == RecommendationCategory.PackageManagerCache &&
            r.SafetyRating == SafetyRating.Safe);
    }

    [Fact]
    public async Task SendAnalysisAsync_UsesDeepSeekProAndDisablesThinkingForDeepSeekAnthropicEndpoint()
    {
        var handler = new CapturingHandler("""
            {
              "content": [
                { "type": "text", "text": "{\"recommendations\":[]}" }
              ],
              "stop_reason": "end_turn"
            }
            """);
        var httpClientFactory = Substitute.For<IHttpClientFactory>();
        httpClientFactory.CreateClient("Anthropic").Returns(new HttpClient(handler));
        var client = new AnthropicClient(httpClientFactory);

        await client.SendAnalysisAsync("system", "metadata", "key", "https://api.deepseek.com/anthropic", null, true, CancellationToken.None);

        handler.RequestUri.Should().Be("https://api.deepseek.com/anthropic/v1/messages");
        using var requestJson = JsonDocument.Parse(handler.RequestBody);
        var root = requestJson.RootElement;
        root.GetProperty("model").GetString().Should().Be("deepseek-v4-pro");
        root.GetProperty("thinking").GetProperty("type").GetString().Should().Be("disabled");
    }

    [Fact]
    public async Task AnalyzeWithDiagnosticsAsync_AllowsWindowsAdjacentPathButDowngradesSafety()
    {
        var targetPath = @"C:\Windows\assembly\temp";
        var root = CreateRootWithDirectory(targetPath, "temp", 87_258_544);
        var session = new ScanSession
        {
            TargetPath = @"C:\",
            RootEntry = root,
        };

        var llmClient = Substitute.For<ILlmClient>();
        llmClient.SendAnalysisAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(CreateRecommendationResponse(targetPath, 87_258_544, "TemporaryFiles", "Safe"));

        var engine = new RecommendationEngine(llmClient, Substitute.For<IDuplicateDetector>());

        var result = await engine.AnalyzeWithDiagnosticsAsync(session, "api-key", null, null, false, "zh-CN", CancellationToken.None);

        result.Diagnostics.ParsedRecommendationCount.Should().Be(1);
        result.Diagnostics.ProtectedFilteredCount.Should().Be(0);
        result.Recommendations.Should().ContainSingle().Which.Should().Match<CleanupRecommendation>(r =>
            r.TargetPath == targetPath &&
            r.SafetyRating == SafetyRating.ReviewFirst &&
            r.Explanation.Contains("System-adjacent location"));
    }

    [Fact]
    public async Task AnalyzeWithDiagnosticsAsync_AllowsUnknownWindowsSubfolderButDowngradesSafety()
    {
        var targetPath = @"C:\Windows\VendorApp\Cache";
        var root = CreateRootWithDirectory(targetPath, "Cache", 512_000_000);
        var session = new ScanSession
        {
            TargetPath = @"C:\",
            RootEntry = root,
        };

        var llmClient = Substitute.For<ILlmClient>();
        llmClient.SendAnalysisAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(CreateRecommendationResponse(targetPath, 512_000_000, "Other", "Safe"));

        var engine = new RecommendationEngine(llmClient, Substitute.For<IDuplicateDetector>());

        var result = await engine.AnalyzeWithDiagnosticsAsync(session, "api-key", null, null, false, "zh-CN", CancellationToken.None);

        result.Diagnostics.ParsedRecommendationCount.Should().Be(1);
        result.Diagnostics.ProtectedFilteredCount.Should().Be(0);
        result.Recommendations.Should().ContainSingle().Which.Should().Match<CleanupRecommendation>(r =>
            r.TargetPath == targetPath &&
            r.SafetyRating == SafetyRating.ReviewFirst &&
            r.Explanation.Contains("System-adjacent location"));
    }

    [Fact]
    public async Task AnalyzeWithDiagnosticsAsync_FiltersHardProtectedWindowsPathInFullDriveAnalysis()
    {
        var targetPath = @"C:\Windows\System32\kernel32.dll";
        var root = CreateRootWithDirectory(targetPath, "kernel32.dll", 1_048_576);
        var session = new ScanSession
        {
            TargetPath = @"C:\",
            RootEntry = root,
        };

        var llmClient = Substitute.For<ILlmClient>();
        llmClient.SendAnalysisAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(CreateRecommendationResponse(targetPath, 1_048_576, "Other", "Safe"));

        var engine = new RecommendationEngine(llmClient, Substitute.For<IDuplicateDetector>());

        var result = await engine.AnalyzeWithDiagnosticsAsync(session, "api-key", null, null, false, "zh-CN", CancellationToken.None);

        result.Diagnostics.ParsedRecommendationCount.Should().Be(1);
        result.Diagnostics.ProtectedFilteredCount.Should().Be(1);
        result.Recommendations.Should().BeEmpty();
    }


    [Fact]
    public async Task AnalyzeWithDiagnosticsAsync_AddsUnityLibraryRecommendationWhenProjectMarkersExist()
    {
        var root = new FileEntry { Path = @"D:\", Name = @"D:\", IsDirectory = true };
        var project = new FileEntry { Path = @"D:\Game", Name = "Game", IsDirectory = true };
        var library = new FileEntry
        {
            Path = @"D:\Game\Library",
            Name = "Library",
            IsDirectory = true,
            Size = 2_147_483_648,
            LastModified = DateTime.Now.AddYears(-2)
        };
        AddChild(root, project);
        AddChild(project, new FileEntry { Path = @"D:\Game\Assets", Name = "Assets", IsDirectory = true });
        AddChild(project, new FileEntry { Path = @"D:\Game\ProjectSettings", Name = "ProjectSettings", IsDirectory = true });
        AddChild(project, library);

        var session = new ScanSession { TargetPath = @"D:\", RootEntry = root };
        var llmClient = Substitute.For<ILlmClient>();
        llmClient.SendAnalysisAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns("""{"recommendations": []}""");
        var engine = new RecommendationEngine(llmClient, Substitute.For<IDuplicateDetector>());

        var result = await engine.AnalyzeWithDiagnosticsAsync(session, "api-key", null, null, false, "zh-CN", CancellationToken.None);

        result.Recommendations.Should().ContainSingle().Which.Should().Match<CleanupRecommendation>(r =>
            r.TargetPath == @"D:\Game\Library" &&
            r.Category == RecommendationCategory.BuildCache &&
            r.SafetyRating == SafetyRating.ReviewFirst &&
            r.Entry == library &&
            r.Explanation.Contains("skill/AI risk review"));
    }

    [Fact]
    public async Task AnalyzeWithDiagnosticsAsync_DoesNotAddGenericLibraryRecommendationWithoutUnityMarkers()
    {
        var root = new FileEntry { Path = @"D:\", Name = @"D:\", IsDirectory = true };
        var generic = new FileEntry { Path = @"D:\Docs", Name = "Docs", IsDirectory = true };
        AddChild(root, generic);
        AddChild(generic, new FileEntry { Path = @"D:\Docs\Library", Name = "Library", IsDirectory = true, Size = 1024 });

        var session = new ScanSession { TargetPath = @"D:\", RootEntry = root };
        var llmClient = Substitute.For<ILlmClient>();
        llmClient.SendAnalysisAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns("""{"recommendations": []}""");
        var engine = new RecommendationEngine(llmClient, Substitute.For<IDuplicateDetector>());

        var result = await engine.AnalyzeWithDiagnosticsAsync(session, "api-key", null, null, false, "zh-CN", CancellationToken.None);

        result.Recommendations.Should().BeEmpty();
    }

    [Fact]
    public async Task AnalyzeWithDiagnosticsAsync_HandlesDeepLargeTreesWithBoundedMetadata()
    {
        var root = new FileEntry { Path = @"C:\", Name = @"C:\", IsDirectory = true };
        var current = root;
        for (var depth = 0; depth < 2_000; depth++)
        {
            var next = new FileEntry
            {
                Path = $@"C:\deep\folder-{depth}",
                Name = $"folder-{depth}",
                IsDirectory = true,
                Size = 10_000 - depth,
            };
            AddChild(current, next);
            current = next;
        }

        for (var index = 0; index < 10_000; index++)
        {
            AddChild(root, new FileEntry
            {
                Path = $@"C:\many\file-{index}.bin",
                Name = $"file-{index}.bin",
                Extension = ".bin",
                IsDirectory = false,
                Size = 1_048_576 + index,
                LastModified = DateTime.Today,
            });
        }

        var session = new ScanSession { TargetPath = @"C:\", RootEntry = root };
        string? metadata = null;
        var llmClient = Substitute.For<ILlmClient>();
        llmClient.SendAnalysisAsync(Arg.Any<string>(), Arg.Do<string>(value => metadata = value), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns("""{"recommendations": []}""");
        var engine = new RecommendationEngine(llmClient, Substitute.For<IDuplicateDetector>());

        var result = await engine.AnalyzeWithDiagnosticsAsync(session, "api-key", null, null, false, "zh-CN", CancellationToken.None);

        result.Recommendations.Should().BeEmpty();
        metadata.Should().NotBeNull();
        metadata!.Split("file-", StringSplitOptions.None).Length.Should().BeLessThan(250);
        metadata.Should().Contain("TOTAL_FILES: 10000");
    }

    [Fact]
    public async Task AnalyzeWithDiagnosticsAsync_LoadsCleanupWhitelistOncePerAnalysis()
    {
        var root = new FileEntry
        {
            Path = @"C:\",
            Name = @"C:\",
            IsDirectory = true,
        };

        for (var i = 0; i < 500; i++)
        {
            var dir = new FileEntry
            {
                Path = $@"C:\Temp\dir-{i}",
                Name = $"dir-{i}",
                IsDirectory = true,
                Size = 1024,
            };
            AddChild(root, dir);
            AddChild(dir, new FileEntry
            {
                Path = $@"C:\Temp\dir-{i}\file.tmp",
                Name = "file.tmp",
                Extension = ".tmp",
                Size = 1024,
                LastModified = DateTime.Today,
            });
        }

        var settingsService = Substitute.For<ISettingsService>();
        settingsService.LoadSettings().Returns(new AppSettings());
        var llmClient = Substitute.For<ILlmClient>();
        llmClient.SendAnalysisAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns("""{"recommendations": []}""");
        var engine = new RecommendationEngine(llmClient, Substitute.For<IDuplicateDetector>(), settingsService);

        await engine.AnalyzeWithDiagnosticsAsync(
            new ScanSession { TargetPath = @"C:\", RootEntry = root },
            "api-key",
            null,
            null,
            false,
            "zh-CN",
            CancellationToken.None);

        settingsService.Received(1).LoadSettings();
    }

    private static FileEntry CreateRootWithDirectory(string targetPath, string name, long size)
    {
        return new FileEntry
        {
            Path = @"C:\",
            Name = @"C:\",
            IsDirectory = true,
            Children =
            [
                new FileEntry
                {
                    Path = targetPath,
                    Name = name,
                    IsDirectory = true,
                    Size = size,
                }
            ]
        };
    }


    private static void AddChild(FileEntry parent, FileEntry child)
    {
        child.Parent = parent;
        child.Depth = parent.Depth + 1;
        parent.Children.Add(child);
    }

    private static string CreateRecommendationResponse(string path, long sizeBytes, string category, string safetyRating)
    {
        return JsonSerializer.Serialize(new
        {
            recommendations = new[]
            {
                new
                {
                    path,
                    size_bytes = sizeBytes,
                    category,
                    safety_rating = safetyRating,
                    explanation = "AI recommended cleanup candidate."
                }
            }
        });
    }

    private sealed class CapturingHandler(string responseBody) : HttpMessageHandler
    {
        public string RequestBody { get; private set; } = string.Empty;
        public string RequestUri { get; private set; } = string.Empty;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestUri = request.RequestUri!.ToString();
            RequestBody = await request.Content!.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responseBody)
            };
        }
    }
}
